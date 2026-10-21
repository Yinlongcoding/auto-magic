using System.Windows;
using AM.Desktop.Models;
using AM.Desktop.Views;
using Wpf.Ui.Controls;

namespace AM.Desktop.Services;

public static class DialogService
{
    /// <summary>二次确认对话框，点主按钮返回 true。</summary>
    public static async Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = title,
            Content = message,
            PrimaryButtonText = confirmText,
            CloseButtonText = "取消",
        };
        return await box.ShowDialogAsync() == Wpf.Ui.Controls.MessageBoxResult.Primary;
    }

    /// <summary>打开新建采集弹窗。开始采集返回新批次，取消返回 null。</summary>
    public static CollectionBatch? ShowNewBatch()
    {
        var window = new NewBatchWindow { Owner = Application.Current.MainWindow };
        return window.ShowDialog() == true ? window.Result : null;
    }
}
