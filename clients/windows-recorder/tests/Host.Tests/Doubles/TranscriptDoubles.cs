using Whisper.Recorder.Core.Ports;
using Whisper.Recorder.Core.Recording;
using Whisper.Recorder.Core.Transcripts;
using Whisper.Recorder.Presentation.Transcripts;

namespace Whisper.Recorder.Host.Tests.Doubles;

public sealed class FakeClipboard : ITranscriptClipboard
{
    public List<string> Copied { get; } = [];
    public bool Succeeds { get; set; } = true;

    public Task<bool> TrySetTextAsync(string text)
    {
        if (Succeeds)
            Copied.Add(text);
        return Task.FromResult(Succeeds);
    }
}

/// <summary>Seletor de arquivo e confirmação de sobrescrita controlados pelo teste.</summary>
public sealed class FakeDialogs : ITranscriptDialogs
{
    public string? Path { get; set; }
    public bool Overwrite { get; set; }
    public List<string> SuggestedNames { get; } = [];
    public List<string> Confirmations { get; } = [];
    /// <summary>Roda antes de devolver o caminho: simula o arquivo que aparece enquanto o seletor está aberto.</summary>
    public Action<string>? BeforeReturning { get; set; }

    public string? ChooseExportPath(string suggestedFileName)
    {
        SuggestedNames.Add(suggestedFileName);
        if (Path is not null) BeforeReturning?.Invoke(Path);
        return Path;
    }

    public Task<bool> ConfirmOverwriteAsync(string path)
    {
        Confirmations.Add(path);
        return Task.FromResult(Overwrite);
    }
}

/// <summary>Uma gravação "transcrita" só com arquivos locais, no formato salvo pelo acompanhamento (V-07). Sem rede nem serviço.</summary>
public sealed class LocalTranscriptFiles : IDisposable
{
    public static readonly string WithTimes = "[00:00:01.240] Olá reunião" + Environment.NewLine
        + "[01:01:01.007] Outro trecho" + Environment.NewLine;
    public static readonly string WithoutTimes = "Olá reunião" + Environment.NewLine + "Outro trecho";

    public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "whisper-host-v08-" + Guid.NewGuid().ToString("N"));
    public Guid Id { get; } = Guid.NewGuid();
    public RecordingRepository Repository { get; }

    public LocalTranscriptFiles(string title = "Reunião: Planejamento/Q4", string segmentsJson =
        "[{\"startMs\":1240,\"text\":\"Olá reunião\"},{\"startMs\":3661007,\"text\":\"Outro trecho\"}]", string? text = null)
    {
        Repository = new RecordingRepository(System.IO.Path.Combine(Root, "recordings"));
        Directory.CreateDirectory(Repository.DirectoryFor(Id));
        Repository.Save(new RecordingMetadata(Id, title, DateTimeOffset.UtcNow, RecordingState.Transcribed,
            TimeSpan.FromMinutes(62), [AudioSourceKind.Microphone], [], JobId: "trn_local"));
        File.WriteAllText(System.IO.Path.Combine(Repository.DirectoryFor(Id), "result-v1.json"),
            "{\"schemaVersion\":1,\"segments\":" + segmentsJson + "}");
        File.WriteAllText(System.IO.Path.Combine(Repository.DirectoryFor(Id), "transcription.txt"), text ?? WithTimes);
    }

    /// <summary>Outra gravação transcrita no mesmo repositório (para abrir uma depois da outra).</summary>
    public Guid AddTranscribed(string title, string segmentsJson)
    {
        var id = Guid.NewGuid();
        Directory.CreateDirectory(Repository.DirectoryFor(id));
        Repository.Save(new RecordingMetadata(id, title, DateTimeOffset.UtcNow, RecordingState.Transcribed,
            TimeSpan.FromMinutes(5), [AudioSourceKind.Microphone], [], JobId: "trn_" + id.ToString("N")));
        File.WriteAllText(System.IO.Path.Combine(Repository.DirectoryFor(id), "result-v1.json"),
            "{\"schemaVersion\":1,\"segments\":" + segmentsJson + "}");
        File.WriteAllText(System.IO.Path.Combine(Repository.DirectoryFor(id), "transcription.txt"), "[00:00:00.001] x" + Environment.NewLine);
        return id;
    }

    public string Folder(string name)
    {
        var path = System.IO.Path.Combine(Root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    public TranscriptScreenViewModel Screen(ITranscriptClipboard clipboard, ITranscriptDialogs dialogs) =>
        new(new TranscriptViewModel(new LocalTranscriptStore(Repository)), clipboard, dialogs);

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { }
    }
}
