using Updatr.Core.Diagnostics;

namespace Updatr.Platform.Elevation;

/// <summary>
/// Owns the session's elevated broker: launched on first need (one UAC prompt), reused after,
/// closed with the host. A declined prompt is remembered so the user is not asked again for every
/// item in a batch; <see cref="ResetDeclined"/> re-enables asking (the "try again" button).
/// </summary>
public sealed class ElevationService(string exePath, string? dataRoot) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private BrokerClient? _broker;

    public bool Declined { get; private set; }

    /// <summary>The broker, launching it if needed. Null when the user declined elevation.</summary>
    public async Task<BrokerClient?> GetBrokerAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_broker is { IsConnected: true })
                return _broker;

            if (_broker is not null)
            {
                await _broker.DisposeAsync().ConfigureAwait(false);
                _broker = null;
            }

            if (Declined)
                return null;

            _broker = await BrokerClient.LaunchAsync(exePath, dataRoot, cancellationToken).ConfigureAwait(false);
            Declined = _broker is null;
            return _broker;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void ResetDeclined() => Declined = false;

    public async ValueTask DisposeAsync()
    {
        if (_broker is not null)
        {
            await _broker.DisposeAsync().ConfigureAwait(false);
            DiagLog.BrokerExited("session ended");
        }

        _gate.Dispose();
    }
}
