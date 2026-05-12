using System.Windows;
using CFX.Transport;

namespace ProtocolTestBench;

public partial class MainWindow : Window
{
    // 两个日志窗口统一保留最近 MaxLogItems 条记录。
    // 这能防止长时间压测、心跳包或 MQTT 高频推送时 TextBox 和内存无限增长。
    private const int MaxLogItems = 5000;

    public MainWindow()
    {
        InitializeComponent();

        // 各协议页维护自己的默认状态，主窗口只负责启动顺序。
        InitializeCfxDefaults();
        InitializeMqttDefaults();
        InitializeStompDefaults();
        UpdateSocketModeUi();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // 窗口关闭时同时释放四个协议页可能持有的网络资源。
        CloseCfxEndpoint();
        CloseMqttClientOnShutdown();
        CloseStompClientOnShutdown();
        StopSocketEndpoint();
    }
}
