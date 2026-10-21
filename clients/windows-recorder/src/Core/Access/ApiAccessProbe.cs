using System.Net;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;
using Whisper.Recorder.Core.Api.Generated;

namespace Whisper.Recorder.Core.Access;

/// <summary>Uses the generated getTranscription operation; never reads or exposes error bodies.</summary>
public sealed class ApiAccessProbe : IDisposable
{
    private readonly HttpMessageHandler handler;
    private readonly TimeSpan timeout;

    public ApiAccessProbe(HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        // Never follow a redirect carrying the user's X-API-Key to another destination.
        this.handler = handler ?? new HttpClientHandler { AllowAutoRedirect = false };
        this.timeout = timeout ?? TimeSpan.FromSeconds(10);
    }

    public async Task<ApiAccessStatus> VerifyAsync(Uri baseAddress, string apiKey, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        // Share connection pools through the handler, while keeping each request's
        // base address and adapter isolated (including concurrent verifications).
        using var http = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        using var adapter = new HttpClientRequestAdapter(new AnonymousAuthenticationProvider(), httpClient: http)
        {
            BaseUrl = baseAddress.AbsoluteUri.TrimEnd('/')
        };
        var api = new WhisperApiClient(adapter);
        var responseHandler = new NativeResponseHandler();
        var operation = api.Transcriptions["trn_" + Guid.NewGuid().ToString("N")];
        try
        {
            await operation.GetAsync(config =>
            {
                config.Headers.Add("X-API-Key", apiKey);
                config.Options.Add(new ResponseHandlerOption { ResponseHandler = responseHandler });
            }, deadline.Token).ConfigureAwait(false);
            using var response = responseHandler.Value as HttpResponseMessage;
            return response?.StatusCode switch
            {
                HttpStatusCode.NotFound => ApiAccessStatus.Confirmed,
                HttpStatusCode.Unauthorized => ApiAccessStatus.Rejected,
                _ => ApiAccessStatus.Unverifiable
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ApiAccessStatus.Unverifiable;
        }
        catch (HttpRequestException)
        {
            return ApiAccessStatus.Unverifiable;
        }
        catch (ArgumentException)
        {
            // Malformed header values are not suitable credentials; do not echo them.
            return ApiAccessStatus.Rejected;
        }
    }

    public void Dispose() => handler.Dispose();
}
