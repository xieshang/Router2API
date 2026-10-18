using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Router.Contracts.Host;
using Router.Infrastructure.Persistence;
using Router.Infrastructure.Services;

namespace Router.Tests;

[TestClass]
public sealed class AnalyticsServiceTests
{
    private static readonly DateTimeOffset FromUtc = new(2026, 1, 2, 3, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ToUtc = new(2026, 1, 2, 5, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task SuccessesByModelCountsOnlySuccessfulRowsPerExactModelName()
    {
        var rows = new List<RequestLogEntity>
        {
            Row("a-1", "gpt-4", true, new DateTime(2026, 1, 2, 3, 5, 0, DateTimeKind.Utc)),
            Row("a-2", "gpt-4", true, new DateTime(2026, 1, 2, 3, 15, 0, DateTimeKind.Utc)),
            Row("a-3", "claude-3", true, new DateTime(2026, 1, 2, 3, 25, 0, DateTimeKind.Utc)),
            Row("a-4", "GPT-4", true, new DateTime(2026, 1, 2, 3, 35, 0, DateTimeKind.Utc)),
            Row("a-5", "gpt-4", false, new DateTime(2026, 1, 2, 3, 45, 0, DateTimeKind.Utc))
        };

        var report = await QueryAsync(rows, FromUtc, ToUtc);

        report.Buckets.Should().ContainSingle();
        var bucket = report.Buckets[0];
        bucket.Requests.Should().Be(5);
        bucket.Successes.Should().Be(4);
        bucket.SuccessesByModel.Should().HaveCount(3);
        bucket.SuccessesByModel["gpt-4"].Should().Be(2);
        bucket.SuccessesByModel["GPT-4"].Should().Be(1);
        bucket.SuccessesByModel["claude-3"].Should().Be(1);
        // 计数降序后按 Ordinal 名称升序，保证前端展示稳定。
        bucket.SuccessesByModel.Keys.Should().Equal("gpt-4", "GPT-4", "claude-3");
    }

    [TestMethod]
    public async Task BlankModelsShareTheEmptyKeyWhileModelNamesStayVerbatim()
    {
        var rows = new List<RequestLogEntity>
        {
            Row("b-1", null, true, new DateTime(2026, 1, 2, 3, 5, 0, DateTimeKind.Utc)),
            Row("b-2", "   ", true, new DateTime(2026, 1, 2, 3, 10, 0, DateTimeKind.Utc)),
            Row("b-3", "", true, new DateTime(2026, 1, 2, 3, 20, 0, DateTimeKind.Utc)),
            Row("b-4", " gpt-4 ", true, new DateTime(2026, 1, 2, 3, 30, 0, DateTimeKind.Utc))
        };

        var report = await QueryAsync(rows, FromUtc, ToUtc);

        report.Buckets.Should().ContainSingle();
        var bucket = report.Buckets[0];
        bucket.Successes.Should().Be(4);
        bucket.SuccessesByModel.Should().HaveCount(2);
        bucket.SuccessesByModel[""].Should().Be(3);
        // 非空白模型保留原始名称，不 Trim，也不与其他大小写变体合并。
        bucket.SuccessesByModel[" gpt-4 "].Should().Be(1);
        bucket.SuccessesByModel.Keys.Should().Equal("", " gpt-4 ");
    }

    [TestMethod]
    public async Task BucketsStayIsolatedAndFailureOnlyBucketReturnsEmptyDictionary()
    {
        var rows = new List<RequestLogEntity>
        {
            Row("c-1", "gpt-4", true, new DateTime(2026, 1, 2, 3, 5, 0, DateTimeKind.Utc)),
            Row("c-2", "claude-3", false, new DateTime(2026, 1, 2, 4, 5, 0, DateTimeKind.Utc)),
            Row("c-3", "claude-3", false, new DateTime(2026, 1, 2, 4, 10, 0, DateTimeKind.Utc))
        };

        var report = await QueryAsync(rows, FromUtc, ToUtc);

        report.Buckets.Should().HaveCount(2);
        report.Buckets[0].SuccessesByModel.Keys.Should().Equal("gpt-4");
        report.Buckets[1].Requests.Should().Be(2);
        report.Buckets[1].Successes.Should().Be(0);
        report.Buckets[1].SuccessesByModel.Should().NotBeNull();
        report.Buckets[1].SuccessesByModel.Should().BeEmpty();
    }

    private static RequestLogEntity Row(string id, string? model, bool success, DateTime createdAtUtc) => new()
    {
        Id = id,
        TraceId = $"trace-{id}",
        Platform = "test-platform",
        Model = model,
        Success = success,
        StatusCode = success ? 200 : 500,
        DurationMs = 100,
        CreatedAtUtc = createdAtUtc
    };

    private static async Task<AnalyticsReport> QueryAsync(
        List<RequestLogEntity> rows,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "test-data");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"analytics-{Guid.NewGuid():N}.db");
        var database = new SqlSugarDatabase(
            Options.Create(new DatabaseOptions { Path = path }),
            NullLogger<SqlSugarDatabase>.Instance);

        try
        {
            new DatabaseInitializer(database, NullLogger<DatabaseInitializer>.Instance).Initialize();
            using (var db = database.CreateClient())
            {
                db.Insertable(rows).ExecuteCommand();
            }

            var service = new AnalyticsService(database);
            return await service.QueryAsync(new AnalyticsQuery(fromUtc, toUtc, Bucket: "hour"));
        }
        finally
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException)
            {
                // SqlSugar 可能会一直持有 SQLite 句柄，直到进程级连接池被回收。
            }
        }
    }
}
