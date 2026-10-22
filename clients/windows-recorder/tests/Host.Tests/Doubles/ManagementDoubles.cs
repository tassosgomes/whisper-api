using NAudio.Wave;
using Whisper.Recorder.Core.Ports;
using Whisper.Recorder.Core.Recording;
using Whisper.Recorder.Host.Management;
using Whisper.Recorder.Host.Playback;
using Whisper.Recorder.Host.Shell;
using Whisper.Recorder.Presentation.Playback;
using Whisper.Recorder.Presentation.Recording;

namespace Whisper.Recorder.Host.Tests.Doubles;

/// <summary>
/// Saída de áudio de mentira (V-09): nada toca nos alto-falantes. <see cref="Pump"/> faz o papel do relógio do dispositivo,
/// lendo do leitor de WAV real o quanto teria sido reproduzido; ao esgotar o arquivo, para como o dispositivo para.
/// </summary>
public sealed class FakeWavePlayer : IWavePlayer
{
    private IWaveProvider? provider;

    public event EventHandler<StoppedEventArgs>? PlaybackStopped;
    public PlaybackState PlaybackState { get; private set; } = PlaybackState.Stopped;
    public float Volume { get; set; } = 1f;
    public WaveFormat OutputWaveFormat => provider?.WaveFormat ?? new WaveFormat(16000, 16, 1);
    public bool Disposed { get; private set; }

    public void Init(IWaveProvider waveProvider) => provider = waveProvider;
    public void Play() => PlaybackState = PlaybackState.Playing;
    public void Pause() => PlaybackState = PlaybackState.Paused;
    public void Stop() => PlaybackState = PlaybackState.Stopped;

    public void Dispose()
    {
        Disposed = true;
        PlaybackState = PlaybackState.Stopped;
    }

    /// <summary>Avança o relógio do dispositivo: consome do arquivo o áudio desse tempo, se estiver tocando.</summary>
    public void Pump(TimeSpan elapsed)
    {
        if (PlaybackState != PlaybackState.Playing || provider is null)
            return;
        var buffer = new byte[(int)(elapsed.TotalSeconds * provider.WaveFormat.AverageBytesPerSecond)];
        if (provider.Read(buffer) < buffer.Length)
        {
            PlaybackState = PlaybackState.Stopped;
            PlaybackStopped?.Invoke(this, new StoppedEventArgs());
        }
    }
}

/// <summary>Cria as saídas de mentira e guarda a mais recente para o teste dar o "tempo" que passou.</summary>
public sealed class FakeOutputs
{
    public List<FakeWavePlayer> Created { get; } = [];
    public FakeWavePlayer Latest => Created[^1];

    public IWavePlayer Create()
    {
        var output = new FakeWavePlayer();
        Created.Add(output);
        return output;
    }

    public Func<IAudioPlayer> PlayerFactory => () => new NAudioAudioPlayer(Create);
}

/// <summary>WAV sintético no formato do app (16 kHz, mono, 16 bits), de silêncio: sem dispositivo e sem áudio real.</summary>
public static class SyntheticWav
{
    public static void Write(string path, TimeSpan duration)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var writer = new WaveFileWriter(path, new WaveFormat(16000, 16, 1));
        writer.Write(new byte[(int)(duration.TotalSeconds * 32000)]);
    }
}

/// <summary>Respostas e registro dos diálogos da gestão; um diálogo pode ficar aberto até o teste liberar.</summary>
public sealed class FakeManagementDialogs : IManagementDialogs
{
    public string? NewTitle { get; set; }
    public bool ConfirmDelete { get; set; } = true;
    public bool ConfirmPending { get; set; } = true;
    public bool ConfirmAll { get; set; } = true;
    /// <summary>Se definido, "apagar tudo" só responde quando o teste concluir esta tarefa (diálogo aberto).</summary>
    public TaskCompletionSource<bool>? HoldDeleteAll { get; set; }
    public TaskCompletionSource? DeleteAllAsked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public List<string> Prompts { get; } = [];
    public List<DeleteRecordingConfirmation> Deletes { get; } = [];
    public List<DeleteRecordingConfirmation> Pendings { get; } = [];
    public List<string> DeleteAlls { get; } = [];

    public Task<string?> PromptTitleAsync(string currentTitle)
    {
        Prompts.Add(currentTitle);
        return Task.FromResult(NewTitle);
    }

    public Task<bool> ConfirmDeleteAsync(DeleteRecordingConfirmation confirmation)
    {
        Deletes.Add(confirmation);
        return Task.FromResult(ConfirmDelete);
    }

    public Task<bool> ConfirmPendingDeleteAsync(DeleteRecordingConfirmation confirmation)
    {
        Pendings.Add(confirmation);
        return Task.FromResult(ConfirmPending);
    }

