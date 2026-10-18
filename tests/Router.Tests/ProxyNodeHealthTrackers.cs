using Moq;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Router.Infrastructure.Persistence;
using Router.Infrastructure.Services;

namespace Router.Tests;

/// <summary>测试构造 PluginAttemptExecutor 时的无副作用健康跟踪器。</summary>
internal static class ProxyNodeHealthTrackers
{
    public static ProxyNodeHealthTracker Create()
        => new(
            Mock.Of<Router.Contracts.Host.IProxyStore>(),
            Options.Create(new ProxyOptions { ConsecutiveFailureThreshold = 0 }),
            NullLogger<ProxyNodeHealthTracker>.Instance);
}
