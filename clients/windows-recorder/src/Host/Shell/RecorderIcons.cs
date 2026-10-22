using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Whisper.Recorder.Presentation.Recording;

namespace Whisper.Recorder.Host.Shell;

/// <summary>
/// Ícones do app e indicadores de estado. O ícone do app vem do recurso <c>Assets/icon.ico</c>.
/// Gravação e pausa desenham o indicador (círculo ou duas barras) sobre ele na bandeja; a forma distingue
/// o estado e a cor só reforça. A barra de tarefas usa só o indicador como overlay. Chame na thread da interface.
/// </summary>
public static class RecorderIcons
{
    /// <summary>URI do recurso do ícone do app, para XAML e código. O componente é o assembly do host.</summary>
    public const string AppIconUri = "pack://application:,,,/WhisperRecorder;component/Assets/icon.ico";

    /// <summary>Indicador isolado, para o overlay da barra de tarefas.</summary>
    public static ImageSource Render(RecordingIndicatorKind kind, int size)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var center = new Point(size / 2d, size / 2d);
            DrawIndicator(context, kind, center, size * 0.42, size * 0.06);
        }
        return Bitmap(visual, size);
    }

    /// <summary>Bytes do arquivo .ico embutido (16 a 256 px).</summary>
    public static byte[] AppIconBytes()
    {
        // O esquema "pack" só é registrado quando o WPF inicializa; sem Application (testes), o Uri seria inválido.
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(Application).TypeHandle);
        var resource = Application.GetResourceStream(new Uri(AppIconUri, UriKind.Absolute))
            ?? throw new InvalidOperationException("Recurso do ícone do app ausente.");
        using var stream = new MemoryStream();
        resource.Stream.CopyTo(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// Ícone GDI para a bandeja, no tamanho pequeno do DPI atual. Parado mostra o ícone do app;
    /// gravando e pausado sobrepõem o indicador no canto inferior direito. O chamador é dono e descarta.
    /// </summary>
    public static System.Drawing.Icon RenderTrayIcon(RecordingIndicatorKind kind)
    {
        if (kind == RecordingIndicatorKind.Idle)
        {
            using var appStream = new MemoryStream(AppIconBytes());
            return new System.Drawing.Icon(appStream, System.Windows.Forms.SystemInformation.SmallIconSize);
        }

        var size = System.Windows.Forms.SystemInformation.SmallIconSize.Width;
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawImage(AppIconFrame(size), new Rect(0, 0, size, size));
            var radius = size * 0.22;
            var center = new Point(size - radius, size - radius);
            DrawIndicator(context, kind, center, radius, size * 0.06);
        }
        return ToGdiIcon(Bitmap(visual, size));
    }

    private static void DrawIndicator(DrawingContext context, RecordingIndicatorKind kind, Point center, double radius, double ring)
    {
        var white = Brushes.White;
        switch (kind)
        {
            case RecordingIndicatorKind.Recording:
                context.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xD1, 0x34, 0x38)), new Pen(white, ring), center, radius, radius);
                break;
            case RecordingIndicatorKind.Paused:
                context.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xC1, 0x9C, 0x00)), new Pen(white, ring), center, radius, radius);
                // Proporções de duas barras centradas: largura 0,29 r, altura 0,95 r, separadas por 0,19 r.
                var bar = radius * 0.2857;
                var height = radius * 0.952;
                context.DrawRectangle(white, null, new Rect(center.X - radius * 0.381, center.Y - height / 2, bar, height));
                context.DrawRectangle(white, null, new Rect(center.X + radius * 0.095, center.Y - height / 2, bar, height));
                break;
            default:
                context.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x2B, 0x57, 0x9A)), null, center, radius, radius);
                context.DrawEllipse(white, null, center, radius * 0.45, radius * 0.45);
                break;
        }
    }

    private static BitmapSource AppIconFrame(int size)
    {
        using var stream = new MemoryStream(AppIconBytes());
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        return decoder.Frames.MinBy(frame => Math.Abs(frame.PixelWidth - size))!;
    }

    private static RenderTargetBitmap Bitmap(Visual visual, int size)
    {
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static System.Drawing.Icon ToGdiIcon(BitmapSource source)
    {
        using var stream = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        encoder.Save(stream);
        stream.Position = 0;
        using var bitmap = new System.Drawing.Bitmap(stream);
        var handle = bitmap.GetHicon();
        try
        {
            using var native = System.Drawing.Icon.FromHandle(handle);
            return (System.Drawing.Icon)native.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint handle);
}
