using System.Text;
using AutoMagic.Infrastructure.Ozon;

namespace AutoMagic.Contracts.Tests;

public sealed class LocalOzonCategoryCatalogTests
{
    [Fact]
    public async Task ParseAsync_FlattensCategoryPathAndFiltersDisabledTypes()
    {
        const string json = """
            {
              "result": [
                {
                  "description_category_id": 10,
                  "category_name": "Clothing",
                  "disabled": false,
                  "children": [
                    {
                      "description_category_id": 20,
                      "category_name": "Dresses",
                      "disabled": false,
                      "children": [
                        { "type_id": 30, "type_name": "Dress", "disabled": false, "children": [] },
                        { "type_id": 31, "type_name": "Disabled", "disabled": true, "children": [] }
                      ]
                    }
                  ]
                }
              ]
            }
            """;
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var categories = await OzonCategoryCatalogParser.ParseAsync(stream);

        var category = Assert.Single(categories);
        Assert.Equal(20, category.DescriptionCategoryId);
        Assert.Equal("Clothing > Dresses", category.DisplayName);
        var type = Assert.Single(category.Types);
        Assert.Equal(30, type.TypeId);
        Assert.Equal("Dress · 30", type.DisplayName);
    }

    [Fact]
    public async Task ProvidedTestSnapshot_ParsesAllSelectableCategoriesAndTypes()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "ozon-category-tree.test.json");
        await using var stream = File.OpenRead(path);

        var categories = await OzonCategoryCatalogParser.ParseAsync(stream);

        Assert.Equal(542, categories.Count);
        Assert.Equal(7365, categories.Sum(category => category.Types.Count));
        Assert.Contains(categories, category => category.DisplayName == "儿童用品 > 铁路");
        Assert.All(categories, category =>
        {
            Assert.True(category.DescriptionCategoryId > 0);
            Assert.NotEmpty(category.DisplayName);
            Assert.NotEmpty(category.Types);
        });
    }
}
