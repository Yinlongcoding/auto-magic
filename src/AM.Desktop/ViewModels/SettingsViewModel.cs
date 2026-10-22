using AM.Desktop.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AM.Desktop.ViewModels;

/// <summary>
/// 设置：系统设置（主题）和个人设置（Ozon、AI、OSS）。
/// 主题即时生效但未持久化。Ozon 已接入：保存后拉一次类目树，既是测试连接，也是更新缓存。AI、OSS 仍只是界面。
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    public SettingsViewModel()
    {
        var saved = SettingsStore.LoadOzon();
        _clientId = saved.ClientId;
        _selectedCurrency = saved.CurrencyCode;
        _hasSavedApiKey = saved.ApiKey.Length > 0;
        _ozonStatus = CategoryStatus();
    }

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
    private string _selectedCurrency;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveOzonCommand))]
    private string _clientId;

    /// <summary>密码框的内容，由页面代码同步。留空表示沿用已保存的密钥。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveOzonCommand))]
    private string _apiKeyInput = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ApiKeyPlaceholder))]
    [NotifyCanExecuteChangedFor(nameof(SaveOzonCommand))]
    private bool _hasSavedApiKey;

    [ObservableProperty]
    private string _ozonStatus;

    [ObservableProperty]
    private bool _ozonFailed;

    public string ApiKeyPlaceholder => HasSavedApiKey ? "已保存（留空则不修改）" : "粘贴 API Key";

    /// <summary>保存后让页面清空密码框。</summary>
    public event Action? ApiKeyCleared;

    private bool CanSaveOzon() => ClientId.Trim().Length > 0 && (ApiKeyInput.Trim().Length > 0 || HasSavedApiKey);

    /// <summary>保存后立即拉类目树。凭据先落盘，拉取失败也保留，方便改正后重试。</summary>
    [RelayCommand(CanExecute = nameof(CanSaveOzon))]
    private async Task SaveOzonAsync()
    {
        var apiKey = ApiKeyInput.Trim().Length > 0 ? ApiKeyInput.Trim() : SettingsStore.LoadOzon().ApiKey;
        var settings = new OzonSettings(ClientId.Trim(), apiKey, SelectedCurrency);
        SettingsStore.SaveOzon(settings);
        HasSavedApiKey = true;
        ApiKeyCleared?.Invoke();

        OzonFailed = false;
        OzonStatus = "已保存，正在连接 Ozon 并拉取类目树…";
        try
        {
            await CategoryCache.RefreshAsync(settings);
            OzonStatus = "连接成功。" + CategoryStatus();
        }
        catch (OzonApiException error)
        {
            OzonFailed = true;
            OzonStatus = $"已保存，但连接失败：{error.Message}";
        }
    }

    private static string CategoryStatus() => CategoryCache.UpdatedAt is { } at
        ? $"类目缓存：{CategoryCache.Categories.Count} 个类目、{CategoryCache.TypeCount} 个类型，更新于 {at:yyyy-MM-dd HH:mm}。"
        : "还没有类目缓存。";

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
