using System.Diagnostics;
using System.Net.Sockets;

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
    private readonly WindowsInterop windows = new();

    public async Task<LauncherState> GetStateAsync(ProxySettings settings)
    {
        if (IsCodexRunning()) return new(LauncherStateKind.CodexRunning);
        var discovery = windows.DiscoverCodex();
        if (discovery.Status == CodexDiscoveryStatus.NotFound) return LauncherState.NotFound(discovery.DiagnosticReason);
        var installation = discovery.Installation!;
        if (!await IsProxyReachableAsync(settings.Uri)) return new(LauncherStateKind.ProxyUnavailable, installation);
        return new(LauncherStateKind.Ready, installation);
    }

    public Task LaunchAsync(CodexInstallation installation, ProxySettings settings)
    {
        if (IsCodexRunning()) throw new InvalidOperationException("Codex is already running.");

        if (installation.AppUserModelId is not null)
        {
            windows.ActivatePackagedApp(installation.AppUserModelId);
        }
        else
        {
            var startInfo = new ProcessStartInfo(installation.ExecutablePath) { UseShellExecute = false };
            startInfo.Environment["HTTP_PROXY"] = settings.ProxyUrl;
            startInfo.Environment["HTTPS_PROXY"] = settings.ProxyUrl;
            startInfo.Environment["ALL_PROXY"] = settings.ProxyUrl;
            startInfo.Environment["NO_PROXY"] = "localhost,127.0.0.1,::1";
            _ = Process.Start(startInfo) ?? throw new InvalidOperationException("Windows did not create the Codex process.");
        }
        return Task.CompletedTask;
    }

    private static bool IsCodexRunning() => Process.GetProcessesByName("Codex").Length > 0;

    private static async Task<bool> IsProxyReachableAsync(Uri proxy)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await client.ConnectAsync(proxy.Host, proxy.Port, timeout.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException) { return false; }
    }
}
