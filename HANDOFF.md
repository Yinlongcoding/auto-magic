# 问题交接文档

## 当前问题
**"生成提交数据"按钮在采集完成后仍然保持禁用状态**

## 问题历史
这个问题已经修复了多次，但一直没有解决。用户决定切换到Opus模型来处理。

## 技术背景

### 按钮绑定机制
位置：[src/AM.Desktop/Views/BatchesPage.xaml:100](src/AM.Desktop/Views/BatchesPage.xaml#L100)

```xaml
<Button Content="生成提交数据" Command="{Binding PreviewSubmitCommand}" />
```

### 命令定义
位置：[src/AM.Desktop/ViewModels/BatchesViewModel.cs:205](src/AM.Desktop/ViewModels/BatchesViewModel.cs#L205)

```csharp
[RelayCommand(CanExecute = nameof(CanSubmit))]
private void PreviewSubmit()
```

### CanSubmit 条件
位置：[src/AM.Desktop/ViewModels/BatchesViewModel.cs:55](src/AM.Desktop/ViewModels/BatchesViewModel.cs#L55)

```csharp
public bool CanSubmit => IsCollected && SelectedBatch!.BlockingCount == 0;
```

要求：
1. `IsCollected` = true（批次状态为 Collected/Listing/Completed）
2. `BlockingCount` = 0（没有价格异常的商品）

## 数据流程

### 采集完成后的处理流程
位置：[src/AM.Desktop/Services/CollectionService.cs:115-125](src/AM.Desktop/Services/CollectionService.cs#L115-L125)

```csharp
case BridgeProtocol.Types.CollectFinished when envelope.PayloadAs<CollectFinishedPayload>() is { } f:
    batch.CollectedCount = f.Succeeded;
    BatchStore.History.Insert(0, new(DateTime.Now, batch.Keyword, "采集完成",
        $"成功 {f.Succeeded}，失败 {f.Failed}"));

    // 定价在切换状态之前做，页签刷新时价格和阻断数已就绪
    BatchPricing.Apply(batch);
    System.Diagnostics.Debug.WriteLine($"[CollectionService] After pricing: BlockingCount={batch.BlockingCount}, State={batch.State}");
    batch.State = BatchState.Collected;
    System.Diagnostics.Debug.WriteLine($"[CollectionService] After state change: State={batch.State}");
    DumpBatch(batch);
    SaveToDatabase(batch);
    Active = null;
    break;
```

### 定价逻辑
位置：[src/AM.Desktop/Services/BatchPricing.cs:12-68](src/AM.Desktop/Services/BatchPricing.cs#L12-L68)

`BatchPricing.Apply`方法会：
1. 遍历所有商品，对每个SKU定价
2. 统计价格异常的商品数量
3. 设置 `batch.BlockingCount = blockingProducts`（第54行）

### 属性变化通知机制

#### CollectionBatch 模型
位置：[src/AM.Desktop/Models/CollectionBatch.cs:57-59](src/AM.Desktop/Models/CollectionBatch.cs#L57-L59)

```csharp
[ObservableProperty]
[NotifyPropertyChangedFor(nameof(ListText))]
private int _blockingCount;
```

使用了 `ObservableProperty` 装饰器，修改时会自动触发 `PropertyChanged` 事件。

#### BatchesViewModel 监听
位置：[src/AM.Desktop/ViewModels/BatchesViewModel.cs:227-247](src/AM.Desktop/ViewModels/BatchesViewModel.cs#L227-L247)

```csharp
private void WatchBatch(CollectionBatch? oldValue, CollectionBatch? newValue)
{
    if (oldValue is not null)
    {
        PropertyChangedEventManager.RemoveHandler(oldValue, OnBatchChanged, string.Empty);
    }

    if (newValue is not null)
    {
        PropertyChangedEventManager.AddHandler(newValue, OnBatchChanged, string.Empty);
    }
}

private void OnBatchChanged(object? sender, PropertyChangedEventArgs e)
{
    System.Diagnostics.Debug.WriteLine($"[BatchesVM] OnBatchChanged: {e.PropertyName}, BlockingCount={SelectedBatch?.BlockingCount}, CanSubmit={CanSubmit}");
    Refresh();
}

private void Refresh()
{
    OnPropertyChanged(string.Empty);
    NewBatchCommand.NotifyCanExecuteChanged();
    PreviewSubmitCommand.NotifyCanExecuteChanged();
}
```

理论上的调用链：
1. `BatchPricing.Apply` 修改 `batch.BlockingCount`
2. `CollectionBatch` 触发 `PropertyChanged` 事件
3. `BatchesViewModel.OnBatchChanged` 被调用
4. `Refresh()` 调用 `PreviewSubmitCommand.NotifyCanExecuteChanged()`
5. WPF 重新评估 `CanSubmit` 并更新按钮状态

## 已添加的调试日志

### CollectionService
采集完成后，定价前后会输出：
```
[CollectionService] After pricing: BlockingCount={batch.BlockingCount}, State={batch.State}
[CollectionService] After state change: State={batch.State}
```

### BatchesViewModel
每次 `OnBatchChanged` 被触发时会输出：
```
[BatchesVM] OnBatchChanged: {e.PropertyName}, BlockingCount={SelectedBatch?.BlockingCount}, CanSubmit={CanSubmit}
```

## 测试步骤

1. 启动应用（已在后台运行，输出重定向到 `src/AM.Desktop/bin/Debug/net10.0-windows/debug.log`）
2. 新建一个采集批次
3. 开始采集
4. 等待采集完成
5. 检查"生成提交数据"按钮是否可点击
6. 查看调试日志，确认：
   - `BlockingCount` 的值是多少
   - `OnBatchChanged` 是否被调用
   - `CanSubmit` 的计算结果是什么

## 可能的问题方向

1. **PropertyChanged 事件没有触发**
   - `CollectionBatch` 的 `BlockingCount` 属性是 `ObservableProperty`，理论上会自动通知
   - 检查是否有线程问题（采集在后台线程，UI更新需要在UI线程）

2. **WatchBatch 没有正确设置监听**
   - 检查 `SelectedBatch` 是否就是正在采集的那个批次
   - 检查 `PropertyChangedEventManager` 是否正常工作

3. **CanSubmit 计算有误**
   - `IsCollected` 的判断依赖 `SelectedBatch?.State`
   - `BlockingCount` 可能不是0（存在价格异常商品）

4. **Command 绑定问题**
   - RelayCommand 的 `NotifyCanExecuteChanged` 可能需要在UI线程调用
   - WPF 可能缓存了旧的 CanExecute 状态

## 当前代码状态

- 应用已编译并启动（后台运行）
- 调试日志已添加到关键位置
- 等待用户测试并提供调试输出

## 下一步建议

1. **查看调试日志**：确认事件触发链是否完整
2. **线程问题检查**：CollectionService 可能在后台线程修改模型，需要切换到UI线程
3. **手动测试**：在采集完成后，尝试切换到其他批次再切回来，看按钮是否变为可用
4. **简化测试**：直接在 Immediate 窗口执行 `PreviewSubmitCommand.NotifyCanExecuteChanged()` 看是否有效

## 相关文件清单

- [src/AM.Desktop/Views/BatchesPage.xaml](src/AM.Desktop/Views/BatchesPage.xaml) - UI定义
- [src/AM.Desktop/ViewModels/BatchesViewModel.cs](src/AM.Desktop/ViewModels/BatchesViewModel.cs) - ViewModel
- [src/AM.Desktop/Models/CollectionBatch.cs](src/AM.Desktop/Models/CollectionBatch.cs) - 模型
- [src/AM.Desktop/Services/CollectionService.cs](src/AM.Desktop/Services/CollectionService.cs) - 采集服务
- [src/AM.Desktop/Services/BatchPricing.cs](src/AM.Desktop/Services/BatchPricing.cs) - 定价逻辑
