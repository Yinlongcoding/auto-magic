namespace AutoMagic.Application.Ozon;

public interface IOzonSchemaService
{
    Task<OzonCategorySchema> GetCategorySchemaAsync(
        OzonTemporaryCredentials credentials,
        long descriptionCategoryId,
        long typeId,
        CancellationToken cancellationToken);
}

public sealed record OzonTemporaryCredentials(string ClientId, string ApiKey)
{
    public OzonTemporaryCredentials Validate()
    {
        if (string.IsNullOrWhiteSpace(ClientId))
        {
            throw new ArgumentException("请输入 Ozon Client-Id。", nameof(ClientId));
        }

        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            throw new ArgumentException("请输入 Ozon Api-Key。", nameof(ApiKey));
        }

        if (ClientId.Length > 128 || ApiKey.Length > 512)
        {
            throw new ArgumentException("Ozon 临时凭证长度无效。");
        }

        return this with { ClientId = ClientId.Trim(), ApiKey = ApiKey.Trim() };
    }
}

public sealed record OzonCategorySchema(
    long DescriptionCategoryId,
    long TypeId,
    DateTimeOffset CapturedAt,
    IReadOnlyList<OzonAttributeDefinition> Attributes)
{
    public int RequiredCount => Attributes.Count(attribute => attribute.IsRequired);
}

public sealed record OzonAttributeDefinition(
    long Id,
    long AttributeComplexId,
    string Name,
    string Description,
    string Type,
    bool IsCollection,
    bool IsRequired,
    long DictionaryId,
    int MaxValueCount,
    string GroupName);
