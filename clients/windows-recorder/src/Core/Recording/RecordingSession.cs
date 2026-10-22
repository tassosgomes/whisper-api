using Whisper.Recorder.Core.Ports;

namespace Whisper.Recorder.Core.Recording;

/// <summary>Local recording coordinator. Capture events and timer ticks are serialized here.</summary>
public sealed class RecordingSession : IAsyncDisposable
{
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromHours(2);
    public static readonly TimeSpan LimitWarningAt = MaximumDuration - TimeSpan.FromMinutes(5);
    public const long LowDiskBytes = 50 * 1024 * 1024;
    /// <summary>Capture blocks arrive after the period they cover; this much of the clock stays unwritten.</summary>
    public static readonly TimeSpan JitterMargin = TimeSpan.FromMilliseconds(500);
    private readonly object sync = new();
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly IAudioCapture capture;
    private readonly IClock clock;
    private readonly IScheduler scheduler;
    private readonly RecordingRepository repository;
    private readonly IDiskSpace disk;
    private readonly AudioTimeline timeline = new();
    private readonly Dictionary<AudioSourceKind, bool> available = [];
    private readonly Dictionary<AudioSourceKind, float> levels = [];
    private readonly Dictionary<AudioSourceKind, string> selectedDevices = [];
    private WavFile? wav;
    private RecordingMetadata? current;
    private DateTimeOffset segmentStart;
    private long segmentFrames;
    private long checkpointFrames;
    private CancellationTokenSource? timerCancellation;
    private Task? timer;
    private bool paused;
    private bool disposed;

    public RecordingSession(IAudioCapture capture, IClock clock, IScheduler scheduler,
        RecordingRepository repository, IDiskSpace? disk = null)
    {
        this.capture = capture;
        this.clock = clock;
        this.scheduler = scheduler;
        this.repository = repository;
        this.disk = disk ?? new LocalDiskSpace();
        capture.BlockAvailable += OnBlock;
        capture.LevelChanged += OnLevel;
        capture.DeviceAvailabilityChanged += OnAvailability;
    }

    public event Action? Changed;

    // Leituras da interface: um único instantâneo imutável, publicado sob o lock e lido sem lock. A captura e o
    // disco nunca fazem a interface esperar, e quem lê vê estado, pausa, disponibilidade e níveis do mesmo momento.
    private volatile SessionSnapshot snapshot = new(null, false, false, new Dictionary<AudioSourceKind, bool>(), new Dictionary<AudioSourceKind, float>());

    public SessionSnapshot Snapshot => snapshot;
    public RecordingMetadata? Current => snapshot.Current;
    public bool IsRecording => snapshot.IsRecording;
    public bool IsPaused => snapshot.IsPaused;
    public IReadOnlyDictionary<AudioSourceKind, bool> SourceAvailability => snapshot.Availability;
    public IReadOnlyDictionary<AudioSourceKind, float> Levels => snapshot.Levels;

    private bool IsRecordingLocked { get { lock (sync) return wav is not null; } }

    // Chamar com o lock: publica o estado atual e avisa a interface.
    private void Publish()
    {
        snapshot = new SessionSnapshot(current, wav is not null, paused,
            new Dictionary<AudioSourceKind, bool>(available), new Dictionary<AudioSourceKind, float>(levels));
    }

    private void Notify()
    {
        Publish();
        Changed?.Invoke();
    }

