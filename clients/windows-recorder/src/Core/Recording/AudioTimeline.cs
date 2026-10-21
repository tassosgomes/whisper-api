using Whisper.Recorder.Core.Ports;

namespace Whisper.Recorder.Core.Recording;

/// <summary>Bounded pending samples on the active (pause-free) recording timeline.</summary>
internal sealed class AudioTimeline
{
    private readonly Dictionary<AudioSourceKind, SortedDictionary<long, short>> pending = [];

    public void Clear() => pending.Clear();

    public void Add(AudioPcmBlock block, long start, long written)
    {
        if (block.SampleRate <= 0 || block.Channels <= 0 || block.Samples.Length % block.Channels != 0)
            return;
        var inputFrames = block.Samples.Length / block.Channels;
        var count = (long)inputFrames * WavFile.SampleRate / block.SampleRate;
        var first = Math.Max(0, written - start);
        var end = Math.Min(count, written + 5 * WavFile.SampleRate - start);
        if (!pending.TryGetValue(block.Source, out var samples))
            pending[block.Source] = samples = [];
        var input = block.Samples.Span;
        for (var i = first; i < end; i++)
        {
            var position = i * (double)block.SampleRate / WavFile.SampleRate;
            var left = (int)position;
            var right = Math.Min(left + 1, inputFrames - 1);
            double mono = 0;
            for (var channel = 0; channel < block.Channels; channel++)
                mono += input[left * block.Channels + channel] * (1 - (position - left))
                    + input[right * block.Channels + channel] * (position - left);
            samples[start + i] = (short)Math.Clamp(mono / block.Channels, short.MinValue, short.MaxValue);
        }
    }

    public void Mix(long start, Span<short> output)
    {
        if (pending.Values.All(source => source.Count == 0))
        {
            output.Clear();
            return;
        }
        for (var i = 0; i < output.Length; i++)
        {
            var sum = 0;
            foreach (var source in pending.Values)
                if (source.Remove(start + i, out var sample))
                    sum += sample / 2; // -6 dB per source, including single-source recordings.
            output[i] = (short)Math.Clamp(sum, short.MinValue, short.MaxValue);
        }
    }
}
