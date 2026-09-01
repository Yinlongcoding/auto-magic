namespace AutoMagic.Application.Ozon.Mapping;

public interface IQwenTestSettingsStore
{
    Task<QwenTestSettings> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(QwenTestSettings settings, CancellationToken cancellationToken);

    Task ClearCredentialsAsync(CancellationToken cancellationToken);
}

public sealed record QwenTestSettings(string ApiKey)
{
    public static QwenTestSettings Empty { get; } = new(string.Empty);
}
