using System.Buffers.Binary;

namespace Whisper.Recorder.Host.Audio;

/// <summary>Converte o PCM nativo do WASAPI em PCM de 16 bits intercalado e mede o pico (0..1).</summary>
public static class PcmConverter
{
    public static bool IsSupported(CaptureFormat format) => format.Channels > 0 && format.SampleRate > 0 &&
        (format.IsFloat ? format.BitsPerSample == 32 : format.BitsPerSample is 16 or 24 or 32);

    /// <summary>Devolve um buffer novo: o núcleo guarda os blocos depois do evento.</summary>
    public static short[] ToInt16(ReadOnlySpan<byte> bytes, CaptureFormat format, out float peak)
    {
        var width = format.BitsPerSample / 8;
        var count = bytes.Length / width;
        count -= count % format.Channels;
        var samples = new short[count];
        var max = 0;
        for (var i = 0; i < count; i++)
        {
            var source = bytes[(i * width)..];
            short value = format switch
            {
                { IsFloat: true } => FromFloat(BinaryPrimitives.ReadSingleLittleEndian(source)),
                { BitsPerSample: 16 } => BinaryPrimitives.ReadInt16LittleEndian(source),
                { BitsPerSample: 24 } => (short)(source[1] | source[2] << 8),
                _ => (short)(BinaryPrimitives.ReadInt32LittleEndian(source) >> 16)
            };
            samples[i] = value;
            max = Math.Max(max, Math.Abs((int)value));
        }
        peak = Math.Min(1f, max / 32768f);
        return samples;
    }

    private static short FromFloat(float value) =>
        float.IsNaN(value) ? (short)0 : (short)Math.Clamp((int)MathF.Round(value * 32768f), short.MinValue, short.MaxValue);
}
