namespace AutoMagic.Application.Ozon;

public interface IOzonTestSettingsStore
{
    Task<OzonTestSettings> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(OzonTestSettings settings, CancellationToken cancellationToken);

    Task ClearCredentialsAsync(CancellationToken cancellationToken);
}

public sealed record OzonTestSettings(
    string ClientId,
    string ApiKey,
    long? DescriptionCategoryId,
    long? TypeId)
{
    public static OzonTestSettings Empty { get; } = new(string.Empty, string.Empty, null, null);
}
