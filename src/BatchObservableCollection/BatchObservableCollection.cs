using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;

namespace WpfApps.Infrastructure.Collections
{
    /// <summary>
    /// 面向 WPF 数据绑定的高性能批量可观察集合。
    /// 在完全兼容 <see cref="ObservableCollection{T}"/> 的基础上，提供：
    /// <list type="bullet">
    /// <item><description>大批量添加 / 移除 / 替换时的单次 <see cref="NotifyCollectionChangedAction.Reset"/> 通知，避免海量逐项通知造成 UI 卡顿；</description></item>
    /// <item><description>小批量添加仍逐项触发 Add 通知，以保留 ListView / DataGrid 的增量动画与滚动位置；</description></item>
    /// <item><description><see cref="SuspendNotifications"/> / <see cref="ResumeNotifications"/> 通知挂起机制，用于复杂批量操作仅触发一次 Reset；亦可通过 <see cref="BeginUpdate"/> 以 using 语句自动配对；</description></item>
    /// <item><description>所有变更均经 <see cref="ObservableCollection{T}.CheckReentrancy"/> 保护，防止集合在事件处理期间被重入修改。</description></item>
    /// </list>
    /// </summary>
    /// <typeparam name="T">集合中元素的类型。</typeparam>
    /// <remarks>
    /// <para><b>线程模型：</b>本集合本身<b>不是线程安全</b>的，也不会通过 <c>Dispatcher</c> 自动将数据变更封送到 UI 线程。
    /// 若从后台线程（例如 <see cref="System.Threading.Tasks.Task"/>）更新集合，调用方必须自行将调用封送至 UI 线程
    /// （如 <c>Dispatcher.InvokeAsync</c> 或 <c>Dispatcher.Invoke</c>）。参见附带的 <c>LogViewModel</c> 示例。</para>
    /// <para><b>重入保护：</b>每次变更方法都会调用 <see cref="ObservableCollection{T}.CheckReentrancy"/>。
    /// 当集合的 <see cref="INotifyCollectionChanged.CollectionChanged"/> 事件拥有多个订阅者且正在被处理时，
    /// 任何再次修改集合的尝试都会抛出 <see cref="InvalidOperationException"/>。</para>
    /// </remarks>
    public class BatchObservableCollection<T> : ObservableCollection<T>
    {
        /// <summary>
        /// <see cref="AddRangeThreshold"/> 的默认值：一次添加超过 50 个元素时改用 Reset 通知。
        /// 该值兼顾了两个方面——小于它时逐项 Add 通知的 UI 开销可接受（保留增量动画），
        /// 大于它时逐项通知的数量已足以造成明显卡顿，值得整体 Reset 一次。
        /// </summary>
        private const int DefaultAddRangeThreshold = 50;

        /// <summary>
        /// 通知挂起的<b>嵌套计数器</b>。
        /// 每调用一次 <see cref="SuspendNotifications"/> 加一，每调用一次 <see cref="ResumeNotifications"/> 减一；
        /// 仅当计数从 1 归 0 时才真正恢复通知并补发一次 Reset。
        /// 使用计数器而非布尔标记，是为了支持“外层方法挂起 → 内层方法也挂起 → 各自恢复”的嵌套调用场景。
        /// 本字段只能在 UI 线程访问（集合整体非线程安全）。
        /// </summary>
        private int _suspensionCount;

        /// <summary>
        /// 内部批量写入开关：在 <see cref="AddRange"/> / <see cref="RemoveRange"/> / <see cref="ReplaceAll"/>
        /// 静默改写底层列表期间置为 <see langword="true"/>，使逐项产生的
        /// <see cref="OnCollectionChanged"/> / <see cref="OnPropertyChanged"/> 调用被短路，
        /// 操作结束后由调用方统一补发一次 Reset。
        /// 与 <see cref="_suspensionCount"/> 的区别：它表达“本方法正在批量写入”，是方法内部的临时状态；
        /// 而 <see cref="_suspensionCount"/> 表达“调用方主动挂起了通知”，是跨方法调用的外部状态。
        /// </summary>
        private bool _batchSuppress;

