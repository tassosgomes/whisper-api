using System.Windows.Shell;
using Whisper.Recorder.Core.Ports;
using Whisper.Recorder.Core.Recording;
using Whisper.Recorder.Host.Audio;
using Whisper.Recorder.Host.Shell;
using Whisper.Recorder.Host.Tests.Doubles;
using Whisper.Recorder.Presentation.Recording;
using Xunit;

namespace Whisper.Recorder.Host.Tests;

/// <summary>Tela de gravação, indicadores e atalho do host. Sem hardware, sem janela real e sem interação.</summary>
public sealed class V04HostShellTests : IAsyncDisposable
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private readonly string directory = Path.Combine(Path.GetTempPath(), "whisper-host-shell-" + Guid.NewGuid().ToString("N"));
    private readonly SyntheticCatalog catalog = new();
    private readonly TestClock clock = new();
    private readonly WasapiAudioCapture capture;
    private readonly RecordingSession session;
    private readonly RecordingViewModel recording;
    private readonly RecordingScreenViewModel screen;

    public V04HostShellTests()
    {
        capture = new WasapiAudioCapture(catalog, clock, recoveryInterval: TimeSpan.FromMilliseconds(10));
        var repository = new RecordingRepository(directory);
        session = new RecordingSession(capture, clock, new IdleScheduler(), repository);
        recording = new RecordingViewModel(session, repository);
        screen = new RecordingScreenViewModel(recording, capture);
    }

    public async ValueTask DisposeAsync()
    {
        await screen.DisposeAsync();
        recording.Dispose();
        await session.DisposeAsync();
        await capture.DisposeAsync();
        try { Directory.Delete(directory, recursive: true); }
        catch (IOException) { }
    }

    private sealed class RecordingSurface : IRecordingIndicatorSurface
    {
        public List<RecordingIndicatorState> States { get; } = [];
        public void Apply(RecordingIndicatorState state) => States.Add(state);
    }

    private sealed class RecordingNotifier : INotifier
    {
        public List<RecorderNotification> Notifications { get; } = [];
        public List<bool> Indicator { get; } = [];

        public ValueTask NotifyAsync(RecorderNotification notification, CancellationToken cancellationToken = default)
        {
            Notifications.Add(notification);
            return ValueTask.CompletedTask;
        }

        public ValueTask SetRecordingIndicatorAsync(bool isRecording, CancellationToken cancellationToken = default)
        {
            Indicator.Add(isRecording);
            return ValueTask.CompletedTask;
        }
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw new InvalidOperationException("Falha na thread STA.", failure);
    }

    [Fact]
    public async Task RecordingIndicatorFollowsStartPauseAndStopAndNotifiesTheUser()
    {
        var surface = new RecordingSurface();
        var notifier = new RecordingNotifier();
        using var coordinator = new RecordingIndicatorCoordinator(screen, notifier, [surface]);
        await screen.RefreshDevicesAsync();

        await screen.StartAsync();
        Assert.Equal(RecordingIndicatorKind.Recording, surface.States.Last().Kind);
        Assert.Contains("gravando", surface.States.Last().ToolTip);
        Assert.Equal([true], notifier.Indicator);
        Assert.Contains(RecorderNotification.RecordingStarted, notifier.Notifications); // aviso de terceiros mesmo minimizado

        clock.UtcNow += TimeSpan.FromSeconds(65);
        session.Tick();
        Assert.Equal("00:01:05", surface.States.Last().Elapsed);

        screen.PauseResume.Execute(null);
        Assert.Equal(RecordingIndicatorKind.Paused, surface.States.Last().Kind);
        Assert.Equal("Retomar", screen.PauseResumeText);
        screen.PauseResume.Execute(null);
        Assert.Equal(RecordingIndicatorKind.Recording, surface.States.Last().Kind);

        await screen.StopAsync();
        await WaitUntil(() => surface.States.Last().Kind == RecordingIndicatorKind.Idle);
        Assert.Equal([true, false], notifier.Indicator);
    }

    [Fact]
    public void TaskbarIndicatorShowsOverlayProgressAndTimeAndClearsWhenIdle()
    {
        RunOnSta(() =>
        {
            var info = new TaskbarItemInfo();
            var taskbar = new TaskbarIndicator(info);

            taskbar.Apply(RecordingIndicatorState.From(true, false, TimeSpan.FromSeconds(5)));
            Assert.Equal(TaskbarItemProgressState.Error, info.ProgressState);
            Assert.NotNull(info.Overlay);
            Assert.Equal("Gravando 00:00:05", info.Description);

            taskbar.Apply(RecordingIndicatorState.From(true, true, TimeSpan.FromSeconds(7)));
            Assert.Equal(TaskbarItemProgressState.Paused, info.ProgressState);
            Assert.Contains("pausada", info.Description);

            taskbar.Apply(RecordingIndicatorState.From(false, false, TimeSpan.Zero));
            Assert.Equal(TaskbarItemProgressState.None, info.ProgressState);
            Assert.Null(info.Overlay);
        });
    }

    [Fact]
    public async Task LostDeviceDuringRecordingNotifiesOnceAndShowsAWarning()
    {
        var notifier = new RecordingNotifier();
        using var coordinator = new RecordingIndicatorCoordinator(screen, notifier, []);
        await screen.RefreshDevicesAsync();
        await screen.StartAsync();

        catalog.Unplug(SyntheticCatalog.Microphone);
        catalog.Replug(SyntheticCatalog.Microphone);
        catalog.Unplug(SyntheticCatalog.Microphone);

        Assert.Single(notifier.Notifications, n => n == RecorderNotification.AudioDeviceLost);
        Assert.Contains("desconectado", screen.WarningText);
        Assert.True(screen.IsRecording);
        Assert.Equal("Desconectado", screen.MicrophoneStatus);
        await screen.StopAsync();
    }

    [Fact]
    public async Task MicrophoneDeniedExplainsHowToFreeItAndAllowsRecordingOnlyTheSystem()
    {
        catalog.DeniedIds.Add(SyntheticCatalog.Microphone.Id);

        await screen.RefreshDevicesAsync();

        Assert.True(screen.MicrophoneDenied);
        Assert.Contains("Privacidade e segurança", screen.MicrophoneDeniedHelp);
        Assert.Equal("Sem acesso", screen.MicrophoneStatus);
        Assert.True(screen.SystemOnly.CanExecute(null));
        await screen.SystemOnly.ExecuteAsync();
        Assert.True(screen.IsRecording);
        Assert.Equal([AudioSourceKind.SystemLoopback], session.Current!.Sources);
        await screen.StopAsync();
    }

    [Fact]
    public async Task LevelsShowBeforeRecordingAndDevicesCanBeSwitched()
    {
        var other = new AudioDevice("mic-2", "Outro microfone", AudioSourceKind.Microphone, false);
        catalog.Devices.Add(other);
        await screen.RefreshDevicesAsync();
        Assert.Equal(SyntheticCatalog.Microphone, screen.SelectedMicrophone);
        Assert.Equal(2, screen.Microphones.Count);

        catalog.Latest(AudioSourceKind.Microphone).Emit(Pcm.Float32Stereo(48000, 440, 0.5, 4800, 0));
        Assert.InRange(screen.MicrophoneLevel, 0.45, 0.5);
        Assert.False(screen.IsRecording);

        screen.SelectedMicrophone = other;
        await WaitUntil(() => catalog.OpenCount == 3);
        Assert.Equal(other, screen.SelectedMicrophone);
        Assert.True(catalog.Opened.First(s => s.Kind == AudioSourceKind.Microphone).IsDisposed);
    }

    [Fact]
    public async Task ClosingWhileRecordingAsksAndNeverDiscards()
    {
        await screen.RefreshDevicesAsync();
        await screen.StartAsync();
        var asked = 0;
        var answer = false;
        var guard = new ShutdownGuard(screen, () =>
        {
            asked++;
            return Task.FromResult(answer);
        });

        Assert.False(await guard.CanCloseAsync()); // "Continuar gravando"
        Assert.True(screen.IsRecording);

        answer = true;
        clock.UtcNow += TimeSpan.FromSeconds(2);
        Assert.True(await guard.CanCloseAsync()); // "Parar e salvar"
        Assert.Equal(2, asked);
        Assert.False(screen.IsRecording);
        Assert.Equal(RecordingState.Saved, Assert.Single(recording.Recordings).State);
        Assert.True(await guard.CanCloseAsync()); // parado: fecha sem perguntar
        Assert.Equal(2, asked);
    }

    [Fact]
    public async Task WindowsSessionEndingStopsAndSavesWithoutDialogOrPreviewRestart()
    {
        await screen.RefreshDevicesAsync();
        await screen.StartAsync();
        clock.UtcNow += TimeSpan.FromSeconds(2);
        var opened = catalog.OpenCount;

        await screen.StopForShutdownAsync();
        await Task.Delay(100, Token); // dá chance a um reinício indevido da pré-visualização

        Assert.False(screen.IsRecording);
        Assert.Equal(RecordingState.Saved, Assert.Single(recording.Recordings).State);
        Assert.Equal(opened, catalog.OpenCount);
        Assert.DoesNotContain(catalog.Opened, s => s.IsStarted);
        await screen.StopForShutdownAsync(); // sem gravação: não faz nada
    }

    private static readonly AudioDevice BuiltInMicrophone = new("mic-builtin", "Microfone do PC", AudioSourceKind.Microphone, true);
    private static readonly AudioDevice BuiltInSpeakers = new("out-builtin", "Alto-falantes", AudioSourceKind.SystemLoopback, true);

    [Fact]
    public async Task StoppingAfterTheSelectedHeadsetVanishedFallsBackToTheDefaultWithoutAnyError()
    {
        await screen.RefreshDevicesAsync();
        await screen.StartAsync();
        Assert.True(screen.IsRecording);

        // Bluetooth desligado: o fone era microfone e saída padrão; o PC assume como padrão.
        catalog.Replace(SyntheticCatalog.Microphone, BuiltInMicrophone);
        catalog.Replace(SyntheticCatalog.Speakers, BuiltInSpeakers);
        await WaitUntil(() => screen.ActiveDeviceNote?.Contains("Alto-falantes") == true); // o loopback foi reaberto no novo padrão
        Assert.Contains("Alto-falantes", screen.ActiveDeviceNote);
        Assert.Contains("desconectado", screen.WarningText);

        clock.UtcNow += TimeSpan.FromSeconds(2);
        await screen.StopAsync();
        await WaitUntil(() => screen.SelectedMicrophone == BuiltInMicrophone && screen.SelectedSystemOutput == BuiltInSpeakers);

        Assert.Null(screen.PreviewError);
        Assert.False(screen.HasError);
        Assert.Null(screen.ActiveDeviceNote);
        Assert.Equal(RecordingState.Saved, Assert.Single(recording.Recordings).State);
        await WaitUntil(() => catalog.Opened.Any(s => s.Kind == AudioSourceKind.Microphone && s.IsStarted));
        Assert.Equal("Nível 0%", screen.MicrophoneStatus);
    }

    [Fact]
    public async Task DeviceListsFollowDevicesComingAndGoingOutsideRecording()
    {
        await screen.RefreshDevicesAsync();
        Assert.Single(screen.Microphones);

        var extra = new AudioDevice("mic-usb", "Microfone USB", AudioSourceKind.Microphone, false);
        catalog.Add(extra);
        await WaitUntil(() => screen.Microphones.Contains(extra));
        Assert.Equal(SyntheticCatalog.Microphone, screen.SelectedMicrophone); // a escolha não muda

        catalog.Unplug(extra);
        await WaitUntil(() => !screen.Microphones.Contains(extra));

        // O selecionado some: cai para o padrão atual, sem erro.
        catalog.Replace(SyntheticCatalog.Microphone, BuiltInMicrophone);
        await WaitUntil(() => screen.SelectedMicrophone == BuiltInMicrophone);
        Assert.Equal([BuiltInMicrophone], screen.Microphones);
        Assert.Null(screen.PreviewError);
        Assert.True(catalog.Latest(AudioSourceKind.Microphone).IsStarted);

        // Sem nenhum microfone: a fonte fica sem dispositivo, com texto neutro.
        catalog.Unplug(BuiltInMicrophone);
        await WaitUntil(() => screen.Microphones.Count == 0);
        Assert.Equal("Sem dispositivo", screen.MicrophoneStatus);
        Assert.Null(screen.PreviewError);
    }

    [Fact]
    public async Task DeviceThatBecomesInactiveDisappearsComesBackWhenActiveAndDefaultChangesRefreshTheList()
    {
        // Bluetooth desligado: o endpoint continua existindo e só muda de estado (não ativo).
        var headset = new AudioDevice("mic-q30", "Soundcore Q30", AudioSourceKind.Microphone, false);
        catalog.Add(headset);
        await screen.RefreshDevicesAsync();
        Assert.Contains(headset, screen.Microphones);

        catalog.Unplug(headset);
        await WaitUntil(() => !screen.Microphones.Contains(headset));

        catalog.Replug(headset);
        await WaitUntil(() => screen.Microphones.Contains(headset));

        // O Windows passa o padrão para o fone: a lista reflete o padrão e a escolha do usuário é mantida.
        catalog.ChangeDefault(headset with { IsDefault = true });
        await WaitUntil(() => screen.Microphones.Single(d => d.Id == headset.Id).IsDefault);
        Assert.False(screen.Microphones.Single(d => d.Id == SyntheticCatalog.Microphone.Id).IsDefault);
        Assert.Equal(SyntheticCatalog.Microphone.Id, screen.SelectedMicrophone?.Id);
        Assert.Null(screen.PreviewError);
    }

    [Fact]
    public void GlobalHotkeyFallsBackWhenAnotherProgramOwnsTheCombinationAndRaisesPressed()
    {
        var registrar = new FakeRegistrar(refuse: 2);
        using var hotkey = new GlobalHotkey(registrar);
        Assert.Equal("Ctrl+Alt+F9", hotkey.Registered?.DisplayName);
        var pressed = 0;
        hotkey.Pressed += () => pressed++;

        Assert.False(hotkey.HandleMessage(0x0001, 0x5748));
        Assert.True(hotkey.HandleMessage(GlobalHotkey.WmHotkey, 0x5748));
        Assert.Equal(1, pressed);

        using var none = new GlobalHotkey(new FakeRegistrar(refuse: 99));
        Assert.Null(none.Registered);
        Assert.False(none.HandleMessage(GlobalHotkey.WmHotkey, 0x5748));
    }

    [Fact]
    public async Task RecordingListShowsTitleDateDurationAndStatusAsTextAndSymbol()
    {
        await screen.RefreshDevicesAsync();
        await screen.StartAsync();
        clock.UtcNow += TimeSpan.FromSeconds(3);
        await screen.StopAsync();
        await WaitUntil(() => screen.Recordings.Count == 1 && screen.Recordings[0].Status == "Salva");

        var item = screen.Recordings[0];
        Assert.Equal("✓", item.StatusGlyph);
        Assert.Equal("00:00:03", item.Duration);
        Assert.False(string.IsNullOrWhiteSpace(item.Title));
        Assert.Contains("situação: Salva", item.AutomationName);
        var interrupted = RecordingListItem.From(new RecordingMetadata(Guid.NewGuid(), "t", clock.UtcNow,
            RecordingState.Interrupted, TimeSpan.FromMinutes(1), [], []));
        Assert.Equal(("Interrompida", "⚠"), (interrupted.Status, interrupted.StatusGlyph));
    }

    private sealed class FakeRegistrar(int refuse) : IHotkeyRegistrar
    {
        private int attempts;
        public bool Register(int id, uint modifiers, uint virtualKey) => ++attempts > refuse;
        public void Unregister(int id) { }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10, Token);
        Assert.True(condition());
    }
}
