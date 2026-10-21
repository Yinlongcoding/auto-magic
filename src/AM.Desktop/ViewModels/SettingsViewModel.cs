using AM.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AM.Desktop.ViewModels;

/// <summary>
/// 设置：系统设置（主题）和个人设置（Ozon、AI、OSS）。
/// 主题即时生效但未持久化；个人设置只是界面，未保存、未接测试连接。
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    public bool IsLight
    {
        get => App.Theme.Current == AppTheme.Light;
        set { if (value) SetTheme(AppTheme.Light); }
    }

    public bool IsDark
    {
        get => App.Theme.Current == AppTheme.Dark;
        set { if (value) SetTheme(AppTheme.Dark); }
    }

    public bool IsSystem
    {
        get => App.Theme.Current == AppTheme.System;
        set { if (value) SetTheme(AppTheme.System); }
    }

    public IReadOnlyList<string> Currencies { get; } = ["CNY", "RUB", "USD"];

    [ObservableProperty]
    private string _selectedCurrency = "CNY";

    private void SetTheme(AppTheme theme)
    {
        if (System.Windows.Application.Current.MainWindow is { } window)
        {
            App.Theme.Apply(theme, window);
        }

        OnPropertyChanged(nameof(IsLight));
        OnPropertyChanged(nameof(IsDark));
        OnPropertyChanged(nameof(IsSystem));
    }
}
