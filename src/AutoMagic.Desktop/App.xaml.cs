using System.Threading;
using System.Windows;
using AutoMagic.Application.ExchangeRates;
using AutoMagic.Application.Search;
using AutoMagic.Desktop.ViewModels;
using AutoMagic.Infrastructure.Bridge;
using AutoMagic.Infrastructure.ExchangeRates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AutoMagic.Desktop;

public partial class App : System.Windows.Application
{
    private IHost? _host;
    private Mutex? _singleInstance;

    protected override async void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(true, @"Local\AutoMagic.Desktop", out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show("Auto Magic 已经在运行。", "Auto Magic");
            Shutdown();
            return;
        }

        var builder = Host.CreateApplicationBuilder(e.Args);
        builder.Services.AddSingleton<DesktopBridgeService>();
        builder.Services.AddSingleton<ISearchBridge>(provider =>
            provider.GetRequiredService<DesktopBridgeService>());
        builder.Services.AddHostedService(provider =>
            provider.GetRequiredService<DesktopBridgeService>());
        builder.Services.AddHttpClient<IExchangeRateService, ExchangeRateApiService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("AutoMagic/0.1");
        });
        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton<MainWindow>();

        _host = builder.Build();
        await _host.StartAsync();

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Show();
        base.OnStartup(e);
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _host.StopAsync(timeout.Token);
            _host.Dispose();
        }

        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
