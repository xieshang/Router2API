using Router.Contracts.Domain;
using Router.Contracts.Plugins;

namespace Router.Infrastructure.Services;

/// <summary>唯一的旧契约适配/决策校验位置；完成后执行器不再解释 reason 或 predicate。</summary>
internal static class PluginAttemptDecisions
{
    public static PluginAttemptResult Resolve(PluginInvocationResult result, PluginProxyPolicy policy,
        PluginAccountPolicy accountPolicy, bool transportFailure, bool proxyFault)
    {
        var attempt = result.Attempt;
        if (!Enum.IsDefined(attempt.Outcome)) throw new InvalidOperationException("Unknown plugin attempt outcome.");
        attempt.Decision?.Validate();
        if (result.Response.IsSuccess)
            return new PluginAttemptDecision { ReasonCode = "success" }.ToResult(attempt.StatusCode, attempt.Reason);
        // Never accept a script/DLL flag as evidence of a failed host transport.
        attempt = attempt with { IsTransportFailure = attempt.IsTransportFailure && transportFailure };
        var explicitDecision = attempt.Decision is not null;
        var decision = attempt.Decision ?? MapLegacy(attempt, policy, accountPolicy);
        decision.Validate();
        if (decision.FailureKind == PluginFailureKind.Transport && !transportFailure)
            decision = decision with { FailureKind = PluginFailureKind.Plugin };
        // 代理冷却：显式决策可以基于上游可观测的拒绝信号（例如 opencode 免费层按出口 IP 返回
        // 403 FreeTierError）要求宿主换出口；「传输故障必须由宿主观测到」只约束传输类声明，
        // 不该连带作废上游类冷却，否则会话黏连会把全部重试钉死在同一台被标记的出口上。
        // 旧契约（谓词推断）不享受该放宽，避免脚本凭一个 429 就能惩罚节点。
        if (!proxyFault && (!explicitDecision
                || decision.FailureKind == PluginFailureKind.Transport
                || decision.FailureKind == PluginFailureKind.Plugin))
            decision = decision with { ProxyAction = PluginProxyAction.None };
        // Ownership has moved to the consumer. Never throw away or replay a returned stream.
        if (PluginResponseLifetime.HasStream(result.Response))
            decision = decision with { Retry = PluginRetryAction.None };
        return decision.ToResult(attempt.StatusCode, attempt.Reason);
    }

    private static PluginAttemptDecision MapLegacy(PluginAttemptResult result, PluginProxyPolicy policy, PluginAccountPolicy account)
    {
        var disable = account.DisablePredicate?.Invoke(result) ?? policy.DisableAccountPredicate?.Invoke(result) ?? false;
        var cooldown = !disable && (account.CooldownPredicate?.Invoke(result) ?? policy.CooldownAccountPredicate?.Invoke(result) ?? false);
        return new PluginAttemptDecision
        {
            FailureKind = result.IsTransportFailure ? PluginFailureKind.Transport
                : result.IndicatesInvalidCredential ? PluginFailureKind.InvalidCredential
                : PluginFailureKind.Upstream,
            Retry = policy.RetryPredicate?.Invoke(result) == true ? PluginRetryAction.NextAttempt : PluginRetryAction.None,
            AccountAction = disable ? PluginAccountAction.Disable : cooldown ? PluginAccountAction.Cooldown : PluginAccountAction.None,
            AccountCooldownUntil = cooldown ? DateTimeOffset.UtcNow.AddMinutes(5) : null,
            ProxyAction = result.Outcome == PluginAttemptOutcome.CooldownNode || policy.CooldownNodePredicate?.Invoke(result) == true
                ? PluginProxyAction.Cooldown : PluginProxyAction.None,
            ReasonCode = "legacy"
        };
    }
}
