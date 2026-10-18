using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Router.Contracts.Domain;
using Router.Infrastructure.Persistence;
using Router.Infrastructure.Services;

namespace Router.Tests;

/// <summary>代理节点连续失败自动停用（软删除 State=Disabled）与成功清零。</summary>
[TestClass]
public sealed class ProxyNodeHealthTrackerTests
{
    private sealed class TestStore : IDisposable
    {
        public TestStore()
        {
            Path = System.IO.Path.Combine(AppContext.BaseDirectory, "test-data", $"router2api-{Guid.NewGuid():N}.db");
            Database = new SqlSugarDatabase(
                Options.Create(new DatabaseOptions { Path = Path }),
                NullLogger<SqlSugarDatabase>.Instance);
            new DatabaseInitializer(Database, NullLogger<DatabaseInitializer>.Instance).Initialize();
            Store = new ProxyStore(Database);
        }

        public SqlSugarDatabase Database { get; }
        public ProxyStore Store { get; }
        public string Path { get; }

        public void Dispose()
        {
            try { System.IO.File.Delete(Path); } catch { /* ignore */ }
        }
    }

    private static ProxyEndpoint CreateProxy(string id)
        => new()
        {
            Id = id,
            SubscriptionId = "sub-1",
            Host = "10.0.0.1",
            Port = 8080,
            Scheme = ProxyScheme.Http,
            Status = new ResourceStatus()
        };

    [TestMethod]
    public async Task DisablesNodeAfterThresholdAndResetsCounter()
    {
        using var suite = new TestStore();
        var store = suite.Store;

        await store.SaveAsync(CreateProxy("node-threshold"));
        var tracker = new ProxyNodeHealthTracker(
            store,
            Options.Create(new ProxyOptions { ConsecutiveFailureThreshold = 3 }),
            NullLogger<ProxyNodeHealthTracker>.Instance);

        (await tracker.RecordFailureAsync("node-threshold", "e1", default)).Should().BeFalse();
        (await tracker.RecordFailureAsync("node-threshold", "e2", default)).Should().BeFalse();
        (await tracker.RecordFailureAsync("node-threshold", "e3", default)).Should().BeTrue();

        var disabled = await store.GetAsync("node-threshold");
        disabled!.Status.State.Should().Be(ResourceState.Disabled);

        // 计数在禁用后清零：再次失败要重新累积阈值。
        var disabledTracker = new ProxyNodeHealthTracker(
            store,
            Options.Create(new ProxyOptions { ConsecutiveFailureThreshold = 3 }),
            NullLogger<ProxyNodeHealthTracker>.Instance);
        (await disabledTracker.RecordFailureAsync("node-threshold", "e4", default)).Should().BeFalse();
    }

    [TestMethod]
    public async Task SuccessResetsConsecutiveFailureCounter()
    {
        using var suite = new TestStore();
        var store = suite.Store;

        await store.SaveAsync(CreateProxy("node-success"));
        var tracker = new ProxyNodeHealthTracker(
            store,
            Options.Create(new ProxyOptions { ConsecutiveFailureThreshold = 2 }),
            NullLogger<ProxyNodeHealthTracker>.Instance);

        await tracker.RecordFailureAsync("node-success", "e1", default);
        tracker.RecordSuccess("node-success");
        (await tracker.RecordFailureAsync("node-success", "e2", default)).Should().BeFalse();
        (await tracker.RecordFailureAsync("node-success", "e3", default)).Should().BeTrue();
    }

    [TestMethod]
    public async Task ZeroThresholdDisablesNothing()
    {
        using var suite = new TestStore();
        var store = suite.Store;

        await store.SaveAsync(CreateProxy("node-zero"));
        var tracker = new ProxyNodeHealthTracker(
            store,
            Options.Create(new ProxyOptions { ConsecutiveFailureThreshold = 0 }),
            NullLogger<ProxyNodeHealthTracker>.Instance);

        for (var index = 0; index < 10; index++)
            (await tracker.RecordFailureAsync("node-zero", $"e{index}", default)).Should().BeFalse();

        var proxy = await store.GetAsync("node-zero");
        proxy!.Status.State.Should().Be(ResourceState.Active);
    }
}
