using System.Windows;

namespace CodexProxySwitcher;

public partial class MainWindow : Window
{
    private readonly ProxySettingsStore settingsStore = new();
    private readonly LauncherService launcherService = new();
    private ProxySettings? settings;
    private LauncherState state = LauncherState.NotFound();
    private bool editingSettings;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        SetBusy("正在检查…", "正在检查 Codex 和本地代理。");
        settings = await settingsStore.LoadAsync();
        if (settings is null) { ShowSettings(true); return; }
        state = await launcherService.GetStateAsync(settings);
        RenderState();
    }

    private void RenderState()
    {
        editingSettings = false;
        SettingsPanel.Visibility = Visibility.Collapsed;
        SettingsButton.Visibility = Visibility.Visible;
        PrimaryButton.IsEnabled = true;
        switch (state.Kind)
        {
            case LauncherStateKind.Ready:
                StatusText.Text = "已准备就绪";
                DetailText.Text = $"本地代理 {settings!.ProxyUrl} 可用。";
                SetPrimary("启动 Codex", true);
                break;
            case LauncherStateKind.CodexRunning:
                StatusText.Text = "Codex 正在运行。请退出 Codex 后再启动。";
                DetailText.Text = "启动器不会关闭或重新启动现有 Codex 进程。";
                SetPrimary("", false);
                break;
            case LauncherStateKind.ProxyUnavailable:
                StatusText.Text = "本地代理不可用";
                DetailText.Text = $"无法连接 {settings!.ProxyUrl}。请启动代理服务或修改设置。";
                SetPrimary("重新检查", true);
                break;
            case LauncherStateKind.CodexNotFound:
                StatusText.Text = "未找到 Codex";
                DetailText.Text = "请先安装 Windows 版 Codex，然后重新检查。";
                SetPrimary("重新检查", true);
                break;
            case LauncherStateKind.LaunchFailed:
                StatusText.Text = $"启动失败（{state.Error!.Code}）";
                DetailText.Text = state.Error.StackTrace;
                SetPrimary("重试", true);
                break;
        }
    }

    private async void PrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        if (editingSettings) { await SaveSettingsAsync(); return; }
        if (state.Kind is not (LauncherStateKind.Ready or LauncherStateKind.LaunchFailed)) { await RefreshAsync(); return; }
        PrimaryButton.IsEnabled = false;
        try { await launcherService.LaunchAsync(state.Installation!, settings!); Close(); }
        catch (Exception ex) { state = LauncherState.Failed(state.Installation!, LaunchError.FromException(ex)); RenderState(); }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => ShowSettings(settings is null);

    private void ShowSettings(bool firstRun)
    {
        editingSettings = true;
        StatusText.Text = firstRun ? "设置本地代理" : "代理设置";
        DetailText.Text = firstRun ? "首次使用前，请填写正在运行的本地代理地址。" : "保存后将重新检查代理和 Codex。";
        ProxyUrlTextBox.Text = settings?.ProxyUrl ?? "http://127.0.0.1:7890";
        SettingsPanel.Visibility = Visibility.Visible;
        SettingsButton.Visibility = Visibility.Collapsed;
        SetPrimary("保存并检查", true);
        ProxyUrlTextBox.Focus(); ProxyUrlTextBox.SelectAll();
    }

    private async Task SaveSettingsAsync()
    {
        if (!ProxySettings.TryCreate(ProxyUrlTextBox.Text, out var candidate, out var error)) { StatusText.Text = "代理地址无效"; DetailText.Text = error; return; }
        await settingsStore.SaveAsync(candidate!); settings = candidate; await RefreshAsync();
    }

    private void SetBusy(string status, string detail)
    {
        StatusText.Text = status; DetailText.Text = detail;
        SettingsPanel.Visibility = Visibility.Collapsed; SettingsButton.Visibility = Visibility.Collapsed; SetPrimary("", false);
    }

    private void SetPrimary(string text, bool visible)
    {
        PrimaryButton.Content = text; PrimaryButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }
}
