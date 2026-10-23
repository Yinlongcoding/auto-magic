using System.IO;
using System.Windows;
using AM.Desktop.Data;
using AM.Desktop.Models;
using AM.Desktop.Services;
using Microsoft.EntityFrameworkCore;
using Wpf.Ui;

namespace AM.Desktop;

public partial class App : Application
{
    public static Services.ThemeService Theme { get; } = new();

    /// <summary>全局提示条，由 MainWindow 提供 presenter；页面可能比窗口先创建。</summary>
    public static ISnackbarService Snackbar { get; } = new SnackbarService();

    /// <summary>采集服务随应用启动，监听插件连接；退出时关闭管道，进行中的采集按中断处理。</summary>
    public static CollectionService Collection { get; } = new();

    /// <summary>批次仓库服务。</summary>
    public static BatchRepository BatchRepo { get; private set; } = null!;

    /// <summary>任务记录服务。</summary>
    public static HistoryStore HistoryStore { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 初始化数据库
        InitializeDatabase();

        // 先加载任务记录再加载批次：加载批次时可能追加"中断"记录，要排在已有记录之前且被写库
        LoadFromDatabase();

        // 在这里订阅一次：页面每次导航都会重建，放在页面里会重复弹提示。
        Collection.FileSaved += (title, path) => Snackbar.Show(
            title, path, Wpf.Ui.Controls.ControlAppearance.Success, null, TimeSpan.FromSeconds(8));
        Collection.FileSaveFailed += (title, message) => Snackbar.Show(
            title, message, Wpf.Ui.Controls.ControlAppearance.Danger, null, TimeSpan.FromSeconds(8));
        HistoryStore.SaveFailed += message => Snackbar.Show(
            "任务记录保存失败", message, Wpf.Ui.Controls.ControlAppearance.Danger, null, TimeSpan.FromSeconds(8));
        Collection.Start();
    }

    private static void InitializeDatabase()
    {
        // 数据库文件放在用户目录
        var appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AM");
        Directory.CreateDirectory(appDataPath);

        var dbPath = Path.Combine(appDataPath, "am.db");

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;

        // 建库只在启动时用一次，仓库里每次操作各自新建上下文。
        // EnsureCreated 只在库不存在时建表，不会改已有表。开发阶段表结构变了要手动删 am.db。
        // TODO: 正式版本改用 Migration
        using (var db = new AppDbContext(options))
        {
            db.Database.EnsureCreated();
        }

        BatchRepo = new BatchRepository(options);
        HistoryStore = new HistoryStore(new HistoryRepository(options));
    }

    // 在 UI 线程上 await：数据库读取是异步的不卡界面，await 之后回到 UI 线程，
    // 往绑定在界面上的集合里加数据才安全。
    private static async void LoadFromDatabase()
    {
        try
        {
            await HistoryStore.LoadAsync();
        }
        catch (Exception error)
        {
            Snackbar.Show("任务记录加载失败", error.Message,
                Wpf.Ui.Controls.ControlAppearance.Danger, null, TimeSpan.FromSeconds(8));
        }

        try
        {
            var results = await BatchRepo.LoadAllAsync();
            foreach (var (batch, products) in results)
            {
                BatchStore.Batches.Add(batch);

                // 上次退出时还在采集：按中断处理，和运行中断开插件一致，暂存数据丢弃。
                if (batch.State == BatchState.Collecting)
                {
                    batch.State = BatchState.Interrupted;
                    BatchStore.History.Insert(0, new(DateTime.Now, batch.Keyword, "中断采集，任务结束", "应用已退出，全部丢弃"));
                    Collection.SaveBatch(batch);
                    continue;
                }

                var productCollection = BatchStore.ProductsFor(batch);
                foreach (var product in products)
                {
                    productCollection.Add(product);
                }

                // 匹配结果不落库，按当前规则重算；已完成的批次只读，不用算
                if (batch.State is BatchState.Collected or BatchState.Listing)
                {
                    BatchMatching.Apply(batch);
                }
            }

            if (BatchRepo.SkippedCount > 0)
            {
                Snackbar.Show("部分历史批次未加载", $"{BatchRepo.SkippedCount} 个批次的数据格式已过时，已跳过",
                    Wpf.Ui.Controls.ControlAppearance.Caution, null, TimeSpan.FromSeconds(8));
            }
        }
        catch (Exception error)
        {
            Snackbar.Show("历史批次加载失败", error.Message,
                Wpf.Ui.Controls.ControlAppearance.Danger, null, TimeSpan.FromSeconds(8));
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Collection.Shutdown();
        base.OnExit(e);
    }
}
