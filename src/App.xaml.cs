using System.Windows;

namespace CodexProxySwitcher;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            var error = LaunchError.FromException(args.Exception);
            MessageBox.Show($"错误代码：{error.Code}\n\n{error.StackTrace}", "Codex Proxy Switcher", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        base.OnStartup(e);
    }
}
