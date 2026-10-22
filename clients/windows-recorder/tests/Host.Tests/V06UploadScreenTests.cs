using System.Globalization;
using System.Net;
using System.Text;
using Whisper.Recorder.Core.Access;
using Whisper.Recorder.Core.Diagnostics;
using Whisper.Recorder.Core.Ports;
using Whisper.Recorder.Core.Recording;
using Whisper.Recorder.Core.Uploading;
using Whisper.Recorder.Host.Audio;
using Whisper.Recorder.Host.Tests.Doubles;
using Whisper.Recorder.Presentation.Access;
using Whisper.Recorder.Presentation.Recording;
using Whisper.Recorder.Presentation.Uploading;
using Xunit;

namespace Whisper.Recorder.Host.Tests;

/// <summary>
/// Fluxo de envio da tela (V-06): confirmação, progresso, estados e falhas do contrato. A API e o armazenamento são
/// um servidor em memória; a captura é sintética. Nada toca a rede, o cofre real nem a pasta do app.
/// </summary>
public sealed class V06UploadScreenTests : IAsyncDisposable
{
    private const string Key = "wsk_00000000000000000000000000000000.segredo-de-teste";
    private const string SignedUrlSecret = "assinatura-secreta-do-armazenamento";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly string directory = Path.Combine(Path.GetTempPath(), "whisper-host-v06-" + Guid.NewGuid().ToString("N"));
    private readonly SyntheticCatalog catalog = new();
    private readonly TestClock clock = new();
    private readonly StringWriter log = new();
    private readonly WasapiAudioCapture capture;
    private readonly RecordingSession session;
    private readonly RecordingViewModel recording;
    private readonly RecordingScreenViewModel screen;
    private readonly RecordingRepository repository;
    private readonly MemoryVault vault = new();
    private readonly ApiConfigurationStore configuration;
    private readonly FakeService service = new();
    private readonly ApiAccessProbe probe;
    private readonly UploadTransport transport;
    private readonly ApiAccessViewModel access;
    private readonly UploadViewModel upload;
    private readonly UploadScreenViewModel flow;
    private int settingsRequests;

    public V06UploadScreenTests()
    {
        Directory.CreateDirectory(directory);
        capture = new WasapiAudioCapture(catalog, clock, recoveryInterval: TimeSpan.FromMilliseconds(10));
        repository = new RecordingRepository(Path.Combine(directory, "recordings"));
        session = new RecordingSession(capture, clock, new IdleScheduler(), repository);
        recording = new RecordingViewModel(session, repository);
        screen = new RecordingScreenViewModel(recording, capture);
        configuration = new ApiConfigurationStore(Path.Combine(directory, "settings.json"));
        probe = new ApiAccessProbe(service);
        transport = new UploadTransport(service, service);
        var diagnostic = new DiagnosticLog(log, clock);
        access = new ApiAccessViewModel(new ApiAccessService(vault, configuration, probe, diagnostic));
        upload = new UploadViewModel(new UploadCoordinator(repository, configuration, vault, transport, clock, diagnostic));
        flow = new UploadScreenViewModel(upload, access, screen);
        // A mesma ligação que App.Compose faz.
        access.ConfigurationRequested += () => settingsRequests++;
        flow.ConfigurationRequested += () => settingsRequests++;
    }

    public async ValueTask DisposeAsync()
    {
        flow.Dispose();
        upload.Dispose();
        access.Dispose();
        transport.Dispose();
        probe.Dispose();
        await screen.DisposeAsync();
        recording.Dispose();
        await session.DisposeAsync();
        await capture.DisposeAsync();
        try { Directory.Delete(directory, recursive: true); }
        catch (IOException) { }
    }

    private async Task Configure()
    {
        configuration.Save(new Uri("https://api.example.test"));
        await vault.SaveAsync(Key, Token);
    }

