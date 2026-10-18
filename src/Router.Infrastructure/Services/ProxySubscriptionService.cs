using System.Collections.Concurrent;
using System.Net;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Infrastructure.Persistence;

namespace Router.Infrastructure.Services;

/// <summary>刷新按行解析的代理订阅。</summary>
public sealed class ProxySubscriptionService(
    SqlSugarDatabase database,
    IProxyStore proxyStore,
    IHttpClientFactory httpClientFactory,
    IProxyProbeService probes) : IProxySubscriptionService, IDisposable
{
    /// <summary>传输层真正支持的代理协议；其余协议（vless/vmess/ss/trojan/hy2 等）不能当 HTTP 代理用，必须跳过。</summary>
    private static readonly HashSet<string> SupportedSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http", "https", "socks", "socks5", "socks5h", "s5"
    };

    private const int MaxConcurrentProbes = 6;
    /// <summary>探测结果的有效期；到期后订阅刷新会重新探测，避免死节点一直顶着旧分数。</summary>
    internal static readonly TimeSpan ProbeRefreshInterval = TimeSpan.FromHours(2);
    private readonly SemaphoreSlim _probeGate = new(MaxConcurrentProbes, MaxConcurrentProbes);

    public void Dispose() => _probeGate.Dispose();

    public Task<IReadOnlyList<ProxySubscription>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.CreateClient();
        var query = db.Queryable<ProxySubscriptionEntity>();
        var values = (database.FactCacheEnabled
                ? query.WithCache(60).ToList()
                : query.ToList())
            .Select(x => x.ToDomain())
            .ToArray();
        return Task.FromResult<IReadOnlyList<ProxySubscription>>(values);
    }

    public Task<ProxySubscription> SaveAsync(
        ProxySubscription subscription,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(subscription.RefreshIntervalSeconds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(subscription.RefreshIntervalSeconds, (long)int.MaxValue * 60);
        cancellationToken.ThrowIfCancellationRequested();
        using var db = database.CreateClient();
        var entity = subscription.ToEntity();
        var exists = db.Queryable<ProxySubscriptionEntity>().Any(x => x.Id == entity.Id);
        if (exists)
            db.Updateable(entity).ExecuteCommand();
        else
            db.Insertable(entity).ExecuteCommand();

        return Task.FromResult(subscription);
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var proxies = await proxyStore.ListAsync(cancellationToken);
        foreach (var proxy in proxies.Where(proxy => proxy.SubscriptionId == id).ToArray())
            await proxyStore.RemoveAsync(proxy.Id, cancellationToken);

        using var db = database.CreateClient();
        db.Deleteable<ProxySubscriptionEntity>().Where(x => x.Id == id).ExecuteCommand();
    }

    public async Task<RefreshResult> RefreshAsync(string id, CancellationToken cancellationToken = default)
    {
        using var db = database.CreateClient();
        var entity = db.Queryable<ProxySubscriptionEntity>().First(x => x.Id == id)
            ?? throw new KeyNotFoundException($"proxy subscription '{id}' was not found");
        var subscription = entity.ToDomain();

        using var response = await httpClientFactory.CreateClient("proxy-subscriptions")
            .GetAsync(subscription.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        // 订阅源可能重复给出同一节点，按 EndpointKey 去重，避免 ToDictionary 抛重复键。
        var parsed = ParseLines(body, subscription)
            .GroupBy(proxy => proxy.EndpointKey)
            .Select(group => group.First())
            .ToArray();
        var existing = await proxyStore.ListAsync(cancellationToken);
        var current = existing
            .Where(x => x.SubscriptionId == id)
            .GroupBy(x => x.EndpointKey)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.LastSeenInSubscriptionAt).First());
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        var updated = 0;
        var draining = 0;
        var probeQueue = new List<ProxyEndpoint>();

        foreach (var proxy in parsed)
        {
            if (current.TryGetValue(proxy.EndpointKey, out var previous))
            {
                seen.Add(proxy.EndpointKey);
                proxy.Id = previous.Id;
                proxy.ConfigurationVersion = HasConnectionChanged(previous, proxy)
                    ? previous.ConfigurationVersion + 1
                    : previous.ConfigurationVersion;
                proxy.Status.Version = previous.Status.Version;
                if (HasConnectionChanged(previous, proxy))
                {
                    proxy.LatencyMs = 0;
                    proxy.AverageLatencyMs = 0;
                    proxy.AverageSpeedBytesPerSecond = 0;
                    proxy.CompositeScore = 0;
                    proxy.LastProbeSuccessAt = null;
                    proxy.ProbeConsecutiveFailures = 0;
                    proxy.ProbeError = null;
                    proxy.ProbeStatus = "Pending";
                    proxy.ProbeConfigurationVersion = proxy.ConfigurationVersion;
                    probeQueue.Add(proxy);
                }
                else
                {
                    proxy.LatencyMs = previous.LatencyMs;
                    proxy.AverageLatencyMs = previous.AverageLatencyMs;
                    proxy.AverageSpeedBytesPerSecond = previous.AverageSpeedBytesPerSecond;
                    proxy.CompositeScore = previous.CompositeScore;
                    proxy.LastProbedAt = previous.LastProbedAt;
                    proxy.LastProbeSuccessAt = previous.LastProbeSuccessAt;
                    proxy.ProbeConsecutiveFailures = previous.ProbeConsecutiveFailures;
                    proxy.ProbeError = previous.ProbeError;
                    proxy.ProbeStatus = previous.ProbeStatus;
                    proxy.ProbeConfigurationVersion = previous.ProbeConfigurationVersion;
                    if (proxy.LastProbeAt is null
                        || proxy.LastProbeAt < DateTimeOffset.UtcNow - ProbeRefreshInterval)
                        probeQueue.Add(proxy);
                }
                proxy.LastSeenInSubscriptionAt = DateTimeOffset.UtcNow;
                await proxyStore.SaveAsync(proxy, cancellationToken);
                updated++;
            }
            else
            {
                await proxyStore.SaveAsync(proxy, cancellationToken);
                added++;
                probeQueue.Add(proxy);
            }
        }

        foreach (var previous in current.Values.Where(value => !seen.Contains(value.EndpointKey)))
        {
            previous.Status.State = ResourceState.Draining;
            draining++;
            if (previous.Status.InFlight == 0)
                await proxyStore.RemoveAsync(previous.Id, cancellationToken);
            else
                await proxyStore.SaveAsync(previous, cancellationToken);
        }

        subscription.LastFetchedAt = DateTimeOffset.UtcNow;
        subscription.LastFetchedCount = parsed.Length;
        subscription.LastError = null;
        await SaveAsync(subscription, cancellationToken);

        var probeErrors = new ConcurrentBag<string>();
        await Parallel.ForEachAsync(
            probeQueue,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = MaxConcurrentProbes,
                CancellationToken = cancellationToken
            },
            async (proxy, token) =>
            {
                await _probeGate.WaitAsync(token);
                try
                {
                    await probes.ProbeAsync(proxy, token);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // A probe failure is reflected in node quality and must not roll back the subscription refresh.
                    probeErrors.Add(exception.Message);
                }
                finally
                {
                    _probeGate.Release();
                }
            });

        if (probeErrors.TryPeek(out var probeError))
            subscription.LastError = probeError;

        if (!string.IsNullOrWhiteSpace(subscription.LastError))
            await SaveAsync(subscription, cancellationToken);

        return new RefreshResult(added, updated, draining, parsed.Length);
    }

    private static IEnumerable<ProxyEndpoint> ParseLines(string body, ProxySubscription subscription)
    {
        foreach (var line in NormalizeBody(body).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith('#')) continue;
            if (!TryParse(line, subscription, out var proxy)) continue;
            yield return proxy;
        }
    }

    private static bool TryParse(string value, ProxySubscription subscription, out ProxyEndpoint proxy)
    {
        proxy = new ProxyEndpoint();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Host.Length > 0
            && uri.Port > 0)
        {
            // 代理池订阅常把 vless/vmess/ss/trojan 等节点混在同一个列表里，
            // 这些协议没法当 HTTP 代理用，按显式 scheme 直接跳过，不要落成假节点。
            if (!SupportedSchemes.Contains(uri.Scheme)) return false;

            var (username, password) = ParseUserInfo(uri.UserInfo);
            proxy = new ProxyEndpoint
            {
                Id = Guid.NewGuid().ToString("N"),
                SubscriptionId = subscription.Id,
                Host = uri.Host,
                Port = uri.Port,
                Scheme = ParseScheme(uri.Scheme, subscription.Scheme),
                Username = username,
                Password = password
            };
            return true;
        }

        var parts = value.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || !int.TryParse(parts[1], out var port)) return false;

        proxy = new ProxyEndpoint
        {
            Id = Guid.NewGuid().ToString("N"),
            SubscriptionId = subscription.Id,
            Host = parts[0],
            Port = port,
            Scheme = subscription.Scheme,
            Username = parts.Length > 2 ? parts[2] : null,
            Password = parts.Length > 3 ? parts[3] : null
        };
        return true;
    }

    private static bool HasConnectionChanged(ProxyEndpoint previous, ProxyEndpoint current)
        => !string.Equals(previous.Host, current.Host, StringComparison.OrdinalIgnoreCase)
            || previous.Port != current.Port
            || previous.Scheme != current.Scheme
            || !string.Equals(previous.Username, current.Username, StringComparison.Ordinal)
            || !string.Equals(previous.Password, current.Password, StringComparison.Ordinal);

    private static (string? Username, string? Password) ParseUserInfo(string userInfo)
    {
        if (string.IsNullOrEmpty(userInfo)) return (null, null);

        var separator = userInfo.IndexOf(':');
        var username = separator < 0 ? userInfo : userInfo[..separator];
        var password = separator < 0 ? null : userInfo[(separator + 1)..];
        return (NormalizeCredential(username), NormalizeCredential(password));
    }

    private static string? NormalizeCredential(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Equals("null", StringComparison.OrdinalIgnoreCase)
            || value.Equals("none", StringComparison.OrdinalIgnoreCase))
            return null;

        return Uri.UnescapeDataString(value);
    }

    /// <summary>
    /// 代理池/机场订阅有两种下发形态：逐行 http(s)/socks5 URI，或整段 base64 包裹的订阅内容。
    /// 这里只做最小归一化：正文里看不到任何 URI 时才尝试 base64 解码，避免误伤普通文本。
    /// </summary>
    private static string NormalizeBody(string body)
    {
        var trimmed = body.Trim();
        if (trimmed.Length < 16 || trimmed.Contains("://", StringComparison.Ordinal)) return body;

        var padded = trimmed.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        try
        {
            var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            return decoded.Contains("://", StringComparison.Ordinal) ? decoded : body;
        }
        catch (FormatException)
        {
            return body;
        }
    }

    private static ProxyScheme ParseScheme(string? value, ProxyScheme fallback)
        => value?.ToLowerInvariant() switch
        {
            "http" => ProxyScheme.Http,
            "https" => ProxyScheme.Https,
            "socks" or "socks5" or "socks5h" or "s5" => ProxyScheme.Socks5,
            _ => fallback
        };
}
