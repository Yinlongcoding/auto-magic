using AutoMagic.Infrastructure.ExchangeRates;

namespace AutoMagic.Contracts.Tests;

public sealed class BocExchangeRateParserTests
{
    [Fact]
    public void Parse_ConvertsSpotSellingPricesIntoCnyCrossRates()
    {
        const string html = """
            <html><body><table>
              <tr>
                <td><span>卢布</span></td><td>7.44</td><td>7.44</td><td>7.82</td>
                <td>7.82</td><td>7.82</td><td>2026.08.30</td><td>05:30:00</td>
              </tr>
              <tr>
                <td>美元</td><td>672.08</td><td>672.08</td><td>674.91</td>
                <td>674.91</td><td>678.11</td><td>2026.08.30</td><td>05:30:00</td>
              </tr>
            </table></body></html>
            """;

        var result = BocExchangeRateParser.Parse(html);

        Assert.Equal(new DateTime(2026, 8, 30, 5, 30, 0), result.PublishedAt);
        Assert.Equal(100m / 674.91m, result.CnyToUsd);
        Assert.Equal(100m / 7.82m, result.CnyToRub);
        Assert.Equal(BocExchangeRateService.SourceName, result.Source);
    }

    [Fact]
    public void Parse_RejectsMissingSpotSellingPrice()
    {
        const string html = """
            <table>
              <tr><td>卢布</td><td>7.44</td><td>7.44</td><td></td><td>7.82</td><td>7.82</td><td>2026.08.30</td><td>05:30:00</td></tr>
              <tr><td>美元</td><td>672.08</td><td>672.08</td><td>674.91</td><td>674.91</td><td>678.11</td><td>2026.08.30</td><td>05:30:00</td></tr>
            </table>
            """;

        Assert.Throws<InvalidDataException>(() => BocExchangeRateParser.Parse(html));
    }
}
