using Whisper.Recorder.Core.Ports;

namespace Whisper.Recorder.Core.Recording;

/// <summary>
/// Bounded pending samples on the active (pause-free) recording timeline. Each source keeps a ring of
/// contiguous slots indexed by absolute position: nothing is allocated or sorted per sample, so the
/// session lock is held only for a short, predictable time.
/// </summary>
internal sealed class AudioTimeline
{
    private const int Window = 5 * WavFile.SampleRate;
    // Positions in [written, written + Window) never collide; the extra room is only a safety margin.
    private const int Capacity = Window + WavFile.SampleRate;
    private const int Empty = int.MinValue;

    private readonly Dictionary<AudioSourceKind, Ring> pending = [];

    public void Clear()
    {
        foreach (var ring in pending.Values)
            ring.Reset();
    }

    public void Add(AudioPcmBlock block, long start, long written)
    {
        if (block.SampleRate <= 0 || block.Channels <= 0 || block.Samples.Length % block.Channels != 0)
            return;
        var inputFrames = block.Samples.Length / block.Channels;
        var count = (long)inputFrames * WavFile.SampleRate / block.SampleRate;
        var first = Math.Max(0, written - start);
        var end = Math.Min(count, written + Window - start);
        if (!pending.TryGetValue(block.Source, out var ring))
            pending[block.Source] = ring = new Ring();
        var input = block.Samples.Span;
        var channels = block.Channels;
        for (var i = first; i < end; i++)
        {
            var position = i * (double)block.SampleRate / WavFile.SampleRate;
            var left = (int)position;
            var right = Math.Min(left + 1, inputFrames - 1);
            double mono = 0;
            for (var channel = 0; channel < channels; channel++)
                mono += input[left * channels + channel] * (1 - (position - left))
                    + input[right * channels + channel] * (position - left);
            ring.Set(start + i, (short)Math.Clamp(mono / channels, short.MinValue, short.MaxValue));
        }
    }

    public void Mix(long start, Span<short> output)
    {
        var rings = pending.Values.Where(r => r.Count > 0).ToArray();
        if (rings.Length == 0)
        {
            output.Clear();
            return;
        }
        for (var i = 0; i < output.Length; i++)
        {
            var sum = 0;
            foreach (var ring in rings)
                if (ring.Take(start + i, out var sample))
                    sum += sample / 2; // -6 dB per source, including single-source recordings.
            output[i] = (short)Math.Clamp(sum, short.MinValue, short.MaxValue);
        }
    }

    private sealed class Ring
    {
        private readonly int[] slots = new int[Capacity];

        public Ring() => Array.Fill(slots, Empty);

        public int Count { get; private set; }

        public void Reset()
        {
            if (Count == 0)
                return;
            Array.Fill(slots, Empty);
            Count = 0;
        }

        public void Set(long position, short sample)
        {
            ref var slot = ref slots[position % Capacity];
            if (slot == Empty)
                Count++;
            slot = sample;
        }

        public bool Take(long position, out short sample)
        {
            ref var slot = ref slots[position % Capacity];
            if (slot == Empty)
            {
                sample = 0;
                return false;
            }
            sample = (short)slot;
            slot = Empty;
            Count--;
            return true;
        }
    }
}
