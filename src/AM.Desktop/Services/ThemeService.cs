using System.Windows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace AM.Desktop.Services;

/// <summary>界面主题，全局设置中只提供这三项。</summary>
public enum AppTheme
{
    Light,
    Dark,
    System,
}

/// <summary>
/// 应用主题切换。设置的持久化未定，当前只在内存中，重启后回到默认值。
/// </summary>
public sealed class ThemeService
{
    public const AppTheme Default = AppTheme.Light;

    private Window? _watchedWindow;

    public AppTheme Current { get; private set; } = Default;

    public void Apply(AppTheme theme, Window window)
    {
        if (_watchedWindow is not null)
        {
            SystemThemeWatcher.UnWatch(_watchedWindow);
            _watchedWindow = null;
        }

        switch (theme)
        {
            case AppTheme.Light:
                ApplicationThemeManager.Apply(ApplicationTheme.Light, WindowBackdropType.Mica);
                break;
            case AppTheme.Dark:
                ApplicationThemeManager.Apply(ApplicationTheme.Dark, WindowBackdropType.Mica);
                break;
            case AppTheme.System:
                ApplicationThemeManager.ApplySystemTheme();
                SystemThemeWatcher.Watch(window, WindowBackdropType.Mica);
                _watchedWindow = window;
                break;
        }

        Current = theme;
    }
}
