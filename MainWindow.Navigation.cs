using System.Windows;

namespace ProtocolTestBench;

public partial class MainWindow
{
    // 顶部菜单点击 Socket 时，只切换界面可见性，不停止当前网络连接。
    // 这样用户可以在 Socket 和 CFX 页之间来回查看配置或日志。
    private void SocketMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ShowSocketView();
    }

    // 顶部菜单点击 CFX / AMQP 时展示 CFX 调试页，Socket 页资源保持原状。
    private void CfxMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ShowCfxView();
    }

    // 顶部菜单点击 MQTT 时展示 MQTT 调试页，其他协议页资源保持原状。
    private void MqttMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ShowMqttView();
    }

    // 顶部菜单点击 STOMP 时展示 STOMP 调试页，其他协议页资源保持原状。
    private void StompMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ShowStompView();
    }

    private void ShowSocketView()
    {
        // 四个视图共用同一个窗口区域，通过 Visibility 做轻量切换。
        SocketView.Visibility = Visibility.Visible;
        CfxView.Visibility = Visibility.Collapsed;
        MqttView.Visibility = Visibility.Collapsed;
        StompView.Visibility = Visibility.Collapsed;

        // MenuItem 是可勾选项，这里手动维护互斥选中状态，让用户清楚当前在哪个页面。
        SocketMenuItem.IsChecked = true;
        CfxMenuItem.IsChecked = false;
        MqttMenuItem.IsChecked = false;
        StompMenuItem.IsChecked = false;

        // 标题栏同步当前协议页，截图或远程协助时更容易识别当前模式。
        Title = "ProtocolTestBench - Socket";
    }

    private void ShowCfxView()
    {
        // CFX 页显示时隐藏其他协议页，避免多套日志和发送区同时挤在界面上。
        SocketView.Visibility = Visibility.Collapsed;
        CfxView.Visibility = Visibility.Visible;
        MqttView.Visibility = Visibility.Collapsed;
        StompView.Visibility = Visibility.Collapsed;

        // 菜单勾选状态保持单选效果。
        SocketMenuItem.IsChecked = false;
        CfxMenuItem.IsChecked = true;
        MqttMenuItem.IsChecked = false;
        StompMenuItem.IsChecked = false;

        // 标题栏同步到 CFX / AMQP。
        Title = "ProtocolTestBench - CFX / AMQP";
    }

    private void ShowMqttView()
    {
        // MQTT 页显示时，同样只保留一个协议页可见，减少界面干扰。
        SocketView.Visibility = Visibility.Collapsed;
        CfxView.Visibility = Visibility.Collapsed;
        MqttView.Visibility = Visibility.Visible;
        StompView.Visibility = Visibility.Collapsed;

        SocketMenuItem.IsChecked = false;
        CfxMenuItem.IsChecked = false;
        MqttMenuItem.IsChecked = true;
        StompMenuItem.IsChecked = false;

        // 标题栏同步到 MQTT，便于用户确认当前发送的是哪种协议。
        Title = "ProtocolTestBench - MQTT";
    }

    private void ShowStompView()
    {
        // STOMP 页显示时同样只保留一个协议页，避免调试内容互相干扰。
        SocketView.Visibility = Visibility.Collapsed;
        CfxView.Visibility = Visibility.Collapsed;
        MqttView.Visibility = Visibility.Collapsed;
        StompView.Visibility = Visibility.Visible;

        SocketMenuItem.IsChecked = false;
        CfxMenuItem.IsChecked = false;
        MqttMenuItem.IsChecked = false;
        StompMenuItem.IsChecked = true;

        // 标题栏同步到 STOMP，方便区分当前使用的消息协议。
        Title = "ProtocolTestBench - STOMP";
    }
}
