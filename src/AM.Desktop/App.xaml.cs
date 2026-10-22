using System.Windows;
using AM.Desktop.Services;
using Wpf.Ui;

namespace AM.Desktop;

public partial class App : Application
{
    public static Services.ThemeService Theme { get; } = new();

    /// <summary>全局提示条，由 MainWindow 提供 presenter；页面可能比窗口先创建。</summary>
    public static ISnackbarService Snackbar { get; } = new SnackbarService();

    /// <summary>采集服务随应用启动，监听插件连接；退出时关闭管道，进行中的采集按中断处理。</summary>
    public static CollectionService Collection { get; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 在这里订阅一次：页面每次导航都会重建，放在页面里会重复弹提示。
        Collection.FileSaved += (title, path) => Snackbar.Show(
            title, path, Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(8));
        Collection.FileSaveFailed += (title, message) => Snackbar.Show(
            title, message, Wpf.Ui.Controls.ControlAppearance.Danger, null, TimeSpan.FromSeconds(8));
        Collection.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Collection.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.OnExit(e);
    }
}