    public async Task<bool> ConfirmDeleteAllAsync(string message)
    {
        DeleteAlls.Add(message);
        DeleteAllAsked!.TrySetResult();
        return HoldDeleteAll is { } hold ? await hold.Task : ConfirmAll;
    }
}

/// <summary>
/// O que o host faz antes de apagar, sem janelas: libera o player da gravação, "fecha" a transcrição dela e, ao apagar tudo,
/// solta o diário de verdade (o mesmo <see cref="ReleasableLogWriter"/> do app).
/// </summary>
public sealed class TestManagementHost(PlayerScreenViewModel player, ReleasableLogWriter diary) : IManagementHost
{
    public Guid? OpenTranscript { get; set; }
    public List<Guid> ClosedTranscripts { get; } = [];
    public List<string> Calls { get; } = [];
    public bool? DeleteAllCompleted { get; private set; }

    public async Task ReleaseRecordingAsync(Guid id)
    {
        Calls.Add("release:" + id);
        if (OpenTranscript == id)
        {
            ClosedTranscripts.Add(id);
            OpenTranscript = null;
        }
        if (player.RecordingId == id)
            await player.ReleaseAsync();
    }

    public async Task ReleaseAllAsync()
    {
        Calls.Add("release-all");
        if (OpenTranscript is { } id)
            ClosedTranscripts.Add(id);
        OpenTranscript = null;
        await player.ReleaseAsync();
        diary.Release();
    }

    public void AfterDeleteAll(bool completed)
    {
        Calls.Add("after:" + completed);
        DeleteAllCompleted = completed;
        if (!completed)
            diary.Resume();
    }
}

/// <summary>A gestão montada como o <c>App.Compose</c>: núcleo real, pasta temporária, diário real e o player sobre o NAudio com saída de mentira.</summary>
public sealed class ManagementRig : IAsyncDisposable
{
    private ManagementRig(TrackingRig rig, ReleasableLogWriter diary)
    {
        Rig = rig;
        Diary = diary;
    }

    public TrackingRig Rig { get; }
    public ReleasableLogWriter Diary { get; }
    public FakeOutputs Outputs { get; } = new();
    public FakeManagementDialogs Dialogs { get; } = new();
    public PlayerScreenViewModel Player { get; private set; } = null!;
    public TestManagementHost Host { get; private set; } = null!;
    public RecordingManagementScreenViewModel Management { get; private set; } = null!;
    public string DiaryPath => Path.Combine(Rig.Directory, "diagnostics.log");

    public static async Task<ManagementRig> CreateAsync(ICredentialVault? vault = null, HttpMessageHandler? uploadHandler = null)
    {
        var rig = await TrackingRig.CreateAsync(vault: vault, uploadHandler: uploadHandler);
        var management = new ManagementRig(rig, new ReleasableLogWriter(Path.Combine(rig.Directory, "diagnostics.log")));
        await rig.Ui.InvokeAsync(() =>
        {
            management.Player = new PlayerScreenViewModel(management.Outputs.PlayerFactory, rig.Repository);
            management.Host = new TestManagementHost(management.Player, management.Diary);
            management.Management = new RecordingManagementScreenViewModel(
                new RecordingManagementViewModel(new RecordingManagement(rig.Repository, rig.Vault, rig.Directory)),
                rig.Screen, rig.Flow, rig.Access, management.Dialogs, management.Host, rig.Gate, rig.Directory);
        });
        return management;
    }

    /// <summary>Grava no disco uma gravação com áudio sintético e a deixa selecionada na lista.</summary>
    public async Task<Guid> SeedSelectedAsync(RecordingState state, string title, int seconds = 60, string? jobId = null)
    {
        var id = Rig.Seed(state, jobId, title);
        SyntheticWav.Write(Rig.Repository.AudioPath(id), TimeSpan.FromSeconds(seconds));
        await SelectAsync(id);
        return id;
    }

    public async Task SelectAsync(Guid id)
    {
        await Rig.Ui.InvokeAsync(() => Rig.Screen.RefreshRecordings());
        await TrackingRig.Eventually(() => Rig.Ui.InvokeAsync(() => Rig.Screen.Recordings.Any(r => r.Id == id)).GetAwaiter().GetResult());
        await Rig.Ui.InvokeAsync(() => Rig.Flow.SelectRecording(id));
    }

    public async ValueTask DisposeAsync()
    {
        await Rig.Ui.InvokeAsync(async () =>
        {
            Management.Dispose();
            await Player.ReleaseAsync();
        });
        Diary.Dispose();
        await Rig.DisposeAsync();
    }
}
