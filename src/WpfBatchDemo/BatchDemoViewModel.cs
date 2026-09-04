using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using WpfApps.Infrastructure.Collections;

namespace WpfBatchDemo
{
    /// <summary>
    /// 演示用日志条目模型。
    /// </summary>
    public class LogEntry
    {
        /// <summary>自增编号，用于在界面上区分条目。</summary>
        public int Id { get; set; }

        /// <summary>日志内容。</summary>
        public string Message { get; set; }

        /// <summary>生成时间。</summary>
        public DateTime CreatedAt { get; set; }

        public override string ToString()
        {
            return string.Format("[{0}] {1}", Id, Message);
        }
    }

    /// <summary>
    /// 主视图模型：演示 <see cref="BatchObservableCollection{T}"/> 的全部核心特性。
    /// <para>线程约定：集合不是线程安全的——后台线程只负责“生产数据”，
    /// 一切对集合的修改都封送回 UI 线程执行（见 <see cref="ExecuteBackgroundLoadAsync"/>）。</para>
    /// </summary>
    public class BatchDemoViewModel : INotifyPropertyChanged
    {
        private readonly Dispatcher _dispatcher;
        private readonly Random _random = new Random();
        private int _nextId;

        // ---- 通知计数器：直观对比“逐项通知”与“单次 Reset”的差异 ----
        private int _addNotifyCount;
        private int _removeNotifyCount;
        private int _resetNotifyCount;
        private int _replaceNotifyCount;
        private int _moveNotifyCount;
        private int _propertyNotifyCount;

        private string _lastOperation = "就绪。点击左侧按钮体验不同的批量操作。";
        private double _threshold;

        /// <summary>
        /// 绑定到列表的批量可观察集合。
        /// </summary>
        public BatchObservableCollection<LogEntry> Logs { get; }

        /// <summary>
        /// 构造视图模型，并订阅集合事件用于统计通知次数。
        /// </summary>
        /// <param name="dispatcher">UI 线程调度器，用于后台线程数据封送。</param>
        public BatchDemoViewModel(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher;
            Logs = new BatchObservableCollection<LogEntry>();
            Threshold = Logs.AddRangeThreshold; // 初始 50，绑定到滑块

            // 订阅 CollectionChanged 统计每种通知的触发次数（演示用，不影响集合本身）。
            Logs.CollectionChanged += OnLogsCollectionChanged;
            // 注意：ObservableCollection<T> 的 PropertyChanged 是显式接口实现（INotifyPropertyChanged.PropertyChanged），
            // 不能直接在类引用上订阅（CS0122），必须转换为接口类型后再订阅。
            ((INotifyPropertyChanged)Logs).PropertyChanged += OnLogsPropertyChanged;

            // 初始化命令。
            AddSmallCommand = new RelayCommand(ExecuteAddSmall);
            AddLargeCommand = new RelayCommand(ExecuteAddLarge);
            RemoveBatchCommand = new RelayCommand(ExecuteRemoveBatch, () => Logs.Count > 0);
            ReplaceAllCommand = new RelayCommand(ExecuteReplaceAll);
            SuspendDemoCommand = new RelayCommand(ExecuteSuspendDemo);
            BackgroundLoadCommand = new RelayCommand(() => { _ = ExecuteBackgroundLoadAsync(); });
            ClearCommand = new RelayCommand(ExecuteClear);
            ResetStatsCommand = new RelayCommand(ExecuteResetStats);
        }

        // ---- 绑定属性 ----