        /// <summary>
        /// 挂起期间发生过变更的脏标记：仅当 <see cref="_suspensionCount"/> 从 1 归 0 且此标记为
        /// <see langword="true"/> 时，<see cref="ResumeNotifications"/> 才补发 Reset，
        /// 避免挂起期间“无任何修改”时恢复通知引发一次多余的整表刷新。
        /// 仅在通知被挂起（<see cref="_suspensionCount"/> &gt; 0）时置位。
        /// </summary>
        private bool _resetPending;

        /// <summary>
        /// <see cref="AddRangeThreshold"/> 属性的后备存储。默认取 <see cref="DefaultAddRangeThreshold"/>。
        /// 通过带校验的属性 setter 写入，保证运行时不会被设为无效值。
        /// </summary>
        private int _addRangeThreshold = DefaultAddRangeThreshold;

        /// <summary>
        /// 获取或设置“大批量”阈值。当 <see cref="AddRange"/> 一次性添加的元素数量<b>大于</b>该值时，
        /// 使用单次 <see cref="NotifyCollectionChangedAction.Reset"/> 通知；否则逐项触发 Add 通知。
        /// </summary>
        /// <value>默认值为 50；取值必须大于等于 1，否则 setter 抛出 <see cref="ArgumentOutOfRangeException"/>。</value>
        /// <exception cref="ArgumentOutOfRangeException">赋值小于 1 时抛出（保持原值不变）。</exception>
        public int AddRangeThreshold
        {
            get { return _addRangeThreshold; }
            set
            {
                if (value < 1)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), value, "阈值必须 >= 1");
                }

