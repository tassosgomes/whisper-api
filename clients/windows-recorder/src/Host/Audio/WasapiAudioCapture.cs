using System.Runtime.InteropServices;
using Whisper.Recorder.Core.Diagnostics;
using Whisper.Recorder.Core.Ports;

namespace Whisper.Recorder.Host.Audio;

/// <summary>
/// Porta de captura sobre o WASAPI. Só entrega o que o dispositivo entrega: não insere silêncio nem
/// mixa (isso é do <c>AudioTimeline</c> do núcleo). Cada fonte tem ciclo próprio; a pré-visualização
/// do nível e a gravação usam as mesmas fontes, então <see cref="StartAsync"/> é idempotente por dispositivo.
/// Quando um dispositivo some, a fonte vira "perdida", o núcleo é avisado e o host tenta reabri-la
/// (o mesmo dispositivo ou, se ele era o padrão, o novo padrão) até a fonte ser parada.
/// </summary>
public sealed class WasapiAudioCapture : IAudioCapture, IAudioDeviceChanges
{
    private const int AccessDenied = unchecked((int)0x80070005);
    private static readonly TimeSpan IdleLevelAfter = TimeSpan.FromMilliseconds(250);

    private readonly ICaptureDeviceCatalog catalog;
    private readonly IClock clock;
    private readonly TimeSpan recoveryInterval;
    private readonly DiagnosticLog? diagnostic;
    private readonly object gate = new();
    private readonly Dictionary<AudioSourceKind, Channel> channels = [];
    private readonly Timer idleTimer;
    private bool disposed;

