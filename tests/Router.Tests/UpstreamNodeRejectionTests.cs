using Router.Contracts.Domain;
using Router.Infrastructure.Services;
using FluentAssertions;

namespace Router.Tests;

/// <summary>
/// 上游按出口拒绝业务（如 opencode 免费层 403 FreeTierError）的宿主识别逻辑：
/// 与出口 IP 相关的拒绝应参与节点健康惩罚，凭据/限流类拒绝不应误伤节点。
/// </summary>
[TestClass]
public sealed class UpstreamNodeRejectionTests
{
    [TestMethod]
    public void FreeTierError403IsNodeRejection()
        => PluginAttemptExecutor.IsUpstreamNodeRejection(403, "{\"type\":\"error\",\"error\":{\"type\":\"FreeTierError\",\"message\":\"OpenCode's free tier can only be used from within OpenCode\"}}").Should().BeTrue();

    [TestMethod]
    public void Plain403WithoutBodyIsNodeRejection()
        => PluginAttemptExecutor.IsUpstreamNodeRejection(403, null).Should().BeTrue();

    [TestMethod]
    public void RegionBlock451IsNodeRejection()
        => PluginAttemptExecutor.IsUpstreamNodeRejection(451, "region not supported").Should().BeTrue();

    [TestMethod]
    public void ForbiddenTextIsNodeRejection()
        => PluginAttemptExecutor.IsUpstreamNodeRejection(403, "access forbidden by upstream").Should().BeTrue();

    [TestMethod]
    public void RateLimit429IsNotNodeRejection()
        => PluginAttemptExecutor.IsUpstreamNodeRejection(429, "too many requests").Should().BeFalse();

    [TestMethod]
    public void Credential401IsNotNodeRejection()
        => PluginAttemptExecutor.IsUpstreamNodeRejection(401, "invalid api key").Should().BeFalse();

    [TestMethod]
    public void Success2xxIsNotNodeRejection()
        => PluginAttemptExecutor.IsUpstreamNodeRejection(200, null).Should().BeFalse();
}
