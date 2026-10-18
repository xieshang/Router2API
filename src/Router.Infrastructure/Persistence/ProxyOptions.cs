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

    /// <summary>
    /// 没有可用代理出口时是否允许退化为直连。
    /// 默认 true 保持上游语义；设为 false 即严格禁直连：插件请求（含模型目录与对话）在无可用出口时直接失败，绝不绕过代理池。
    /// 该开关是宿主级策略，优先于调用方传入的 AllowDirectFallback（ProxyPoolHttpClientOptions 或插件 http 选项）。
    /// </summary>
    public bool AllowDirectFallback { get; set; } = true;
}
