using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MQTTnet;
using MQTTnet.Protocol;

namespace ProtocolTestBench;

public partial class MainWindow
{
    // MQTT 页面的日志缓存，和 Socket / CFX 各自独立，方便切页后继续回看历史。
    private readonly List<string> _mqttLogItems = [];

    // MQTTnet 官方客户端工厂；创建客户端、主题过滤器等对象时统一从这里构造。
    private readonly MqttClientFactory _mqttFactory = new();

    // 当前 MQTT 连接对象以及订阅状态。
    private IMqttClient? _mqttClient;
    private bool _isMqttConnected;
    private bool _isMqttDisconnecting;
    private string? _mqttSubscribedTopic;

    private void InitializeMqttDefaults()
    {
        // 默认放入一段 JSON 文本，便于用户连接后直接做发布连通性测试。
        LoadMqttSampleMessage();
        SetMqttConnectedState(false, "未连接：填写 broker、ClientId 和主题后连接");
    }

    // MQTT 页的连接按钮在“未连接”时建立 broker 连接，在“已连接”时断开连接。
    private async void MqttConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isMqttConnected)
        {
            await CloseMqttClientAsync();
            return;
        }

        await ConnectMqttAsync();
    }

    private async Task ConnectMqttAsync()
    {
        string host = MqttBrokerHostTextBox.Text.Trim();
        string clientId = MqttClientIdTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(host))
        {
            MessageBox.Show("请输入 MQTT Broker 地址。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(clientId))
        {
            MessageBox.Show("请输入 MQTT ClientId。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!TryReadMqttPort(out int port))
        {
            return;
        }

        IMqttClient client = _mqttFactory.CreateMqttClient();
        WireMqttClientEvents(client);

        try
        {
            MqttClientOptionsBuilder optionsBuilder = new MqttClientOptionsBuilder()
                .WithTcpServer(host, port)
                .WithClientId(clientId)
                .WithTimeout(TimeSpan.FromSeconds(10))
                .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
                .WithCleanSession(MqttCleanSessionCheckBox.IsChecked == true);

            string userName = MqttUserNameTextBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(userName))
            {
                optionsBuilder.WithCredentials(userName, MqttPasswordBox.Password);
            }

            _mqttClient = client;
            MqttClientConnectResult result = await client.ConnectAsync(optionsBuilder.Build(), CancellationToken.None);

            _isMqttConnected = true;
            _isMqttDisconnecting = false;
            SetMqttConnectedState(true, $"已连接：{host}:{port}，Result={result.ResultCode}");
            AppendMqttLog("连接", $"已连接到 {host}:{port}，ClientId={clientId}，Result={result.ResultCode}");
        }
        catch (Exception ex)
        {
            client.Dispose();
            _mqttClient = null;
            _isMqttConnected = false;
            _isMqttDisconnecting = false;
            _mqttSubscribedTopic = null;
            SetMqttConnectedState(false, "连接失败");
            AppendMqttLog("错误", $"连接 MQTT Broker 失败：{ex.Message}");
        }
    }

    // 统一挂接 MQTTnet 事件，所有回调都切回 Dispatcher 后再更新 WPF 控件。
    private void WireMqttClientEvents(IMqttClient client)
    {
        client.ConnectedAsync += args => Dispatcher.InvokeAsync(() =>
        {
            string? reason = args.ConnectResult?.ReasonString;
            if (!string.IsNullOrWhiteSpace(reason))
            {
                AppendMqttLog("连接", $"Broker 附加信息：{reason}");
            }
        }).Task;

        client.DisconnectedAsync += args => Dispatcher.InvokeAsync(() =>
        {
            string? detail = args.Exception?.Message;
            if (string.IsNullOrWhiteSpace(detail) && !string.IsNullOrWhiteSpace(args.ReasonString))
            {
                detail = args.ReasonString;
            }

            HandleMqttDisconnected(_isMqttDisconnecting ? "已断开" : "连接已断开", detail);
        }).Task;

        client.ApplicationMessageReceivedAsync += args => Dispatcher.InvokeAsync(() =>
        {
            MqttApplicationMessage message = args.ApplicationMessage;
            string payload = message.ConvertPayloadToString();
            string topic = string.IsNullOrWhiteSpace(message.Topic) ? "(无主题)" : message.Topic;

            AppendMqttLog(
                $"收到 <- {topic} [QoS {(int)message.QualityOfServiceLevel}, Retain={message.Retain}]",
                payload);
        }).Task;
    }

    private async Task CloseMqttClientAsync()
    {
        if (_mqttClient is null)
        {
            HandleMqttDisconnected("未连接");
            return;
        }

        IMqttClient client = _mqttClient;
        _mqttClient = null;
        _isMqttDisconnecting = true;

        try
        {
            if (client.IsConnected)
            {
                MqttClientDisconnectOptions disconnectOptions = _mqttFactory.CreateClientDisconnectOptionsBuilder().Build();
                await client.DisconnectAsync(disconnectOptions, CancellationToken.None);
            }
            else
            {
                HandleMqttDisconnected("未连接");
            }
        }
        catch (Exception ex)
        {
            HandleMqttDisconnected("断开失败", ex.Message);
        }
        finally
        {
            client.Dispose();
        }
    }

    // 窗口关闭时不再等待 UI 回调，只尽量做一次干净断开并释放底层连接。
    private void CloseMqttClientOnShutdown()
    {
        IMqttClient? client = _mqttClient;
        _mqttClient = null;

        if (client is null)
        {
            return;
        }

        try
        {
            if (client.IsConnected)
            {
                MqttClientDisconnectOptions disconnectOptions = _mqttFactory.CreateClientDisconnectOptionsBuilder().Build();
                client.DisconnectAsync(disconnectOptions, CancellationToken.None).GetAwaiter().GetResult();
            }
        }
        catch
        {
        }
        finally
        {
            client.Dispose();
        }
    }

    // 订阅当前输入框中的主题；如果之前已订阅其他主题，会先取消旧主题，避免状态混乱。
    private async void MqttSubscribeButton_Click(object sender, RoutedEventArgs e)
    {
        await SubscribeMqttTopicAsync();
    }

    private async Task SubscribeMqttTopicAsync()
    {
        if (_mqttClient is null || !_isMqttConnected)
        {
            AppendMqttLog("提示", "MQTT 客户端尚未连接。");
            return;
        }

        string topic = NormalizeMqttSubscriptionTopic(MqttSubscribeTopicTextBox.Text.Trim());
        if (string.IsNullOrWhiteSpace(topic))
        {
            AppendMqttLog("提示", "请输入要订阅的主题。");
            return;
        }

        // 把规范化后的主题回写到输入框，避免界面展示和实际订阅条件不一致。
        MqttSubscribeTopicTextBox.Text = topic;

        try
        {
            if (!string.IsNullOrWhiteSpace(_mqttSubscribedTopic) &&
                !string.Equals(_mqttSubscribedTopic, topic, StringComparison.Ordinal))
            {
                await _mqttClient.UnsubscribeAsync(_mqttSubscribedTopic, CancellationToken.None);
                AppendMqttLog("订阅", $"已取消旧主题：{_mqttSubscribedTopic}");
            }

            MqttQualityOfServiceLevel qos = GetSelectedMqttQoS(MqttSubscribeQoSComboBox);
            MqttClientSubscribeResult result = await _mqttClient.SubscribeAsync(topic, qos, CancellationToken.None);

            _mqttSubscribedTopic = topic;
            UpdateMqttSubscriptionState();
            AppendMqttLog("订阅", $"主题={topic}，QoS={(int)qos}，Result={result.Items.FirstOrDefault()?.ResultCode}");
        }
        catch (Exception ex)
        {
            AppendMqttLog("错误", $"订阅失败：{ex.Message}");
        }
    }

    private async void MqttUnsubscribeButton_Click(object sender, RoutedEventArgs e)
    {
        await UnsubscribeMqttTopicAsync();
    }

    private async Task UnsubscribeMqttTopicAsync()
    {
        if (_mqttClient is null || !_isMqttConnected)
        {
            AppendMqttLog("提示", "MQTT 客户端尚未连接。");
            return;
        }

        if (string.IsNullOrWhiteSpace(_mqttSubscribedTopic))
        {
            AppendMqttLog("提示", "当前没有已订阅主题。");
            return;
        }

        string topic = _mqttSubscribedTopic;

        try
        {
            MqttClientUnsubscribeResult result = await _mqttClient.UnsubscribeAsync(topic, CancellationToken.None);
            _mqttSubscribedTopic = null;
            UpdateMqttSubscriptionState();
            AppendMqttLog("订阅", $"已取消订阅：{topic}，Result={result.Items.FirstOrDefault()?.ResultCode}");
        }
        catch (Exception ex)
        {
            AppendMqttLog("错误", $"取消订阅失败：{ex.Message}");
        }
    }

    private void MqttSampleButton_Click(object sender, RoutedEventArgs e)
    {
        LoadMqttSampleMessage();
    }

    private void LoadMqttSampleMessage()
    {
        // MQTT 页面默认给一段普通业务 JSON，既方便观察 payload，又方便和 JSON 美化器联动。
        MqttMessageTextBox.Text = """
                                  {
                                    "messageType": "demo",
                                    "source": "ProtocolTestBench",
                                    "content": "Hello from MQTT"
                                  }
                                  """;
    }

    private async void MqttPublishButton_Click(object sender, RoutedEventArgs e)
    {
        await PublishMqttMessageAsync();
    }

    private async Task PublishMqttMessageAsync()
    {
        if (_mqttClient is null || !_isMqttConnected)
        {
            AppendMqttLog("提示", "MQTT 客户端尚未连接。");
            return;
        }

        string topic = MqttPublishTopicTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(topic))
        {
            AppendMqttLog("提示", "请输入发布主题。");
            return;
        }

        string payload = MqttMessageTextBox.Text;
        MqttQualityOfServiceLevel qos = GetSelectedMqttQoS(MqttPublishQoSComboBox);
        bool retain = MqttRetainCheckBox.IsChecked == true;

        try
        {
            MqttApplicationMessage message = _mqttFactory.CreateApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithQualityOfServiceLevel(qos)
                .WithRetainFlag(retain)
                .Build();

            MqttClientPublishResult result = await _mqttClient.PublishAsync(message, CancellationToken.None);
            AppendMqttLog(
                $"发送 -> {topic} [QoS {(int)qos}, Retain={retain}]",
                string.IsNullOrWhiteSpace(payload) ? "(空消息体)" : payload);
            AppendMqttLog("发布结果", $"Reason={result.ReasonCode}，Success={result.IsSuccess}");
        }
        catch (Exception ex)
        {
            AppendMqttLog("错误", $"发布失败：{ex.Message}");
        }
    }

    // MQTT 消息编辑框保留普通 Enter 换行，Ctrl+Enter 用于快速发布。
    private async void MqttMessageTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            await PublishMqttMessageAsync();
        }
    }

    private void MqttClearLogButton_Click(object sender, RoutedEventArgs e)
    {
        // 只清空 MQTT 页日志，不影响 Socket / CFX 页面历史。
        _mqttLogItems.Clear();
        MqttLogTextBox.Clear();
    }

    // 统一维护 MQTT 连接相关控件的启停状态，避免连接中途修改关键参数。
    private void SetMqttConnectedState(bool isConnected, string status)
    {
        _isMqttConnected = isConnected;

        MqttBrokerHostTextBox.IsEnabled = !isConnected;
        MqttBrokerPortTextBox.IsEnabled = !isConnected;
        MqttClientIdTextBox.IsEnabled = !isConnected;
        MqttUserNameTextBox.IsEnabled = !isConnected;
        MqttPasswordBox.IsEnabled = !isConnected;
        MqttCleanSessionCheckBox.IsEnabled = !isConnected;

        MqttConnectButton.Content = isConnected ? "断开" : "连接";
        MqttStatusTextBlock.Text = status;
        MqttConnectionTextBlock.Text = isConnected ? "状态：已连接" : "状态：未连接";
        MqttPublishButton.IsEnabled = isConnected;
        MqttSubscribeButton.IsEnabled = isConnected;

        UpdateMqttSubscriptionState();
    }

    // 订阅状态单独维护，这样连接状态变化和主题变化都能复用一套按钮刷新逻辑。
    private void UpdateMqttSubscriptionState()
    {
        bool hasSubscription = !string.IsNullOrWhiteSpace(_mqttSubscribedTopic);

        MqttUnsubscribeButton.IsEnabled = _isMqttConnected && hasSubscription;
        MqttSubscribeTopicTextBox.IsEnabled = _isMqttConnected;
        MqttSubscribeQoSComboBox.IsEnabled = _isMqttConnected;
    }

    private void HandleMqttDisconnected(string status, string? detail = null)
    {
        _isMqttConnected = false;
        _isMqttDisconnecting = false;
        _mqttSubscribedTopic = null;
        SetMqttConnectedState(false, status);

        if (!string.IsNullOrWhiteSpace(detail))
        {
            AppendMqttLog("连接", $"{status}：{detail}");
        }
        else
        {
            AppendMqttLog("连接", status);
        }
    }

    private bool TryReadMqttPort(out int port)
    {
        if (!int.TryParse(MqttBrokerPortTextBox.Text.Trim(), out port) || port < 1 || port > 65535)
        {
            MessageBox.Show("请输入 1-65535 之间的 MQTT 端口号。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        return true;
    }

    // 从下拉框里读取 QoS 级别；界面用 Tag 存 0/1/2，代码里转回 MQTTnet 枚举。
    private static MqttQualityOfServiceLevel GetSelectedMqttQoS(ComboBox comboBox)
    {
        string? tag = (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();

        return tag switch
        {
            "1" => MqttQualityOfServiceLevel.AtLeastOnce,
            "2" => MqttQualityOfServiceLevel.ExactlyOnce,
            _ => MqttQualityOfServiceLevel.AtMostOnce
        };
    }

    // 有些业务习惯把单层通配写成 *，这里自动转换成 MQTT 标准的 +。
    // 只处理订阅过滤器，不处理发布主题；发布主题本身不允许携带通配符。
    private static string NormalizeMqttSubscriptionTopic(string topic)
    {
        return topic.Replace('*', '+');
    }

    private void AppendMqttLog(string source, string message)
    {
        string time = DateTime.Now.ToString("HH:mm:ss");
        string logLine = $"[{time}] {source}: {message}";
        _mqttLogItems.Add(logLine);

        if (_mqttLogItems.Count > MaxLogItems)
        {
            // MQTT 高频推送场景下会很容易刷满日志，因此达到上限后直接裁剪旧记录。
            _mqttLogItems.RemoveRange(0, _mqttLogItems.Count - MaxLogItems);
            MqttLogTextBox.Text = string.Join(Environment.NewLine, _mqttLogItems);
        }
        else
        {
            MqttLogTextBox.AppendText($"{logLine}{Environment.NewLine}");
        }

        MqttLogTextBox.ScrollToEnd();
    }
}
