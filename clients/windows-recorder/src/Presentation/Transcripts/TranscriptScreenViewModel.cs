using System.ComponentModel;
using System.Globalization;
using Whisper.Recorder.Core.Transcripts;
using Whisper.Recorder.Presentation.Recording;

namespace Whisper.Recorder.Presentation.Transcripts;

/// <summary>Área de transferência do Windows. O host implementa; devolve falso quando não conseguiu copiar.</summary>
public interface ITranscriptClipboard
{
    Task<bool> TrySetTextAsync(string text);
}

/// <summary>Seletor de arquivo e confirmação do sistema. O host implementa; os testes usam dublês.</summary>
public interface ITranscriptDialogs
{
    /// <summary>Caminho escolhido, ou nulo se o usuário cancelou. Não pergunta sobre sobrescrever: isso é <see cref="ConfirmOverwriteAsync"/>.</summary>
    string? ChooseExportPath(string suggestedFileName);

    Task<bool> ConfirmOverwriteAsync(string path);
}

/// <summary>Um trecho como a tela o mostra; o nome de acessibilidade lê o tempo e o texto sem colchetes.</summary>
public sealed record TranscriptLine(string Time, string Text)
{
    public string AutomationName => $"{Time}: {Text}";
    public override string ToString() => AutomationName;
}

/// <summary>
/// Tela de transcrição (RF-07): trechos com o tempo, copiar com e sem tempos, exportar. Tudo vem dos arquivos locais
/// (<see cref="TranscriptViewModel"/>); nunca consulta o serviço. Sem tipos WPF.
/// </summary>
public sealed class TranscriptScreenViewModel : INotifyPropertyChanged
{
    private static readonly CultureInfo Brazil = CultureInfo.GetCultureInfo("pt-BR");

    private readonly TranscriptViewModel transcript;
    private readonly ITranscriptClipboard clipboard;
    private readonly ITranscriptDialogs dialogs;
    private IReadOnlyList<TranscriptLine> lines = [];
    private string? notice;
    private bool isLoading;
    private int generation;

    public TranscriptScreenViewModel(TranscriptViewModel transcript, ITranscriptClipboard clipboard, ITranscriptDialogs dialogs)
    {
        this.transcript = transcript;
        this.clipboard = clipboard;
        this.dialogs = dialogs;
        CopyWithTimes = new AsyncRelayCommand(() => CopyAsync(true), () => CanCopy);
        CopyWithoutTimes = new AsyncRelayCommand(() => CopyAsync(false), () => CanCopy);
        Export = new AsyncRelayCommand(ExportAsync, () => CanExport);
        Close = new RelayCommand(() => CloseRequested?.Invoke());
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>O usuário pediu para fechar a tela (botão ou Esc); a janela do host se fecha.</summary>
    public event Action? CloseRequested;

    public AsyncRelayCommand CopyWithTimes { get; }
    public AsyncRelayCommand CopyWithoutTimes { get; }
    public AsyncRelayCommand Export { get; }
    public RelayCommand Close { get; }

    public bool IsLoading => isLoading;
    public IReadOnlyList<TranscriptLine> Lines => lines;
    public string Title => transcript.Recording?.Title ?? "Transcrição";
    public string Details => transcript.Recording is { } r
        ? $"{r.CreatedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm", Brazil)} · duração {DurationFormat.Format(r.Duration)}" : "";
    public bool ShowsLines => !isLoading && lines.Count > 0 && !transcript.IsEmpty;
    /// <summary>Transcrição vazia (nenhuma fala): mensagem própria no lugar da lista; copiar e exportar ficam desligados.</summary>
    public bool IsEmpty => !isLoading && transcript.IsEmpty;
    public bool UpdateRequired => transcript.UpdateRequired;
    public bool CanCopy => !isLoading && transcript.CanCopy && !transcript.IsEmpty;
    public bool CanExport => CanCopy;

    /// <summary>Resultado da última ação em texto simples; vazio some da tela.</summary>
    public string Status => isLoading ? "Carregando a transcrição…" : notice ?? transcript.Message;

    public string SuggestedFileName
    {
        get
        {
            var invalid = Path.GetInvalidFileNameChars();
            var title = string.Join(" ", new string((transcript.Recording?.Title ?? "").Select(c => Array.IndexOf(invalid, c) >= 0 ? ' ' : c).ToArray())
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (title.Length > 80) title = title[..80].Trim();
            return (title.Length == 0 ? "Transcrição" : title + " - transcrição") + ".txt";
        }
    }

    /// <summary>Lê os arquivos fora da thread da interface e mostra os trechos. Não consulta o serviço.</summary>
    public async Task OpenAsync(Guid recordingId)
    {
        // Uma leitura superada por outra mais nova não mexe na tela: quem termina por último é a escolha atual.
        var current = ++generation;
        isLoading = true;
        notice = null;
        lines = [];
        Notify();
        try
        {
            await transcript.OpenAsync(recordingId);
        }
        finally
        {
            if (current == generation)
            {
                lines = transcript.Segments.Select(s => new TranscriptLine(s.Timestamp.Trim('[', ']'), s.Text)).ToArray();
                isLoading = false;
                Notify();
            }
        }
    }

    private async Task CopyAsync(bool includeTimestamps)
    {
        if (!CanCopy) return;
        var copied = await clipboard.TrySetTextAsync(transcript.CopyText(includeTimestamps));
        notice = copied
            ? includeTimestamps ? "Transcrição copiada com os tempos." : "Transcrição copiada sem os tempos."
            : "Não foi possível copiar: outro programa está usando a área de transferência. Tente de novo.";
        Notify();
    }

    private async Task ExportAsync()
    {
        if (!CanExport) return;
        var path = dialogs.ChooseExportPath(SuggestedFileName);
        if (path is null)
        {
            notice = "Exportação cancelada.";
            Notify();
            return;
        }
        var outcome = await transcript.ExportAsync(path);
        if (outcome == TranscriptExportOutcome.ConfirmationRequired)
        {
            // A regra é do núcleo: ele recusa criar sobre um arquivo existente até o usuário confirmar aqui.
            if (!await dialogs.ConfirmOverwriteAsync(path))
            {
                notice = "Exportação cancelada. O arquivo existente foi mantido.";
                Notify();
                return;
            }
            outcome = await transcript.ExportAsync(path, overwriteConfirmed: true);
        }
        notice = outcome == TranscriptExportOutcome.Saved ? $"Transcrição exportada para {Path.GetFileName(path)}." : null;
        Notify();
    }

    private void Notify()
    {
        CopyWithTimes.RaiseCanExecuteChanged();
        CopyWithoutTimes.RaiseCanExecuteChanged();
        Export.RaiseCanExecuteChanged();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
}
