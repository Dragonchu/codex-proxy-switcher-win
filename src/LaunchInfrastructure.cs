using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace CodexProxySwitcher;

public interface IProxyReachability
{
    Task<bool> IsReachableAsync(Uri proxy, CancellationToken cancellationToken = default);
}

public sealed class TcpProxyReachability : IProxyReachability
{
    public async Task<bool> IsReachableAsync(Uri proxy, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await client.ConnectAsync(proxy.Host, proxy.Port, timeout.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException) { return false; }
    }
}

public interface ICodexProcessProbe
{
    bool IsCodexRunning();
    bool WaitForCodex(uint processId, TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed class CodexProcessProbe : ICodexProcessProbe
{
    public bool IsCodexRunning()
    {
        var processes = Process.GetProcessesByName("Codex");
        try { return processes.Length > 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    public bool WaitForCodex(uint processId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (processId != 0)
            {
                try { using var process = Process.GetProcessById(checked((int)processId)); if (!process.HasExited) return true; }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { }
            }
            if (IsCodexRunning()) return true;
            if (cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(100))) cancellationToken.ThrowIfCancellationRequested();
        }
        return false;
    }
}

public interface IPackagedAppActivator
{
    uint ActivatePackagedApp(string appUserModelId);
}

public interface ICompatibilityLaunchLock
{
    void Execute(Action action, CancellationToken cancellationToken);
}

public sealed class NamedCompatibilityLaunchLock : ICompatibilityLaunchLock
{
    private const string MutexName = @"Local\CodexProxySwitcher.CompatibilityProxyInjection";
    private static readonly TimeSpan AcquisitionTimeout = TimeSpan.FromSeconds(15);

    public void Execute(Action action, CancellationToken cancellationToken)
    {
        using var mutex = new Mutex(false, MutexName);
        var acquired = false;
        try
        {
            try
            {
                var waitHandles = new[] { mutex, cancellationToken.WaitHandle };
                var index = WaitHandle.WaitAny(waitHandles, AcquisitionTimeout);
                if (index == 1) cancellationToken.ThrowIfCancellationRequested();
                if (index == WaitHandle.WaitTimeout) throw new TimeoutException("Timed out waiting for the compatibility launch lock.");
                acquired = true;
            }
            catch (AbandonedMutexException) { acquired = true; }
            action();
        }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }
}

public enum EnvironmentScope { Process, User }

public interface IEnvironmentAccessor
{
    string? Get(string name, EnvironmentScope scope);
    void Set(string name, string? value, EnvironmentScope scope);
}

public sealed class SystemEnvironmentAccessor : IEnvironmentAccessor
{
    public string? Get(string name, EnvironmentScope scope) => Environment.GetEnvironmentVariable(name, ToTarget(scope));
    public void Set(string name, string? value, EnvironmentScope scope) => Environment.SetEnvironmentVariable(name, value, ToTarget(scope));
    private static EnvironmentVariableTarget ToTarget(EnvironmentScope scope) => scope == EnvironmentScope.User ? EnvironmentVariableTarget.User : EnvironmentVariableTarget.Process;
}

public interface IEnvironmentBroadcaster { void Broadcast(); }

public sealed class EnvironmentChangeBroadcaster : IEnvironmentBroadcaster
{
    private static readonly IntPtr HwndBroadcast = new(0xffff);
    public void Broadcast() => _ = SendMessageTimeout(HwndBroadcast, 0x001A, UIntPtr.Zero, "Environment", 0x0002, 5000, out _);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, UIntPtr wParam, string lParam, uint flags, uint timeout, out UIntPtr result);
}

public interface ICompatibilityEnvironmentLeaseFactory
{
    IDisposable Create(ProxySettings settings);
}

public sealed class CompatibilityEnvironmentLeaseFactory : ICompatibilityEnvironmentLeaseFactory
{
    private static readonly string[] ProxyKeys = ["HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy"];
    private static readonly string[] NoProxyKeys = ["NO_PROXY", "no_proxy"];
    private readonly IEnvironmentAccessor environment;
    private readonly IEnvironmentBroadcaster broadcaster;

    public CompatibilityEnvironmentLeaseFactory(IEnvironmentAccessor environment, IEnvironmentBroadcaster broadcaster)
    {
        this.environment = environment;
        this.broadcaster = broadcaster;
    }

    public IDisposable Create(ProxySettings settings) => new CompatibilityEnvironmentLease(environment, broadcaster, settings);

    private sealed class CompatibilityEnvironmentLease : IDisposable
    {
        private readonly IEnvironmentAccessor environment;
        private readonly IEnvironmentBroadcaster broadcaster;
        private readonly List<(string Name, EnvironmentScope Scope, string? Value)> snapshot = [];
        private bool disposed;

