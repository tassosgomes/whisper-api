using System.Globalization;
using Whisper.Recorder.Core.Recording;
using Whisper.Recorder.Core.Uploading;

namespace Whisper.Recorder.Presentation.Recording;

/// <summary>Linha da lista de gravações. A situação tem texto e símbolo: não depende só de cor.</summary>
public sealed record RecordingListItem(Guid Id, string Title, string CreatedAt, string Duration, string Status, string StatusGlyph,
    RecordingState State = RecordingState.Saved)
{
    private static readonly CultureInfo Brazil = CultureInfo.GetCultureInfo("pt-BR");

    /// <summary>Falso enquanto grava ou quando o serviço já está processando: não há o que enviar agora.</summary>
    public bool CanTranscribe => State is not (RecordingState.Recording or RecordingState.Processing);

    public string AutomationName => $"{Title}, {CreatedAt}, duração {Duration}, situação: {Status}";

    /// <summary>O WPF usa o texto do item como nome para leitores de tela.</summary>
    public override string ToString() => AutomationName;

    public static RecordingListItem From(RecordingMetadata metadata)
    {
        var (status, glyph) = metadata.State switch
        {
            RecordingState.Recording => ("Gravando", "●"),
            RecordingState.Interrupted => ("Interrompida", "⚠"),
            // Com problema registrado, o envio parou e espera o "tentar de novo".
            RecordingState.Uploading when metadata.UploadProblem != UploadProblem.None => ("Envio não concluído", "⚠"),
            RecordingState.Uploading => ("Enviando", "↑"),
            RecordingState.Processing => ("Processando", "…"),
            RecordingState.Transcribed => ("Transcrita", "✓"),
            RecordingState.Failed => ("Falhou", "✕"),
            RecordingState.ResultExpired => ("Resultado expirado", "⚠"),
            _ => ("Salva", "✓")
        };
        return new(metadata.Id, metadata.Title,
            metadata.CreatedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm", Brazil),
            DurationFormat.Format(metadata.Duration), status, glyph, metadata.State);
    }
}
