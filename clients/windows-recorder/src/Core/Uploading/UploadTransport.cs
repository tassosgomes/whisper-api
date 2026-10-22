using System.Net;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;
using Whisper.Recorder.Core.Api.Generated;
using Whisper.Recorder.Core.Api.Generated.Models;

namespace Whisper.Recorder.Core.Uploading;

/// <summary>No automatic retries, redirects, HTTP logging or shared authentication with storage.</summary>
public sealed class UploadTransport : IDisposable
{
    private readonly HttpMessageHandler apiHandler;
    private readonly HttpMessageHandler storageHandler;

    public UploadTransport(HttpMessageHandler? apiHandler = null, HttpMessageHandler? storageHandler = null)
    {
        this.apiHandler = apiHandler ?? new HttpClientHandler { AllowAutoRedirect = false };
        this.storageHandler = storageHandler ?? new HttpClientHandler { AllowAutoRedirect = false };
    }

    public async Task<TranscriptionUpload> CreateAsync(Uri address, string key, string idempotencyKey,
        CreateTranscriptionUploadRequest body, CancellationToken cancellationToken)
    {
        using var http = new HttpClient(apiHandler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) };
        using var adapter = Adapter(http, address);
        return await new WhisperApiClient(adapter).TranscriptionUploads.PostAsync(body, config =>
        {
            config.Headers.Add("X-API-Key", key);
            config.Headers.Add("Idempotency-Key", idempotencyKey);
        }, cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException();
    }

    public async Task<string> CompleteAsync(Uri address, string key, string uploadId, CancellationToken cancellationToken)
    {
        using var http = new HttpClient(apiHandler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) };
        using var adapter = Adapter(http, address);
        var job = await new WhisperApiClient(adapter).TranscriptionUploads[uploadId].Complete.PostAsync(
            config => config.Headers.Add("X-API-Key", key), cancellationToken).ConfigureAwait(false);
        return !string.IsNullOrWhiteSpace(job?.JobId) ? job.JobId : throw new InvalidDataException();
    }

    private static HttpClientRequestAdapter Adapter(HttpClient http, Uri address) =>
        new(new AnonymousAuthenticationProvider(), httpClient: http) { BaseUrl = address.AbsoluteUri.TrimEnd('/') };

    public async Task<HttpStatusCode> PutAsync(UploadTarget target, Stream audio, IProgress<UploadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (target.Method != "PUT" || !Uri.TryCreate(target.Url, UriKind.Absolute, out var url)
            || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
            || target.Headers is null)
            throw new InvalidDataException();
        using var http = new HttpClient(storageHandler, disposeHandler: false) { Timeout = TimeSpan.FromMinutes(30) };
        using var request = new HttpRequestMessage(HttpMethod.Put, url);
        audio.Position = 0;
        request.Content = new ProgressContent(audio, progress);
        foreach (var header in target.Headers.AdditionalData)
        {
            if (header.Value is not string value
                || (!request.Content.Headers.TryAddWithoutValidation(header.Key, value)
                    && !request.Headers.TryAddWithoutValidation(header.Key, value)))
                throw new InvalidDataException();
        }
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        return response.StatusCode;
    }

    public void Dispose()
    {
        apiHandler.Dispose();
        if (!ReferenceEquals(storageHandler, apiHandler)) storageHandler.Dispose();
    }

    private sealed class ProgressContent(Stream audio, IProgress<UploadProgress>? progress) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = audio.Length; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            var buffer = new byte[81920];
            long sent = 0;
            progress?.Report(new(0, audio.Length));
            int read;
            while ((read = await audio.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
            {
                await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                sent += read;
                progress?.Report(new(sent, audio.Length));
            }
        }
    }
}
