using Whisper.Recorder.Core.Diagnostics;
using Whisper.Recorder.Core.Ports;

namespace Whisper.Recorder.Core.Access;

public sealed class ApiAccessService(
    ICredentialVault vault, ApiConfigurationStore configuration, ApiAccessProbe probe, DiagnosticLog? diagnostic = null)
{
    private readonly SemaphoreSlim operations = new(1, 1);
    public ApiAccessState State { get; private set; } = new(ApiAccessStatus.Absent);
    public event Action? Changed;

    /// <summary>Loads and verifies the saved access, also suitable immediately before sending.</summary>
    public async Task CheckSavedAsync(CancellationToken cancellationToken = default)
    {
        await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var address = configuration.Read();
            var key = await vault.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (address is null || string.IsNullOrWhiteSpace(key))
            {
                SetState(new(ApiAccessStatus.Absent, address));
                return;
            }
            var status = await probe.VerifyAsync(address, key, cancellationToken).ConfigureAwait(false);
            SetState(new(status, address, Mask(key)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            StorageFailed();
        }
        finally { operations.Release(); }
    }

    /// <summary>Replaces the single saved key only after the candidate is accepted.</summary>
    public async Task SaveAsync(string? addressInput, string apiKey, CancellationToken cancellationToken = default)
    {
        await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!ApiConfigurationStore.TryNormalizeAddress(addressInput, out var address)
                || (!string.IsNullOrEmpty(apiKey)
                    && Uri.UnescapeDataString(address!.AbsoluteUri).Contains(apiKey, StringComparison.Ordinal)))
            {
                SetState(State with { Status = ApiAccessStatus.Unverifiable, Problem = ApiAccessProblem.InvalidAddress });
                return;
            }
            var status = string.IsNullOrWhiteSpace(apiKey)
                ? ApiAccessStatus.Rejected
                : await probe.VerifyAsync(address!, apiKey, cancellationToken).ConfigureAwait(false);
            if (status != ApiAccessStatus.Confirmed)
            {
                SetState(State with { Status = status, Problem = ApiAccessProblem.None });
                return;
            }

            var previousKey = await vault.ReadAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            // Finish this paired write even if the UI cancels after verification.
            // If the atomic address write fails, restore the previous credential.
            try
            {
                await vault.SaveAsync(apiKey, CancellationToken.None).ConfigureAwait(false);
                configuration.Save(address!);
            }
            catch (Exception)
            {
                if (previousKey is null) await vault.RemoveAsync(CancellationToken.None).ConfigureAwait(false);
                else await vault.SaveAsync(previousKey, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            SetState(new(ApiAccessStatus.Confirmed, address, Mask(apiKey)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            StorageFailed();
        }
        finally { operations.Release(); }
    }

    public async Task RemoveAsync(CancellationToken cancellationToken = default)
    {
        await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await vault.RemoveAsync(cancellationToken).ConfigureAwait(false);
            SetState(new(ApiAccessStatus.Absent, State.BaseAddress));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            StorageFailed();
        }
        finally { operations.Release(); }
    }

    private static string Mask(string key) => key.Length > 4 ? "••••" + key[^4..] : "••••";

    private void StorageFailed() => SetState(State with
    {
        Status = ApiAccessStatus.Unverifiable,
        Problem = ApiAccessProblem.StorageUnavailable
    });

    private void SetState(ApiAccessState state)
    {
        State = state;
        if (state.Status == ApiAccessStatus.Unverifiable)
            diagnostic?.Write(state.Problem == ApiAccessProblem.None
                ? DiagnosticEvent.NetworkUnavailable : DiagnosticEvent.RequestFailed);
        else if (state.Status == ApiAccessStatus.Rejected)
            diagnostic?.Write(DiagnosticEvent.RequestFailed);
        Changed?.Invoke();
    }
}
