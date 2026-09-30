using System.IO;
using System.Text.Json;
using AutoMagic.Contracts.Protocol;
using AutoMagic.Infrastructure.Ozon;

namespace AutoMagic.Desktop.ViewModels;

public partial class MainViewModel
{
    private readonly OzonSchemaCache _schemaCache = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AutoMagic", "schema-cache"));

    private void RestoreCachedSchema()
    {
        if (_isRestoringOzonSettings || SelectedOzonCategory is null || SelectedOzonType is null) return;
        try
        {
            var schema = _schemaCache.Load(SelectedOzonCategory.DescriptionCategoryId, SelectedOzonType.TypeId);
            if (schema is null)
            {
                OzonSchemaStatus = "本类型暂无 Schema 缓存，请点击“请求并刷新 Schema”。";
                return;
            }
            _ozonSchema = schema;
            OzonAttributes.Clear();
            foreach (var attribute in schema.Attributes) OzonAttributes.Add(attribute);
            OzonSchemaJson = JsonSerializer.Serialize(schema, BridgeJson.IndentedOptions);
            OzonSchemaStatus = $"开发缓存：已加载 {schema.Attributes.Count} 个属性，获取于 {schema.CapturedAt.ToLocalTime():yyyy-MM-dd HH:mm}；可手动刷新。";
            RefreshFieldMatchingInput();
            RefreshRuleReadinessStatus();
            RunRuleMappingCommand.NotifyCanExecuteChanged();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            OzonSchemaStatus = $"缓存读取失败：{error.Message}；请手动请求刷新 Schema。";
        }
    }
}