        /// <summary>AddRange 阈值（滑块双向绑定，实时写入集合属性）。</summary>
        public double Threshold
        {
            get { return _threshold; }
            set
            {
                // BatchObservableCollection 内部会校验 < 1 的赋值，这里夹住滑块范围即可。
                _threshold = Math.Max(1, value);
                Logs.AddRangeThreshold = (int)_threshold;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ThresholdText));
            }
        }

        /// <summary>阈值的展示文本。</summary>
        public string ThresholdText
        {
            get { return string.Format("阈值 = {0}（AddRange 项数 > 阈值 → 单次 Reset；≤ 阈值 → 逐项 Add）", (int)_threshold); }
        }

        /// <summary>当前条目数。</summary>
        public int ItemCount
        {
            get { return Logs.Count; }
        }

        /// <summary>是否已有数据（控制移除 / 清空按钮可用性）。</summary>
        public bool HasItems
        {
            get { return Logs.Count > 0; }
        }

        /// <summary>最近一次操作的描述（含耗时与触发的通知类型）。</summary>
        public string LastOperation
        {
            get { return _lastOperation; }
            set
            {
                _lastOperation = value;
                OnPropertyChanged();
            }
        }

        /// <summary>通知统计摘要（每次通知后刷新）。</summary>
        public string StatsText
        {
            get
            {
                return string.Format(
                    "通知统计  |  Add: {0}   Remove: {1}   Replace: {2}   Move: {3}   Reset: {4}   PropertyChanged: {5}",
                    _addNotifyCount, _removeNotifyCount, _replaceNotifyCount,
                    _moveNotifyCount, _resetNotifyCount, _propertyNotifyCount);
            }
        }

        // ---- 命令 ----

        /// <summary>小批量添加：项数 ≤ 阈值，逐项触发 Add 通知。</summary>
        public ICommand AddSmallCommand { get; }

        /// <summary>大批量添加：项数 > 阈值，仅触发一次 Reset。</summary>
        public ICommand AddLargeCommand { get; }

        /// <summary>批量移除：随机挑一批条目删除，仅触发一次 Reset。</summary>
        public ICommand RemoveBatchCommand { get; }

        /// <summary>整体替换：清空 + 重新加载一批数据，仅触发一次 Reset。</summary>
        public ICommand ReplaceAllCommand { get; }

        /// <summary>挂起 / 恢复演示：挂起后连续多次变更，恢复时仅触发一次 Reset。</summary>
        public ICommand SuspendDemoCommand { get; }

        /// <summary>后台线程加载：Task.Run 生产数据 + Dispatcher 封送回 UI 线程。</summary>
        public ICommand BackgroundLoadCommand { get; }

        /// <summary>清空集合。</summary>
        public ICommand ClearCommand { get; }

        /// <summary>重置统计计数器。</summary>
        public ICommand ResetStatsCommand { get; }

        // ---- 命令实现 ----

        /// <summary>小批量添加 10 条（默认阈值下走逐项 Add 路径）。</summary>
        private void ExecuteAddSmall()
        {
            var sw = Stopwatch.StartNew();
            List<LogEntry> items = Generate(10);

            Logs.AddRange(items);
            sw.Stop();

            LastOperation = string.Format(
                "AddRange ×10 → 逐项 Add 通知（项数 ≤ 阈值 {0}），耗时 {1:F2} ms。注意右侧 Add 计数 +10。",
                Logs.AddRangeThreshold, sw.Elapsed.TotalMilliseconds);
        }

        /// <summary>大批量添加 5000 条（走单次 Reset 路径）。</summary>
        private void ExecuteAddLarge()
        {
            var sw = Stopwatch.StartNew();
            List<LogEntry> items = Generate(5000);

            Logs.AddRange(items);
            sw.Stop();

            LastOperation = string.Format(
                "AddRange ×5000 → 单次 Reset 通知（项数 > 阈值 {0}），耗时 {1:F2} ms。Reset 计数仅 +1，UI 无卡顿。",
                Logs.AddRangeThreshold, sw.Elapsed.TotalMilliseconds);
        }

        /// <summary>随机移除一批条目。</summary>
        private void ExecuteRemoveBatch()
        {
            if (Logs.Count == 0)
            {
                return;
            }

            var sw = Stopwatch.StartNew();

            // 随机挑“至多一半、至少 5 条”的条目（按引用挑选，LogEntry 未重写 Equals，引用即唯一）。
            int takeCount = Math.Max(5, Logs.Count / 4);
            List<LogEntry> victims = Enumerable.Range(0, takeCount)
                .Select(_ => Logs[_random.Next(Logs.Count)])
                .Distinct()
                .ToList();

            Logs.RemoveRange(victims);
            sw.Stop();

            LastOperation = string.Format(
                "RemoveRange ×{0} → 单次 Reset 通知，耗时 {1:F2} ms（内部为 O(n+m) 倒序定位移除）。",
                victims.Count, sw.Elapsed.TotalMilliseconds);
        }

        /// <summary>用一批新数据整体替换现有内容。</summary>
        private void ExecuteReplaceAll()
        {
            var sw = Stopwatch.StartNew();
            List<LogEntry> items = Generate(800);

            Logs.ReplaceAll(items);
            sw.Stop();

            LastOperation = string.Format(
                "ReplaceAll ×800 → 单次 Reset 通知（清空 + 重填只刷新一次），耗时 {0:F2} ms。",
                sw.Elapsed.TotalMilliseconds);
        }

        /// <summary>
        /// 挂起 / 恢复演示：挂起期间连续执行 3 次小批量添加 + 1 次移除，
        /// 挂起期间所有通知被吞掉，恢复时统一触发一次 Reset。
        /// </summary>
        private void ExecuteSuspendDemo()
        {
            var sw = Stopwatch.StartNew();
            int before = _resetNotifyCount;

            Logs.SuspendNotifications();
            try
            {
                // 挂起期间：这些操作默认会触发大量通知，但现在全部静默。
                Logs.AddRange(Generate(50));
                Logs.AddRange(Generate(50));
                Logs.AddRange(Generate(50));
                if (Logs.Count > 0)
                {
                    Logs.RemoveRange(new[] { Logs[0], Logs[Math.Min(5, Logs.Count - 1)] });
                }
            }
            finally
            {
                // finally 保证即使中途异常也必然恢复，否则集合会永久“失聪”。
                Logs.ResumeNotifications();
            }

            sw.Stop();

            LastOperation = string.Format(
                "Suspend → 3×AddRange(50) + RemoveRange(2) → Resume：挂起期间 0 通知，恢复时 Reset 仅 +{0}，耗时 {1:F2} ms。",
                _resetNotifyCount - before, sw.Elapsed.TotalMilliseconds);
        }

        /// <summary>
        /// 后台线程加载 3000 条：后台线程只生产数据（不触碰集合），
        /// 之后封送回 UI 线程调用 AddRange——集合本身不内置 Dispatcher。
        /// </summary>
        private async Task ExecuteBackgroundLoadAsync()
        {
            var sw = Stopwatch.StartNew();

            // 1) 后台线程：只准备数据，绝不访问非线程安全的集合。
            List<LogEntry> prepared = await Task.Run(() => Generate(3000));

            // 2) 封送回 UI 线程后再修改集合。
            if (_dispatcher != null && !_dispatcher.CheckAccess())
            {
                await _dispatcher.InvokeAsync(() => Logs.AddRange(prepared));
            }
            else
            {
                Logs.AddRange(prepared);
            }

            sw.Stop();

            LastOperation = string.Format(
                "后台 Task.Run 生产 3000 条 → Dispatcher 封送回 UI 线程 AddRange → 单次 Reset，总耗时 {0:F2} ms。",
                sw.Elapsed.TotalMilliseconds);
        }

        /// <summary>清空集合（走 Reset 路径）。</summary>
        private void ExecuteClear()
        {
            var sw = Stopwatch.StartNew();
            Logs.ClearAndAdd(null);
            sw.Stop();

            LastOperation = string.Format("ClearAndAdd(null) → 单次 Reset 通知，耗时 {0:F2} ms。", sw.Elapsed.TotalMilliseconds);
        }

        /// <summary>重置统计计数器。</summary>
        private void ExecuteResetStats()
        {
            _addNotifyCount = 0;
            _removeNotifyCount = 0;
            _resetNotifyCount = 0;
            _replaceNotifyCount = 0;
            _moveNotifyCount = 0;
            _propertyNotifyCount = 0;

            OnPropertyChanged(nameof(StatsText));
            LastOperation = "通知统计已清零。";
        }

        // ---- 辅助 ----

        /// <summary>生成 count 条演示日志。</summary>
        private List<LogEntry> Generate(int count)
        {
            var list = new List<LogEntry>(count);
            for (int i = 0; i < count; i++)
            {
                int id = _nextId++;
                list.Add(new LogEntry
                {
                    Id = id,
                    Message = string.Format("日志 #{0} —— 由【{1}】生成", id, _random.Next(1000)),
                    CreatedAt = DateTime.Now
                });
            }

            return list;
        }

        /// <summary>统计 CollectionChanged 各 Action 的触发次数。</summary>
        private void OnLogsCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Add:
                    _addNotifyCount++;
                    break;
                case NotifyCollectionChangedAction.Remove:
                    _removeNotifyCount++;
                    break;
                case NotifyCollectionChangedAction.Replace:
                    _replaceNotifyCount++;
                    break;
                case NotifyCollectionChangedAction.Move:
                    _moveNotifyCount++;
                    break;
                case NotifyCollectionChangedAction.Reset:
                    _resetNotifyCount++;
                    break;
            }

            // Count / 统计文本一并刷新。
            OnPropertyChanged(nameof(ItemCount));
            OnPropertyChanged(nameof(HasItems));
            OnPropertyChanged(nameof(StatsText));
        }

        /// <summary>统计 PropertyChanged（Count / Item[]）的触发次数。</summary>
        private void OnLogsPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            _propertyNotifyCount++;
            OnPropertyChanged(nameof(StatsText));
        }

        #region INotifyPropertyChanged

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion
    }

    /// <summary>
    /// 极简 <see cref="ICommand"/> 实现：把委托包装成命令（演示程序足够）。
    /// </summary>
    public class RelayCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool> _canExecute;

        public RelayCommand(Action execute, Func<bool> canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public bool CanExecute(object parameter)
        {
            return _canExecute == null || _canExecute();
        }

        public void Execute(object parameter)
        {
            _execute();
        }

        public event EventHandler CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }
    }
}
