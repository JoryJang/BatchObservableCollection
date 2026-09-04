using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows.Threading;
using WpfApps.Infrastructure.Collections;

namespace WpfApps.Examples
{
    /// <summary>
    /// 日志条目（示例数据模型）。
    /// </summary>
    public class LogEntry
    {
        /// <summary>日志编号。</summary>
        public int Id { get; set; }

        /// <summary>日志内容。</summary>
        public string Message { get; set; }
    }

    /// <summary>
    /// 演示如何在 ViewModel 中安全使用 <see cref="BatchObservableCollection{T}"/>：
    /// 后台线程只负责“生产数据”，所有对集合的访问都封送到 UI 线程执行（因为集合本身不内置 Dispatcher）。
    /// </summary>
    public class LogViewModel : INotifyPropertyChanged
    {
        private readonly Dispatcher _dispatcher;

        /// <summary>
        /// 绑定到 ListView / DataGrid 的日志集合。
        /// </summary>
        public BatchObservableCollection<LogEntry> Logs { get; }

        /// <summary>
        /// 初始化视图模型，并传入用于线程封送的 <see cref="Dispatcher"/>（通常来自 View 或 Application.Current.Dispatcher）。
        /// </summary>
        /// <param name="dispatcher">UI 线程的调度器；为 <see langword="null"/> 时按当前线程直接操作集合。</param>
        public LogViewModel(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher;
            Logs = new BatchObservableCollection<LogEntry>
            {
                AddRangeThreshold = 50
            };
        }

        /// <summary>
        /// 在后台线程生成大量日志，再封送回 UI 线程批量加载。
        /// 集合本身不内置 Dispatcher，因此“线程切换”这一步由本方法负责。
        /// </summary>
        /// <param name="count">要生成的日志条数。</param>
        /// <returns>表示异步加载操作的任务。</returns>
        public async Task LoadLogsInBackgroundAsync(int count)
        {
            // 1) 后台线程：仅准备数据，不触碰（非线程安全的）集合。
            List<LogEntry> prepared = await Task.Run(() =>
            {
                var buffer = new List<LogEntry>(count);
                for (int i = 0; i < count; i++)
                {
                    buffer.Add(new LogEntry { Id = i, Message = "Log " + i });
                }

                return buffer;
            });

            // 2) 封送回 UI 线程后再操作集合。
            if (_dispatcher != null && !_dispatcher.CheckAccess())
            {
                await _dispatcher.InvokeAsync(() => Logs.AddRange(prepared));
            }
            else
            {
                Logs.AddRange(prepared);
            }
        }

        /// <summary>
        /// 复杂批量更新：挂起通知 → 清空并加载 → 恢复（仅一次 Reset）。
        /// 同样需确保在 UI 线程执行。
        /// </summary>
        /// <param name="newItems">用于替换的新日志序列。</param>
        public void Rebuild(IEnumerable<LogEntry> newItems)
        {
            Logs.SuspendNotifications();
            try
            {
                Logs.ClearAndAdd(newItems);
            }
            finally
            {
                Logs.ResumeNotifications(); // 统一触发一次 Reset
            }
        }

        /// <summary>
        /// 实现 <see cref="INotifyPropertyChanged"/>，供绑定本视图模型的标量属性（如有）使用。
        /// </summary>
        public event PropertyChangedEventHandler PropertyChanged;
    }
}
