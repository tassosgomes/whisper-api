using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Whisper.Recorder.Presentation.Recording;

namespace Whisper.Recorder.Host.Shell;

/// <summary>
/// Ícones desenhados em código (sem arquivos de imagem): anel = parado, círculo cheio = gravando,
/// duas barras = pausado. A forma distingue o estado; a cor só reforça. Chame na thread da interface.
/// </summary>
public static class RecorderIcons
{
    public static ImageSource Render(RecordingIndicatorKind kind, int size)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var center = new Point(size / 2d, size / 2d);
            var radius = size * 0.42;
            var white = Brushes.White;
            switch (kind)
            {
                case RecordingIndicatorKind.Recording:
                    context.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xD1, 0x34, 0x38)), new Pen(white, size * 0.06), center, radius, radius);
                    break;
                case RecordingIndicatorKind.Paused:
                    context.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xC1, 0x9C, 0x00)), new Pen(white, size * 0.06), center, radius, radius);
                    var bar = size * 0.12;
                    context.DrawRectangle(white, null, new Rect(size * 0.34, size * 0.3, bar, size * 0.4));
                    context.DrawRectangle(white, null, new Rect(size * 0.54, size * 0.3, bar, size * 0.4));
                    break;
                default:
                    context.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x2B, 0x57, 0x9A)), null, center, radius, radius);
                    context.DrawEllipse(white, null, center, radius * 0.45, radius * 0.45);
                    break;
            }
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Ícone GDI para a bandeja. O chamador é dono e descarta com <see cref="System.Drawing.Icon.Dispose"/>.</summary>
    public static System.Drawing.Icon RenderTrayIcon(RecordingIndicatorKind kind)
    {
        var source = (BitmapSource)Render(kind, 32);
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
