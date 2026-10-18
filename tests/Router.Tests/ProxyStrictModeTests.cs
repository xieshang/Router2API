using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Contracts.Pipeline;
using Router.Contracts.Plugins;
using Router.Infrastructure.Persistence;
using Router.Infrastructure.Services;

namespace Router.Tests;

/// <summary>宿主级严格禁直连（Proxy.AllowDirectFallback=false）在对话尝试路径上的行为。</summary>
[TestClass]
public sealed class ProxyStrictModeTests
{
    [TestMethod]
    public async Task StrictModeFailsTheAttemptInsteadOfGoingDirect()
    {
        using var resilience = new PluginResiliencePipelines();
        var handler = new ReplyHandler();
        var executor = Executor(new ProxyOptions { AllowDirectFallback = false }, resilience, handler);

        var result = await executor.ExecuteAsync("test", "test", Terminal(), Request(), "trace", Overrides());

        result.Response.StatusCode.Should().Be(500);
        result.Response.Error.Should().Contain("direct fallback is disabled");
        result.Attempt.IsTransportFailure.Should().BeFalse("绑定阶段的宿主策略失败不是上游传输故障");
        handler.Requests.Should().Be(0, "严格模式不得向直连通道发出任何请求");
    }

    [TestMethod]
    public async Task DefaultModeKeepsDirectFallbackWhenNoProxyExists()
    {
        using var resilience = new PluginResiliencePipelines();
        var handler = new ReplyHandler();
        var executor = Executor(new ProxyOptions(), resilience, handler);

        var result = await executor.ExecuteAsync("test", "test", Terminal(), Request(), "trace", Overrides());

        result.Response.IsSuccess.Should().BeTrue();
        handler.Requests.Should().Be(1);
    }

    private static PluginAttemptExecutor Executor(
        ProxyOptions options,
        PluginResiliencePipelines resilience,
        ReplyHandler handler)
    {
        var proxies = new Mock<IProxyStore>();
        proxies.Setup(value => value.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProxyEndpoint>());
        var subscriptions = new Mock<IProxySubscriptionService>();
        subscriptions.Setup(value => value.ListAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProxySubscription>());
        var factory = new Mock<IProxyHttpClientFactory>();
        factory.Setup(value => value.CreateDirectClient(It.IsAny<string>())).Returns(() => new HttpClient(handler));
        var policies = new PluginPolicyRegistry();
        policies.Set("test", PluginProxyPolicy.Default with { MaxAttempts = 1 });

        return new PluginAttemptExecutor(
            Accounts().Object,
            proxies.Object,
            subscriptions.Object,
            new ResourceLeaseManager(),
            Mock.Of<IProxyPolicyStore>(),
            Mock.Of<ISharedKeyValueStore>(),
            factory.Object,
            policies,
            Mock.Of<IModelMetadataCatalog>(),
            Mock.Of<IPluginLogSink>(),
            NullLogger<PluginAttemptExecutor>.Instance,
            resilience,
            Options.Create(new PluginExecutionOptions()),
            ProxyNodeHealthTrackers.Create(),
            Options.Create(options));
    }

    private static IPlatformTerminal Terminal()
    {
        var terminal = new Mock<IPlatformTerminal>();
        terminal.Setup(value => value.InvokeAsync(It.IsAny<PluginAttemptContext>()))
            .Returns(async (PluginAttemptContext context) =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/v1/models");
                using var response = await context.HttpClient.SendAsync(request);
                return new PluginInvocationResult(
                    new AdapterResponse { StatusCode = (int)response.StatusCode, IsStreaming = false },
                    new PluginAttemptResult(PluginAttemptOutcome.NoPenalty, (int)response.StatusCode));
            });
        return terminal.Object;
    }

    private static AdapterRequest Request() => new() { Model = "m" };

    private static TestOverrides Overrides() => new() { AccountId = "a", SkipCooldown = true };

    private static Mock<IAccountService> Accounts()
    {
        var accounts = new Mock<IAccountService>();
        accounts.Setup(value => value.GetAsync(It.IsAny<string>(), "a", It.IsAny<CancellationToken>()))
            .Returns((string key, string id, CancellationToken _) =>
                Task.FromResult<Account?>(new Account { Id = id, PluginKey = key, Platform = key }));
        return accounts;
    }

    private sealed class ReplyHandler : HttpMessageHandler
    {
        private int _requests;
        public int Requests => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });
        }
    }
}
