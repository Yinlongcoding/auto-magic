using AutoMagic.Contracts.Protocol;
using AutoMagic.Infrastructure.Collection;

namespace AutoMagic.Contracts.Tests;

public sealed class CollectionSnapshotStoreTests
{
    [Fact]
    public async Task LoadLatestAsync_RestoresTheNewestSavedCollection()
    {
        var root = Path.Combine(Path.GetTempPath(), $"automagic-collections-{Guid.NewGuid():N}");
        try
        {
            var store = new CollectionSnapshotStore(root);
            await store.SaveAsync(Result("first"));
            await Task.Delay(20);
            var newestPath = await store.SaveAsync(Result("latest"));

            var restored = await store.LoadLatestAsync();

            Assert.NotNull(restored);
            Assert.Equal(newestPath, restored.Directory);
            Assert.Equal("latest", restored.Result.Keyword);
            Assert.Single(restored.Result.DetailResults!);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static SearchResultPayload Result(string keyword) => new(
        keyword,
        DateTimeOffset.UtcNow.ToString("O"),
        1,
        [new ProductItemDto("https://detail.1688.com/offer/1.html", "image", "title", "1")],
        null,
        DetailResults:
        [
            new DetailCollectionResultDto(
                0,
                1,
                "title",
                "https://detail.1688.com/offer/1.html",
                "success",
                null,
                DateTimeOffset.UtcNow.ToString("O"),
                "title",
                [new DetailFactDto("材质", "PBT", "normal-attributes")],
                [],
                [])
        ]);
}
