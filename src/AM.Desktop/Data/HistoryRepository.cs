using AM.Desktop.Models;
using Microsoft.EntityFrameworkCore;

namespace AM.Desktop.Data;

/// <summary>
/// 任务记录持久化仓储
/// </summary>
public class HistoryRepository
{
    // DbContext 不是线程安全的，每次操作新建一个，用完即释放。
    private readonly DbContextOptions<AppDbContext> _options;

    public HistoryRepository(DbContextOptions<AppDbContext> options)
    {
        _options = options;
    }

    /// <summary>
    /// 保存单条任务记录
    /// </summary>
    public async Task SaveAsync(HistoryEntry entry)
    {
        var entity = new HistoryEntity
        {
            Time = entry.Time,
            Batch = entry.Batch,
            Action = entry.Action,
            Result = entry.Result
        };

        await using var context = new AppDbContext(_options);
        context.History.Add(entity);
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// 加载所有任务记录
    /// </summary>
    public async Task<List<HistoryEntry>> LoadAllAsync()
    {
        await using var context = new AppDbContext(_options);
        var entities = await context.History
            .OrderByDescending(h => h.Time)
            .ThenByDescending(h => h.Id)
            .ToListAsync();

        return entities.Select(e => new HistoryEntry(e.Time, e.Batch, e.Action, e.Result)).ToList();
    }
}
