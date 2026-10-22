using Whisper.Recorder.Core.Ports;
using Whisper.Recorder.Core.Recording;
using Xunit;

namespace Whisper.Recorder.Core.Tests;

/// <summary>
/// Compara a linha do tempo atual (anel de slots) com a implementação de referência original
/// (um <see cref="SortedDictionary{TKey, TValue}"/> por fonte), amostra a amostra.
/// </summary>
public sealed class V04TimelineDifferentialTests
{
    private const int MixFrames = 1600; // 100 ms, como o escritor da sessão

    [Theory]
    [InlineData(7)]
    [InlineData(1234)]
    [InlineData(99)]
    public void RingTimelineMatchesTheReferenceForTwoRatesLateAndOutOfOrderBlocks(int seed)
    {
        var random = new Random(seed);
        var actual = new AudioTimeline();
        var reference = new ReferenceTimeline();
        var pending = new List<(int Due, AudioPcmBlock Block, long Start)>();

        // 48 kHz estéreo (480 quadros = 10 ms) e 44,1 kHz mono (441 quadros = 10 ms): 160 quadros de saída por bloco.
        for (var index = 0; index < 3000; index++)
        {
            var start = index * 160L;
            var due = (int)((start + 160) / MixFrames);
            pending.Add((due + Delay(random), Block(random, AudioSourceKind.Microphone, 48000, 2, 480), start));
            pending.Add((due + Delay(random), Block(random, AudioSourceKind.SystemLoopback, 44100, 1, 441), start));
        }
        // Blocos muito adiantados (além de 5 s à frente) e com início fora do alinhamento.
        pending.Add((10, Block(random, AudioSourceKind.Microphone, 48000, 2, 48000), 10 * MixFrames + 3));
        pending.Add((20, Block(random, AudioSourceKind.SystemLoopback, 44100, 1, 44100 * 2), 20 * MixFrames + 61 * 160));

        var written = 0L;
        var different = 0;
        for (var step = 0; step < 330; step++)
        {
            // Dentro da mesma rodada a ordem de chegada é embaralhada.
            foreach (var item in pending.Where(p => p.Due <= step).OrderBy(_ => random.Next()).ToArray())
            {
                actual.Add(item.Block, item.Start, written);
                reference.Add(item.Block, item.Start, written);
                pending.Remove(item);
            }
            if (step == 150) // pausa: tudo o que estava pendente é descartado
            {
                actual.Clear();
                reference.Clear();
            }
            var expected = new short[MixFrames];
            var result = new short[MixFrames];
            reference.Mix(written, expected);
            actual.Mix(written, result);
            if (!expected.AsSpan().SequenceEqual(result))
                different++;
            Assert.Equal(expected, result);
            written += MixFrames;
        }
        Assert.Equal(0, different);
    }

    // Maioria no prazo, alguns dentro da margem de 500 ms (até 4 rodadas) e poucos além dela (cortados na frente).
    private static int Delay(Random random) => random.Next(100) switch
    {
        < 60 => 0,
        < 85 => random.Next(1, 3),
        < 97 => random.Next(3, 5),
        _ => random.Next(6, 10)
    };

    private static AudioPcmBlock Block(Random random, AudioSourceKind source, int rate, int channels, int frames)
    {
        var samples = new short[frames * channels];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (short)random.Next(short.MinValue, short.MaxValue + 1);
        return new(source, DateTimeOffset.UnixEpoch, rate, channels, samples);
    }

    /// <summary>A implementação anterior à otimização, copiada sem alterações de comportamento.</summary>
    private sealed class ReferenceTimeline
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
                        sum += sample / 2;
                output[i] = (short)Math.Clamp(sum, short.MinValue, short.MaxValue);
            }
        }
    }
}