    public async Task StartAsync(IReadOnlyList<AudioDevice> devices, CancellationToken cancellationToken = default)
    {
        await lifecycle.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (devices.Count == 0)
                throw new InvalidOperationException("Escolha ao menos uma fonte de áudio para gravar.");
            if (devices.Select(d => d.Source).Distinct().Count() != devices.Count)
                throw new ArgumentException("Escolha apenas um dispositivo por fonte.", nameof(devices));
            if (IsRecordingLocked)
                throw new InvalidOperationException("Já há uma gravação em curso.");
            if (timer is not null)
                await timer;
            timerCancellation?.Dispose();

            lock (sync)
            {
                var now = clock.UtcNow;
                current = new RecordingMetadata(Guid.NewGuid(), now.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                    now, RecordingState.Recording, TimeSpan.Zero, devices.Select(d => d.Source).ToArray(), []);
                repository.Create(current);
                wav = new WavFile(repository.AudioPath(current.Id));
                segmentStart = now;
                segmentFrames = checkpointFrames = 0;
                paused = false;
                timeline.Clear();
                available.Clear();
                levels.Clear();
                selectedDevices.Clear();
                foreach (var device in devices)
                {
                    available[device.Source] = true;
                    levels[device.Source] = 0;
                    selectedDevices[device.Source] = device.Id;
                }
                Publish();
            }
            var started = new List<AudioSourceKind>();
            try
            {
                foreach (var device in devices)
                {
                    // Also stop the failing source: a host may acquire a handle before throwing.
                    started.Add(device.Source);
                    await capture.StartAsync(device, cancellationToken);
                }
            }
            catch
            {
                lock (sync)
                {
                    Warn(RecordingWarning.CaptureFailed);
                    Finish(RecordingState.Interrupted);
                    Publish();
                }
                foreach (var source in started)
                    await capture.StopAsync(source, CancellationToken.None);
                throw;
            }
            timerCancellation = new CancellationTokenSource();
            timer = RunTimerAsync(timerCancellation.Token);
            lock (sync)
                Notify();
        }
        finally { lifecycle.Release(); }
    }

    /// <summary>Also usable by a host's UI timer; the session schedules this every 100 ms.</summary>
    public void Tick() => Write(flush: false);

    /// <summary>
    /// Mixes up to the clock, keeping <see cref="JitterMargin"/> open for blocks still in flight;
    /// pause, stop and the 2 h limit flush the margin.
    /// </summary>
    private void Write(bool flush)
    {
        lock (sync)
        {
            if (wav is null || paused)
                return;
            var limit = (long)(MaximumDuration.TotalSeconds * WavFile.SampleRate);
            var live = Math.Min(limit,
                segmentFrames + Math.Max(0, (clock.UtcNow - segmentStart).Ticks) * WavFile.SampleRate / TimeSpan.TicksPerSecond);
            var target = flush || live >= limit ? live : live - JitterMargin.Ticks * WavFile.SampleRate / TimeSpan.TicksPerSecond;
            current = current! with { Duration = TimeSpan.FromSeconds(live / 16000d) };
            try
            {
                Span<short> buffer = stackalloc short[1600];
                while (wav is not null && wav.Frames < target)
                {
                    var free = disk.AvailableBytes(repository.DirectoryFor(current!.Id));
                    if (free < LowDiskBytes)
                        Warn(RecordingWarning.LowDiskSpace);
                    if (free < buffer.Length * 2 + 65536)
                    {
                        Warn(RecordingWarning.DiskFull);
                        Finish(RecordingState.Saved);
                        break;
                    }
                    var count = (int)Math.Min(buffer.Length, target - wav.Frames);
                    timeline.Mix(wav.Frames, buffer[..count]);
                    wav.Append(buffer[..count]);
                    if (wav.Frames - checkpointFrames >= WavFile.SampleRate)
                    {
                        wav.Checkpoint();
                        repository.Save(current);
                        checkpointFrames = wav.Frames;
                    }
                }
                if (wav is not null && current.Duration >= LimitWarningAt)
                    Warn(RecordingWarning.LimitApproaching);
                if (wav is not null && wav.Frames >= limit)
                {
                    Warn(RecordingWarning.ServiceLimitReached);
                    Finish(RecordingState.Saved);
                }
            }
            catch (IOException ex) when (IsDiskFull(ex))
            {
                Warn(RecordingWarning.DiskFull);
                Finish(RecordingState.Saved);
            }
            Notify();
        }
    }

    private static bool IsDiskFull(IOException ex) => (ex.HResult & 0xffff) is 28 or 39 or 112;

    public void Pause()
    {
        lock (sync)
        {
            Write(flush: true);
            if (wav is null || paused)
                return;
            paused = true;
            timeline.Clear();
            foreach (var source in levels.Keys)
                levels[source] = 0;
            wav.Checkpoint();
            repository.Save(current!);
            Notify();
        }
    }

    public void Resume()
    {
        lock (sync)
        {
            if (wav is null || !paused)
                return;
            segmentFrames = wav.Frames;
            segmentStart = clock.UtcNow;
            paused = false;
            Notify();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await lifecycle.WaitAsync(cancellationToken);
        try
        {
            lock (sync)
            {
                Write(flush: true);
                if (wav is not null)
                    Finish(RecordingState.Saved);
                timerCancellation?.Cancel();
            }
            try
            {
                if (timer is not null)
                    await timer;
            }
            finally
            {
                // Também quando parar um dispositivo falha: a tela precisa ver que a gravação terminou.
                lock (sync)
                    Notify();
            }
        }
        finally { lifecycle.Release(); }
    }

    private void Finish(RecordingState state)
    {
        if (wav is null)
            return;
        repository.ReleaseReserve(current!.Id);
        wav.Checkpoint();
        current = current with { State = state, Duration = TimeSpan.FromSeconds(wav.Frames / 16000d) };
        wav.Dispose();
        wav = null;
        paused = false;
        timeline.Clear();
        foreach (var source in levels.Keys)
            levels[source] = 0;
        repository.Save(current);
        Publish();
    }

    private void Warn(RecordingWarning warning)
    {
        if (!current!.Warnings.Contains(warning))
            current = current with { Warnings = [.. current.Warnings, warning] };
    }

    private async Task RunTimerAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (IsRecordingLocked)
            {
                await scheduler.DelayAsync(TimeSpan.FromMilliseconds(100), cancellationToken);
                Tick();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            // Use a fresh token: stopping a recording must release every device even on cancellation.
            AudioSourceKind[] sources;
            lock (sync)
                sources = current!.Sources;
            foreach (var source in sources)
                await capture.StopAsync(source, CancellationToken.None);
        }
    }

    private void OnBlock(AudioPcmBlock block)
    {
        lock (sync)
        {
            if (wav is null || paused || !available.GetValueOrDefault(block.Source) || block.CapturedAt < segmentStart)
                return;
            var offset = (block.CapturedAt - segmentStart).Ticks * WavFile.SampleRate / TimeSpan.TicksPerSecond;
            timeline.Add(block, segmentFrames + offset, wav.Frames);
        }
    }

    private void OnLevel(AudioLevel level)
    {
        lock (sync)
        {
            if (wav is null || (!paused && available.GetValueOrDefault(level.Source)))
                levels[level.Source] = Math.Clamp(level.Peak, 0, 1);
            snapshot = snapshot with { Levels = new Dictionary<AudioSourceKind, float>(levels) };
            Changed?.Invoke();
        }
    }

    private void OnAvailability(AudioDeviceAvailability change)
    {
        lock (sync)
        {
            if (wav is null || selectedDevices.GetValueOrDefault(change.Device.Source) != change.Device.Id)
                return;
            // A different device with the same kind must not invalidate the selected device.
            if (!change.IsAvailable)
            {
                available[change.Device.Source] = false;
                levels[change.Device.Source] = 0;
                Warn(RecordingWarning.DeviceLost);
                repository.Save(current!);
                Notify();
            }
            else if (!available[change.Device.Source])
            {
                // O dispositivo voltou e o host reabriu a captura: os blocos voltam a entrar na linha do tempo.
                available[change.Device.Source] = true;
                Notify();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        await StopAsync();
        disposed = true;
        capture.BlockAvailable -= OnBlock;
        capture.LevelChanged -= OnLevel;
        capture.DeviceAvailabilityChanged -= OnAvailability;
        timerCancellation?.Dispose();
        lifecycle.Dispose();
    }
}

/// <summary>Estado da sessão num único momento: imutável, seguro para ler de qualquer thread.</summary>
public sealed record SessionSnapshot(
    RecordingMetadata? Current,
    bool IsRecording,
    bool IsPaused,
    IReadOnlyDictionary<AudioSourceKind, bool> Availability,
    IReadOnlyDictionary<AudioSourceKind, float> Levels);
