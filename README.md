# BatchObservableCollection

面向 WPF 数据绑定的高性能批量可观察集合（`ObservableCollection<T>` 的增强替代品），附带一个可运行的交互式演示程序。

## 这是什么

`ObservableCollection<T>` 是 WPF 绑定的标配，但它有一个致命弱点：**每一次增删都会触发一次 `CollectionChanged` 通知**。往 `ListBox` / `DataGrid` 里加载 1 万条数据，就会触发 1 万次通知 + 1 万次 UI 更新请求，界面直接卡死。

`BatchObservableCollection<T>` 在**完全兼容** `ObservableCollection<T>` 的基础上解决了这个问题：

| 能力 | 说明 |
| --- | --- |
| `AddRange(items)` | 批量追加。项数 **> 阈值**（默认 50）→ 静默写入 + **单次 `Reset`** 通知；项数 **≤ 阈值** → 逐项 `Add` 通知，保留增量动画与滚动位置 |
| `RemoveRange(items)` | 批量移除。O(n+m) 倒序定位（对比逐项 `Remove` 的 O(n×m)），无论移除多少项只触发**一次 `Reset`** |
| `ReplaceAll(items)` / `ClearAndAdd(items)` | 清空 + 重填，仅触发**一次 `Reset`** |
| `SuspendNotifications()` / `ResumeNotifications()` | 通知挂起（支持嵌套计数）：复杂批量操作期间全部静默，恢复时统一补发一次 `Reset`；挂起期间无任何修改则恢复时不通知 |
| `BeginUpdate()` | `IDisposable` 作用域版挂起：`using` 语句自动配对 Suspend / Resume，支持嵌套，防重复 `Dispose` |
| `AddRangeThreshold` | "大批量"阈值，可运行时调节（赋值 < 1 抛异常） |
| 重入保护 | 所有变更方法均经 `CheckReentrancy()`，事件处理期间禁止重入修改 |

### 设计要点

- **小批量仍逐项通知**：阈值以内走 `base.Add`，`ListView` / `DataGrid` 的插入动画、滚动位置等增量行为不受影响——不是无脑全 `Reset`。
- **阈值语义**：`AddRange` 项数 **大于** `AddRangeThreshold` 才切换为单次 `Reset`；默认 50 兼顾了"增量动画可保留"与"逐项通知不卡顿"。
- **`Reset` 前先发 `Count` / `Item[]` 属性变更**：与基类通知顺序一致，绑定引擎先读到新的 `Count` 再处理 `Reset`。
- **抑制标志用 `try/finally` 复位**：即使元素回调（`GetHashCode` / `Equals` 等）抛异常，集合也不会永久"失聪"。
- **`RemoveRange` 保持多重集语义**：`items` 中某元素出现 k 次，恰好移除集合中前 k 个匹配项，与逐个调用 `Remove` 的结果完全一致；无任何匹配时保持静默，不做无意义的整表刷新。
- **挂起恢复带脏标记**：挂起期间没有任何修改时，`ResumeNotifications()` 不会补发多余的 `Reset`（避免一次无意义的整表刷新）。

### 线程模型（重要）

本集合**不是线程安全的**，也不内置 `Dispatcher` 封送。后台线程只应"生产数据"，所有对集合的修改必须封送回 UI 线程（`Dispatcher.InvokeAsync` / `Dispatcher.Invoke`）。参见 [`src/WpfBatchDemo/BatchDemoViewModel.cs`](src/WpfBatchDemo/BatchDemoViewModel.cs) 中的 `ExecuteBackgroundLoadAsync` 示例。

## 快速开始

环境要求：.NET 8 SDK（`net8.0-windows`，需 Windows）。

```bash
git clone https://github.com/JoryJang/BatchObservableCollection.git
cd BatchObservableCollection

# 直接运行演示程序
dotnet run --project src/WpfBatchDemo
```

在你的项目中引入：拷贝 [`src/BatchObservableCollection`](src/BatchObservableCollection) 下的两个 `.cs` 文件（或直接引用该 csproj），集合类型位于命名空间 `WpfApps.Infrastructure.Collections`。

### 最小用法

```csharp
var logs = new BatchObservableCollection<LogEntry>();

logs.AddRange(smallBatch);   // 10 条：逐项 Add 通知，保留动画
logs.AddRange(bigBatch);     // 5000 条：单次 Reset 通知，不卡 UI
logs.RemoveRange(toDelete);  // 单次 Reset
logs.ReplaceAll(freshData);  // 单次 Reset

// 复杂批量更新：挂起 → 任意操作 → 恢复（仅一次 Reset）
logs.SuspendNotifications();
try
{
    logs.ClearAndAdd(newData);
    logs.AddRange(moreData);
}
finally
{
    logs.ResumeNotifications(); // 统一触发一次 Reset
}

// 更省心的写法：using 作用域自动配对（Dispose 时统一补发一次 Reset）
using (logs.BeginUpdate())
{
    logs.ClearAndAdd(newData);
    logs.AddRange(moreData);
}
```

## 演示程序（WpfBatchDemo）

交互式演示程序 `src/WpfBatchDemo`，右侧是绑定到 `BatchObservableCollection<LogEntry>` 的 `ListBox`（开启虚拟化 + 回收），左侧提供：

1. **AddRange** —— 小批量 ×10（逐项 Add） vs 大批量 ×5000（单次 Reset）对比；
2. **AddRangeThreshold** —— 滑块实时调节阈值；把它调到 5000 以上再点"大批量添加"，能直观看到逐项通知的代价（Add 计数暴涨、UI 变卡）；
3. **RemoveRange / ReplaceAll** —— 批量移除与整体替换；
4. **Suspend / Resume** —— 挂起期间连续多次变更，恢复时只 +1 次 Reset；
5. **后台线程加载** —— `Task.Run` 生产 3000 条后经 `Dispatcher` 封送回 UI 线程的标准写法；
6. **底部通知计数器** —— 实时统计 Add / Remove / Replace / Move / Reset / PropertyChanged 各自的触发次数。

## 项目结构

```
src/
├── BatchObservableCollection/          # 集合库（net8.0-windows 类库）
│   ├── BatchObservableCollection.cs    # 核心实现，命名空间 WpfApps.Infrastructure.Collections
│   └── LogViewModelExample.cs          # ViewModel 线程封送示例（LogEntry / LogViewModel）
└── WpfBatchDemo/                       # 交互式演示程序（引用上面的库）
    ├── MainWindow.xaml                 # 演示界面
    └── BatchDemoViewModel.cs           # 演示 ViewModel + 通知计数器
```

## License

MIT
