using AutoMagic.Infrastructure.ExchangeRates;

namespace AutoMagic.Contracts.Tests;

public sealed class ExchangeRateApiParserTests
{
    [Fact]
    public void Parse_ReadsCnyUsdRubRates()
    {
        const string json = """
            {
              "result": "success",
              "time_last_update_unix": 1788062400,
              "base_code": "CNY",
              "rates": {
                "CNY": 1,
                "USD": 0.140123,
                "RUB": 11.2305
              }
            }
            """;

        var result = ExchangeRateApiParser.Parse(json);

        Assert.Equal(0.140123m, result.CnyToUsd);
        Assert.Equal(11.2305m, result.CnyToRub);
        Assert.Equal(ExchangeRateApiService.SourceName, result.Source);
    }

    [Fact]
    public void Parse_RejectsMissingRubRate()
    {
        const string json = """
            {
              "result": "success",
              "time_last_update_unix": 1788062400,
              "base_code": "CNY",
              "rates": { "USD": 0.140123 }
            }
            """;

        Assert.Throws<InvalidDataException>(() => ExchangeRateApiParser.Parse(json));
    }
}
