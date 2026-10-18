using Microsoft.Extensions.Logging;
using Router.Contracts.Domain;
using Router.Contracts.Host;

namespace Router.Infrastructure.Services;

/// <summary>
/// 代理节点选择：会话黏连（同一 x-session-id 复用同一节点）+ 优选指数（CompositeScore）。
/// </summary>
public static class ProxyAffinity
{
    internal static async Task<ProxyEndpoint?> SelectAsync(
        string pluginKey,
        IReadOnlyList<ProxyEndpoint> proxies,
        string? forcedId,
        string? sessionId,
        bool preferHighScore,
        int ttlMinutes,
        ISharedKeyValueStore store,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (proxies.Count == 0) return null;
        if (!string.IsNullOrWhiteSpace(forcedId))
            return proxies.FirstOrDefault(proxy => proxy.Id.Equals(forcedId, StringComparison.OrdinalIgnoreCase));

        // 会话黏连：同一会话优先复用仍然可用的同一节点。
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            var affinityKey = AffinityKey(pluginKey, sessionId);
            var stickyId = await ReadStickyAsync(store, affinityKey, logger, sessionId, cancellationToken);
            if (!string.IsNullOrWhiteSpace(stickyId))
            {
                var sticky = proxies.FirstOrDefault(proxy => proxy.Id.Equals(stickyId, StringComparison.OrdinalIgnoreCase));
                if (sticky is not null)
                {
                    // 刷新 TTL，长时间会话持续黏连同一节点。
                    await WriteStickyAsync(store, affinityKey, stickyId, ttlMinutes, logger, sessionId, cancellationToken);
                    return sticky;
                }

                await SafeRemoveAsync(store, affinityKey, logger, cancellationToken);
            }
        }

        var selected = preferHighScore
            ? SelectByScore(proxies)
            : SelectBySample(proxies);

        if (!string.IsNullOrWhiteSpace(sessionId) && selected is not null)
            await WriteStickyAsync(store, AffinityKey(pluginKey, sessionId), selected.Id, ttlMinutes, logger, sessionId, cancellationToken);

        return selected;
    }

    /// <summary>会话黏连键：按插件命名空间隔离（proxy-affinity:{pluginKey}:{sessionId}）。</summary>
    public static string AffinityKey(string pluginKey, string sessionId)
        => $"proxy-affinity:{pluginKey}:{sessionId}";

    /// <summary>
    /// 优选指数优先：先取近期（30 分钟内）探测健康者中指数最高者；没有新鲜探测数据时
    /// 才回落全量按衰减指数排序。避免历史高分但早已探不通的死节点持续被选中。
    /// </summary>
    internal static ProxyEndpoint SelectByScore(IReadOnlyList<ProxyEndpoint> proxies)
    {
        var freshQuality = proxies
            .Where(proxy => proxy.ProbeStatus.Equals("Healthy", StringComparison.OrdinalIgnoreCase)
                && proxy.ProbeConfigurationVersion == proxy.ConfigurationVersion
                && proxy.LastProbeAt is { } lastProbe
                && lastProbe >= DateTimeOffset.UtcNow.AddMinutes(-30))
            .ToArray();
        if (freshQuality.Length > 0)
            return freshQuality
                .OrderByDescending(proxy => CompositeScore(proxy))
                .ThenBy(proxy => proxy.AverageLatencyMs)
                .ThenBy(_ => Random.Shared.Next())
                .First();

        return proxies
            .Where(proxy => !proxy.ProbeStatus.Equals("Failed", StringComparison.OrdinalIgnoreCase))
            .DefaultIfEmpty(proxies[0])
            .OrderByDescending(proxy => CompositeScore(proxy))
            .ThenBy(proxy => proxy.AverageLatencyMs)
            .ThenBy(_ => Random.Shared.Next())
            .First();
    }

    /// <summary>旧行为：随机抽样 10 个，优先近期探测健康且指数高者。</summary>
    internal static ProxyEndpoint SelectBySample(IReadOnlyList<ProxyEndpoint> proxies)
    {
        var sample = proxies.OrderBy(_ => Random.Shared.Next()).Take(10).ToArray();
        var freshQuality = sample
            .Where(proxy => proxy.ProbeStatus.Equals("Healthy", StringComparison.OrdinalIgnoreCase)
                && proxy.ProbeConfigurationVersion == proxy.ConfigurationVersion
                && proxy.LastProbeAt is { } lastProbe
                && lastProbe >= DateTimeOffset.UtcNow.AddMinutes(-30))
            .ToArray();
        if (freshQuality.Length == 0)
            return sample[Random.Shared.Next(sample.Length)];

        return freshQuality
            .OrderByDescending(proxy => CompositeScore(proxy))
            .ThenBy(proxy => proxy.AverageLatencyMs)
            .ThenBy(_ => Random.Shared.Next())
            .First();
    }

    /// <summary>
    /// 优选指数：探测写入的 CompositeScore 为主；未探测节点用实测延时/速度合成，
    /// 再按连续失败次数（探测+请求）衰减，避免失败节点靠旧分数占位。
    /// </summary>
    internal static double CompositeScore(ProxyEndpoint proxy)
    {
        var score = proxy.CompositeScore;
        if (score <= 0)
        {
            var latency = proxy.AverageLatencyMs > 0 ? proxy.AverageLatencyMs : proxy.LatencyMs;
            if (latency <= 0 && proxy.AverageSpeedBytesPerSecond <= 0) return 0;
            score = (latency > 0 ? 1000d / latency : 0)
                + (proxy.AverageSpeedBytesPerSecond > 0 ? Math.Log10(proxy.AverageSpeedBytesPerSecond) : 0);
        }

        var failures = Math.Max(0, proxy.Status.ConsecutiveFailures) + Math.Max(0, proxy.ProbeConsecutiveFailures);
        return score / (1d + failures);
    }

    private static async Task<string?> ReadStickyAsync(
        ISharedKeyValueStore store,
        string key,
        ILogger logger,
        string sessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await store.GetStringAsync(key, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug(exception, "proxy affinity read failed session={SessionId}", sessionId);
            return null;
        }
    }

    private static async Task WriteStickyAsync(
        ISharedKeyValueStore store,
        string key,
        string proxyId,
        int ttlMinutes,
        ILogger logger,
        string sessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            await store.SetStringAsync(key, proxyId, TimeSpan.FromMinutes(Math.Max(1, ttlMinutes)), cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug(exception, "proxy affinity persist failed session={SessionId}", sessionId);
        }
    }

    private static async Task SafeRemoveAsync(
        ISharedKeyValueStore store,
        string key,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            await store.RemoveAsync(key, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug(exception, "proxy affinity clear failed");
        }
    }
}
