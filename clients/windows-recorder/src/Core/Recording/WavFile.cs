using System.Buffers.Binary;

namespace Whisper.Recorder.Core.Recording;

/// <summary>Canonical PCM WAV. The data length on disk is authoritative during recovery.</summary>
internal sealed class WavFile : IDisposable
{
    public const int SampleRate = 16000;
    public const int HeaderSize = 44;
    private readonly FileStream stream;
    public long Frames => Math.Max(0, stream.Length - HeaderSize) / 2;

    public WavFile(string path, bool recover = false)
    {
        stream = new FileStream(path, recover ? FileMode.Open : FileMode.CreateNew,
            FileAccess.ReadWrite, FileShare.Read, 4096);
        if (recover)
            stream.SetLength(HeaderSize + Frames * 2);
        else
            stream.SetLength(HeaderSize);
        Checkpoint();
    }

    public void Append(ReadOnlySpan<short> samples)
    {
        Span<byte> bytes = stackalloc byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(bytes[(i * 2)..], samples[i]);
        stream.Position = stream.Length;
        stream.Write(bytes);
    }

    public void Checkpoint()
    {
        // An interrupted write can leave one incomplete sample. Never expose it as PCM.
        stream.SetLength(HeaderSize + Frames * 2);
        var length = checked((int)(Frames * 2));
        Span<byte> header = stackalloc byte[HeaderSize];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], length + 36);
        "WAVEfmt "u8.CopyTo(header[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(header[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(header[22..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header[28..], SampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(header[32..], 2);
        BinaryPrimitives.WriteInt16LittleEndian(header[34..], 16);
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[40..], length);
        stream.Position = 0;
        stream.Write(header);
        stream.Flush(flushToDisk: true);
    }

    public void Dispose() => stream.Dispose();
}
