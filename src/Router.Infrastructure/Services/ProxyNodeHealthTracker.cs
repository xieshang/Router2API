using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Infrastructure.Persistence;

namespace Router.Infrastructure.Services;

/// <summary>
/// Tracks per-node consecutive request failures and disables nodes that exceed the configured
/// threshold. Failure counting is process-local (in-memory) keyed by proxy id; disabling is
/// persisted through <see cref="IProxyStore"/> so all hosts observe it.
/// </summary>
public sealed class ProxyNodeHealthTracker
{
    private readonly IProxyStore proxies;
    private readonly IOptions<ProxyOptions> options;
    private readonly ILogger<ProxyNodeHealthTracker> logger;
    private readonly Dictionary<string, int> failures = new(StringComparer.OrdinalIgnoreCase);

    public ProxyNodeHealthTracker(
        IProxyStore proxies,
        IOptions<ProxyOptions> options,
        ILogger<ProxyNodeHealthTracker> logger)
    {
        this.proxies = proxies;
        this.options = options;
        this.logger = logger;
    }

    /// <summary>Resets the consecutive failure counter after a successful request.</summary>
    public void RecordSuccess(string proxyId)
    {
        if (string.IsNullOrWhiteSpace(proxyId)) return;
        lock (failures)
        {
            failures.Remove(proxyId);
        }
    }

    /// <summary>
    /// Records a request failure. Returns true when the node reached the configured threshold and
    /// was disabled (soft-removed: State=Disabled, visible in admin UI, no real deletion).
    /// </summary>
    public async Task<bool> RecordFailureAsync(string proxyId, string reason, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(proxyId)) return false;
        int count;
        lock (failures)
        {
            failures.TryGetValue(proxyId, out var current);
            count = current + 1;
            failures[proxyId] = count;
        }

        var threshold = options.Value.ConsecutiveFailureThreshold;
        if (threshold <= 0 || count < threshold) return false;

        lock (failures)
        {
            failures.Remove(proxyId);
        }

        try
        {
            var proxy = await proxies.GetAsync(proxyId, cancellationToken);
            if (proxy is null || proxy.Status.State is not ResourceState.Active) return false;
            proxy.Status.State = ResourceState.Disabled;
            await proxies.SaveAsync(proxy, cancellationToken);
            logger.LogWarning(
                "proxy node disabled after {Count} consecutive failures proxy={ProxyId} reason={Reason}",
                count, proxyId, reason);
            return true;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "proxy node disable failed proxy={ProxyId}", proxyId);
            return false;
        }
    }
}
