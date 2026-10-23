using System.Text.Json;
using AM.Desktop.Models;
using AM.Desktop.Services;
using Microsoft.EntityFrameworkCore;

namespace AM.Desktop.Data;

/// <summary>
/// 批次数据仓库
/// </summary>
public class BatchRepository
{
    // DbContext 不是线程安全的，每次操作新建一个，用完即释放。
    private readonly DbContextOptions<AppDbContext> _options;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    /// <summary>上次 LoadAllAsync 中因数据读不出来而跳过的批次数。</summary>
    public int SkippedCount { get; private set; }

    public BatchRepository(DbContextOptions<AppDbContext> options)
    {
        _options = options;
    }

    /// <summary>
    /// 保存批次到数据库（包括商品列表）
    /// </summary>
    public async Task SaveAsync(CollectionBatch batch)
    {
        var batchJson = JsonSerializer.Serialize(batch, JsonOptions);

        // 从 BatchStore 获取商品列表
        var products = BatchStore.ProductsFor(batch).ToList();
        var productsJson = JsonSerializer.Serialize(products, JsonOptions);

        // 同一批次可能连着保存两次（如连点放弃），先查后插要串行，否则会撞唯一索引
        await _writeLock.WaitAsync();
        try
        {
            await SaveCoreAsync(batch, batchJson, productsJson);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private async Task SaveCoreAsync(CollectionBatch batch, string batchJson, string productsJson)
    {
        await using var context = new AppDbContext(_options);
        var entity = await context.Batches.FirstOrDefaultAsync(e => e.BatchId == batch.Id);

        if (entity == null)
        {
            entity = new BatchEntity
            {
                BatchId = batch.Id,
                Keyword = batch.Keyword,
                DataJson = batchJson,
                ProductsJson = productsJson,
                CreatedAt = batch.CreatedAt
            };
            context.Batches.Add(entity);
        }
        else
        {
            entity.DataJson = batchJson;
            entity.ProductsJson = productsJson;
            entity.CompletedAt = batch.State.IsFinished()
                ? DateTime.Now
                : null;
        }

        await context.SaveChangesAsync();
    }

    /// <summary>
    /// 加载所有批次（按创建时间倒序），返回批次和对应的商品列表
    /// </summary>
    public async Task<List<(CollectionBatch Batch, List<CollectedProduct> Products)>> LoadAllAsync()
    {
        SkippedCount = 0;
        await using var context = new AppDbContext(_options);
        var entities = await context.Batches
            .OrderByDescending(e => e.CreatedAt)
            .ToListAsync();

        var results = new List<(CollectionBatch, List<CollectedProduct>)>();

        foreach (var entity in entities)
        {
            // 一条读不出来（如模型改过字段）不影响其他批次
            try
            {
                var batch = JsonSerializer.Deserialize<CollectionBatch>(entity.DataJson, JsonOptions);
                var products = string.IsNullOrEmpty(entity.ProductsJson)
                    ? new List<CollectedProduct>()
                    : JsonSerializer.Deserialize<List<CollectedProduct>>(entity.ProductsJson, JsonOptions)
                      ?? new List<CollectedProduct>();

                if (batch != null)
                {
                    results.Add((batch, products));
                }
            }
            catch (JsonException)
            {
                SkippedCount++;
            }
        }

        return results;
    }

    /// <summary>
    /// 删除批次
    /// </summary>
    public async Task DeleteAsync(string batchId)
    {
        await using var context = new AppDbContext(_options);
        var entity = await context.Batches.FirstOrDefaultAsync(e => e.BatchId == batchId);
        if (entity != null)
        {
            context.Batches.Remove(entity);
            await context.SaveChangesAsync();
        }
    }
}
