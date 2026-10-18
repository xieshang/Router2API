using Microsoft.Extensions.Hosting;
using Router.Contracts.Domain;
using Router.Contracts.Host;

namespace Router.Host.Plugins;

/// <summary>
/// 周期性探测代理节点，让「探测状态/延时/速度」保持新鲜：
/// 优选指数依赖探测数据，探测一旦停摆，死节点会一直顶着数天前的旧高分被反复选中。
/// 每轮按「最久未探测」优先补测一小批，避免一次性打满订阅刷新时的大批量探测。
/// </summary>
public sealed class ProxyProbeHostedService(
    IProxyStore proxies,
    IProxyProbeService probes,
    ILogger<ProxyProbeHostedService> logger) : BackgroundService
{
    /// <summary>每轮间隔。</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);

    /// <summary>每轮最多探测的节点数（并发 6，20s 超时，即最坏约 30 秒一批）。</summary>
    private const int BatchSize = 24;

    /// <summary>探测数据被认为新鲜的时长；超过即重新排队。</summary>
    private static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(30);

    private readonly SemaphoreSlim _gate = new(6, 6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 启动后先等一会，让订阅刷新/插件激活完成。
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(Tick);
        try
        {
            do
            {
                await ProbeBatchAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 宿主正常关闭。
        }
    }

    private async Task ProbeBatchAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ProxyEndpoint> all;
        try
        {
            all = await proxies.ListAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "proxy probe sweep skipped: node listing failed");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var due = all
            .Where(proxy => proxy.Status.State == ResourceState.Active)
            .Where(proxy => proxy.LastProbeAt is null
                || proxy.LastProbeAt < now - FreshFor)
            .OrderBy(proxy => proxy.LastProbeAt ?? DateTimeOffset.MinValue)
            .Take(BatchSize)
            .ToArray();
        if (due.Length == 0) return;

        var failures = 0;
        await Parallel.ForEachAsync(
            due,
            new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = cancellationToken },
            async (proxy, token) =>
            {
                await _gate.WaitAsync(token);
                try
                {
                    await probes.ProbeAsync(proxy, token);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    Interlocked.Increment(ref failures);
                    logger.LogDebug(exception, "proxy probe failed proxy={ProxyId}", proxy.Id);
                }
                finally
                {
                    _gate.Release();
                }
            });

        logger.LogInformation(
            "proxy probe sweep probed={Probed} failures={Failures} pending={Pending}",
            due.Length, failures, all.Count(proxy => proxy.Status.State == ResourceState.Active) - due.Length);
    }
}
