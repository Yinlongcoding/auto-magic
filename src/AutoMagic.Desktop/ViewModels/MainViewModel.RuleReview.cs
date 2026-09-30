using System.Collections.ObjectModel;
using System.Text.Json;
using AutoMagic.Application.Ozon;
using AutoMagic.Application.Ozon.Mapping;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AutoMagic.Desktop.ViewModels;

public partial class MainViewModel
{
    private readonly RuleReviewService _ruleReviewService;
    private RuleReviewSession? _reviewSession;
    public ObservableCollection<RuleReviewRow> ReviewRows { get; } = [];
    [ObservableProperty] private RuleReviewRow? _selectedReviewRow;
    [ObservableProperty] private string _reviewStatus = "请先运行规则匹配。";
    [ObservableProperty] private bool _isReviewBusy;
    [ObservableProperty] private bool _isPricingExpanded = true;
    public bool IsReviewEditable => !IsReviewBusy && !IsRunningRuleMapping;
    partial void OnIsReviewBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsReviewEditable));
        RunRuleMappingCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void CancelReview()
    {
        _ruleMappingCancellation?.Cancel();
        ReviewStatus = "已请求取消；填写内容保留，尚未提交的规则不会写入。";
    }

    [RelayCommand]
    private void ClearReviewSource()
    {
        if (IsReviewEditable && SelectedReviewRow is not null) SelectedReviewRow.SourceFactId = "";
    }

    [RelayCommand]
    private void SaveReviewDraft()
    {
        if (_reviewSession is null || !IsReviewEditable) return;
        try { _ruleReviewService.SaveDraft(_reviewSession); ReviewStatus = "填写草稿已保存；未修改规则，也未增加审核次数。"; }
        catch (Exception) { ReviewStatus = "草稿保存失败，请检查规则目录是否可写。"; }
    }

    [RelayCommand]
    private async Task SearchReviewDictionaryAsync()
    {
        if (_reviewSession is null || SelectedReviewRow is null || !IsReviewEditable) return;
        var session = _reviewSession; var row = SelectedReviewRow;
        using var cancellation = new CancellationTokenSource();
        _ruleMappingCancellation = cancellation;
        IsReviewBusy = true;
        try
        {
            await _ruleReviewService.SearchRowAsync(session, row, new(OzonClientId, OzonApiKey), cancellation.Token);
            if (ReferenceEquals(session, _reviewSession)) ReviewStatus = row.Status;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception) { if (ReferenceEquals(session, _reviewSession)) ReviewStatus = "字典查询失败，请检查 Ozon 配置或稍后重试。"; }
        finally { if (ReferenceEquals(_ruleMappingCancellation, cancellation)) _ruleMappingCancellation = null; IsReviewBusy = false; }
    }

    [RelayCommand]
    private async Task ConfirmReviewAsync()
    {
        if (_reviewSession is null || !IsReviewEditable) return;
        if (!_reviewSession.Rows.Any(r => r.Reviewed)) { ReviewStatus = "请先勾选本次确实核对过的字段；未核对项不计入学习。"; return; }
        var session = _reviewSession;
        using var cancellation = new CancellationTokenSource();
        _ruleMappingCancellation = cancellation;
        IsReviewBusy = true;
        try
        {
            var result = await _ruleReviewService.ConfirmAsync(session, new(OzonClientId, OzonApiKey), cancellation.Token);
            if (!ReferenceEquals(session, _reviewSession)) return;
            _reviewSession = _ruleReviewService.RefreshBundle(session);
            ProductMappingRows.Clear(); ProductMappingIssues.Clear();
            foreach (var row in result.Run.Validation.Rows) ProductMappingRows.Add(row);
            foreach (var issue in result.Run.Validation.Issues) ProductMappingIssues.Add(issue);
            FieldMatchingFinalOutputJson = JsonSerializer.Serialize(new
            {
                mapping = result.Run,
                ozonImportDraft = OzonFieldCompositionEngine.Compose(result.Run.Request, result.Run.Response, result.Run.Validation),
            }, ProductMappingJson.IndentedOptions);
            ReviewStatus = FieldMatchingFinalStatus = result.Message;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) when (error is InvalidOperationException or System.IO.IOException or ArgumentException)
        { if (ReferenceEquals(session, _reviewSession)) ReviewStatus = error.Message; }
        catch (Exception) { if (ReferenceEquals(session, _reviewSession)) ReviewStatus = "保存失败，请检查规则文件与本地数据库；填写内容仍保留在表单中。"; }
        finally { if (ReferenceEquals(_ruleMappingCancellation, cancellation)) _ruleMappingCancellation = null; IsReviewBusy = false; }
    }
}
