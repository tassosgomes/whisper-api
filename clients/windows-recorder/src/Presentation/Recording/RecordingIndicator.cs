using Whisper.Recorder.Core.Ports;
using Whisper.Recorder.Core.Recording;

namespace Whisper.Recorder.Presentation.Recording;

public enum RecordingIndicatorKind { Idle, Recording, Paused }

/// <summary>O que a barra de tarefas e a bandeja mostram. Texto estático e tempo: nunca conteúdo da reunião.</summary>
public sealed record RecordingIndicatorState(RecordingIndicatorKind Kind, string Elapsed, string ToolTip)
{
    public bool IsRecording => Kind != RecordingIndicatorKind.Idle;

    public static RecordingIndicatorState From(bool isRecording, bool isPaused, TimeSpan duration)
    {
        var elapsed = DurationFormat.Format(duration);
        return !isRecording
            ? new(RecordingIndicatorKind.Idle, elapsed, "Whisper")
            : isPaused
                ? new(RecordingIndicatorKind.Paused, elapsed, $"Whisper: gravação pausada, {elapsed}")
                : new(RecordingIndicatorKind.Recording, elapsed, $"Whisper: gravando, {elapsed}");
    }
}

public static class DurationFormat
{
    public static string Format(TimeSpan duration)
    {
        var total = (long)Math.Max(0, duration.TotalSeconds);
        return $"{total / 3600:00}:{total / 60 % 60:00}:{total % 60:00}";
    }
}

/// <summary>Uma superfície do host (barra de tarefas, bandeja) que mostra o indicador.</summary>
public interface IRecordingIndicatorSurface
{
    void Apply(RecordingIndicatorState state);
}

/// <summary>
/// Liga a tela de gravação às superfícies de indicador e ao notificador. Atualiza as superfícies
/// só quando o texto muda (uma vez por segundo) e notifica uma única vez cada aviso novo.
/// </summary>
public sealed class RecordingIndicatorCoordinator : IDisposable
{
    private readonly RecordingScreenViewModel screen;
    private readonly INotifier notifier;
    private readonly IReadOnlyList<IRecordingIndicatorSurface> surfaces;
    private readonly HashSet<RecordingWarning> notified = [];
    private RecordingIndicatorState? last;
    private Guid? currentRecording;

    public RecordingIndicatorCoordinator(RecordingScreenViewModel screen, INotifier notifier,
        IEnumerable<IRecordingIndicatorSurface> surfaces)
    {
        this.screen = screen;
        this.notifier = notifier;
        this.surfaces = surfaces.ToArray();
        screen.PropertyChanged += OnChanged;
        Refresh();
    }

    public RecordingIndicatorState? Current => last;

    private void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        var state = screen.Indicator;
        if (state != last)
        {
            var wasRecording = last?.IsRecording ?? false;
            last = state;
            foreach (var surface in surfaces)
                surface.Apply(state);
            if (wasRecording != state.IsRecording)
            {
                _ = notifier.SetRecordingIndicatorAsync(state.IsRecording);
                // Com a janela minimizada ou a gravação iniciada pelo atalho, o aviso de terceiros ainda precisa aparecer.
                if (state.IsRecording)
                    _ = notifier.NotifyAsync(RecorderNotification.RecordingStarted);
            }
        }
        var id = screen.CurrentRecordingId;
        if (id != currentRecording)
        {
            currentRecording = id;
            notified.Clear();
        }
        foreach (var warning in screen.ActiveWarnings)
        {
            if (!notified.Add(warning))
                continue;
            var notification = warning switch
            {
                RecordingWarning.DeviceLost => RecorderNotification.AudioDeviceLost,
                RecordingWarning.LowDiskSpace => RecorderNotification.LowDiskSpace,
                RecordingWarning.LimitApproaching => RecorderNotification.RecordingLimitApproaching,
                _ => (RecorderNotification?)null
            };
            if (notification is { } value)
                _ = notifier.NotifyAsync(value);
        }
    }

    public void Dispose() => screen.PropertyChanged -= OnChanged;
}
