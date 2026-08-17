using CodexProxySwitcher;
using Xunit;

namespace CodexProxySwitcher.Tests;

public sealed class DiscoveryTests
{
    [Fact]
    public void DiscoveryUsesDocumentedHeadPackageFilter()
    {
        var packages = new FakePackageApi();
        var result = new WindowsInterop(packages, new FakeFileSystem(), "C:\\LocalAppData").DiscoverCodex();

        Assert.Equal(CodexDiscoveryStatus.NotFound, result.Status);
        Assert.Equal(WindowsInterop.PackageFilterHead, packages.ObservedFilter);
        Assert.Equal(0x00000010u, packages.ObservedFilter);
    }

    [Fact]
    public void PackageQueryFailureBecomesNotFoundWithDiagnostic()
    {
        var packages = new FakePackageApi { QueryException = new InvalidOperationException("native query failed") };
        var result = new WindowsInterop(packages, new FakeFileSystem(), "C:\\LocalAppData").DiscoverCodex();

        Assert.Equal(CodexDiscoveryStatus.NotFound, result.Status);
        Assert.Null(result.Installation);
        Assert.Contains("native query failed", result.DiagnosticReason);
    }

    [Fact]
    public void PackageQueryFailureFallsBackToUnpackagedCodex()
    {
        var packages = new FakePackageApi { QueryException = new InvalidOperationException("native query failed") };
        var files = new FakeFileSystem { ExistingSuffix = "Programs\\Codex\\Codex.exe" };
        var result = new WindowsInterop(packages, files, "C:\\LocalAppData").DiscoverCodex();

        Assert.Equal(CodexDiscoveryStatus.Found, result.Status);
        Assert.NotNull(result.Installation);
        Assert.Null(result.Installation.AppUserModelId);
        Assert.EndsWith("Programs\\Codex\\Codex.exe", result.Installation.ExecutablePath);
        Assert.Contains("native query failed", result.DiagnosticReason);
    }

    private sealed class FakePackageApi : IPackageIdentityApi
    {
        public uint ObservedFilter { get; private set; }
        public Exception? QueryException { get; init; }
        public IReadOnlyList<string> FindPackageNames(string familyName, uint packageFilter)
        {
            ObservedFilter = packageFilter;
            if (QueryException is not null) throw QueryException;
            return Array.Empty<string>();
        }
        public string? GetStagedPackagePath(string packageFullName) => null;
    }

    private sealed class FakeFileSystem : IDiscoveryFileSystem
    {
        public string? ExistingSuffix { get; init; }
        public bool FileExists(string path) => ExistingSuffix is not null && path.EndsWith(ExistingSuffix, StringComparison.OrdinalIgnoreCase);
        public string? ReadApplicationId(string packagePath) => "App";
    }
}
