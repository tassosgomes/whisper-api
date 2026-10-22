using System.Globalization;
using Whisper.Recorder.Core.Recording;
using Whisper.Recorder.Core.Tracking;
using Whisper.Recorder.Core.Uploading;

namespace Whisper.Recorder.Presentation.Recording;

/// <summary>
/// Linha da lista de gravações. A situação tem texto e símbolo: não depende só de cor. <see cref="Detail"/> traz a
/// explicação em linguagem simples do acompanhamento (falha, resultado expirado, "sem conexão"); é nulo quando não há o que explicar.
/// </summary>
public sealed record RecordingListItem(Guid Id, string Title, string CreatedAt, string Duration, string Status, string StatusGlyph,
    RecordingState State = RecordingState.Saved, string? Detail = null, bool IsOffline = false)
{
    private static readonly CultureInfo Brazil = CultureInfo.GetCultureInfo("pt-BR");

    /// <summary>Falso enquanto grava ou quando o serviço já está processando: não há o que enviar agora.</summary>
    public bool CanTranscribe => State is not (RecordingState.Recording or RecordingState.Processing);

    public string AutomationName => $"{Title}, {CreatedAt}, duração {Duration}, situação: {Status}"
        + (Detail is null ? "" : $". {Detail}");

    /// <summary>O WPF usa o texto do item como nome para leitores de tela.</summary>
    public override string ToString() => AutomationName;

    public static RecordingListItem From(RecordingMetadata metadata) => From(new TrackingStatus(metadata, false));

    /// <summary>Monta a linha a partir do acompanhamento; "sem conexão" aparece como aviso, nunca como falha.</summary>
    public static RecordingListItem From(TrackingStatus tracking)
    {
        var metadata = tracking.Recording;
        var (status, glyph) = metadata.State switch
        {
            RecordingState.Recording => ("Gravando", "●"),
            RecordingState.Interrupted => ("Interrompida", "⚠"),
            // Com problema registrado, o envio parou e espera o "tentar de novo".
            RecordingState.Uploading when metadata.UploadProblem != UploadProblem.None
                || metadata.TrackingProblem == TrackingProblem.SendRequired => ("Envio não concluído", "⚠"),
            RecordingState.Uploading => ("Enviando", "↑"),
            RecordingState.Processing => ("Processando", "…"),
            RecordingState.Transcribed => ("Transcrita", "✓"),
            RecordingState.Failed => ("Falhou", "✕"),
            RecordingState.ResultExpired => ("Resultado expirado", "⚠"),
            _ => ("Salva", "✓")
        };
        var detail = metadata.State switch
        {
            RecordingState.Processing or RecordingState.Failed or RecordingState.ResultExpired => tracking.Message,
            RecordingState.Uploading when tracking.IsOffline || metadata.TrackingProblem != TrackingProblem.None => tracking.Message,
            RecordingState.Transcribed when metadata.TrackingProblem != TrackingProblem.None => tracking.Message,
            _ => null
        };
        return new(metadata.Id, metadata.Title,
            metadata.CreatedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm", Brazil),
            DurationFormat.Format(metadata.Duration), status, glyph, metadata.State, detail, tracking.IsOffline);
    }
}
