using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutoMagic.Application.Ozon.Rules;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace AutoMagic.Desktop.ViewModels;

public partial class MainViewModel
{
    private static readonly JsonSerializerOptions ResultJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    [ObservableProperty] private string _cleaningStatus = "选择商品并读取 Schema 后执行清洗与匹配。";
    [ObservableProperty] private bool _showCleaningJson;
    [ObservableProperty] private bool _isCleaningExpanded;

    /// <summary>One row per attribute and distinct value, so shared product values are reviewed once.</summary>
    public ObservableCollection<CleaningRow> CleaningRows { get; } = [];

    private bool CanCleanProduct() => !IsBusy && _currentSource is not null && _ozonSchema is not null;

    [RelayCommand(CanExecute = nameof(CanCleanProduct))]
    private async Task CleanProductAsync()
    {
        if (_currentSource is null || _ozonSchema is null) return;
        var source = _currentSource;
        var schema = _ozonSchema;
        InvalidateCleaningResult();
        try
        {
            var cleaned = await _cleaningEngine.CleanAsync(source, schema);
            var matched = await _matchingEngine.MatchAsync(cleaned, schema);
            if (!ReferenceEquals(source, _currentSource) || !ReferenceEquals(schema, _ozonSchema)) return;
            _lastCleaned = cleaned;
            _lastMatched = matched;
            foreach (var row in CleaningRow.Group(cleaned, matched, schema)) CleaningRows.Add(row);
            CleanedProductJson = JsonSerializer.Serialize(new { cleaned.OfferId, cleaned.CategoryId, cleaned.TypeId, rows = CleaningRows }, ResultJson);
            var blocking = CleaningRows.Count(r => r.IsBlocking);
            CleaningStatus = $"{cleaned.Skus.Count} 个 SKU、{CleaningRows.Count} 个属性值分组；" +
                (blocking == 0 ? "没有阻断项。" : $"阻断 {blocking} 组（必填但未得到值），需人工补全。");
        }
        catch (Exception error) when (error is RuleConfigurationException or InvalidOperationException or IOException or JsonException or UnauthorizedAccessException)
        {
            CleaningStatus = $"清洗失败：{error.Message}";
        }
        ExportCleanedProductCommand.NotifyCanExecuteChanged();
    }

    private bool CanExportCleanedProduct() => !IsBusy && _lastCleaned is not null;

    [RelayCommand(CanExecute = nameof(CanExportCleanedProduct))]
    private void ExportCleanedProduct()
    {
        if (_lastCleaned is null) return;
        var dialog = new SaveFileDialog { Filter = "JSON 文件|*.json", FileName = $"cleaned-{_lastCleaned.OfferId}-{_lastCleaned.TypeId}.json" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, CleanedProductJson);
            CleaningStatus = $"已导出到 {dialog.FileName}";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            CleaningStatus = $"导出失败：{error.Message}";
        }
    }

    [RelayCommand]
    private void ToggleCleaningExpanded() => IsCleaningExpanded = !IsCleaningExpanded;

    private void InvalidateCleaningResult()
    {
        _lastCleaned = null;
        _lastMatched = null;
        CleaningRows.Clear();
        CleanedProductJson = "尚未执行清洗。";
        CleaningStatus = _ozonSchema is null ? "请先读取当前类型的 Schema。" : "可以执行清洗与匹配。";
        CleanProductCommand.NotifyCanExecuteChanged();
        ExportCleanedProductCommand.NotifyCanExecuteChanged();
    }
}

public sealed record SourceFact(string Label, string Value);

public sealed record CleaningRow(
    long Id,
    string Name,
    string Level,
    int SkuCount,
    string? SourceField,
    string? SourceValue,
    string? Value,
    long? ValueId,
    string Resolution,
    bool IsBlocking,
    string? Detail)
{
    [JsonIgnore] public string StatusColor => IsBlocking ? "#DC2626" : ValueId is null && Value is null ? "#94A3B8" : "#15803D";

    public static IEnumerable<CleaningRow> Group(CleanedProduct cleaned, MatchedProduct matched, Application.Ozon.OzonCategorySchema schema)
    {
        var totalSkus = cleaned.Skus.Count;
        foreach (var attribute in schema.Attributes.OrderByDescending(a => a.IsRequired).ThenBy(a => a.Id))
        {
            var id = (int)attribute.Id;
            var pairs = cleaned.Skus.Zip(matched.Skus)
                .Where(p => p.First.Values.ContainsKey(id) && p.Second.Values.ContainsKey(id))
                .Select(p => (Clean: p.First.Values[id], Match: p.Second.Values[id]))
                .ToArray();
            if (pairs.Length == 0) continue; // no rule and not required: not submitted
            foreach (var group in pairs.GroupBy(p => (p.Clean.SourceValue, p.Clean.Value)))
            {
                var (clean, match) = group.First();
                var level = clean.FromSpecAxis ? "SKU" : "商品";
                var resolution = match.Status switch
                {
                    MatchStatus.Matched => "已匹配",
                    MatchStatus.FreeText => "自由文本",
                    MatchStatus.Unmatched => "未匹配",
                    _ => attribute.IsRequired ? "缺失" : "不提交",
                };
                var detail = string.Join("；", new[] { $"来源：{clean.Origin}", clean.Conversion is null ? null : $"换算：{clean.Conversion}", clean.Note, match.Note }.OfType<string>());
                yield return new(attribute.Id, attribute.Name, level, group.Count(), clean.SourceField, clean.SourceValue,
                    match.Value, match.ValueId, resolution, match.IsBlocking, detail);
            }
        }
    }
}