    /// <summary>Grava dois segundos com a captura sintética, para e deixa a gravação selecionada na tela de envio.</summary>
    private async Task<RecordingMetadata> RecordAndSelect()
    {
        await screen.RefreshDevicesAsync();
        await screen.StartAsync();
        catalog.Latest(AudioSourceKind.Microphone).Emit(Pcm.Float32Stereo(48000, 440, 0.5, 48000, 0));
        clock.UtcNow += TimeSpan.FromSeconds(2);
        await screen.StopAsync();
        var saved = Assert.Single(repository.List());
        screen.RefreshRecordings();
        flow.Select(Assert.Single(screen.Recordings));
        return saved;
    }

    /// <summary>Estende o WAV (o conteúdo não importa para o servidor em memória) e atualiza a lista.</summary>
    private void Grow(Guid id, long size)
    {
        using (var file = new FileStream(repository.AudioPath(id), FileMode.Open, FileAccess.Write))
            file.SetLength(size);
        screen.RefreshRecordings();
    }

    private RecordingMetadata Current(Guid id) => repository.Read(id);
    private RecordingListItem Item(Guid id) => screen.Recordings.Single(r => r.Id == id);

    private void AssertNoSecretsAnywhere(params string[] shown)
    {
        foreach (var text in shown.Append(log.ToString()))
        {
            Assert.DoesNotContain(Key, text, StringComparison.Ordinal);
            Assert.DoesNotContain(SignedUrlSecret, text, StringComparison.Ordinal);
            Assert.DoesNotContain("storage.example.test", text, StringComparison.Ordinal);
        }
        foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".wav", StringComparison.Ordinal)))
        {
            var content = File.ReadAllText(file);
            Assert.DoesNotContain(Key, content, StringComparison.Ordinal);
            Assert.DoesNotContain(SignedUrlSecret, content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ConfirmationShowsWhatWillBeSentAndNothingLeavesWithoutIt()
    {
        await Configure();
        var saved = await RecordAndSelect();
        Assert.True(flow.CanTranscribe);

        await flow.Transcribe.ExecuteAsync();

        Assert.Equal(UploadPhase.Confirming, flow.Phase);
        Assert.Equal($"Título: {saved.DisplayTitle}", flow.ConfirmationTitle);
        Assert.Equal("Duração: 00:00:02", flow.ConfirmationDuration);
        Assert.StartsWith("Tamanho aproximado: ", flow.ConfirmationSize);
        Assert.Contains("processado por ele", flow.Notice);
        Assert.Contains("Nada é enviado sem a sua confirmação", flow.Notice);
        // A verificação do acesso é o único tráfego antes da confirmação.
        Assert.All(service.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
        Assert.Equal(RecordingState.Saved, Current(saved.Id).State);
        Assert.Equal(0, Current(saved.Id).UploadAttempt);

        flow.Cancel.Execute(null);
        Assert.Equal(UploadPhase.Idle, flow.Phase);
        Assert.All(service.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
        Assert.Equal(0, settingsRequests);

        await flow.Transcribe.ExecuteAsync();
        await flow.Confirm.ExecuteAsync();

        Assert.Equal(UploadPhase.Accepted, flow.Phase);
        Assert.Contains("em processamento", flow.ResultMessage);
        Assert.Equal(["POST /v1/transcription-uploads", "PUT /whisper/uploads/upl_1", "POST /v1/transcription-uploads/upl_1/complete"],
            service.Requests.Where(r => r.Method != HttpMethod.Get).Select(r => $"{r.Method} {r.Path}"));
        Assert.Equal(Key, service.Requests.First(r => r.Method == HttpMethod.Post).ApiKey);
        Assert.Null(service.Requests.Single(r => r.Method == HttpMethod.Put).ApiKey); // a chave nunca vai ao armazenamento
        var stored = Current(saved.Id);
        Assert.Equal((RecordingState.Processing, "trn_1"), (stored.State, stored.JobId));
        Assert.Equal("Processando", Item(saved.Id).Status);
        Assert.False(flow.CanTranscribe); // já processando: não há o que enviar de novo
        AssertNoSecretsAnywhere(flow.ResultMessage!, flow.Notice, flow.ProgressText);
    }

    [Fact]
    public async Task ProgressAndStatesFollowTheUploadFromUploadingToProcessing()
    {
        await Configure();
        var saved = await RecordAndSelect();
        Grow(saved.Id, 1024 * 1024);
        service.PutGate = new PutGate();
        await flow.Transcribe.ExecuteAsync();

        var sending = flow.Confirm.ExecuteAsync();
        await service.PutGate.Halfway.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);

        Assert.Equal(UploadPhase.Sending, flow.Phase);
        Assert.True(flow.ShowsProgress);
        Assert.False(flow.IsIndeterminate);
        Assert.InRange(flow.ProgressFraction, 0.3, 0.9);
        Assert.Contains("Enviando…", flow.ProgressText);
        Assert.Contains("%", flow.ProgressText);
        Assert.Equal(RecordingState.Uploading, Current(saved.Id).State);
        Assert.Equal(("Enviando", "↑"), (Item(saved.Id).Status, Item(saved.Id).StatusGlyph));
        Assert.False(flow.CanTranscribe); // um clique duplo não inicia outro envio
        Assert.False(flow.Confirm.CanExecute(null));
        Assert.True(flow.Cancel.CanExecute(null));

        service.PutGate.Release.SetResult();
        await sending;

        Assert.Equal(UploadPhase.Accepted, flow.Phase);
        Assert.Equal(1.0, flow.ProgressFraction, 3);
        Assert.Equal(RecordingState.Processing, Current(saved.Id).State);
        Assert.Equal(("Processando", "…"), (Item(saved.Id).Status, Item(saved.Id).StatusGlyph));
        Assert.True(flow.Dismiss.CanExecute(null));
        flow.Dismiss.Execute(null);
        Assert.Equal(UploadPhase.Idle, flow.Phase);
    }

    [Fact]
    public async Task RevokedKeyOnSendOpensSettingsKeepsTheRecordingAndRetryWorksAfterUpdatingTheKey()
    {
        await Configure();
        var saved = await RecordAndSelect();
        var audioLength = new FileInfo(repository.AudioPath(saved.Id)).Length;
        service.CreateFailure = (HttpStatusCode.Unauthorized, "UNAUTHORIZED", null);
        await flow.Transcribe.ExecuteAsync();

        await flow.Confirm.ExecuteAsync();

        Assert.Equal(UploadPhase.Failed, flow.Phase);
        Assert.Equal(1, settingsRequests);
        Assert.True(flow.NeedsSettings);
        Assert.Contains("Atualize a chave nas configurações", flow.ResultMessage);
        Assert.True(flow.OpenSettings.CanExecute(null));
        flow.OpenSettings.Execute(null);
        Assert.Equal(2, settingsRequests);
        Assert.Equal(audioLength, new FileInfo(repository.AudioPath(saved.Id)).Length);
        Assert.Equal("Envio não concluído", Item(saved.Id).Status);
        Assert.DoesNotContain(service.Requests, r => r.Method == HttpMethod.Put);

        // O usuário atualiza a chave nas configurações e tenta de novo, sem confirmar tudo outra vez.
        service.CreateFailure = null;
        await vault.SaveAsync(Key + "-nova", Token);
        Assert.True(flow.Retry.CanExecute(null));
        await flow.Retry.ExecuteAsync();

        Assert.Equal(UploadPhase.Accepted, flow.Phase);
        Assert.Equal(RecordingState.Processing, Current(saved.Id).State);
        Assert.Equal(1, Current(saved.Id).UploadAttempt);
        AssertNoSecretsAnywhere(flow.ResultMessage!);
    }

    [Fact]
    public async Task FirstSendWithoutConfigurationOpensSettingsAndNeverShowsTheConfirmation()
    {
        var saved = await RecordAndSelect();
        Assert.Equal(0, settingsRequests); // gravar nunca pede configuração

        await flow.Transcribe.ExecuteAsync();

        Assert.Equal(1, settingsRequests);
        Assert.Equal(UploadPhase.Idle, flow.Phase);
        Assert.Contains("Configure o endereço da API e a chave", flow.Hint);
        Assert.Empty(service.Requests);
        Assert.Equal(RecordingState.Saved, Current(saved.Id).State);
        Assert.True(flow.CanTranscribe); // depois de configurar, basta tocar de novo

        await Configure();
        await flow.Transcribe.ExecuteAsync();
        Assert.Equal(UploadPhase.Confirming, flow.Phase);
        Assert.Equal(1, settingsRequests);
    }

    [Fact]
    public async Task NetworkFailureOffersManualRetryWithTheSameKeyAndNeverLoops()
    {
        await Configure();
        var saved = await RecordAndSelect();
        await flow.Transcribe.ExecuteAsync();
        service.NetworkDown = true;

        await flow.Confirm.ExecuteAsync();

        Assert.Equal(UploadPhase.Failed, flow.Phase);
        Assert.Contains("A gravação está no PC. Tente de novo.", flow.ResultMessage);
        Assert.True(flow.Retry.CanExecute(null));
        Assert.Equal("Envio não concluído", Item(saved.Id).Status);
        await Task.Delay(200, Token); // nenhuma nova tentativa sozinha
        var attempts = service.Requests.Count(r => r.Method == HttpMethod.Post);
        Assert.Equal(1, attempts);

        service.NetworkDown = false;
        await flow.Retry.ExecuteAsync();

        Assert.Equal(UploadPhase.Accepted, flow.Phase);
        var keys = service.Requests.Where(r => r.IdempotencyKey is not null).Select(r => r.IdempotencyKey).Distinct().ToArray();
        Assert.Equal([$"recording-{saved.Id:D}-attempt-1"], keys); // a repetição reaproveita a mesma chave
        Assert.Equal(1, service.Jobs);
        AssertNoSecretsAnywhere(flow.ResultMessage!);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "MEDIA_SIZE_LIMIT_EXCEEDED", "5 GiB")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "MEDIA_DURATION_LIMIT_EXCEEDED", "2 horas")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "UNSUPPORTED_MEDIA_FORMAT", "formato do áudio")]
    public async Task ServiceLimitsAreExplainedInPlainLanguageWithoutLosingTheRecording(HttpStatusCode status, string code, string expected)
    {
        await Configure();
        var saved = await RecordAndSelect();
        var audio = File.ReadAllBytes(repository.AudioPath(saved.Id));
        service.CreateFailure = (status, code, null);
        await flow.Transcribe.ExecuteAsync();

        await flow.Confirm.ExecuteAsync();

        Assert.Equal(UploadPhase.Failed, flow.Phase);
        Assert.Contains(expected, flow.ResultMessage);
        Assert.DoesNotContain(code, flow.ResultMessage!, StringComparison.Ordinal);
        Assert.Equal(0, settingsRequests);
        Assert.Equal(audio, File.ReadAllBytes(repository.AudioPath(saved.Id)));
    }

    [Fact]
    public async Task TooManyRequestsShowsWhenToTryAgainAndRefusesToCallEarlier()
    {
        await Configure();
        var saved = await RecordAndSelect();
        service.CreateFailure = (HttpStatusCode.TooManyRequests, "TOO_MANY_PENDING_UPLOADS", "60");
        await flow.Transcribe.ExecuteAsync();

        await flow.Confirm.ExecuteAsync();

        var from = clock.UtcNow.AddSeconds(60).ToLocalTime().ToString("HH:mm", CultureInfo.GetCultureInfo("pt-BR"));
        Assert.Equal(UploadPhase.Failed, flow.Phase);
        Assert.Contains("Limite de envios atingido", flow.ResultMessage);
        Assert.EndsWith($"Disponível a partir das {from}.", flow.ResultMessage);
        var calls = service.Requests.Count(r => r.Method == HttpMethod.Post);

        await flow.Retry.ExecuteAsync(); // antes do prazo o núcleo nem chama a API

        Assert.Equal(calls, service.Requests.Count(r => r.Method == HttpMethod.Post));
        Assert.Equal(RecordingState.Uploading, Current(saved.Id).State);
        Assert.Equal(DateTimeOffset.Parse("2026-10-21T12:01:02Z", CultureInfo.InvariantCulture), Current(saved.Id).UploadRetryAt);

        clock.UtcNow += TimeSpan.FromSeconds(61);
        service.CreateFailure = null;
        await flow.Retry.ExecuteAsync();
        Assert.Equal(UploadPhase.Accepted, flow.Phase);
    }

    [Fact]
    public async Task ResendingATranscribedRecordingNeedsExtraConfirmation()
    {
        await Configure();
        var saved = await RecordAndSelect();
        File.WriteAllText(Path.Combine(repository.DirectoryFor(saved.Id), "metadata.json"),
            System.Text.Json.JsonSerializer.Serialize(saved with { State = RecordingState.Transcribed }, new System.Text.Json.JsonSerializerOptions
            {
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase) }
            }));
        screen.RefreshRecordings();
        flow.Select(Item(saved.Id));
        Assert.Equal("Enviar de novo para transcrição", flow.TranscribeText);

        await flow.Transcribe.ExecuteAsync();

        Assert.True(flow.AsksExistingConfirmation);
        Assert.Contains("já tem uma transcrição", flow.ExistingNotice);
        Assert.False(flow.Confirm.CanExecute(null));
        await flow.ConfirmAsync();
        Assert.Equal(UploadPhase.Confirming, flow.Phase);
        Assert.DoesNotContain(service.Requests, r => r.Method == HttpMethod.Post);

        flow.ExistingTranscriptionConfirmed = true;
        await flow.Confirm.ExecuteAsync();
        Assert.Equal(UploadPhase.Accepted, flow.Phase);
    }

    [Fact]
    public async Task LargeFileIsStreamedWithoutLoadingItIntoMemory()
    {
        await Configure();
        var saved = await RecordAndSelect();
        const long size = 200L * 1024 * 1024;
        Grow(saved.Id, size);
        await flow.Transcribe.ExecuteAsync();
        Assert.Equal("Tamanho aproximado: 200,0 MB", flow.ConfirmationSize);
        var notifications = 0;
        flow.PropertyChanged += (_, _) => Interlocked.Increment(ref notifications);

        GC.Collect();
        var before = GC.GetTotalAllocatedBytes(precise: true);
        await flow.Confirm.ExecuteAsync();
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        Assert.Equal(UploadPhase.Accepted, flow.Phase);
        Assert.Equal(size, service.PutBytes);
        // Hash e PUT leem em blocos: bem abaixo do tamanho do arquivo (carregá-lo inteiro alocaria pelo menos 200 MB).
        Assert.InRange(allocated, 0, 48L * 1024 * 1024);
        // A tela é avisada quando o percentual muda, não a cada bloco de 80 KiB (cerca de 2.600 blocos).
        Assert.InRange(notifications, 1, 400);
    }

    [Fact]
    public async Task ChangingTheListSelectionDuringTheSendNeverRedirectsRetryToAnotherRecording()
    {
        await Configure();
        var first = await RecordAndSelect();
        // Uma segunda gravação, copiada da primeira com outro identificador.
        var otherId = Guid.NewGuid();
        Directory.CreateDirectory(repository.DirectoryFor(otherId));
        File.Copy(repository.AudioPath(first.Id), repository.AudioPath(otherId));
        File.WriteAllText(Path.Combine(repository.DirectoryFor(otherId), "metadata.json"),
            File.ReadAllText(Path.Combine(repository.DirectoryFor(first.Id), "metadata.json"))
                .Replace(first.Id.ToString("D"), otherId.ToString("D"), StringComparison.Ordinal));
        Grow(first.Id, 1024 * 1024);
        service.PutGate = new PutGate();
        await flow.Transcribe.ExecuteAsync();
        var sending = flow.Confirm.ExecuteAsync();
        await service.PutGate.Halfway.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);

        flow.Select(Item(otherId)); // o usuário clica em outra linha com o envio em curso
        service.NetworkDown = true; // o complete falha depois do PUT
        service.PutGate.Release.SetResult();
        await sending;

        Assert.Equal(UploadPhase.Failed, flow.Phase);
        Assert.Equal(otherId, flow.SelectedRecordingId);
        Assert.Equal($"Título: {first.DisplayTitle}", flow.ConfirmationTitle);
        Assert.True(flow.Retry.CanExecute(null));
        service.NetworkDown = false;
        // Os fatos da gravação mudam: a nova confirmação é da gravação do envio, não da linha selecionada agora.
        File.WriteAllText(Path.Combine(repository.DirectoryFor(first.Id), "metadata.json"),
            System.Text.Json.JsonSerializer.Serialize(Current(first.Id) with { Title = "Título novo" }, new System.Text.Json.JsonSerializerOptions
            {
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase) }
            }));
        await flow.Retry.ExecuteAsync();
        Assert.Equal(UploadPhase.Confirming, flow.Phase);
        Assert.Equal("Título: Título novo", flow.ConfirmationTitle);
        Assert.Equal(otherId, flow.SelectedRecordingId);
        await flow.Confirm.ExecuteAsync();
        Assert.Equal(UploadPhase.Accepted, flow.Phase);

        Assert.Equal(UploadPhase.Accepted, flow.Phase);
        Assert.Equal(RecordingState.Processing, Current(first.Id).State);
        Assert.Equal(RecordingState.Saved, Current(otherId).State);
        Assert.Equal(0, Current(otherId).UploadAttempt);
        Assert.DoesNotContain(service.Requests, r => r.IdempotencyKey is not null && r.IdempotencyKey.Contains(otherId.ToString("D"), StringComparison.Ordinal));
    }

    [Fact]
    public async Task RetryOfATranscribedResendAfterNetworkFailureKeepsTheAttemptAndKeyWithoutAskingAgain()
    {
        await Configure();
        var saved = await RecordAndSelect();
        await flow.Transcribe.ExecuteAsync();
        await flow.Confirm.ExecuteAsync();
        Assert.Equal(UploadPhase.Accepted, flow.Phase);
        flow.Dismiss.Execute(null);
        File.WriteAllText(Path.Combine(repository.DirectoryFor(saved.Id), "metadata.json"),
            System.Text.Json.JsonSerializer.Serialize(Current(saved.Id) with { State = RecordingState.Transcribed }, new System.Text.Json.JsonSerializerOptions
            {
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase) }
            }));
        screen.RefreshRecordings();
        flow.Select(Item(saved.Id));
        await flow.Transcribe.ExecuteAsync();
        flow.ExistingTranscriptionConfirmed = true;
        service.NetworkDown = true;

        await flow.Confirm.ExecuteAsync();

        Assert.Equal(UploadPhase.Failed, flow.Phase);
        Assert.Equal(2, Current(saved.Id).UploadAttempt);
        service.NetworkDown = false;
        await flow.Retry.ExecuteAsync();

        Assert.Equal(UploadPhase.Accepted, flow.Phase); // sem voltar à confirmação nem em laço
        Assert.Equal((RecordingState.Processing, 2), (Current(saved.Id).State, Current(saved.Id).UploadAttempt));
        Assert.Equal([$"recording-{saved.Id:D}-attempt-1", $"recording-{saved.Id:D}-attempt-2"],
            service.Requests.Where(r => r.IdempotencyKey is not null).Select(r => r.IdempotencyKey!).Distinct());
    }

    private sealed class MemoryVault : ICredentialVault
    {
        private string? value;

        public ValueTask SaveAsync(string apiKey, CancellationToken cancellationToken = default)
        {
            value = apiKey;
            return ValueTask.CompletedTask;
        }

        public ValueTask<string?> ReadAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(value);

        public ValueTask RemoveAsync(CancellationToken cancellationToken = default)
        {
            value = null;
            return ValueTask.CompletedTask;
        }
    }

    private sealed record Seen(HttpMethod Method, string Path, string? ApiKey, string? IdempotencyKey);

    /// <summary>Segura o PUT na metade, para observar o progresso e o estado com o envio em andamento.</summary>
    private sealed class PutGate
    {
        public TaskCompletionSource Halfway { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>API e armazenamento em memória (contrato 1.1.0), sem rede real.</summary>
    private sealed class FakeService : HttpMessageHandler
    {
        private readonly object sync = new();
        private readonly List<Seen> requests = [];
        public (HttpStatusCode Status, string Code, string? RetryAfter)? CreateFailure { get; set; }
        public bool NetworkDown { get; set; }
        public PutGate? PutGate { get; set; }
        public long PutBytes { get; private set; }
        public int Jobs { get; private set; }
        public IReadOnlyList<Seen> Requests { get { lock (sync) return requests.ToArray(); } }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (sync)
            {
                requests.Add(new(request.Method, request.RequestUri!.AbsolutePath,
                    request.Headers.TryGetValues("X-API-Key", out var key) ? key.Single() : null,
                    request.Headers.TryGetValues("Idempotency-Key", out var idempotency) ? idempotency.Single() : null));
            }
            if (NetworkDown)
                throw new HttpRequestException("sem conexão");
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get)
                return Problem(HttpStatusCode.NotFound, "NOT_FOUND"); // a verificação do acesso espera 404
            if (request.Method == HttpMethod.Put)
                return await ReceiveAsync(request, cancellationToken);
            if (path.EndsWith("/complete", StringComparison.Ordinal))
            {
                Jobs = 1;
                return Json(HttpStatusCode.Accepted, "{\"jobId\":\"trn_1\",\"status\":\"queued\",\"createdAt\":\"2026-10-21T12:00:00Z\",\"statusUrl\":\"/v1/transcriptions/trn_1\"}");
            }
            if (CreateFailure is { } failure)
            {
                var response = Problem(failure.Status, failure.Code);
                if (failure.RetryAfter is not null)
                    response.Headers.TryAddWithoutValidation("Retry-After", failure.RetryAfter);
                return response;
            }
            return Json(HttpStatusCode.Created,
                "{\"uploadId\":\"upl_1\",\"expiresAt\":\"2026-10-21T15:00:00Z\",\"upload\":{\"method\":\"PUT\"," +
                $"\"url\":\"https://storage.example.test/whisper/uploads/upl_1?signature={SignedUrlSecret}\"," +
                "\"headers\":{\"Content-Type\":\"audio/wav\"}},\"completeUrl\":\"/v1/transcription-uploads/upl_1/complete\"}");
        }

        private async Task<HttpResponseMessage> ReceiveAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var sink = new CountingStream(request.Content!.Headers.ContentLength ?? 0, PutGate);
            await request.Content.CopyToAsync(sink, cancellationToken);
            PutBytes = sink.Total;
            return new HttpResponseMessage(HttpStatusCode.OK);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        private static HttpResponseMessage Problem(HttpStatusCode status, string code) =>
            new(status)
            {
                Content = new StringContent(
                    $"{{\"type\":\"about:blank\",\"title\":\"Erro\",\"status\":{(int)status},\"code\":\"{code}\"}}",
                    Encoding.UTF8, "application/problem+json")
            };
    }

    /// <summary>Descarta o que recebe, só conta e, na metade, espera o teste liberar.</summary>
    private sealed class CountingStream(long expected, PutGate? gate) : Stream
    {
        private bool paused;
        public long Total { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => Total; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Total += count;

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Total += buffer.Length;
            if (gate is not null && !paused && Total >= expected / 2)
            {
                paused = true;
                gate.Halfway.TrySetResult();
                await gate.Release.Task.WaitAsync(cancellationToken);
            }
        }
    }
}
