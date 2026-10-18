namespace Router.Infrastructure.Persistence;

/// <summary>代理池运行策略：失败阈值、会话黏连与优选排序。</summary>
public sealed class ProxyOptions
{
    /// <summary>连续失败自动停用阈值；0 表示不自动停用。</summary>
    public int ConsecutiveFailureThreshold { get; set; } = 5;

    /// <summary>是否启用会话黏连（同会话优先复用同一节点）。</summary>
    public bool SessionAffinityEnabled { get; set; }

    /// <summary>会话黏连映射的存活时间（分钟）。</summary>
    public int SessionAffinityTtlMinutes { get; set; } = 30;

    /// <summary>是否按优选指数（CompositeScore）全局排序选节点；关闭时保持随机抽样行为。</summary>
    public bool PreferHighScore { get; set; } = true;
}
