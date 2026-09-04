using System.Windows;

namespace WpfBatchDemo
{
    /// <summary>
    /// 主窗口：仅负责把视图模型挂到 DataContext。
    /// 视图模型需要 Dispatcher 做线程封送，因此用当前窗口的 Dispatcher。
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            DataContext = new BatchDemoViewModel(Dispatcher);
        }
    }
}
