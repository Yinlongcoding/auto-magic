namespace AutoMagic.Application.Ozon;

public interface ILocalOzonCategoryCatalog
{
    Task<OzonCategoryCatalogSnapshot> LoadAsync(CancellationToken cancellationToken);
}

public sealed record OzonCategoryCatalogSnapshot(
    string Source,
    DateTimeOffset LoadedAt,
    IReadOnlyList<OzonCategoryOption> Categories)
{
    public int TypeCount => Categories.Sum(category => category.Types.Count);
}

public sealed record OzonCategoryOption(
    long DescriptionCategoryId,
    string Name,
    string DisplayName,
    IReadOnlyList<OzonTypeOption> Types);

public sealed record OzonTypeOption(
    long TypeId,
    string Name,
    string DisplayName);
