namespace AutoMagic.Application.Ozon;

/// <summary>
/// Reads the Ozon dictionary values for one category/type attribute.
/// The implementation must return only values supplied by Ozon; callers decide
/// how to narrow them for a semantic-mapping request.
/// </summary>
public interface IOzonDictionaryService
{
    /// <summary>
    /// Searches Ozon's reference values for one candidate text. The returned
    /// IDs and values are the only dictionary values that may be selected for
    /// a publish request.
    /// </summary>
    Task<IReadOnlyList<OzonDictionaryValue>> SearchAttributeValuesAsync(
        OzonTemporaryCredentials credentials,
        long descriptionCategoryId,
        long typeId,
        long attributeId,
        string value,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<OzonDictionaryValue>> GetAttributeValuesAsync(
        OzonTemporaryCredentials credentials,
        long descriptionCategoryId,
        long typeId,
        long attributeId,
        string language,
        CancellationToken cancellationToken);
}

public sealed record OzonDictionaryValue(
    long ValueId,
    string Value,
    string Info,
    string Picture);
