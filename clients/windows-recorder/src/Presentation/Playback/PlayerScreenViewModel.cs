using System.ComponentModel;
using System.Text.Json;
using Whisper.Recorder.Core.Ports;
using Whisper.Recorder.Core.Recording;
using Whisper.Recorder.Presentation.Recording;

namespace Whisper.Recorder.Presentation.Playback;

/// <summary>
/// Tela do player (RF-04): tocar/pausar, posição, avançar e voltar. Sem tipos WPF. Cada gravação aberta usa um player
/// novo, criado por <c>createPlayer</c>, para que <see cref="ReleaseAsync"/> feche o arquivo de áudio de verdade
/// (apagar a gravação só funciona com o WAV liberado). Tudo roda na thread da interface, exceto a abertura e a
/// liberação do arquivo, que vão para fora dela. O host chama <see cref="Tick"/> em um temporizador enquanto a janela está aberta.
/// </summary>
public sealed class PlayerScreenViewModel : INotifyPropertyChanged
{
    /// <summary>Quanto "avançar" e "voltar" deslocam a posição.</summary>
    public static readonly TimeSpan Step = TimeSpan.FromSeconds(10);

    private readonly Func<IAudioPlayer> createPlayer;
    private readonly RecordingRepository recordings;
    private readonly SemaphoreSlim sync = new(1, 1);
    private IAudioPlayer? player;
    private string title = "";
    private string? error;
    private bool opening;
    private bool ended;
    private bool wasPlaying;
    private TimeSpan position;
    private TimeSpan duration;

    public PlayerScreenViewModel(Func<IAudioPlayer> createPlayer, RecordingRepository recordings)
    {
        this.createPlayer = createPlayer;
        this.recordings = recordings;
        PlayPause = new AsyncRelayCommand(PlayPauseAsync, () => IsOpen);
        Back = new AsyncRelayCommand(() => SeekToAsync(position - Step), () => IsOpen);
        Forward = new AsyncRelayCommand(() => SeekToAsync(position + Step), () => IsOpen);
        Close = new RelayCommand(() => CloseRequested?.Invoke());
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>O usuário pediu para fechar a tela (botão ou Esc); a janela do host se fecha.</summary>
    public event Action? CloseRequested;

    public AsyncRelayCommand PlayPause { get; }
    public AsyncRelayCommand Back { get; }
    public AsyncRelayCommand Forward { get; }
    public RelayCommand Close { get; }

    /// <summary>A gravação aberta (ou em abertura) nesta tela; o host a fecha antes de apagá-la do PC.</summary>
    public Guid? RecordingId { get; private set; }
    public string Title => title;
    public bool IsOpen => player is not null;
    public bool IsPlaying => IsOpen && player!.IsPlaying;
    public string? Error => error;
    public bool HasError => error is not null;

    public TimeSpan Position => position;
    public TimeSpan Duration => duration;
    public string PositionText => DurationFormat.Format(position);
    public string DurationText => DurationFormat.Format(duration);
    public double MaximumSeconds => duration.TotalSeconds;

    /// <summary>Posição em segundos para o controle deslizante; definir busca (alterações menores que meio segundo vêm do próprio relógio).</summary>
    public double PositionSeconds
    {
        get => position.TotalSeconds;
        set
        {
            if (IsOpen && Math.Abs(value - position.TotalSeconds) >= 0.5)
                _ = SeekToAsync(TimeSpan.FromSeconds(value));
        }
    }

    public string PlayPauseText => IsPlaying ? "Pausar" : "Reproduzir";
    public string PlayPauseGlyph => IsPlaying ? "⏸" : "▶";

    /// <summary>A situação em texto (não depende de cor): abrindo, reproduzindo, pausado, parado, fim ou o erro.</summary>
    public string StateText => error ?? (opening ? "Abrindo o áudio…"
        : !IsOpen ? "Nenhum áudio aberto"
        : IsPlaying ? "Reproduzindo"
        : ended ? "Fim da gravação"
        : position > TimeSpan.Zero ? "Pausado" : "Parado");

    public string PositionAutomationName => $"Posição: {PositionText} de {DurationText}";

    /// <summary>Abre a gravação (substitui a anterior). A leitura do cabeçalho do WAV roda fora da thread da interface.</summary>
    public async Task OpenAsync(Guid recordingId)
    {
        await ReleaseAsync();
        await sync.WaitAsync();
        IAudioPlayer? next = null;
        try
        {
            RecordingId = recordingId;
            title = "";
            error = null;
            ended = false;
            wasPlaying = false;
            position = duration = TimeSpan.Zero;
            opening = true;
            Notify();
            var (displayTitle, path) = await Task.Run(() => (recordings.Read(recordingId).DisplayTitle, recordings.AudioPath(recordingId)));
            title = displayTitle;
            next = createPlayer();
            await next.OpenAsync(path);
            duration = next.Duration;
            position = next.Position;
            player = next;
            next = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            // Texto fixo: a mensagem da exceção traz caminhos locais.
            error = "Não foi possível abrir o áudio desta gravação. Confira se o arquivo ainda existe no PC.";
        }
        finally
        {
            if (next is not null)
                await next.DisposeAsync();
            opening = false;
            sync.Release();
            Notify();
        }
    }

    /// <summary>Para a reprodução e fecha o arquivo de áudio. Pode ser chamada de novo; só termina quando o arquivo foi liberado.</summary>
    public async Task ReleaseAsync()
    {
        await sync.WaitAsync();
        try
        {
            var current = player;
            player = null;
            RecordingId = null;
            ended = false;
            wasPlaying = false;
            position = duration = TimeSpan.Zero;
            error = null;
            if (current is not null)
                await Task.Run(async () => await current.DisposeAsync());
        }
        finally
        {
            sync.Release();
            Notify();
        }
    }

    /// <summary>Atualiza posição e situação a partir do player. Chamado pelo temporizador da janela, na thread da interface.</summary>
    public void Tick()
    {
        if (player is not { } current)
            return;
        var playing = current.IsPlaying;
        var now = current.Position;
        var reachedEnd = wasPlaying && !playing && duration > TimeSpan.Zero && now >= duration - TimeSpan.FromMilliseconds(250);
        var changed = playing != wasPlaying || reachedEnd
            || (long)now.TotalSeconds != (long)position.TotalSeconds;
        wasPlaying = playing;
        position = now;
        if (reachedEnd)
            ended = true;
        if (changed)
            Notify();
    }

    public async Task SeekToAsync(TimeSpan target)
    {
        if (player is not { } current)
            return;
        target = target < TimeSpan.Zero ? TimeSpan.Zero : target > duration ? duration : target;
        try
        {
            await current.SeekAsync(target);
            position = current.Position;
            ended = false;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            error = "Não foi possível mover a reprodução.";
        }
        Notify();
    }

    private async Task PlayPauseAsync()
    {
        if (player is not { } current)
            return;
        try
        {
            if (current.IsPlaying)
                await current.PauseAsync();
            else
            {
                if (ended || (duration > TimeSpan.Zero && current.Position >= duration))
                    await current.SeekAsync(TimeSpan.Zero);
                ended = false;
                await current.PlayAsync();
            }
            wasPlaying = current.IsPlaying;
            position = current.Position;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            error = "Não foi possível reproduzir. Confira o dispositivo de saída de áudio do Windows.";
        }
        Notify();
    }

    private void Notify()
    {
        PlayPause.RaiseCanExecuteChanged();
        Back.RaiseCanExecuteChanged();
        Forward.RaiseCanExecuteChanged();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
}
