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
    private RecordingIndicatorKind? kind;

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
        SetKind(RecordingIndicatorKind.Idle, "Whisper");
        icon.Visible = true;
    }

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

    public ValueTask NotifyAsync(RecorderNotification notification, CancellationToken cancellationToken = default)
    {
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
                ("A transcrição ficou pronta.", System.Windows.Forms.ToolTipIcon.Info),
            _ => ("A transcrição falhou.", System.Windows.Forms.ToolTipIcon.Error)
        };
        icon.ShowBalloonTip(5000, "Whisper", text, severity);
        return ValueTask.CompletedTask;
    }

    public ValueTask SetRecordingIndicatorAsync(bool isRecording, CancellationToken cancellationToken = default)
    {
        // O formato do ícone e a dica seguem o estado completo em Apply; aqui só garante o ícone visível.
        icon.Visible = true;
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
        foreach (var value in icons.Values)
            value.Dispose();
    }
}
