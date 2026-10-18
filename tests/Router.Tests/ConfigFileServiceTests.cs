using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Router.Contracts.Host;
using Router.Host.Configuration;
using Router.Host.Security;
using Router.Infrastructure.Persistence;

namespace Router.Tests;

/// <summary>Config/Config.json 中「直连兜底」开关的保存、读回与不误清除。</summary>
[TestClass]
[DoNotParallelize]
public sealed class ConfigFileServiceTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "router2api-config-" + Guid.NewGuid().ToString("N"));
    private readonly string _previousDirectory = Directory.GetCurrentDirectory();
    private ApiKeyService? _apiKeys;

    [TestInitialize]
    public void Initialize()
    {
        Directory.CreateDirectory(_directory);
        Directory.SetCurrentDirectory(_directory);
    }

    [TestCleanup]
    public void Dispose()
    {
        _apiKeys?.Dispose();
        Directory.SetCurrentDirectory(_previousDirectory);
        try { Directory.Delete(_directory, true); }
        catch (IOException) { }
    }

    [TestMethod]
    public async Task AllowDirectFallbackIsPersistedAndReadBack()
    {
        var (service, configuration, configPath) = CreateService();

        var snapshot = await service.SaveAsync(new ConfigUpdateRequest
        {
            Proxy = new ConfigProxyUpdate { AllowDirectFallback = false }
        });

        var document = JsonNode.Parse(await File.ReadAllTextAsync(configPath))!;
        document["Proxy"]!["AllowDirectFallback"]!.GetValue<bool>().Should().BeFalse();
        configuration.GetValue("Proxy:AllowDirectFallback", true).Should().BeFalse("配置文件重载后应读到新值");
        snapshot.Proxy.AllowDirectFallback.Should().BeFalse();
    }

    [TestMethod]
    public async Task OtherProxyUpdatesKeepTheStoredSwitch()
    {
        var (service, _, configPath) = CreateService();
        await service.SaveAsync(new ConfigUpdateRequest
        {
            Proxy = new ConfigProxyUpdate { AllowDirectFallback = false }
        });

        await service.SaveAsync(new ConfigUpdateRequest
        {
            Proxy = new ConfigProxyUpdate { PreferHighScore = false }
        });

        var document = JsonNode.Parse(await File.ReadAllTextAsync(configPath))!;
        document["Proxy"]!["AllowDirectFallback"]!.GetValue<bool>().Should().BeFalse();
        document["Proxy"]!["PreferHighScore"]!.GetValue<bool>().Should().BeFalse();
    }

    private (ConfigFileService Service, IConfigurationRoot Configuration, string ConfigPath) CreateService()
    {
        var configPath = Path.Combine(_directory, "Config", "Config.json");
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(configPath, optional: true, reloadOnChange: false)
            .Build();
        var auth = Monitor(new AuthOptions());
        _apiKeys = new ApiKeyService(auth);
        var proxy = new Mock<IOptionsMonitor<ProxyOptions>>();
        proxy.SetupGet(value => value.CurrentValue).Returns(() => new ProxyOptions
        {
            AllowDirectFallback = configuration.GetValue("Proxy:AllowDirectFallback", true)
        });
        var service = new ConfigFileService(
            configuration,
            auth,
            Monitor(new DatabaseOptions()),
            Monitor(new RedisOptions()),
            Monitor(new LogRetentionOptions()),
            Monitor(new PluginLogOptions()),
            proxy.Object,
            _apiKeys,
            NullLogger<ConfigFileService>.Instance);
        service.EnsureCreated();
        return (service, configuration, configPath);
    }

    private static IOptionsMonitor<T> Monitor<T>(T value) where T : class
    {
        var monitor = new Mock<IOptionsMonitor<T>>();
        monitor.SetupGet(item => item.CurrentValue).Returns(value);
        return monitor.Object;
    }
}
