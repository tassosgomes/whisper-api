using System.Globalization;
using Whisper.Recorder.Core.Recording;

namespace Whisper.Recorder.Presentation.Recording;

/// <summary>Linha da lista de gravações. A situação tem texto e símbolo: não depende só de cor.</summary>
public sealed record RecordingListItem(Guid Id, string Title, string CreatedAt, string Duration, string Status, string StatusGlyph)
{
    private static readonly CultureInfo Brazil = CultureInfo.GetCultureInfo("pt-BR");

    public string AutomationName => $"{Title}, {CreatedAt}, duração {Duration}, situação: {Status}";

    /// <summary>O WPF usa o texto do item como nome para leitores de tela.</summary>
    public override string ToString() => AutomationName;

    public static RecordingListItem From(RecordingMetadata metadata)
    {
        var (status, glyph) = metadata.State switch
        {
            RecordingState.Recording => ("Gravando", "●"),
            RecordingState.Interrupted => ("Interrompida", "⚠"),
            _ => ("Salva", "✓")
        };
        return new(metadata.Id, metadata.Title,
            metadata.CreatedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm", Brazil),
            DurationFormat.Format(metadata.Duration), status, glyph);
    }
}
