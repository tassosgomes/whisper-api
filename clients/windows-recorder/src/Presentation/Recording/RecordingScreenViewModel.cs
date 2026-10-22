using System.Collections.Concurrent;
using System.ComponentModel;
using Whisper.Recorder.Core.Ports;
using Whisper.Recorder.Core.Recording;
using Whisper.Recorder.Core.Tracking;

namespace Whisper.Recorder.Presentation.Recording;

/// <summary>
/// Estado da tela de gravação: fontes, dispositivos, nível antes de gravar, comandos e lista.
/// Não usa tipos WPF. O nível antes de gravar vem da própria porta de captura, aberta como
/// pré-visualização; a sessão descarta esses blocos até a gravação começar.
/// </summary>
public sealed class RecordingScreenViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    public const string MicrophoneHelp =
        "O Windows não deixou o app usar o microfone. Abra Configurações > Privacidade e segurança > Microfone, " +
        "ative \"Acesso ao microfone\" e \"Permitir que aplicativos da área de trabalho acessem seu microfone\" e toque em \"Tentar de novo\". " +
        "Enquanto isso, você pode gravar só o áudio do sistema.";

    private readonly RecordingViewModel recording;
    private readonly IAudioCapture capture;
    private readonly DataOperationGate? gate;
    private readonly Dictionary<AudioSourceKind, AudioDevice> running = [];
    private IReadOnlyList<RecordingListItem> recordings = [];
    private IReadOnlyList<AudioDevice> microphones = [];
    private IReadOnlyList<AudioDevice> outputs = [];
    private AudioDevice? selectedMicrophone;
    private AudioDevice? selectedOutput;
    private bool useMicrophone = true;
    private bool useSystemAudio = true;
    private bool microphoneDenied;
    private bool wasRecording;
    private readonly SynchronizationContext? context = SynchronizationContext.Current;
    private string? startError;
    private string? previewError;
    private bool refreshing;
    private bool refreshAgain;
    private volatile bool disposed;
    private bool shuttingDown;
    private bool initialized;
    private (bool, bool, int, string?) lastShape;
    private int refreshScheduled;
    private long refreshRequested;
    private long refreshApplied;
    // Acompanhamento (V-07): a última situação de cada gravação fica aqui até o próximo ciclo da interface.
    private readonly ConcurrentDictionary<Guid, TrackingStatus> trackingPending = new();
    private readonly ConcurrentDictionary<Guid, bool> offline = new();
    private int trackingScheduled;

    public RecordingScreenViewModel(RecordingViewModel recording, IAudioCapture capture, DataOperationGate? gate = null)
    {
        this.recording = recording;
        this.capture = capture;
        this.gate = gate;
        Start = new AsyncRelayCommand(StartAsync, () => !IsRecording && !DataOperationBusy);
        PauseResume = new RelayCommand(TogglePause, () => IsRecording);
        Stop = new AsyncRelayCommand(StopAsync, () => IsRecording);
        Toggle = new AsyncRelayCommand(ToggleAsync);
        SystemOnly = new AsyncRelayCommand(SystemOnlyAsync, () => !IsRecording && !DataOperationBusy);
        RetryMicrophone = new AsyncRelayCommand(RetryMicrophoneAsync, () => !IsRecording && MicrophoneDenied);
        RefreshDevices = new AsyncRelayCommand(RefreshDevicesAsync, () => !IsRecording);
        if (capture is IAudioDeviceChanges changes)
            changes.DevicesChanged += OnDevicesChanged;
        if (gate is not null)
            gate.Changed += OnGateChanged;
        recording.PropertyChanged += OnRecordingChanged;
        ReloadRecordings();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public AsyncRelayCommand Start { get; }
    public RelayCommand PauseResume { get; }
    public AsyncRelayCommand Stop { get; }
    /// <summary>Atalho global: inicia quando parado e para (salvando) quando grava ou está pausado.</summary>
    public AsyncRelayCommand Toggle { get; }
    public AsyncRelayCommand SystemOnly { get; }
    public AsyncRelayCommand RetryMicrophone { get; }
    public AsyncRelayCommand RefreshDevices { get; }

    /// <summary>Apagar dados do app em curso: nenhuma captura nova começa até a operação terminar.</summary>
    public bool DataOperationBusy => gate?.IsBusy == true;

    public bool IsRecording => recording.IsRecording;
    public bool IsPaused => recording.IsPaused;
    public bool CanChangeSources => !IsRecording;
    public IReadOnlyList<AudioDevice> Microphones => microphones;
    public IReadOnlyList<AudioDevice> SystemOutputs => outputs;
    public IReadOnlyList<RecordingListItem> Recordings => recordings;
    public IReadOnlyList<RecordingWarning> ActiveWarnings => recording.Warnings;
    public Guid? CurrentRecordingId => recording.CurrentId;
    public string DurationText => DurationFormat.Format(recording.Duration);
    public RecordingIndicatorState Indicator => RecordingIndicatorState.From(IsRecording, IsPaused, recording.Duration);
    public string? ThirdPartyNotice => recording.ThirdPartyNotice;
    public bool MicrophoneDenied => microphoneDenied;
    public string? MicrophoneDeniedHelp => microphoneDenied ? MicrophoneHelp : null;
    public string PauseResumeText => IsPaused ? "Retomar" : "Pausar";

    public string StateText => !IsRecording
        ? "Pronto para gravar"
        : IsPaused ? "Gravação pausada" : "Gravando";

    public string StateGlyph => !IsRecording ? "○" : IsPaused ? "⏸" : "●";

    /// <summary>Falha ao iniciar a gravação.</summary>
    public string? Error => recording.Error ?? startError;
    public bool HasError => Error is not null;
    /// <summary>Problema ao abrir um dispositivo para mostrar o nível; não é falha de gravação.</summary>
    public string? PreviewError => previewError;
    public bool HasPreviewError => previewError is not null;

    /// <summary>Quando uma fonte foi reaberta em outro dispositivo (o novo padrão), diz qual está em uso.</summary>
    public string? ActiveDeviceNote
    {
        get
        {
            if (capture is not IAudioDeviceChanges changes)
                return null;
            var notes = new List<string>();
            foreach (var (kind, selected, label) in new[]
            {
                (AudioSourceKind.Microphone, selectedMicrophone, "Microfone"),
                (AudioSourceKind.SystemLoopback, selectedOutput, "Áudio do sistema")
            })
            {
                if (selected is not null && changes.ActiveDevice(kind) is { } active && active.Id != selected.Id)
                    notes.Add($"{label}: em uso {active.DisplayName}.");
            }
            return notes.Count == 0 ? null : string.Join(" ", notes);
        }
    }
    public string? EmptyListText => recordings.Count == 0 ? "Nenhuma gravação ainda." : null;
    public bool HasWarning => recording.Warnings.Count > 0;

    public string? WarningText
    {
        get
        {
            var lines = recording.Warnings.Select(w => w switch
            {
                RecordingWarning.DeviceLost => "Um dispositivo foi desconectado. A gravação continua com a outra fonte.",
                RecordingWarning.LowDiskSpace => "Pouco espaço livre no disco.",
                RecordingWarning.DiskFull => "O disco encheu. A gravação foi salva até aqui.",
                RecordingWarning.LimitApproaching => "A gravação chega a 2 horas em breve.",
                RecordingWarning.ServiceLimitReached => "Limite de 2 horas atingido. A gravação foi salva.",
                _ => "Não foi possível abrir um dispositivo de áudio."
            }).ToArray();
            return lines.Length == 0 ? null : string.Join(" ", lines);
        }
    }

    public AudioDevice? SelectedMicrophone
    {
        get => selectedMicrophone;
        set => _ = ChangeDeviceAsync(AudioSourceKind.Microphone, value);
    }

    public AudioDevice? SelectedSystemOutput
    {
        get => selectedOutput;
        set => _ = ChangeDeviceAsync(AudioSourceKind.SystemLoopback, value);
    }

    public bool UseMicrophone
    {
        get => useMicrophone;
        set => _ = ChangeEnabledAsync(AudioSourceKind.Microphone, value);
    }

    public bool UseSystemAudio
    {
        get => useSystemAudio;
        set => _ = ChangeEnabledAsync(AudioSourceKind.SystemLoopback, value);
    }

    public double MicrophoneLevel => Level(AudioSourceKind.Microphone);
    public double SystemLevel => Level(AudioSourceKind.SystemLoopback);
    public string MicrophoneStatus => SourceStatus(AudioSourceKind.Microphone, useMicrophone, microphoneDenied);
    public string SystemStatus => SourceStatus(AudioSourceKind.SystemLoopback, useSystemAudio, false);

    private double Level(AudioSourceKind source) =>
        recording.Levels.TryGetValue(source, out var peak) ? peak : 0;

    private string SourceStatus(AudioSourceKind source, bool enabled, bool denied)
    {
        if (denied)
            return "Sem acesso";
        if (!enabled)
            return "Desligado";
        if (recording.Sources.TryGetValue(source, out var available) && !available)
            return "Desconectado";
        if (!IsRecording && (source == AudioSourceKind.Microphone ? selectedMicrophone : selectedOutput) is null)
            return "Sem dispositivo";
        if (!running.ContainsKey(source) && !IsRecording)
            return "Indisponível";
        return $"Nível {(int)Math.Round(Level(source) * 100)}%";
    }

    /// <summary>Lista os dispositivos, escolhe os padrão do Windows e abre a pré-visualização do nível.</summary>
    public async Task RefreshDevicesAsync()
    {
        initialized = true;
        microphones = await capture.ListDevicesAsync(AudioSourceKind.Microphone);
        outputs = await capture.ListDevicesAsync(AudioSourceKind.SystemLoopback);
        selectedMicrophone = Pick(microphones, selectedMicrophone);
        selectedOutput = Pick(outputs, selectedOutput);
        // Um dispositivo que saiu da lista (fone desligado) é largado: a fonte cai para o padrão atual do Windows.
        foreach (var (kind, selected) in new[] { (AudioSourceKind.Microphone, selectedMicrophone), (AudioSourceKind.SystemLoopback, selectedOutput) })
        {
            if (running.TryGetValue(kind, out var open) && open.Id != selected?.Id)
                await StopPreviewAsync(kind);
        }
        await StartPreviewAsync();
    }

    private static AudioDevice? Pick(IReadOnlyList<AudioDevice> devices, AudioDevice? previous) =>
        devices.FirstOrDefault(d => previous is not null && d.Id == previous.Id)
        ?? devices.FirstOrDefault(d => d.IsDefault) ?? devices.FirstOrDefault();

    /// <summary>Abre (de modo idempotente) as fontes ligadas para mostrar o nível. Não grava nada.</summary>
    public async Task StartPreviewAsync()
    {
        previewError = null;
        foreach (var (kind, device, enabled) in Selection())
        {
            if (!enabled || device is null)
            {
                await StopPreviewAsync(kind);
                continue;
            }
            try
            {
                await capture.StartAsync(device);
                running[kind] = device;
                if (kind == AudioSourceKind.Microphone)
                    microphoneDenied = false;
            }
            catch (UnauthorizedAccessException) when (kind == AudioSourceKind.Microphone)
            {
                microphoneDenied = true;
                running.Remove(kind);
                useMicrophone = false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A mensagem da exceção pode conter dados do dispositivo: só texto fixo vai para a tela.
                running.Remove(kind);
                previewError = "Não foi possível abrir o dispositivo de áudio selecionado. Escolha outro na lista.";
            }
        }
        Raise();
    }

    private (AudioSourceKind Kind, AudioDevice? Device, bool Enabled)[] Selection() =>
    [
        (AudioSourceKind.Microphone, selectedMicrophone, useMicrophone),
        (AudioSourceKind.SystemLoopback, selectedOutput, useSystemAudio)
    ];

    private async Task StopPreviewAsync(AudioSourceKind kind)
    {
        if (running.Remove(kind))
            await capture.StopAsync(kind);
    }

    private async Task ChangeDeviceAsync(AudioSourceKind kind, AudioDevice? value)
    {
        var current = kind == AudioSourceKind.Microphone ? selectedMicrophone : selectedOutput;
        // O ComboBox do WPF devolve nulo ao trocar a lista; só uma escolha explícita vale.
        if (value is null)
            return;
        if (IsRecording || value == current)
            return;
        if (kind == AudioSourceKind.Microphone)
            selectedMicrophone = value;
        else
            selectedOutput = value;
        await StopPreviewAsync(kind);
        await StartPreviewAsync();
    }

    private async Task ChangeEnabledAsync(AudioSourceKind kind, bool value)
    {
        var current = kind == AudioSourceKind.Microphone ? useMicrophone : useSystemAudio;
        if (IsRecording || value == current)
            return;
        if (kind == AudioSourceKind.Microphone)
        {
            useMicrophone = value;
            if (value)
                microphoneDenied = false;
        }
        else
            useSystemAudio = value;
        await StartPreviewAsync();
    }

    public async Task StartAsync()
    {
        if (DataOperationBusy)
        {
            // O atalho global e a bandeja chamam este método direto: o bloqueio vale para todos os caminhos.
            startError = BusyMessage;
            Raise();
            return;
        }
        startError = null;
        previewError = null;
        var devices = new List<AudioDevice>();
        if (useMicrophone && selectedMicrophone is not null)
            devices.Add(selectedMicrophone);
        if (useSystemAudio && selectedOutput is not null)
            devices.Add(selectedOutput);
        if (devices.Count == 0)
        {
            startError = microphoneDenied
                ? "Sem acesso ao microfone e sem saída de áudio do sistema. Libere o microfone ou ligue um dispositivo de saída."
                : "Escolha ao menos uma fonte de áudio para gravar.";
            Raise();
            return;
        }
        await recording.StartAsync(devices);
        if (!IsRecording)
        {
            // A sessão libera as fontes quando a abertura falha: reabre a pré-visualização.
            running.Clear();
            await StartPreviewAsync();
        }
        ReloadRecordings();
        Raise();
    }

    /// <summary>
    /// Logoff ou desligamento do Windows: para e salva sem diálogo (um diálogo bloquearia o desligamento)
    /// e não reabre a pré-visualização. Sem gravação em curso, só marca o encerramento.
    /// </summary>
    public async Task StopForShutdownAsync()
    {
        shuttingDown = true;
        if (IsRecording)
            await recording.StopAsync();
    }

    /// <summary>Para e salva. O fim da gravação reabre o nível em <see cref="FinishedAsync"/>.</summary>
    public Task StopAsync() => recording.StopAsync();

    private void TogglePause()
    {
        if (IsPaused)
            recording.Resume();
        else
            recording.Pause();
    }

    private Task ToggleAsync() => IsRecording ? StopAsync() : StartAsync();

    private async Task SystemOnlyAsync()
    {
        await ChangeEnabledAsync(AudioSourceKind.Microphone, false);
        await StartAsync();
    }

    private async Task RetryMicrophoneAsync()
    {
        useMicrophone = true;
        await StartPreviewAsync();
    }

    private async Task AfterRecordingAsync()
    {
        // StopAsync da sessão aguarda o cronômetro, que libera as fontes; só então reabre o nível.
        if (shuttingDown)
            return;
        running.Clear();
        ReloadRecordings();
        // A lista é recarregada: o dispositivo escolhido pode ter saído durante a gravação.
        await RefreshDevicesAsync();
    }

    private void OnRecordingChanged(object? sender, PropertyChangedEventArgs e)
    {
        var nowRecording = recording.IsRecording;
        var ended = wasRecording && !nowRecording && !disposed;
        wasRecording = nowRecording;
        // Cronômetro e níveis chegam em alta frequência: só as propriedades afetadas são avisadas. Mudança de
        // estado (gravando, pausa, avisos, erro) reavalia a tela toda.
        var shape = (nowRecording, recording.IsPaused, recording.Warnings.Count, recording.Error);
        if (shape != lastShape)
        {
            lastShape = shape;
            Raise();
        }
        else
            RaiseTick();
        if (ended)
            _ = FinishedAsync();
    }

    /// <summary>Fim automático (disco cheio, limite de 2 h) ou por Stop: a sessão já salvou; reabre o nível.</summary>
    private async Task FinishedAsync()
    {
        try
        {
            await recording.StopAsync();
            await AfterRecordingAsync();
        }
        catch (ObjectDisposedException) { }
    }

    private void OnDevicesChanged()
    {
        if (disposed || shuttingDown)
            return;
        if (context is not null && SynchronizationContext.Current != context)
            context.Post(_ => _ = HandleDevicesChangedAsync(), null);
        else
            _ = HandleDevicesChangedAsync();
    }

    /// <summary>Com a gravação em curso só atualiza a tela; a lista é recarregada ao terminar.</summary>
    private async Task HandleDevicesChangedAsync()
    {
        if (disposed || shuttingDown)
            return;
        if (IsRecording || !initialized)
        {
            Raise();
            return;
        }
        if (refreshing)
        {
            refreshAgain = true;
            return;
        }
        refreshing = true;
        try
        {
            do
            {
                refreshAgain = false;
                await RefreshDevicesAsync();
            }
            while (refreshAgain);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A lista não pôde ser lida agora; a próxima notificação tenta de novo.
        }
        finally { refreshing = false; }
    }

    /// <summary>
    /// Relê a lista depois de uma mudança vinda de outra thread (envio em andamento). Se a leitura
    /// colidir com uma gravação de metadados, mantém a lista anterior em vez de esvaziá-la.
    /// </summary>
    public void RefreshRecordings()
    {
        if (disposed)
            return;
        if (context is null)
        {
            // Sem contexto de interface (testes): relê na hora.
            LoadRecordings();
            return;
        }
        // Vários avisos seguidos viram uma única leitura, feita fora da thread da interface.
        if (Interlocked.Exchange(ref refreshScheduled, 1) == 1)
            return;
        _ = RefreshLaterAsync();
    }

    private async Task RefreshLaterAsync()
    {
        await Task.Delay(RecordingViewModel.UpdateInterval).ConfigureAwait(false);
        Volatile.Write(ref refreshScheduled, 0);
        // A numeração fecha a corrida entre leituras em andamento: só a mais recente é aplicada.
        var sequence = Interlocked.Increment(ref refreshRequested);
        IReadOnlyList<RecordingListItem> loaded;
        try { loaded = recording.Recordings.Select(ItemFor).ToArray(); }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { return; }
        context!.Post(_ =>
        {
            if (disposed || sequence < refreshApplied)
                return;
            refreshApplied = sequence;
            recordings = loaded;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Recordings)));
        }, null);
    }

    /// <summary>
    /// Situação do acompanhamento de uma gravação (chega de qualquer thread). Não relê o disco: a linha afetada é
    /// trocada na lista em memória. Avisos seguidos viram um único ciclo da interface, como em <see cref="RefreshRecordings"/>.
    /// </summary>
    public void ApplyTracking(TrackingStatus status)
    {
        if (disposed)
            return;
        // "Sem conexão" também vale para as releituras do disco que acontecerem antes do próximo aviso do núcleo.
        if (status.IsOffline)
            offline[status.Recording.Id] = true;
        else
            offline.TryRemove(status.Recording.Id, out _);
        trackingPending[status.Recording.Id] = status;
        if (context is null)
        {
            FlushTracking();
            return;
        }
        if (Interlocked.Exchange(ref trackingScheduled, 1) == 1)
            return;
        _ = FlushTrackingLaterAsync();
    }

    private async Task FlushTrackingLaterAsync()
    {
        await Task.Delay(RecordingViewModel.UpdateInterval).ConfigureAwait(false);
        Volatile.Write(ref trackingScheduled, 0);
        context!.Post(_ => FlushTracking(), null);
    }

    private void FlushTracking()
    {
        if (disposed)
            return;
        var updated = recordings.ToArray();
        var changed = false;
        var missing = false;
        foreach (var id in trackingPending.Keys.ToArray())
        {
            if (!trackingPending.TryRemove(id, out var status))
                continue;
            var index = Array.FindIndex(updated, r => r.Id == id);
            if (index < 0)
            {
                // Gravação que a lista ainda não conhece (criada em outro caminho): uma releitura resolve.
                missing = true;
                continue;
            }
            var item = RecordingListItem.From(status);
            if (updated[index] == item)
                continue;
            updated[index] = item;
            changed = true;
        }
        if (changed)
        {
            recordings = updated;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Recordings)));
        }
        if (missing)
            RefreshRecordings();
    }

    private RecordingListItem ItemFor(RecordingMetadata metadata) =>
        RecordingListItem.From(new TrackingStatus(metadata,
            metadata.State is RecordingState.Uploading or RecordingState.Processing && offline.ContainsKey(metadata.Id)));

    private void LoadRecordings()
    {
        try { recordings = recording.Recordings.Select(ItemFor).ToArray(); }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { return; }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Recordings)));
    }

    private void ReloadRecordings()
    {
        try { recordings = recording.Recordings.Select(ItemFor).ToArray(); }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { recordings = []; }
    }

    private static readonly string[] TickProperties =
    [
        nameof(DurationText), nameof(Indicator), nameof(MicrophoneLevel), nameof(SystemLevel),
        nameof(MicrophoneStatus), nameof(SystemStatus)
    ];

    private void RaiseTick()
    {
        foreach (var name in TickProperties)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private const string BusyMessage = "Aguarde: os dados do app estão sendo apagados.";

    private void OnGateChanged()
    {
        // A recusa por operação em curso some junto com ela.
        if (!DataOperationBusy && startError == BusyMessage)
            startError = null;
        Raise();
    }

    private void Raise()
    {
        foreach (var command in new[] { Start, Stop, Toggle, SystemOnly, RetryMicrophone, RefreshDevices })
            command.RaiseCanExecuteChanged();
        PauseResume.RaiseCanExecuteChanged();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        disposed = true;
        recording.PropertyChanged -= OnRecordingChanged;
        if (capture is IAudioDeviceChanges changes)
            changes.DevicesChanged -= OnDevicesChanged;
        if (gate is not null)
            gate.Changed -= OnGateChanged;
        foreach (var kind in running.Keys.ToArray())
            await StopPreviewAsync(kind);
    }
}