    public WasapiAudioCapture(ICaptureDeviceCatalog catalog, IClock clock,
        TimeSpan? recoveryInterval = null, DiagnosticLog? diagnostic = null)
    {
        this.catalog = catalog;
        this.clock = clock;
        this.recoveryInterval = recoveryInterval ?? TimeSpan.FromSeconds(1);
        this.diagnostic = diagnostic;
        catalog.DeviceAvailabilityChanged += OnCatalogChanged;
        catalog.DevicesChanged += OnCatalogDevicesChanged;
        idleTimer = new Timer(_ => PumpIdleLevels(), null, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
    }

    public event Action<AudioPcmBlock>? BlockAvailable;
    public event Action<AudioLevel>? LevelChanged;
    public event Action<AudioDeviceAvailability>? DeviceAvailabilityChanged;
    public event Action? DevicesChanged;

    public AudioDevice? ActiveDevice(AudioSourceKind source)
    {
        lock (gate)
            return channels.TryGetValue(source, out var channel) && !channel.Lost ? channel.ActiveDevice : null;
    }

    private void OnCatalogDevicesChanged() => DevicesChanged?.Invoke();

    public ValueTask<IReadOnlyList<AudioDevice>> ListDevicesAsync(
        AudioSourceKind source, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(catalog.List(source));
    }

    public ValueTask StartAsync(AudioDevice device, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Channel? replaced = null;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (channels.TryGetValue(device.Source, out var existing))
            {
                // Já aberta (por exemplo, pela pré-visualização): a gravação só continua de onde estava.
                if (existing.Device.Id == device.Id)
                    return ValueTask.CompletedTask;
                channels.Remove(device.Source);
                replaced = existing;
            }
        }
        if (replaced is not null)
            Release(replaced);

        var channel = new Channel(device);
        Attach(channel, device);
        lock (gate)
        {
            if (disposed)
            {
                Release(channel);
                throw new ObjectDisposedException(nameof(WasapiAudioCapture));
            }
            channels[device.Source] = channel;
        }
        diagnostic?.Write(DiagnosticEvent.CaptureStarted);
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(AudioSourceKind source, CancellationToken cancellationToken = default)
    {
        Channel? channel;
        lock (gate)
        {
            if (!channels.Remove(source, out channel))
                return ValueTask.CompletedTask;
        }
        Release(channel);
        diagnostic?.Write(DiagnosticEvent.CaptureStopped);
        LevelChanged?.Invoke(new AudioLevel(source, 0));
        return ValueTask.CompletedTask;
    }

    /// <summary>Zera o nível das fontes que pararam de entregar dados (o loopback só entrega enquanto algo toca).</summary>
    public void PumpIdleLevels()
    {
        List<AudioSourceKind>? idle = null;
        lock (gate)
        {
            if (disposed)
                return;
            foreach (var (kind, channel) in channels)
            {
                if (channel.Lost || channel.LastPeak <= 0 || clock.UtcNow - channel.LastDataAt < IdleLevelAfter)
                    continue;
                channel.LastPeak = 0;
                (idle ??= []).Add(kind);
            }
        }
        foreach (var kind in idle ?? [])
            LevelChanged?.Invoke(new AudioLevel(kind, 0));
    }

    private void Attach(Channel channel, AudioDevice device, bool recovering = false)
    {
        ICaptureSource source;
        try
        {
            source = catalog.Open(device);
        }
        catch (COMException ex) when (ex.HResult == AccessDenied)
        {
            throw new UnauthorizedAccessException("O Windows negou o acesso ao dispositivo de áudio.");
        }
        try
        {
            var format = source.Format;
            if (!PcmConverter.IsSupported(format))
                throw new InvalidOperationException("Formato de áudio do dispositivo não suportado.");
            var stamper = new BlockTimestamper(format.SampleRate);
            source.DataAvailable += (buffer, count) => OnData(channel, source, stamper, buffer, count);
            source.Stopped += error => OnStopped(channel, source, error);
            lock (gate)
            {
                channel.Source = source;
                channel.ActiveId = device.Id;
                channel.ActiveDevice = device;
                channel.Lost = false;
                channel.LastDataAt = clock.UtcNow;
            }
            source.Start();
        }
        catch (COMException ex) when (ex.HResult == AccessDenied)
        {
            Discard(channel, source, recovering);
            throw new UnauthorizedAccessException("O Windows negou o acesso ao dispositivo de áudio.");
        }
        catch
        {
            Discard(channel, source, recovering);
            throw;
        }
    }

    private void Discard(Channel channel, ICaptureSource source, bool recovering)
    {
        lock (gate)
        {
            if (channel.Source == source)
                channel.Source = null;
            if (recovering)
                channel.Lost = true;
        }
        DisposeSource(source);
    }

    private void OnData(Channel channel, ICaptureSource source, BlockTimestamper stamper, byte[] buffer, int count)
    {
        var arrival = clock.UtcNow;
        AudioSourceKind kind;
        lock (gate)
        {
            if (channel.Source != source || channel.Lost || disposed)
                return;
            kind = channel.Device.Source;
        }
        var format = source.Format;
        var samples = PcmConverter.ToInt16(buffer.AsSpan(0, count), format, out var peak);
        if (samples.Length == 0)
            return;
        var capturedAt = stamper.Stamp(arrival, samples.Length / format.Channels);
        lock (gate)
        {
            channel.LastDataAt = arrival;
            channel.LastPeak = peak;
        }
        BlockAvailable?.Invoke(new AudioPcmBlock(kind, capturedAt, format.SampleRate, format.Channels, samples));
        LevelChanged?.Invoke(new AudioLevel(kind, peak));
    }

    private void OnStopped(Channel channel, ICaptureSource source, Exception? error)
    {
        lock (gate)
        {
            // Parada pedida por este host (Stopping/troca de fonte) não é perda de dispositivo.
            if (channel.Source != source || channel.Stopping || disposed)
                return;
        }
        MarkLost(channel);
    }

    private void OnCatalogChanged(string id, bool available)
    {
        Channel? affected = null;
        lock (gate)
        {
            if (!available)
                affected = channels.Values.FirstOrDefault(c => !c.Lost && c.ActiveId == id);
        }
        if (affected is not null)
            MarkLost(affected);
        // O retorno do dispositivo é tratado pela rotina de recuperação, que tenta reabrir a fonte.
    }

    private void MarkLost(Channel channel)
    {
        ICaptureSource? old;
        CancellationToken token;
        lock (gate)
        {
            if (channel.Lost || channel.Stopping || disposed)
                return;
            channel.Lost = true;
            channel.LastPeak = 0;
            old = channel.Source;
            channel.Source = null;
            channel.Recovery = new CancellationTokenSource();
            token = channel.Recovery.Token;
        }
        diagnostic?.Write(DiagnosticEvent.AudioDeviceLost);
        var kind = channel.Device.Source;
        DeviceAvailabilityChanged?.Invoke(new AudioDeviceAvailability(channel.Device, false));
        DevicesChanged?.Invoke();
        LevelChanged?.Invoke(new AudioLevel(kind, 0));
        // Pode vir de um callback do sistema de áudio: nada de bloquear nem chamar de volta a pilha de áudio.
        _ = Task.Run(() =>
        {
            if (old is not null)
                DisposeSource(old);
            return RecoverAsync(channel, token);
        });
    }

    private async Task RecoverAsync(Channel channel, CancellationToken token)
    {
        var kind = channel.Device.Source;
        try
        {
            while (true)
            {
                await Task.Delay(recoveryInterval, token);
                foreach (var candidate in Candidates(channel))
                {
                    try { Attach(channel, candidate, recovering: true); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        continue;
                    }
                    lock (gate)
                    {
                        if (channel.Stopping || disposed || !channels.TryGetValue(kind, out var current) || current != channel)
                        {
                            var source = channel.Source;
                            channel.Source = null;
                            if (source is not null)
                                DisposeSource(source);
                            return;
                        }
                        channel.Lost = false;
                    }
                    diagnostic?.Write(DiagnosticEvent.AudioDeviceRestored);
                    // O núcleo conhece o dispositivo pedido: o aviso de volta usa o dispositivo lógico.
                    DeviceAvailabilityChanged?.Invoke(new AudioDeviceAvailability(channel.Device, true));
                    DevicesChanged?.Invoke();
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private IEnumerable<AudioDevice> Candidates(Channel channel)
    {
        var kind = channel.Device.Source;
        List<AudioDevice> active;
        try { active = [.. catalog.List(kind)]; }
        catch (COMException) { yield break; }
        if (active.Any(d => d.Id == channel.Device.Id))
            yield return channel.Device;
        if (channel.Device.IsDefault && catalog.GetDefault(kind) is { } fallback && fallback.Id != channel.Device.Id)
            yield return fallback;
    }

    private void Release(Channel channel)
    {
        ICaptureSource? source;
        lock (gate)
        {
            channel.Stopping = true;
            channel.Recovery?.Cancel();
            source = channel.Source;
            channel.Source = null;
        }
        if (source is not null)
            DisposeSource(source);
    }

    private static void DisposeSource(ICaptureSource source)
    {
        try { source.Stop(); }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ObjectDisposedException) { }
        try { source.Dispose(); }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ObjectDisposedException) { }
    }

    public ValueTask DisposeAsync()
    {
        Channel[] all;
        lock (gate)
        {
            if (disposed)
                return ValueTask.CompletedTask;
            disposed = true;
            all = [.. channels.Values];
            channels.Clear();
        }
        idleTimer.Dispose();
        catalog.DeviceAvailabilityChanged -= OnCatalogChanged;
        catalog.DevicesChanged -= OnCatalogDevicesChanged;
        foreach (var channel in all)
            Release(channel);
        catalog.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed class Channel(AudioDevice device)
    {
        public AudioDevice Device { get; } = device;
        public string ActiveId { get; set; } = device.Id;
        public AudioDevice ActiveDevice { get; set; } = device;
        public ICaptureSource? Source { get; set; }
        public bool Lost { get; set; }
        public bool Stopping { get; set; }
        public CancellationTokenSource? Recovery { get; set; }
        public DateTimeOffset LastDataAt { get; set; }
        public float LastPeak { get; set; }
    }
}
