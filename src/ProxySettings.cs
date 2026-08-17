using System.IO;
using System.Text.Json;

namespace CodexProxySwitcher;

public sealed record ProxySettings(string ProxyUrl)
{
    public Uri Uri => new(ProxyUrl, UriKind.Absolute);

    public static bool TryCreate(string? value, out ProxySettings? settings, out string error)
    {
        settings = null;
        error = "请输入有效的本地代理地址。";
        if (!System.Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme is not ("http" or "socks5")) { error = "代理协议必须是 http 或 socks5。"; return false; }
        if (uri.Port is < 1 or > 65535) { error = "代理端口必须介于 1 到 65535。"; return false; }
        if (uri.Host is not ("localhost" or "127.0.0.1" or "::1")) { error = "代理必须指向本机（localhost、127.0.0.1 或 ::1）。"; return false; }
        if (!string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        { error = "代理地址不能包含凭据、路径、查询或片段。"; return false; }
        settings = new ProxySettings(uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped));
        error = "";
        return true;
    }
}

public sealed class ProxySettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public string DirectoryPath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexProxySwitcher");
    public string FilePath => Path.Combine(DirectoryPath, "settings.json");

    public async Task<ProxySettings?> LoadAsync()
    {
        if (!File.Exists(FilePath)) return null;
        try
        {
            var stored = await JsonSerializer.DeserializeAsync<ProxySettings>(File.OpenRead(FilePath), JsonOptions);
            return ProxySettings.TryCreate(stored?.ProxyUrl, out var valid, out _) ? valid : null;
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }

    public async Task SaveAsync(ProxySettings settings)
    {
        Directory.CreateDirectory(DirectoryPath);
        var temporaryPath = FilePath + ".tmp";
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            await JsonSerializer.SerializeAsync(stream, settings, JsonOptions);
        File.Move(temporaryPath, FilePath, true);
    }
}
