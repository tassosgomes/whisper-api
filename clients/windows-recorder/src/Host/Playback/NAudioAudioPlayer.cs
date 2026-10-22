using System.IO;
using NAudio.Wave;
using Whisper.Recorder.Core.Ports;

namespace Whisper.Recorder.Host.Playback;

/// <summary>Falha ao abrir ou reproduzir o WAV. Só carrega texto fixo: a mensagem original traz caminhos locais.</summary>
public sealed class AudioPlaybackException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>
/// Porta <see cref="IAudioPlayer"/> sobre o NAudio: <see cref="WaveFileReader"/> lê o WAV e um <see cref="IWavePlayer"/>
/// (por padrão <see cref="WaveOut"/>, que no NAudio 3 é o antigo WaveOutEvent: callback por evento, sem janela, criável e descartável em qualquer thread; o Windows adapta de 16 kHz mono para o formato do dispositivo) toca.
/// Escolhido no lugar do MediaPlayer do WPF por quatro razões: não depende do dispatcher nem de janela; o arquivo só fica
/// aberto entre <see cref="OpenAsync"/> e <see cref="DisposeAsync"/>, sem atraso na liberação (apagar a gravação exige o
/// WAV fechado); a posição é exata (o leitor é o relógio) e a busca é síncrona; e a saída é injetável, então os testes usam
/// um dublê de <see cref="IWavePlayer"/> sem tocar nada nos alto-falantes. Os métodos são curtos; a abertura roda fora da
/// thread de quem chama.
/// </summary>
public sealed class NAudioAudioPlayer : IAudioPlayer
{
    private readonly Func<IWavePlayer> createOutput;
    private readonly Lock gate = new();
    // Só protege o leitor: a thread da saída lê por aqui enquanto a interface busca. Nunca se espera `gate` com esta trava presa
    // (a ordem é sempre gate -> readLock), então descartar a saída sob `gate` não trava a leitura.
    private readonly Lock readLock = new();
    private WaveFileReader? reader;
    private IWavePlayer? output;
    private bool disposed;

    public NAudioAudioPlayer(Func<IWavePlayer>? createOutput = null) =>
        this.createOutput = createOutput ?? (() => new WaveOut());

    public TimeSpan Position
    {
        get { lock (gate) lock (readLock) return reader is null ? TimeSpan.Zero : reader.CurrentTime; }
    }

    public TimeSpan Duration
    {
        get { lock (gate) lock (readLock) return reader is null ? TimeSpan.Zero : reader.TotalTime; }
    }

    public bool IsPlaying
    {
        get { lock (gate) return output?.PlaybackState == PlaybackState.Playing; }
    }

    public async ValueTask OpenAsync(string wavPath, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        WaveFileReader opened;
        try
        {
            opened = await Task.Run(() => new WaveFileReader(wavPath), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            throw new AudioPlaybackException("O arquivo de áudio não está em um formato que o player entende.", ex);
        }
        WaveFileReader? previous;
        IWavePlayer? previousOutput;
        lock (gate)
        {
            previous = reader;
            previousOutput = output;
            reader = opened;
            output = null;
        }
        previousOutput?.Dispose();
        previous?.Dispose();
    }

    public ValueTask PlayAsync(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var source = reader ?? throw new InvalidOperationException("Nenhum áudio aberto.");
            if (output is null || output.PlaybackState == PlaybackState.Stopped)
            {
                // Depois do fim ou de Stop a saída precisa ser iniciada de novo; no fim do arquivo, recomeça do início.
                output?.Dispose();
                lock (readLock)
                {
                    if (source.Position >= source.Length)
                        source.Position = 0;
                }
                output = Initialize(new LockedProvider(source, readLock));
            }
            output.Play();
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask PauseAsync(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (output?.PlaybackState == PlaybackState.Playing)
                output.Pause();
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var source = reader ?? throw new InvalidOperationException("Nenhum áudio aberto.");
            // A busca e a leitura da saída passam pela mesma trava (ver LockedProvider).
            lock (readLock)
            {
                var clamped = position < TimeSpan.Zero ? TimeSpan.Zero : position > source.TotalTime ? source.TotalTime : position;
                source.CurrentTime = clamped;
            }
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            output?.Stop();
            if (reader is not null)
            {
                lock (readLock)
                    reader.Position = 0;
            }
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        WaveFileReader? oldReader;
        IWavePlayer? oldOutput;
        lock (gate)
        {
            if (disposed)
                return ValueTask.CompletedTask;
            disposed = true;
            oldReader = reader;
            oldOutput = output;
            reader = null;
            output = null;
        }
        // A saída para primeiro: ela lê do leitor em outra thread.
        try { oldOutput?.Dispose(); }
        finally { oldReader?.Dispose(); }
        return ValueTask.CompletedTask;
    }

    /// <summary>Lê o WAV sob a mesma trava da busca: a saída lê em outra thread e a posição do leitor muda no meio da leitura sem isso.</summary>
    private sealed class LockedProvider(WaveFileReader reader, Lock readLock) : IWaveProvider
    {
        public WaveFormat WaveFormat => reader.WaveFormat;

        public int Read(Span<byte> buffer)
        {
            lock (readLock)
                return reader.Read(buffer);
        }
    }

    private IWavePlayer Initialize(IWaveProvider source)
    {
        IWavePlayer? created = null;
        try
        {
            created = createOutput();
            created.Init(source);
            return created;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Sem dispositivo de saída (ou driver recusou): mensagem fixa, o arquivo permanece aberto e liberável.
            created?.Dispose();
            throw new AudioPlaybackException("Não foi possível iniciar o dispositivo de saída de áudio.", ex);
        }
    }
}
