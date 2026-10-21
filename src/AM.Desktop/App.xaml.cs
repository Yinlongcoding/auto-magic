using System.Windows;
using AM.Desktop.Services;

namespace AM.Desktop;

public partial class App : Application
{
    public static ThemeService Theme { get; } = new();

    /// <summary>采集服务随应用启动，监听插件连接；退出时关闭管道，进行中的采集按中断处理。</summary>
    public static CollectionService Collection { get; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Collection.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Collection.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.OnExit(e);
    }
}
