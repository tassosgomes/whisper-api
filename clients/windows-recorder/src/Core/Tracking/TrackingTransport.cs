using Microsoft.Kiota.Abstractions.Serialization;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;
using Whisper.Recorder.Core.Api.Generated;
using Whisper.Recorder.Core.Api.Generated.Models;

namespace Whisper.Recorder.Core.Tracking;

/// <summary>Generated public routes, without automatic HTTP retries, redirects or logging.</summary>
public sealed class TrackingTransport(HttpMessageHandler? handler = null) : IDisposable
{
    private readonly HttpMessageHandler handler = handler ?? new HttpClientHandler { AllowAutoRedirect = false };

    public async Task<TranscriptionJob> GetJobAsync(Uri address, string key, string jobId, CancellationToken cancellationToken)
    {
        using var http = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) };
        using var adapter = Adapter(http, address);
        return await new WhisperApiClient(adapter).Transcriptions[jobId].GetAsync(
            config => config.Headers.Add("X-API-Key", key), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException();
    }

    public async Task<byte[]> GetResultAsync(Uri address, string key, string jobId, CancellationToken cancellationToken)
    {
        using var http = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) };
        using var adapter = Adapter(http, address);
        var request = new WhisperApiClient(adapter).Transcriptions[jobId].Result.ToGetRequestInformation(
            config => config.Headers.Add("X-API-Key", key));
        // Preserve the exact response, including unknown versions/fields, instead of
        // deserializing and reserializing the generated v1 model.
        var errors = new Dictionary<string, ParsableFactory<IParsable>>
        {
            ["4XX"] = Problem.CreateFromDiscriminatorValue,
            ["5XX"] = Problem.CreateFromDiscriminatorValue
        };
        using var stream = await adapter.SendPrimitiveAsync<Stream>(request, errors, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private static HttpClientRequestAdapter Adapter(HttpClient http, Uri address) =>
        new(new AnonymousAuthenticationProvider(), httpClient: http) { BaseUrl = address.AbsoluteUri.TrimEnd('/') };

    public void Dispose() => handler.Dispose();
}
