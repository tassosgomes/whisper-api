using Whisper.Recorder.Core.Ports;
using Whisper.Recorder.Presentation.Recording;

namespace Whisper.Recorder.Host.Shell;

/// <summary>
/// Ícone na área de notificação: estado da gravação (forma e dica de texto), menu de contexto e avisos.
/// Implementa o notificador do núcleo com textos fixos: nunca exibe conteúdo da reunião.
/// </summary>
public sealed class TrayIndicator : INotifier, IRecordingIndicatorSurface, IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon icon = new() { Text = "Whisper" };
    private readonly Dictionary<RecordingIndicatorKind, System.Drawing.Icon> icons = [];
    private readonly System.Windows.Forms.ToolStripMenuItem toggleItem;
    private readonly string hotkeyName;
    private readonly SynchronizationContext? context = SynchronizationContext.Current;
    private RecordingIndicatorKind? kind;
    // A gravação do último aviso de conclusão/falha: o Windows entrega o toque sem dizer qual balão foi tocado.
    private Guid? target;
    private bool disposed;

    public TrayIndicator(string hotkeyName, Action open, Action toggle, Action exit)
    {
        this.hotkeyName = hotkeyName;
        foreach (var value in Enum.GetValues<RecordingIndicatorKind>())
            icons[value] = RecorderIcons.RenderTrayIcon(value);
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Abrir Whisper", null, (_, _) => open());
        toggleItem = new System.Windows.Forms.ToolStripMenuItem($"Iniciar gravação ({hotkeyName})");
        toggleItem.Click += (_, _) => toggle();
        menu.Items.Add(toggleItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => exit());
        icon.ContextMenuStrip = menu;
        icon.DoubleClick += (_, _) => open();
        icon.BalloonTipClicked += (_, _) => NotificationActivated?.Invoke(target);
        SetKind(RecordingIndicatorKind.Idle, "Whisper");
        icon.Visible = true;
    }

    /// <summary>O usuário tocou no aviso do Windows; leva a gravação a que ele se refere, quando houver.</summary>
    public event Action<Guid?>? NotificationActivated;

    public void Apply(RecordingIndicatorState state)
    {
        SetKind(state.Kind, state.ToolTip);
        toggleItem.Text = state.IsRecording
            ? $"Parar e salvar gravação ({hotkeyName})"
            : $"Iniciar gravação ({hotkeyName})";
    }

    private void SetKind(RecordingIndicatorKind value, string toolTip)
    {
        if (kind != value)
        {
            kind = value;
            icon.Icon = icons[value];
        }
        // O Windows aceita até 63 caracteres na dica da bandeja.
        icon.Text = toolTip.Length <= 63 ? toolTip : toolTip[..63];
    }

    public ValueTask NotifyAsync(RecorderNotification notification, CancellationToken cancellationToken = default, Guid? recordingId = null)
    {
        // Quem notifica (acompanhamento) roda em outra thread; o ícone pertence à thread da interface.
        if (context is not null && SynchronizationContext.Current != context)
            context.Post(_ => Show(notification, recordingId), null);
        else
            Show(notification, recordingId);
        return ValueTask.CompletedTask;
    }

    private void Show(RecorderNotification notification, Guid? recordingId)
    {
        if (disposed)
            return;
        var (text, severity) = notification switch
        {
            RecorderNotification.RecordingStarted =>
                ("Gravando. Informe os participantes sobre a gravação.", System.Windows.Forms.ToolTipIcon.Info),
            RecorderNotification.AudioDeviceLost =>
                ("Um dispositivo de áudio foi desconectado. A gravação continua com a outra fonte.", System.Windows.Forms.ToolTipIcon.Warning),
            RecorderNotification.LowDiskSpace =>
                ("Pouco espaço livre no disco.", System.Windows.Forms.ToolTipIcon.Warning),
            RecorderNotification.RecordingLimitApproaching =>
                ("A gravação chega ao limite de 2 horas em breve.", System.Windows.Forms.ToolTipIcon.Warning),
            RecorderNotification.TranscriptionCompleted =>
                ("A transcrição ficou pronta. Toque para abrir a gravação.", System.Windows.Forms.ToolTipIcon.Info),
            _ => ("A transcrição falhou. Toque para ver o motivo.", System.Windows.Forms.ToolTipIcon.Error)
        };
        target = recordingId;
        icon.ShowBalloonTip(5000, "Whisper", text, severity);
    }

    public ValueTask SetRecordingIndicatorAsync(bool isRecording, CancellationToken cancellationToken = default)
    {
        // O formato do ícone e a dica seguem o estado completo em Apply; aqui só garante o ícone visível.
        icon.Visible = true;
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        disposed = true;
        icon.Visible = false;
        icon.Dispose();
        foreach (var value in icons.Values)
            value.Dispose();
    }
}
