using System.Windows;

namespace ProtocolTestBench;

/// <summary>
/// WPF 应用入口类型。
/// App.xaml 通过 StartupUri 指向 MainWindow.xaml，因此这里不需要手动创建窗口。
/// 后续如果要加入全局异常处理、启动参数解析或应用级依赖初始化，可以从这个类扩展。
/// </summary>
public partial class App : Application
{
}
