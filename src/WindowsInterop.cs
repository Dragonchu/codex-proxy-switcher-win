using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace CodexProxySwitcher;

public sealed record CodexInstallation(string ExecutablePath, string? AppUserModelId);
public enum CodexDiscoveryStatus { Found, NotFound }

public sealed record CodexDiscoveryResult(CodexDiscoveryStatus Status, CodexInstallation? Installation, string DiagnosticReason)
{
    public static CodexDiscoveryResult Found(CodexInstallation installation, string diagnostic = "") => new(CodexDiscoveryStatus.Found, installation, diagnostic);
    public static CodexDiscoveryResult NotFound(string diagnostic) => new(CodexDiscoveryStatus.NotFound, null, diagnostic);
}

public interface IPackageIdentityApi
{
    IReadOnlyList<string> FindPackageNames(string familyName, uint packageFilter);
    string? GetStagedPackagePath(string packageFullName);
}

public interface IDiscoveryFileSystem
{
    bool FileExists(string path);
    string? ReadApplicationId(string packagePath);
}

public sealed class WindowsInterop
{
    public const uint PackageFilterHead = 0x00000010;
    private const string CodexPackageFamilyName = "OpenAI.Codex_2p2nqsd0c76g0";
    private readonly IPackageIdentityApi packageApi;
    private readonly IDiscoveryFileSystem fileSystem;
    private readonly string localAppData;

    public WindowsInterop(IPackageIdentityApi? packageApi = null, IDiscoveryFileSystem? fileSystem = null, string? localAppData = null)
    {
        this.packageApi = packageApi ?? new NativePackageIdentityApi();
        this.fileSystem = fileSystem ?? new DiscoveryFileSystem();
        this.localAppData = localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    }

    public CodexDiscoveryResult DiscoverCodex()
    {
        var diagnostics = new List<string>();
        try
        {
            var packageNames = packageApi.FindPackageNames(CodexPackageFamilyName, PackageFilterHead);
            foreach (var packageName in packageNames)
            {
                try
                {
                    var packagePath = packageApi.GetStagedPackagePath(packageName);
                    if (string.IsNullOrWhiteSpace(packagePath)) { diagnostics.Add($"包路径不可用：{packageName}"); continue; }
                    var executablePath = Path.Combine(packagePath, "app", "Codex.exe");
                    if (!fileSystem.FileExists(executablePath)) { diagnostics.Add($"包内未找到 Codex.exe：{packageName}"); continue; }
                    var appId = fileSystem.ReadApplicationId(packagePath);
                    if (string.IsNullOrWhiteSpace(appId)) { diagnostics.Add($"包清单缺少 Application Id：{packageName}"); continue; }
                    return CodexDiscoveryResult.Found(new CodexInstallation(executablePath, $"{CodexPackageFamilyName}!{appId}"), JoinDiagnostics(diagnostics));
                }
                catch (Exception ex) { diagnostics.Add($"解析包 {packageName} 失败：{ex.Message}"); }
            }
        }
        catch (Exception ex) { diagnostics.Add($"查询 Store/MSIX 包失败：{ex.Message}"); }

        try
        {
            var executablePath = Path.Combine(localAppData, "Programs", "Codex", "Codex.exe");
            if (fileSystem.FileExists(executablePath))
                return CodexDiscoveryResult.Found(new CodexInstallation(executablePath, null), JoinDiagnostics(diagnostics));
            diagnostics.Add("未找到非打包版 Codex.exe。");
        }
        catch (Exception ex) { diagnostics.Add($"查询非打包安装失败：{ex.Message}"); }

        return CodexDiscoveryResult.NotFound(JoinDiagnostics(diagnostics));
    }

    public void ActivatePackagedApp(string appUserModelId)
    {
        object instance = new ApplicationActivationManager();
        try
        {
            var result = ((IApplicationActivationManager)instance).ActivateApplication(appUserModelId, null, ActivateOptions.None, out _);
            Marshal.ThrowExceptionForHR(result);
        }
        finally { Marshal.FinalReleaseComObject(instance); }
    }

    private static string JoinDiagnostics(IEnumerable<string> diagnostics) => string.Join(" ", diagnostics);
    [Flags] private enum ActivateOptions { None = 0 }

    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private sealed class ApplicationActivationManager { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("2e941141-7f97-4756-ba1d-9decde894a3d")]
    private interface IApplicationActivationManager
    {
        [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, [MarshalAs(UnmanagedType.LPWStr)] string? arguments, ActivateOptions options, out uint processId);
        [PreserveSig] int ActivateForFile(IntPtr appUserModelId, IntPtr itemArray, string verb, out uint processId);
        [PreserveSig] int ActivateForProtocol(IntPtr appUserModelId, IntPtr itemArray, out uint processId);
    }
}

internal sealed class NativePackageIdentityApi : IPackageIdentityApi
{
    public IReadOnlyList<string> FindPackageNames(string familyName, uint packageFilter)
    {
        uint count = 0, bufferLength = 0;
        var result = FindPackagesByPackageFamily(familyName, packageFilter, ref count, IntPtr.Zero, ref bufferLength, IntPtr.Zero, IntPtr.Zero);
        if (result == 15700) return Array.Empty<string>();
        if (result != 122) throw new Win32Exception(result);

        var namesMemory = Marshal.AllocHGlobal(checked((int)count * IntPtr.Size));
        var bufferMemory = Marshal.AllocHGlobal(checked((int)bufferLength * sizeof(char)));
        try
        {
            result = FindPackagesByPackageFamily(familyName, packageFilter, ref count, namesMemory, ref bufferLength, bufferMemory, IntPtr.Zero);
            if (result != 0) throw new Win32Exception(result);
            var names = new string[count];
            for (var index = 0; index < count; index++)
                names[index] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(namesMemory, checked((int)index * IntPtr.Size)))!;
            return names.OrderByDescending(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        finally
        {
            Marshal.FreeHGlobal(bufferMemory);
            Marshal.FreeHGlobal(namesMemory);
        }
    }

    public string? GetStagedPackagePath(string packageFullName)
    {
        uint length = 0;
        var result = GetStagedPackagePathByFullName(packageFullName, ref length, null);
        if (result != 122) return null;
        var buffer = new char[length];
        result = GetStagedPackagePathByFullName(packageFullName, ref length, buffer);
        return result == 0 ? new string(buffer, 0, (int)length - 1) : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int FindPackagesByPackageFamily(string packageFamilyName, uint packageFilters, ref uint count, IntPtr packageFullNames, ref uint bufferLength, IntPtr buffer, IntPtr packageProperties);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetStagedPackagePathByFullName(string packageFullName, ref uint pathLength, [Out] char[]? path);
}

internal sealed class DiscoveryFileSystem : IDiscoveryFileSystem
{
    public bool FileExists(string path) => File.Exists(path);
    public string? ReadApplicationId(string packagePath)
    {
        var manifest = XDocument.Load(Path.Combine(packagePath, "AppxManifest.xml"));
        return manifest.Descendants().FirstOrDefault(element => element.Name.LocalName == "Application")?.Attribute("Id")?.Value;
    }
}
