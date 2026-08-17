namespace CodexProxySwitcher;

public enum LauncherStateKind { Ready, CodexRunning, ProxyUnavailable, CodexNotFound, LaunchFailed }

public sealed record LaunchError(string Code, string StackTrace)
{
    public static LaunchError FromException(Exception exception) => new("CPS-LAUNCH-001", exception.ToString());
}

public sealed record LauncherState(LauncherStateKind Kind, CodexInstallation? Installation = null, LaunchError? Error = null, string DiagnosticReason = "")
{
    public static LauncherState NotFound(string diagnosticReason = "") => new(LauncherStateKind.CodexNotFound, DiagnosticReason: diagnosticReason);
    public static LauncherState Failed(CodexInstallation installation, LaunchError error) => new(LauncherStateKind.LaunchFailed, installation, error);
}

public sealed class LauncherService
{
    private readonly WindowsInterop windows;
    private readonly ICodexProcessProbe processProbe;
    private readonly IProxyReachability proxyReachability;
    private readonly ICompatibilityProxyInjection compatibilityInjection;
    private readonly IDirectProcessLauncher directLauncher;

    public LauncherService()
    {
        windows = new WindowsInterop();
        processProbe = new CodexProcessProbe();
        proxyReachability = new TcpProxyReachability();
        compatibilityInjection = new CompatibilityProxyInjection(
            new NamedCompatibilityLaunchLock(),
            new CompatibilityEnvironmentLeaseFactory(new SystemEnvironmentAccessor(), new EnvironmentChangeBroadcaster()),
            windows,
            processProbe,
            proxyReachability);
        directLauncher = new DirectProcessLauncher();
    }

    public LauncherService(
        WindowsInterop windows,
        ICodexProcessProbe processProbe,
        IProxyReachability proxyReachability,
        ICompatibilityProxyInjection compatibilityInjection,
        IDirectProcessLauncher directLauncher)
    {
        this.windows = windows;
        this.processProbe = processProbe;
        this.proxyReachability = proxyReachability;
        this.compatibilityInjection = compatibilityInjection;
        this.directLauncher = directLauncher;
    }

    public async Task<LauncherState> GetStateAsync(ProxySettings settings)
    {
        if (processProbe.IsCodexRunning()) return new(LauncherStateKind.CodexRunning);
        var discovery = windows.DiscoverCodex();
        if (discovery.Status == CodexDiscoveryStatus.NotFound) return LauncherState.NotFound(discovery.DiagnosticReason);
        var installation = discovery.Installation!;
        if (!await proxyReachability.IsReachableAsync(settings.Uri)) return new(LauncherStateKind.ProxyUnavailable, installation);
        return new(LauncherStateKind.Ready, installation);
    }

    public async Task LaunchAsync(CodexInstallation installation, ProxySettings settings, CancellationToken cancellationToken = default)
    {
        if (processProbe.IsCodexRunning()) throw new InvalidOperationException("Codex is already running.");
        if (!await proxyReachability.IsReachableAsync(settings.Uri, cancellationToken))
            throw new InvalidOperationException($"Local proxy is unavailable: {settings.ProxyUrl}");

        if (installation.AppUserModelId is not null)
            await compatibilityInjection.LaunchAsync(installation, settings, cancellationToken);
        else
            directLauncher.Launch(installation, settings);
    }
}