        public CompatibilityEnvironmentLease(IEnvironmentAccessor environment, IEnvironmentBroadcaster broadcaster, ProxySettings settings)
        {
            this.environment = environment;
            this.broadcaster = broadcaster;
            try
            {
                foreach (var scope in new[] { EnvironmentScope.Process, EnvironmentScope.User })
                {
                    foreach (var key in ProxyKeys) SetWithSnapshot(key, settings.ProxyUrl, scope);
                    foreach (var key in NoProxyKeys) SetWithSnapshot(key, "localhost,127.0.0.1,::1", scope);
                }
                broadcaster.Broadcast();
            }
            catch
            {
                Restore();
                throw;
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Restore();
        }

        private void SetWithSnapshot(string key, string value, EnvironmentScope scope)
        {
            snapshot.Add((key, scope, environment.Get(key, scope)));
            environment.Set(key, value, scope);
        }

        private void Restore()
        {
            List<Exception>? failures = null;
            for (var index = snapshot.Count - 1; index >= 0; index--)
            {
                var item = snapshot[index];
                try { environment.Set(item.Name, item.Value, item.Scope); }
                catch (Exception ex) { (failures ??= []).Add(ex); }
            }
            try { broadcaster.Broadcast(); }
            catch (Exception ex) { (failures ??= []).Add(ex); }
            if (failures is not null) throw new AggregateException("Failed to restore compatibility proxy environment.", failures);
        }
    }
}

public interface ICompatibilityProxyInjection
{
    Task LaunchAsync(CodexInstallation installation, ProxySettings settings, CancellationToken cancellationToken);
}

public interface ICompatibilityLaunchDelay
{
    void Wait(TimeSpan duration, CancellationToken cancellationToken);
}

public sealed class CompatibilityLaunchDelay : ICompatibilityLaunchDelay
{
    public void Wait(TimeSpan duration, CancellationToken cancellationToken)
    {
        if (cancellationToken.WaitHandle.WaitOne(duration)) cancellationToken.ThrowIfCancellationRequested();
    }
}

public sealed class CompatibilityProxyInjection : ICompatibilityProxyInjection
{
    private static readonly TimeSpan ProcessAppearanceTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SuccessfulLaunchLease = TimeSpan.FromMilliseconds(1500);
    private readonly ICompatibilityLaunchLock launchLock;
    private readonly ICompatibilityEnvironmentLeaseFactory leaseFactory;
    private readonly IPackagedAppActivator activator;
    private readonly ICodexProcessProbe processProbe;
    private readonly IProxyReachability proxyReachability;
    private readonly ICompatibilityLaunchDelay launchDelay;

    public CompatibilityProxyInjection(ICompatibilityLaunchLock launchLock, ICompatibilityEnvironmentLeaseFactory leaseFactory, IPackagedAppActivator activator, ICodexProcessProbe processProbe, IProxyReachability proxyReachability, ICompatibilityLaunchDelay? launchDelay = null)
    {
        this.launchLock = launchLock;
        this.leaseFactory = leaseFactory;
        this.activator = activator;
        this.processProbe = processProbe;
        this.proxyReachability = proxyReachability;
        this.launchDelay = launchDelay ?? new CompatibilityLaunchDelay();
    }

    public Task LaunchAsync(CodexInstallation installation, ProxySettings settings, CancellationToken cancellationToken) => Task.Run(() =>
    {
        launchLock.Execute(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (processProbe.IsCodexRunning()) throw new InvalidOperationException("Codex is already running.");
            if (!proxyReachability.IsReachableAsync(settings.Uri, cancellationToken).GetAwaiter().GetResult())
                throw new InvalidOperationException($"Local proxy is unavailable: {settings.ProxyUrl}");
            using var lease = leaseFactory.Create(settings);
            var processId = activator.ActivatePackagedApp(installation.AppUserModelId ?? throw new InvalidOperationException("Packaged app identity is missing."));
            if (!processProbe.WaitForCodex(processId, ProcessAppearanceTimeout, cancellationToken))
                throw new TimeoutException("Codex did not appear before the compatibility launch timeout.");
            launchDelay.Wait(SuccessfulLaunchLease, cancellationToken);
        }, cancellationToken);
    }, cancellationToken);
}

public interface IDirectProcessLauncher { void Launch(CodexInstallation installation, ProxySettings settings); }

public interface IProcessStarter { bool Start(ProcessStartInfo startInfo); }

public sealed class SystemProcessStarter : IProcessStarter
{
    public bool Start(ProcessStartInfo startInfo)
    {
        using var process = Process.Start(startInfo);
        return process is not null;
    }
}

public sealed class DirectProcessLauncher : IDirectProcessLauncher
{
    private readonly IProcessStarter processStarter;

    public DirectProcessLauncher(IProcessStarter? processStarter = null) => this.processStarter = processStarter ?? new SystemProcessStarter();

    public void Launch(CodexInstallation installation, ProxySettings settings)
    {
        var startInfo = new ProcessStartInfo(installation.ExecutablePath) { UseShellExecute = false };
        foreach (var key in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy" }) startInfo.Environment[key] = settings.ProxyUrl;
        startInfo.Environment["NO_PROXY"] = "localhost,127.0.0.1,::1";
        startInfo.Environment["no_proxy"] = "localhost,127.0.0.1,::1";
        if (!processStarter.Start(startInfo)) throw new InvalidOperationException("Windows did not create the Codex process.");
    }
}
