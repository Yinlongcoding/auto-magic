using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using AutoMagic.Application.Ozon.Mapping;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace AutoMagic.Desktop.ViewModels;

public partial class MainViewModel
{
    private readonly FieldMappingService _fieldMappingService;
    [ObservableProperty] private bool _isPricingExpanded = true;
    public ObservableCollection<FieldBindingRow> BindingRows { get; } = [];
    [ObservableProperty] private string _bindingStatus = "读取 Schema 后，点击生成初步方案。";
    [ObservableProperty] private string _bindingJson = "";
    private FieldMatchingInput? _bindingSource;
    private void OnBindingRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs change)
    {
        if (change.PropertyName is not (nameof(FieldBindingRow.InputText) or nameof(FieldBindingRow.Reviewed))) return;
        BindingJson = ""; FieldMatchingFinalOutputJson = "";
        BindingStatus = "确认内容或勾选状态已变化，请重新匹配 valueId。";
    }

    [RelayCommand(CanExecute = nameof(CanPreviewFieldBindings))]
    private void PreviewFieldBindings()
    {
        BindingRows.Clear(); BindingJson = ""; FieldMatchingFinalOutputJson = ""; _bindingSource = null;
        if (_latestFieldMatchingInput is null || _ozonSchema is null || !CanPreviewFieldBindings())
        { BindingStatus = "请先选择商品及对应类型并读取 Schema。"; return; }
        try
        {
            var category = _ozonSchema.DescriptionCategoryId;
            var type = _ozonSchema.TypeId;
            foreach (var row in _fieldMappingService.CreateRequired(_latestFieldMatchingInput)) BindingRows.Add(row);
            ShowSkuIdentityPlan(ProductMappingInputBuilder.Create(_latestFieldMatchingInput).Request);
            _bindingSource = _latestFieldMatchingInput;
            BindingJson = JsonSerializer.Serialize(new {
                stage = "preliminary-field-bindings", descriptionCategoryId = category, typeId = type,
                sourceOfferId = _bindingSource.ProductRef.OfferId, dictionaryResolved = false,
                attributes = BindingRows
            }, ProductMappingJson.IndentedOptions);
            FieldMatchingFinalOutputJson = BindingJson;
            BindingStatus = $"已生成 {BindingRows.Count} 个必填属性分组；修改中文并勾选已确认，再匹配 valueId。未命中配置的值留空，非必填属性当前不处理。";
            SelectedFieldMatchingTabIndex = 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        { BindingRows.Clear(); BindingStatus = $"生成失败：{error.Message}"; }
    }

    [RelayCommand]
    private void MatchConfirmedFieldValues()
    {
        if (_bindingSource is null || !ReferenceEquals(_bindingSource, _latestFieldMatchingInput))
        { BindingStatus = "商品或 Schema 已变化，请重新生成方案。"; return; }
        try
        {
            ResolveAndSerializeBindings();
            var confirmed = BindingRows.Count(r => r.Reviewed && r.SchemaValid);
            var matched = BindingRows.Count(r => r.ValueResolution == "json");
            var pending = BindingRows.Count(r => r.Reviewed && r.SchemaValid && r.DictionaryId > 0 && r.ValueId is null);
            BindingStatus = $"已确认 {confirmed} 个分组；JSON 命中 {matched} 个，字典值留空 {pending} 个。补充 JSON 后可重新匹配。";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        {
            BindingJson = ""; FieldMatchingFinalOutputJson = "";
            BindingStatus = $"匹配失败：{error.Message}";
        }
    }

    private void ResolveAndSerializeBindings()
    {
        // Reload local rules on each match/export so manual JSON edits take effect immediately.
        _fieldMappingService.ResolveConfirmedFields(_bindingSource!, BindingRows.ToArray());
        BindingJson = JsonSerializer.Serialize(new {
            stage = "human-confirmed-value-mapping",
            dictionaryResolved = BindingRows.Where(r => r.DictionaryId > 0).All(r => r.ValueId is > 0),
            descriptionCategoryId = _bindingSource!.Target.DescriptionCategoryId, typeId = _bindingSource.Target.TypeId,
            sourceOfferId = _bindingSource.ProductRef.OfferId,
            attributes = BindingRows.Select(r => new {
                id = r.AttributeId, name = r.Name, dictionaryId = r.DictionaryId,
                scopeKeys = r.ScopeKeys, factIds = r.FactIds, sourceValues = r.SourceValues,
                modulePath = r.ModulePath,
                valueSourcePaths = r.ValueSourcePaths,
                usedDefault = r.UsedDefault, resolution = r.Resolution,
                confirmed = r.Reviewed && r.SchemaValid,
                confirmedValue = r.Reviewed && r.SchemaValid ? r.InputText : null,
                targetValue = r.TargetValue, valueId = r.ValueId,
                valueResolution = r.ValueResolution, valueStatus = r.ValueStatus
            })
        }, ProductMappingJson.IndentedOptions);
        FieldMatchingFinalOutputJson = BindingJson;
    }

    [RelayCommand]
    private void ExportFieldBindings()
    {
        if (_bindingSource is null || !ReferenceEquals(_bindingSource, _latestFieldMatchingInput))
        { BindingStatus = "商品或 Schema 已变化，请重新生成方案。"; return; }
        var confirmed = BindingRows.Where(r => r.Reviewed).ToArray();
        if (confirmed.Length == 0 || confirmed.Any(r => !r.SchemaValid))
        { BindingStatus = "请确认至少一行；Schema 不支持的行不能确认。"; return; }
        var dialog = new SaveFileDialog { Filter = "JSON 文件|*.json", FileName = "confirmed-field-mapping.json" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            ResolveAndSerializeBindings();
            var currentConfirmed = BindingRows.Count(r => r.Reviewed && r.SchemaValid);
            if (currentConfirmed == 0)
            { BindingStatus = "字段规则或引用已变化，原确认已失效；请重新生成方案并确认。"; return; }
            File.WriteAllText(dialog.FileName, BindingJson);
            FieldMatchingFinalOutputJson = BindingJson;
            BindingStatus = $"已导出 {currentConfirmed} 个确认分组及本地匹配结果；未命中的 valueId 留空，未写入值规则。";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        { BindingJson = ""; FieldMatchingFinalOutputJson = ""; BindingStatus = $"导出失败：{error.Message}"; }
    }
}
