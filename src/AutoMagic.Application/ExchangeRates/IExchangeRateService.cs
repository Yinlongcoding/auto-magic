namespace AutoMagic.Application.ExchangeRates;

public interface IExchangeRateService
{
    Task<ExchangeRateSnapshot> GetLatestAsync(CancellationToken cancellationToken);
}

public sealed record ExchangeRateSnapshot(
    DateTime PublishedAt,
    decimal CnyToUsd,
    decimal CnyToRub,
    string Source);
