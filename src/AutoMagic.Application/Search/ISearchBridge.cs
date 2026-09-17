using AutoMagic.Contracts.Protocol;

namespace AutoMagic.Application.Search;

public interface ISearchBridge
{
    bool IsExtensionConnected { get; }

    event EventHandler<bool>? ConnectionChanged;

    event EventHandler<SearchProgressPayload>? SearchProgressChanged;

    Task<SearchResultPayload> SearchAsync(
        string keyword,
        int maxItems,
        decimal procurementMinimumCny,
        decimal procurementMaximumCny,
        string sortMode,
        bool includeDetailFacts,
        CancellationToken cancellationToken);
}
