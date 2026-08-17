using System.IO;
using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace CodexProxySwitcher;

public sealed record CodexInstallation(string ExecutablePath, string? AppUserModelId);

public sealed class WindowsInterop
{
    private const string CodexPackageFamilyName = "OpenAI.Codex_2p2nqsd0c76g0";

    public CodexInstallation? FindCodex()
    {
        var packageNames = FindPackageNames(CodexPackageFamilyName);
        foreach (var packageName in packageNames)
        {
            var packagePath = GetPackagePath(packageName);
            if (packagePath is null) continue;
            var executablePath = Path.Combine(packagePath, "app", "Codex.exe");
            if (!File.Exists(executablePath)) continue;
            var appId = ReadApplicationId(packagePath) ?? "App";
            return new CodexInstallation(executablePath, $"{CodexPackageFamilyName}!{appId}");
        }

        var localAppDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Codex", "Codex.exe");
        return File.Exists(localAppDataPath) ? new CodexInstallation(localAppDataPath, null) : null;
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

    private static IReadOnlyList<string> FindPackageNames(string familyName)
    {
        uint count = 0, bufferLength = 0;
        var result = FindPackagesByPackageFamily(familyName, 0, ref count, IntPtr.Zero, ref bufferLength, IntPtr.Zero, IntPtr.Zero);
        if (result == 15700) return Array.Empty<string>(); // APPMODEL_ERROR_NO_PACKAGE
        if (result != 122) Marshal.ThrowExceptionForHR(HResultFromWin32(result)); // ERROR_INSUFFICIENT_BUFFER

        var namesMemory = Marshal.AllocHGlobal(checked((int)count * IntPtr.Size));
        var bufferMemory = Marshal.AllocHGlobal(checked((int)bufferLength * sizeof(char)));
        try
        {
            result = FindPackagesByPackageFamily(familyName, 0, ref count, namesMemory, ref bufferLength, bufferMemory, IntPtr.Zero);
            if (result != 0) Marshal.ThrowExceptionForHR(HResultFromWin32(result));
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

    private static string? GetPackagePath(string packageName)
    {
        uint length = 0;
        var result = GetStagedPackagePathByFullName(packageName, ref length, null);
        if (result != 122) return null;
        var buffer = new char[length];
        result = GetStagedPackagePathByFullName(packageName, ref length, buffer);
        return result == 0 ? new string(buffer, 0, (int)length - 1) : null;
    }

    private static string? ReadApplicationId(string packagePath)
    {
        try
        {
            var manifest = XDocument.Load(Path.Combine(packagePath, "AppxManifest.xml"));
            return manifest.Descendants().FirstOrDefault(element => element.Name.LocalName == "Application")?.Attribute("Id")?.Value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException) { return null; }
    }

    private static int HResultFromWin32(int error) => error <= 0 ? error : unchecked((int)(0x80070000u | (uint)error));

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int FindPackagesByPackageFamily(string packageFamilyName, uint packageFilters, ref uint count, IntPtr packageFullNames, ref uint bufferLength, IntPtr buffer, IntPtr packageProperties);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetStagedPackagePathByFullName(string packageFullName, ref uint pathLength, [Out] char[]? path);

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