                _addRangeThreshold = value;
            }
        }

        /// <summary>
        /// 获取一个值，指示变更通知当前是否处于挂起状态（即 <see cref="SuspendNotifications"/> 的嵌套计数大于 0）。
        /// </summary>
        public bool NotificationsSuspended
        {
            get { return _suspensionCount > 0; }
        }

        /// <summary>
        /// 初始化 <see cref="BatchObservableCollection{T}"/> 类的新实例（空集合）。
        /// </summary>
        /// <param name="resetThreshold">
        /// <see cref="AddRangeThreshold"/> 的初始值，必须大于等于 1；缺省为 50。
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="resetThreshold"/> 小于 1 时抛出。</exception>
        public BatchObservableCollection(int resetThreshold = DefaultAddRangeThreshold)
        {
            // 复用属性 setter 的校验逻辑，避免两处维护相同的取值范围规则。
            AddRangeThreshold = resetThreshold;
        }

        /// <summary>
        /// 使用指定序列中的元素初始化 <see cref="BatchObservableCollection{T}"/> 类的新实例。
        /// </summary>
        /// <param name="collection">用于填充集合的初始元素序列。允许为 <see langword="null"/>（集合将为空）。</param>
        /// <remarks>构造期间不触发任何通知（此时尚无订阅者，逐项走 <c>base.Add</c> 即可）。</remarks>
        public BatchObservableCollection(IEnumerable<T> collection)
        {
            if (collection != null)
            {
                foreach (T item in collection)
                {
                    base.Add(item);
                }
            }
        }

        // ---- 通知抑制 ----

        /// <summary>
        /// 重写集合变更通知：在通知挂起（<see cref="SuspendNotifications"/>）或内部批处理抑制期间，不向外抛出事件。
        /// </summary>
        /// <param name="e">集合变更事件参数。</param>
        protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
        {
            if (_suspensionCount > 0 || _batchSuppress)
            {
                // 仅在“调用方主动挂起”期间记录脏标记；内部批量写入的 Reset 由批量方法自身负责补发。
                if (_suspensionCount > 0)
                {
                    _resetPending = true;
                }
                return;
            }

            base.OnCollectionChanged(e);
        }

        /// <summary>
        /// 重写属性变更通知：与 <see cref="OnCollectionChanged"/> 同步抑制，避免批量 / 挂起期间
        /// 抛出 <c>Count</c> / <c>Item[]</c> 的 <see cref="INotifyPropertyChanged.PropertyChanged"/>。
        /// </summary>
        /// <param name="e">属性变更事件参数。</param>
        protected override void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            if (_suspensionCount > 0 || _batchSuppress)
            {
                if (_suspensionCount > 0)
                {
                    _resetPending = true;
                }
                return;
            }

            base.OnPropertyChanged(e);
        }

        // ---- 批量操作 ----

        /// <summary>
        /// 将指定序列中的元素一次性追加到集合末尾，并按阈值选择通知策略。
        /// </summary>
        /// <param name="items">要追加的元素序列。允许为 <see langword="null"/> 或空序列（方法直接返回，不触发通知）。</param>
        /// <remarks>
        /// <para><b>通知策略：</b></para>
        /// <list type="bullet">
        /// <item><description>元素数量 <b>大于</b> <see cref="AddRangeThreshold"/>：先静默写入底层列表，再触发<b>单次</b>
        /// <see cref="NotifyCollectionChangedAction.Reset"/>（适用于万级数据加载，避免 UI 卡顿）。</description></item>
        /// <item><description>元素数量 <b>小于等于</b> 阈值：逐项触发 Add 通知，保留 ListView / DataGrid 的增量动画与滚动位置。</description></item>
        /// </list>
        /// <para>若当前处于 <see cref="SuspendNotifications"/> 挂起状态，则仅写入底层列表而不触发任何通知；
        /// 挂起结束后由 <see cref="ResumeNotifications"/> 统一触发一次 Reset。</para>
        /// <para>本方法内部调用 <see cref="ObservableCollection{T}.CheckReentrancy"/> 以防重入修改。</para>
        /// </remarks>
        public void AddRange(IEnumerable<T> items)
        {
            if (items == null)
            {
                return;
            }

            CheckReentrancy();

            // 若 items 本身就是 IList<T>（如 List<T>、T[]），直接复用避免 ToList() 的一次完整复制；
            // 否则（如 LINQ 惰性序列）先物化成列表，防止枚举过程中源序列被修改或重复求值。
            IList<T> list = items as IList<T> ?? items.ToList();
            if (list.Count == 0)
            {
                return;
            }

            // 决定通知策略：
            //   - 已挂起通知：无论多少项都静默写入，Reset 由 ResumeNotifications 统一补发；
            //   - 未挂起且数量超阈值：静默写入后本方法补发一次 Reset；
            //   - 未挂起且数量未超阈值：逐项 base.Add，保留逐项 Add 通知。
            bool suppressDuringWrite = _suspensionCount > 0 || list.Count > AddRangeThreshold;
            if (!suppressDuringWrite)
            {
                foreach (T item in list)
                {
                    base.Add(item);
                }

                return;
            }

            // 静默写入阶段：抑制逐项通知。使用 try/finally 保证即使元素回调
            // （例如某个 GetHashCode/Equals 或后续异常）抛出，抑制标志也必然被复位，
            // 否则集合会永久“失聪”，之后所有变更都不再通知 UI。
            _batchSuppress = true;
            try
            {
                foreach (T item in list)
                {
                    // 走 base.InsertItem 而非直接改 base.Items：保持虚方法分发点，
                    // 若子类重写 InsertItem 仍能生效；Count 每轮递增，等价于 append。
                    base.InsertItem(Count, item);
                }
            }
            finally
            {
                _batchSuppress = false;
            }

            // 仅“未挂起 + 超阈值”路径在此补发 Reset；挂起路径交由 ResumeNotifications 补发。
            if (_suspensionCount == 0)
            {
                RaiseReset();
            }
        }

        /// <summary>
        /// 从集合中移除与 <paramref name="items"/> 中元素相等（默认相等比较器）的每一项，结束后触发一次 Reset。
        /// </summary>
        /// <param name="items">要移除的元素序列。允许为 <see langword="null"/> 或空序列（方法直接返回）。</param>
        /// <remarks>
        /// <para>无论移除多少项，本方法都触发<b>单次</b> <see cref="NotifyCollectionChangedAction.Reset"/>
        /// （非连续位置的多项移除无法用单一 Remove 通知精确表达，Reset 是最稳妥的策略）。
        /// 与逐项 <c>Remove</c> 的语义一致：<paramref name="items"/> 中某元素出现 k 次，
        /// 集合中该元素的<b>前 k 个</b>匹配项会被移除（多重集语义）。</para>
        /// <para><b>性能说明：</b>不逐个调用 <see cref="ObservableCollection{T}.Remove(T)"/>
        /// （其每次都要从头线性查找，整体复杂度 O(n×m)），而是先统计待移除元素的出现次数，
        /// 再从尾部向前一遍扫描定位，复杂度 O(n+m)；倒序移除还可避免正序移除时反复搬移后续元素。
        /// 若没有任何元素匹配，则不触发任何通知（避免无意义的 UI 整体刷新）。</para>
        /// <para>若处于 <see cref="SuspendNotifications"/> 挂起状态，则不触发任何通知，由恢复时统一 Reset。
        /// 本方法内部调用 <see cref="ObservableCollection{T}.CheckReentrancy"/>。</para>
        /// </remarks>
        public void RemoveRange(IEnumerable<T> items)
        {
            if (items == null)
            {
                return;
            }

            CheckReentrancy();

            // 统计“待移除元素 → 还需移除的出现次数”。
            // 用 Dictionary 而非 HashSet，是为了精确保持多重集语义：
            // items 中同一元素出现 k 次，则集合中该元素恰好移除 k 个匹配项，
            // 与“逐个调用 k 次 base.Remove(item)”的最终结果完全一致。
            // EqualityComparer<T>.Default 与 base.Remove 内部使用的相等比较完全相同（含 T 为 null 的情况）。
            Dictionary<T, int> pendingCounts = new Dictionary<T, int>(EqualityComparer<T>.Default);
            foreach (T item in items)
            {
                int count;
                pendingCounts.TryGetValue(item, out count);
                pendingCounts[item] = count + 1;
            }

            if (pendingCounts.Count == 0)
            {
                return;
            }

            bool removedAny = false;

            // 静默移除阶段：抑制逐项通知；try/finally 保证抑制标志必然复位。
            _batchSuppress = true;
            try
            {
                // 从尾向头扫描：移除元素时只需搬移“已扫过”的部分，整体搬移量最小；
                // 且索引不会因移除操作而失效（尾部之后的元素已处理完毕）。
                for (int i = Count - 1; i >= 0; i--)
                {
                    T current = this[i];

                    int remaining;
                    if (pendingCounts.TryGetValue(current, out remaining) && remaining > 0)
                    {
                        // RemoveItem(index) 跳过了 base.Remove 的从头查找（O(n) → O(1) 定位），
                        // 并与 base.Remove 一样会触发被抑制的逐项通知与重入检查。
                        RemoveItem(i);
                        pendingCounts[current] = remaining - 1;
                        removedAny = true;
                    }
                }
            }
            finally
            {
                _batchSuppress = false;
            }

            // 一个元素都没匹配到时保持静默，避免无意义的 Reset 引发整表刷新。
            if (removedAny && _suspensionCount == 0)
            {
                RaiseReset();
            }
        }

        /// <summary>
        /// 用新的元素序列整体替换集合中的现有内容，结束后触发一次 Reset。
        /// </summary>
        /// <param name="items">用于替换的新元素序列。允许为 <see langword="null"/>（此时集合被清空）。</param>
        /// <remarks>
        /// <para>等价于“先清空再批量添加”，但仅触发<b>单次</b> <see cref="NotifyCollectionChangedAction.Reset"/> 通知。
        /// 若处于 <see cref="SuspendNotifications"/> 挂起状态，则不触发任何通知，由恢复时统一 Reset。</para>
        /// <para>本方法内部调用 <see cref="ObservableCollection{T}.CheckReentrancy"/>。</para>
        /// </remarks>
        public void ReplaceAll(IEnumerable<T> items)
        {
            CheckReentrancy();

            // 清空 + 重填全程静默，最后统一补发一次 Reset（或交由 ResumeNotifications 补发）。
            _batchSuppress = true;
            try
            {
                base.ClearItems();
                if (items != null)
                {
                    IList<T> list = items as IList<T> ?? items.ToList();
                    foreach (T item in list)
                    {
                        base.InsertItem(Count, item);
                    }
                }
            }
            finally
            {
                _batchSuppress = false;
            }

            if (_suspensionCount == 0)
            {
                RaiseReset();
            }
        }

        /// <summary>
        /// 先清空集合，再一次性添加新元素，结束后触发一次 Reset。
        /// 语义上与 <see cref="ReplaceAll"/> 一致；提供该方法以更直观地表达“清空并加载”的意图。
        /// </summary>
        /// <param name="items">要添加的新元素序列。允许为 <see langword="null"/>（仅执行清空）。</param>
        /// <remarks>本方法直接委托给 <see cref="ReplaceAll"/>，同样受挂起机制与重入保护约束。</remarks>
        public void ClearAndAdd(IEnumerable<T> items)
        {
            ReplaceAll(items);
        }

        // ---- 通知挂起 ----

        /// <summary>
        /// 挂起集合的变更通知。在需要连续执行多次变更（或与其他集合操作组合）时使用，
        /// 可避免其间每一次变更都触发 UI 刷新；与之配对的 <see cref="ResumeNotifications"/> 会一次性触发 Reset。
        /// </summary>
        /// <remarks>
        /// <para>支持<b>嵌套</b>：内部以计数器维护，只有当 <see cref="ResumeNotifications"/> 的调用次数
        /// 与 <see cref="SuspendNotifications"/> 相等时，才真正恢复通知并触发 Reset。</para>
        /// <para>挂起期间对集合的修改不会对外通知；若挂起期间发生过修改，
        /// 恢复时（计数归零）会触发一次 Reset；若挂起期间无任何修改，恢复时不会触发通知。</para>
        /// <para>本方法内部调用 <see cref="ObservableCollection{T}.CheckReentrancy"/>。</para>
        /// </remarks>
        public void SuspendNotifications()
        {
            CheckReentrancy();
            _suspensionCount++;
        }

        /// <summary>
        /// 恢复集合的变更通知。若此前存在与之配对的 <see cref="SuspendNotifications"/> 嵌套调用，
        /// 仅当嵌套计数归零时才真正恢复；此时若挂起期间<b>发生过实际变更</b>（脏标记置位），
        /// 则统一触发一次 <see cref="NotifyCollectionChangedAction.Reset"/>；
        /// 若挂起期间没有任何修改，则不补发 Reset，避免无意义的整表刷新。
        /// </summary>
        /// <exception cref="InvalidOperationException">在未处于挂起状态（Resume 调用次数多于 Suspend）时调用将抛出。</exception>
        /// <remarks>本方法内部调用 <see cref="ObservableCollection{T}.CheckReentrancy"/>。</remarks>
        public void ResumeNotifications()
        {
            CheckReentrancy();

            if (_suspensionCount == 0)
            {
                throw new InvalidOperationException(
                    "ResumeNotifications 的调用次数多于 SuspendNotifications：集合当前并未挂起通知。");
            }

            _suspensionCount--;
            if (_suspensionCount == 0 && _resetPending)
            {
                _resetPending = false;
                RaiseReset();
            }
        }

        /// <summary>
        /// 以 <see cref="IDisposable"/> 方式挂起通知，配合 <c>using</c> 语句自动恢复，无需手动配对：
        /// <code>
        /// using (collection.BeginUpdate())
        /// {
        ///     collection.Add(item1);
        ///     collection.RemoveAt(0);
        /// } // 此处统一补发一次 Reset（若期间发生过修改）
        /// </code>
        /// 内部即调用 <see cref="SuspendNotifications"/> / <see cref="ResumeNotifications"/>，
        /// 支持嵌套（与手动挂起共用同一计数器），同样受重入保护约束。
        /// </summary>
        /// <returns>Dispose 时调用 <see cref="ResumeNotifications"/> 的作用域对象。</returns>
        public IDisposable BeginUpdate()
        {
            SuspendNotifications();
            return new UpdateScope(this);
        }

        // ---- 内部辅助 ----

        /// <summary>
        /// 在批量 / 挂起操作结束后，统一对外触发一次 Reset 通知，并附带 Count / Item[] 属性变更。
        /// </summary>
        /// <remarks>
        /// 属性变更先于集合变更发出：与 <see cref="ObservableCollection{T}"/> 基类在 InsertItem / RemoveItem 中的
        /// 通知顺序保持一致（先 Count / Item[]，再 CollectionChanged），确保绑定引擎先读到新的 Count 再处理 Reset。
        /// </remarks>
        private void RaiseReset()
        {
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }

        /// <summary>
        /// <see cref="BeginUpdate"/> 返回的通知挂起作用域对象。
        /// Dispose 时通知所属集合恢复通知；内部置空引用以防止 Dispose 被调用两次导致挂起计数失衡。
        /// </summary>
        private sealed class UpdateScope : IDisposable
        {
            private BatchObservableCollection<T> _owner;

            public UpdateScope(BatchObservableCollection<T> owner)
            {
                _owner = owner;
            }

            public void Dispose()
            {
                var owner = _owner;
                _owner = null; // 防止 Dispose 被调用两次导致计数失衡
                if (owner != null)
                {
                    owner.ResumeNotifications();
                }
            }
        }
    }
}
