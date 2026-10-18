using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Infrastructure.Services;

namespace Router.Tests;

/// <summary>代理会话黏连与优选指数选择（ProxyAffinity）。</summary>
[TestClass]
public sealed class ProxyAffinityTests
{
    private sealed class MemoryStore : ISharedKeyValueStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        public bool IsConfigured => true;

        public Task<string?> GetStringAsync(string key, CancellationToken cancellationToken = default)
            => Task.FromResult(Values.TryGetValue(key, out var value) ? value : null);

        public Task<bool> SetStringAsync(string key, string value, TimeSpan ttl, CancellationToken cancellationToken = default)
        {
            Values[key] = value;
            return Task.FromResult(true);
        }

        public Task<DateTimeOffset?> GetExpiryAsync(string key, CancellationToken cancellationToken = default)
            => Task.FromResult<DateTimeOffset?>(null);

        public Task<bool> SetExpiryAsync(string key, TimeSpan ttl, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<bool> SetExpiryIfLaterAsync(string key, DateTimeOffset until, string value, CancellationToken cancellationToken = default)
        {
            Values[key] = value;
            return Task.FromResult(true);
        }

        public Task<bool> TryAcquireAsync(string key, TimeSpan ttl, CancellationToken cancellationToken = default)
            => Task.FromResult(!Values.ContainsKey(key));

        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            Values.Remove(key);
            return Task.CompletedTask;
        }

        public Task<bool> PutIfAbsentAsync(string key, string value, TimeSpan ttl, CancellationToken cancellationToken = default)
            => Task.FromResult(Values.TryAdd(key, value));

        public Task<long> IncrementAsync(string key, long delta, TimeSpan ttl, CancellationToken cancellationToken = default)
            => Task.FromResult(0L);
    }

    private static ProxyEndpoint Proxy(string id, double score, int failures = 0)
        => new()
        {
            Id = id,
            SubscriptionId = "sub-1",
            Host = "10.0.0.1",
            Port = 8080,
            Scheme = ProxyScheme.Http,
            CompositeScore = score,
            Status = new ResourceStatus { ConsecutiveFailures = failures }
        };

    private static Task<ProxyEndpoint?> SelectAsync(
        IReadOnlyList<ProxyEndpoint> proxies,
        MemoryStore store,
        string? sessionId,
        bool preferHighScore = true,
        string? forcedId = null)
        => ProxyAffinity.SelectAsync(
            "plugin-a",
            proxies,
            forcedId,
            sessionId,
            preferHighScore,
            ttlMinutes: 30,
            store,
            NullLogger.Instance,
            CancellationToken.None);

    [TestMethod]
    public async Task SameSessionReusesSameProxyAndWritesMapping()
    {
        var store = new MemoryStore();
        var proxies = new[] { Proxy("node-a", 10), Proxy("node-b", 5) };

        var first = await SelectAsync(proxies, store, "session-1");
        var second = await SelectAsync(proxies, store, "session-1");

        first!.Id.Should().Be("node-a");
        second!.Id.Should().Be(first.Id);
        store.Values[ProxyAffinity.AffinityKey("plugin-a", "session-1")].Should().Be(first.Id);
    }

    [TestMethod]
    public async Task StickyProxyOutsideCandidatesIsReplaced()
    {
        var store = new MemoryStore();
        var proxies = new[] { Proxy("node-a", 10), Proxy("node-b", 5) };
        await SelectAsync(proxies, store, "session-2");

        // 该节点被自动停用（不在候选里）后，必须重新选择并改写映射。
        var narrowed = new[] { Proxy("node-b", 5) };
        var next = await SelectAsync(narrowed, store, "session-2");

        next!.Id.Should().Be("node-b");
        store.Values[ProxyAffinity.AffinityKey("plugin-a", "session-2")].Should().Be("node-b");
    }

    [TestMethod]
    public async Task HighScoreModePrefersHighestIndex()
    {
        var store = new MemoryStore();
        var proxies = new[] { Proxy("low", 1), Proxy("high", 99), Proxy("mid", 50) };

        var selected = await ProxyAffinity.SelectAsync(
            "plugin-a", proxies, null, null, preferHighScore: true, ttlMinutes: 30,
            store, NullLogger.Instance, CancellationToken.None);

        selected!.Id.Should().Be("high");
    }

    [TestMethod]
    public async Task FailureCountDecaysIndex()
    {
        // 分数相同但连续失败多者指数更低，优选时不被选中。
        var healthy = Proxy("healthy", 10);
        var failing = Proxy("failing", 10, failures: 3);

        ProxyAffinity.CompositeScore(healthy).Should().BeGreaterThan(ProxyAffinity.CompositeScore(failing));
    }

    [TestMethod]
    public async Task HighScoreModePrefersFreshHealthyOverStaleHighScore()
    {
        // 历史高分但从未探测/探测已过期的死节点，不得压过近期探测健康者。
        var staleHigh = Proxy("stale-high", 99);
        var freshLow = Proxy("fresh-low", 2);
        freshLow.ProbeStatus = "Healthy";
        freshLow.LastProbeAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        freshLow.ProbeConfigurationVersion = freshLow.ConfigurationVersion;

        var selected = await ProxyAffinity.SelectAsync(
            "plugin-a", new[] { staleHigh, freshLow }, null, null, preferHighScore: true, ttlMinutes: 30,
            new MemoryStore(), NullLogger.Instance, CancellationToken.None);

        selected!.Id.Should().Be("fresh-low");
    }

    [TestMethod]
    public async Task HighScoreModeFallsBackToDecayedScoreWhenNothingFresh()
    {
        // 没有任何新鲜探测数据时，仍按衰减指数全量排序。
        var a = Proxy("a", 99, failures: 9);
        var b = Proxy("b", 20);

        var selected = await ProxyAffinity.SelectAsync(
            "plugin-a", new[] { a, b }, null, null, preferHighScore: true, ttlMinutes: 30,
            new MemoryStore(), NullLogger.Instance, CancellationToken.None);

        selected!.Id.Should().Be("b");
    }

    [TestMethod]
    public async Task ForcedProxyWinsOverAffinity()
    {
        var store = new MemoryStore();
        var proxies = new[] { Proxy("node-a", 10), Proxy("node-b", 5) };
        await SelectAsync(proxies, store, "session-3");

        var forced = await SelectAsync(proxies, store, "session-3", forcedId: "node-b");

        forced!.Id.Should().Be("node-b");
    }

    [TestMethod]
    public async Task NoSessionSkipsStore()
    {
        var store = new MemoryStore();
        var proxies = new[] { Proxy("node-a", 10) };

        var selected = await SelectAsync(proxies, store, sessionId: null);

        selected!.Id.Should().Be("node-a");
        store.Values.Should().BeEmpty();
    }

    [TestMethod]
    public async Task EmptyCandidatesReturnsNull()
    {
        var store = new MemoryStore();
        (await SelectAsync(Array.Empty<ProxyEndpoint>(), store, "session-4")).Should().BeNull();
    }
}
