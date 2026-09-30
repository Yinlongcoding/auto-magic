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
    public ObservableCollection<FieldBindingRow> BindingRows { get; } = [];
    [ObservableProperty] private string _bindingStatus = "读取 Schema 后，点击生成初步方案。";
    [ObservableProperty] private string _bindingJson = "";
    private FieldMatchingInput? _bindingSource;

    [RelayCommand]
    private void PreviewFieldBindings()
    {
        BindingRows.Clear(); BindingJson = ""; _bindingSource = null;
        if (_latestFieldMatchingInput is null || _ozonSchema is null || !CanRunRuleMapping())
        { BindingStatus = "请先选择商品及对应类型并读取 Schema。"; return; }
        try
        {
            var category = _ozonSchema.DescriptionCategoryId;
            var type = _ozonSchema.TypeId;
            var root = Path.Combine(_ruleReviewService.RulesPath, "categories", category.ToString());
            var bindings = new Dictionary<long, FieldBinding>();
            foreach (var (path, expectedType) in new (string, long?)[] {
                (Path.Combine(root, "bindings.json"), null),
                (Path.Combine(root, "types", type + ".bindings.json"), type) })
            {
                if (!File.Exists(path)) continue;
                var file = JsonSerializer.Deserialize<FieldBindingFile>(File.ReadAllText(path), ProductMappingJson.StrictOptions)
                    ?? throw new InvalidDataException("映射清单为空。");
                if (file.DescriptionCategoryId != category || file.TypeId != expectedType || file.Attributes is null ||
                    file.Attributes.Any(a => a is null) ||
                    file.Attributes.Select(a => a.Id).Distinct().Count() != file.Attributes.Count)
                    throw new InvalidDataException("映射目录与品类/类型不一致，或目标 ID 重复。");
                foreach (var item in file.Attributes) bindings[item.Id] = item;
            }
            if (bindings.Count == 0) { BindingStatus = "该品类/类型尚未配置字段映射清单。"; return; }
            foreach (var row in FieldBindingPreview.CreateRequired(_latestFieldMatchingInput, bindings.Values.ToArray())) BindingRows.Add(row);
            _bindingSource = _latestFieldMatchingInput;
            BindingJson = JsonSerializer.Serialize(new {
                stage = "preliminary-field-bindings", descriptionCategoryId = category, typeId = type,
                sourceOfferId = _bindingSource.ProductRef.OfferId, dictionaryResolved = false,
                attributes = BindingRows
            }, ProductMappingJson.IndentedOptions);
            FieldMatchingFinalOutputJson = BindingJson;
            BindingStatus = $"已生成 {BindingRows.Count} 个必填属性分组；修改内容并勾选已确认，再导出。非必填属性当前不处理，此阶段不查询 valueId。";
            SelectedFieldMatchingTabIndex = 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        { BindingRows.Clear(); BindingStatus = $"生成失败：{error.Message}"; }
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
            BindingJson = JsonSerializer.Serialize(new {
                stage = "human-confirmed-fields", dictionaryResolved = false,
                descriptionCategoryId = _ozonSchema!.DescriptionCategoryId, typeId = _ozonSchema.TypeId,
                sourceOfferId = _bindingSource.ProductRef.OfferId,
                attributes = BindingRows.Select(r => new {
                    id = r.AttributeId, name = r.Name, dictionaryId = r.DictionaryId,
                    scopeKeys = r.ScopeKeys, factIds = r.FactIds, sourceValues = r.SourceValues,
                    usedDefault = r.UsedDefault,
                    resolution = r.Resolution,
                    confirmed = r.Reviewed && r.SchemaValid,
                    confirmedValue = r.Reviewed && r.SchemaValid ? r.InputText : null
                })
            }, ProductMappingJson.IndentedOptions);
            File.WriteAllText(dialog.FileName, BindingJson);
            FieldMatchingFinalOutputJson = BindingJson;
            BindingStatus = $"已导出 {confirmed.Length} 个确认分组；其余保留待处理，未写入值规则。";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { BindingStatus = $"导出失败：{error.Message}"; }
    }
}
