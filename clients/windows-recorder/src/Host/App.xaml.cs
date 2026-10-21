using System.IO;
using System.Windows;
using Whisper.Recorder.Core.Diagnostics;
using Whisper.Recorder.Core.Ports;

namespace Whisper.Recorder.Host;

public partial class App : Application
{
    private TextWriter diagnosticWriter = TextWriter.Null;
    private DiagnosticLog? diagnostic;

    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Whisper", "Recorder");
            Directory.CreateDirectory(directory);
            diagnosticWriter = new StreamWriter(Path.Combine(directory, "diagnostics.log"), append: true);
            diagnostic = new DiagnosticLog(diagnosticWriter, new SystemClock());
            diagnostic.Write(DiagnosticEvent.ApplicationStarted);
        }
        catch (IOException)
        {
            // An unavailable diagnostic file must not prevent the app from starting.
        }
        catch (UnauthorizedAccessException)
        {
            // Do not log exception messages: they can contain local paths.
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            diagnostic?.Write(DiagnosticEvent.ApplicationStopped);
        }
        catch (IOException)
        {
            // Diagnostics are best effort and never block shutdown.
        }
        try
        {
            diagnosticWriter.Dispose();
        }
        catch (IOException)
        {
            // A failed flush must not prevent shutdown either.
        }

        base.OnExit(e);
    }
}
