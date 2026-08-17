using System.Diagnostics;
using CodexProxySwitcher;
using Xunit;

namespace CodexProxySwitcher.Tests;

public sealed class CompatibilityInjectionTests
{
    private static readonly ProxySettings Settings = new("http://127.0.0.1:7890");
    private static readonly CodexInstallation StoreInstallation = new("C:\\WindowsApps\\Codex.exe", "OpenAI.Codex_test!App");

    [Fact]
    public async Task StoreLaunchUsesUpperAndLowerCaseProxyEnvironment()
    {
        var environment = new FakeEnvironment();
        var broadcaster = new FakeBroadcaster();
        var activator = new FakeActivator(() =>
        {
            foreach (var scope in new[] { EnvironmentScope.Process, EnvironmentScope.User })
            {
                foreach (var key in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy" })
                    Assert.Equal(Settings.ProxyUrl, environment.Get(key, scope));
                Assert.Equal("localhost,127.0.0.1,::1", environment.Get("NO_PROXY", scope));
                Assert.Equal("localhost,127.0.0.1,::1", environment.Get("no_proxy", scope));
            }
        });
        var injection = CreateInjection(environment, broadcaster, activator, out _);

        await injection.LaunchAsync(StoreInstallation, Settings, CancellationToken.None);

        Assert.Equal(2, broadcaster.Count);
        Assert.All(environment.Values, pair => Assert.Null(pair.Value));
    }

    [Fact]
    public async Task ActivationFailureRestoresEnvironmentAndCleansLockAndLease()
    {
        var environment = new FakeEnvironment();
        environment.SeedAll("before");
        var broadcaster = new FakeBroadcaster();
        var activator = new FakeActivator(() => throw new InvalidOperationException("activation failed"));
        var injection = CreateInjection(environment, broadcaster, activator, out var launchLock);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => injection.LaunchAsync(StoreInstallation, Settings, CancellationToken.None));

        Assert.Equal("activation failed", error.Message);
        Assert.True(launchLock.Entered);
        Assert.True(launchLock.Exited);
        Assert.Equal(2, broadcaster.Count);
        Assert.All(environment.Values, pair => Assert.Equal("before", pair.Value));
    }

    [Fact]
    public void DirectLaunchUsesOnlyChildProcessEnvironment()
    {
        var starter = new FakeProcessStarter();
        var launcher = new DirectProcessLauncher(starter);

        launcher.Launch(new CodexInstallation("C:\\Programs\\Codex.exe", null), Settings);

        Assert.NotNull(starter.StartInfo);
        Assert.False(starter.StartInfo.UseShellExecute);
        Assert.Equal(Settings.ProxyUrl, starter.StartInfo.Environment["HTTP_PROXY"]);
        Assert.Equal(Settings.ProxyUrl, starter.StartInfo.Environment["http_proxy"]);
        Assert.Equal("localhost,127.0.0.1,::1", starter.StartInfo.Environment["NO_PROXY"]);
        Assert.Empty(starter.UserEnvironmentWrites);
    }

    private static CompatibilityProxyInjection CreateInjection(FakeEnvironment environment, FakeBroadcaster broadcaster, FakeActivator activator, out FakeLaunchLock launchLock)
    {
        launchLock = new FakeLaunchLock();
        return new CompatibilityProxyInjection(
            launchLock,
            new CompatibilityEnvironmentLeaseFactory(environment, broadcaster),
            activator,
            new FakeProcessProbe(),
            new ReachableProxy(),
            new NoDelay());
    }

    private sealed class FakeEnvironment : IEnvironmentAccessor
    {
        public Dictionary<(string Name, EnvironmentScope Scope), string?> Values { get; } = [];
        public string? Get(string name, EnvironmentScope scope) => Values.GetValueOrDefault((name, scope));
        public void Set(string name, string? value, EnvironmentScope scope) => Values[(name, scope)] = value;
        public void SeedAll(string value)
        {
            foreach (var scope in new[] { EnvironmentScope.Process, EnvironmentScope.User })
                foreach (var key in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY", "http_proxy", "https_proxy", "all_proxy", "no_proxy" })
                    Values[(key, scope)] = value;
        }
    }

    private sealed class FakeBroadcaster : IEnvironmentBroadcaster
    {
        public int Count { get; private set; }
        public void Broadcast() => Count++;
    }

    private sealed class FakeActivator(Action onActivate) : IPackagedAppActivator
    {
        public uint ActivatePackagedApp(string appUserModelId) { onActivate(); return 42; }
    }

    private sealed class FakeProcessProbe : ICodexProcessProbe
    {
        public bool IsCodexRunning() => false;
        public bool WaitForCodex(uint processId, TimeSpan timeout, CancellationToken cancellationToken) => true;
    }

    private sealed class FakeLaunchLock : ICompatibilityLaunchLock
    {
        public bool Entered { get; private set; }
        public bool Exited { get; private set; }
        public void Execute(Action action, CancellationToken cancellationToken)
        {
            Entered = true;
            try { action(); }
            finally { Exited = true; }
        }
    }

    private sealed class NoDelay : ICompatibilityLaunchDelay
    {
        public void Wait(TimeSpan duration, CancellationToken cancellationToken) { }
    }

    private sealed class ReachableProxy : IProxyReachability
    {
        public Task<bool> IsReachableAsync(Uri proxy, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class FakeProcessStarter : IProcessStarter
    {
        public ProcessStartInfo? StartInfo { get; private set; }
        public List<string> UserEnvironmentWrites { get; } = [];
        public bool Start(ProcessStartInfo startInfo) { StartInfo = startInfo; return true; }
    }
}
