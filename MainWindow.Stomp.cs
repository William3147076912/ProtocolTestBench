using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Stomp.Net;

namespace ProtocolTestBench;

public partial class MainWindow
{
    // STOMP 页面的日志缓存，和其他协议页分开维护，方便切换对比。
    private readonly List<string> _stompLogItems = [];

    // STOMP 连接资源：connection 负责底层 TCP/SSL 会话，session 负责 producer/consumer 的创建。
    private IConnection? _stompConnection;
    private ISession? _stompSession;
    private IMessageProducer? _stompProducer;
    private IMessageConsumer? _stompConsumer;

    // 保存当前订阅状态，便于刷新按钮和日志摘要。
    private bool _isStompConnected;
    private string? _stompSubscribedDestinationLabel;

    private void InitializeStompDefaults()
    {
        // 默认放入一段 JSON 文本，方便连通性验证和与 JSON 工具联动。
        LoadStompSampleMessage();
        SetStompConnectedState(false, "未连接：填写 broker、ClientId 和目标地址后连接");
    }

    // STOMP 页的连接按钮在未连接时建立会话，在已连接时断开并释放所有资源。
    private void StompConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isStompConnected)
        {
            CloseStompClient();
            return;
        }

        ConnectStomp();
    }

    private void ConnectStomp()
    {
        string host = StompBrokerHostTextBox.Text.Trim();
        string clientId = StompClientIdTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(host))
        {
            MessageBox.Show("请输入 STOMP Broker 地址。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(clientId))
        {
            MessageBox.Show("请输入 STOMP ClientId。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!TryReadStompPort(out int port))
        {
            return;
        }

        try
        {
            CloseStompClient();

            Uri brokerUri = BuildStompBrokerUri(host, port);
            string hostHeader = StompHostHeaderTextBox.Text.Trim();
            StompConnectionSettings settings = new()
            {
                ClientId = clientId,
                UserName = StompUserNameTextBox.Text.Trim(),
                Password = StompPasswordBox.Password,
                AcknowledgementMode = GetSelectedStompAckMode(),
                RequestTimeout = TimeSpan.FromSeconds(10),
                // STOMP 1.1/1.2 的 host 头在 RabbitMQ 里表示 virtual host，而不是 broker 的 IP/域名。
                // 例如 RabbitMQ 默认 vhost 通常是 "/"；如果把 127.0.0.1 当 host 发过去，就很容易得到 Bad CONNECT。
                SetHostHeader = !string.IsNullOrWhiteSpace(hostHeader),
                HostHeaderOverride = string.IsNullOrWhiteSpace(hostHeader) ? null : hostHeader
            };

            ConnectionFactory connectionFactory = new(brokerUri.ToString(), settings);
            IConnection connection = connectionFactory.CreateConnection();

            // STOMP 库异常可能从后台网络线程抛出，这里统一回到 Dispatcher 写日志。
            connection.ExceptionListener += StompConnection_ExceptionListener;
            connection.ClientId = clientId;

            ISession session = connection.CreateSession(settings.AcknowledgementMode);
            IMessageProducer producer = session.CreateProducer();

            // 连接建立后要显式 Start，consumer 才会开始接收 broker 推送的消息。
            connection.Start();

            _stompConnection = connection;
            _stompSession = session;
            _stompProducer = producer;
            _isStompConnected = true;

            SetStompConnectedState(true, $"已连接：{brokerUri}");
            AppendStompLog("连接", $"已连接到 {brokerUri}，ClientId={clientId}");
        }
        catch (Exception ex)
        {
            CloseStompClient();
            SetStompConnectedState(false, "连接失败");
            AppendStompLog("错误", $"连接 STOMP Broker 失败：{ex.Message}");
        }
    }

    private void StompConnection_ExceptionListener(Exception ex)
    {
        Dispatcher.Invoke(() => { AppendStompLog("异常", ex.Message); });
    }

    private void CloseStompClient()
    {
        IMessageConsumer? consumer = _stompConsumer;
        IMessageProducer? producer = _stompProducer;
        ISession? session = _stompSession;
        IConnection? connection = _stompConnection;

        _stompConsumer = null;
        _stompProducer = null;
        _stompSession = null;
        _stompConnection = null;
        _stompSubscribedDestinationLabel = null;
        _isStompConnected = false;

        try
        {
            if (consumer is not null)
            {
                consumer.Listener -= OnStompMessageReceived;
                consumer.Close();
            }
        }
        catch (Exception ex)
        {
            AppendStompLog("错误", $"关闭订阅失败：{ex.Message}");
        }

        try
        {
            producer?.Close();
        }
        catch (Exception ex)
        {
            AppendStompLog("错误", $"关闭发送器失败：{ex.Message}");
        }

        try
        {
            session?.Close();
        }
        catch (Exception ex)
        {
            AppendStompLog("错误", $"关闭会话失败：{ex.Message}");
        }

        try
        {
            if (connection is not null)
            {
                connection.ExceptionListener -= StompConnection_ExceptionListener;
                connection.Close();
            }
        }
        catch (Exception ex)
        {
            AppendStompLog("错误", $"关闭连接失败：{ex.Message}");
        }

        SetStompConnectedState(false, "已断开");
    }

    // 窗口关闭时只做资源释放，不再额外写 UI 状态，避免窗口销毁过程中的无效访问。
    private void CloseStompClientOnShutdown()
    {
        try
        {
            _stompConsumer?.Close();
        }
        catch
        {
        }

        try
        {
            _stompProducer?.Close();
        }
        catch
        {
        }

        try
        {
            _stompSession?.Close();
        }
        catch
        {
        }

        try
        {
            _stompConnection?.Close();
        }
        catch
        {
        }
    }

    private void StompSubscribeButton_Click(object sender, RoutedEventArgs e)
    {
        SubscribeStompDestination();
    }

    private void SubscribeStompDestination()
    {
        if (_stompSession is null || !_isStompConnected)
        {
            AppendStompLog("提示", "STOMP 客户端尚未连接。");
            return;
        }

        string destinationName = StompSubscribeDestinationTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(destinationName))
        {
            AppendStompLog("提示", "请输入要订阅的目标地址。");
            return;
        }

        try
        {
            if (_stompConsumer is not null)
            {
                _stompConsumer.Listener -= OnStompMessageReceived;
                _stompConsumer.Close();
            }

            IDestination destination = BuildStompDestination(_stompSession, StompSubscribeTypeComboBox, destinationName);
            IMessageConsumer consumer = _stompSession.CreateConsumer(destination);
            consumer.Listener += OnStompMessageReceived;

            _stompConsumer = consumer;
            _stompSubscribedDestinationLabel = $"{GetSelectedStompDestinationType(StompSubscribeTypeComboBox)}:{destinationName}";
            UpdateStompSubscriptionState();
            AppendStompLog("订阅", $"已订阅 {_stompSubscribedDestinationLabel}，Ack={GetSelectedStompAckMode()}");
        }
        catch (Exception ex)
        {
            AppendStompLog("错误", $"订阅失败：{ex.Message}");
        }
    }

    private void StompUnsubscribeButton_Click(object sender, RoutedEventArgs e)
    {
        UnsubscribeStompDestination();
    }

    private void UnsubscribeStompDestination()
    {
        if (_stompConsumer is null)
        {
            AppendStompLog("提示", "当前没有活动订阅。");
            return;
        }

        try
        {
            _stompConsumer.Listener -= OnStompMessageReceived;
            _stompConsumer.Close();
            AppendStompLog("订阅", $"已取消订阅：{_stompSubscribedDestinationLabel}");
        }
        catch (Exception ex)
        {
            AppendStompLog("错误", $"取消订阅失败：{ex.Message}");
        }
        finally
        {
            _stompConsumer = null;
            _stompSubscribedDestinationLabel = null;
            UpdateStompSubscriptionState();
        }
    }

    private void OnStompMessageReceived(IBytesMessage message)
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                string destination = message.FromDestination?.PhysicalName
                    ?? message.StompDestination?.PhysicalName
                    ?? _stompSubscribedDestinationLabel
                    ?? "(未知目标)";
                string payload = DecodeStompMessageBody(message);

                AppendStompLog($"收到 <- {destination}", payload);

                if (GetSelectedStompAckMode() != AcknowledgementMode.AutoAcknowledge)
                {
                    // 非自动确认模式下，这里收到并记录后立刻 ACK，避免 broker 重复投递。
                    message.Acknowledge();
                    AppendStompLog("ACK", $"已确认消息：{message.StompMessageId}");
                }
            }
            catch (Exception ex)
            {
                AppendStompLog("错误", $"处理收到的 STOMP 消息失败：{ex.Message}");
            }
        });
    }

    private void StompSampleButton_Click(object sender, RoutedEventArgs e)
    {
        LoadStompSampleMessage();
    }

    private void LoadStompSampleMessage()
    {
        StompMessageTextBox.Text = """
                                   {
                                     "messageType": "demo",
                                     "source": "ProtocolTestBench",
                                     "content": "Hello from STOMP"
                                   }
                                   """;
    }

    private void StompSendButton_Click(object sender, RoutedEventArgs e)
    {
        SendStompMessage();
    }

    private void SendStompMessage()
    {
        if (_stompSession is null || _stompProducer is null || !_isStompConnected)
        {
            AppendStompLog("提示", "STOMP 客户端尚未连接。");
            return;
        }

        string destinationName = StompPublishDestinationTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(destinationName))
        {
            AppendStompLog("提示", "请输入发送目标地址。");
            return;
        }

        string payload = StompMessageTextBox.Text;

        try
        {
            IDestination destination = BuildStompDestination(_stompSession, StompPublishTypeComboBox, destinationName);
            IBytesMessage message = _stompSession.CreateBytesMessage(Encoding.UTF8.GetBytes(payload));

            // 附带 content-type 头，方便对端按 UTF-8 文本或 JSON 方式解析。
            message.Headers["content-type"] = "application/json; charset=utf-8";
            _stompProducer.Send(destination, message);

            AppendStompLog($"发送 -> {GetSelectedStompDestinationType(StompPublishTypeComboBox)}:{destinationName}",
                string.IsNullOrWhiteSpace(payload) ? "(空消息体)" : payload);
        }
        catch (Exception ex)
        {
            AppendStompLog("错误", $"发送失败：{ex.Message}");
        }
    }

    // STOMP 发送框保留普通 Enter 换行，Ctrl+Enter 用于快速发送。
    private void StompMessageTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            SendStompMessage();
        }
    }

    private void StompClearLogButton_Click(object sender, RoutedEventArgs e)
    {
        _stompLogItems.Clear();
        StompLogTextBox.Clear();
    }

    private void SetStompConnectedState(bool isConnected, string status)
    {
        _isStompConnected = isConnected;

        StompSchemeComboBox.IsEnabled = !isConnected;
        StompBrokerHostTextBox.IsEnabled = !isConnected;
        StompBrokerPortTextBox.IsEnabled = !isConnected;
        StompClientIdTextBox.IsEnabled = !isConnected;
        StompHostHeaderTextBox.IsEnabled = !isConnected;
        StompUserNameTextBox.IsEnabled = !isConnected;
        StompPasswordBox.IsEnabled = !isConnected;
        StompAckModeComboBox.IsEnabled = !isConnected;

        StompConnectButton.Content = isConnected ? "断开" : "连接";
        StompStatusTextBlock.Text = status;
        StompConnectionTextBlock.Text = isConnected ? "状态：已连接" : "状态：未连接";
        StompSendButton.IsEnabled = isConnected;
        StompSubscribeButton.IsEnabled = isConnected;

        UpdateStompSubscriptionState();
    }

    private void UpdateStompSubscriptionState()
    {
        bool hasSubscription = _stompConsumer is not null;
        StompUnsubscribeButton.IsEnabled = _isStompConnected && hasSubscription;
        StompSubscribeTypeComboBox.IsEnabled = _isStompConnected;
        StompSubscribeDestinationTextBox.IsEnabled = _isStompConnected;
    }

    private bool TryReadStompPort(out int port)
    {
        if (!int.TryParse(StompBrokerPortTextBox.Text.Trim(), out port) || port < 1 || port > 65535)
        {
            MessageBox.Show("请输入 1-65535 之间的 STOMP 端口号。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        return true;
    }

    private Uri BuildStompBrokerUri(string host, int port)
    {
        string scheme = (StompSchemeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "tcp";
        return new Uri($"{scheme}://{host}:{port}");
    }

    private AcknowledgementMode GetSelectedStompAckMode()
    {
        string? tag = (StompAckModeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();

        return tag switch
        {
            "ClientAcknowledge" => AcknowledgementMode.ClientAcknowledge,
            "IndividualAcknowledge" => AcknowledgementMode.IndividualAcknowledge,
            _ => AcknowledgementMode.AutoAcknowledge
        };
    }

    private static string GetSelectedStompDestinationType(ComboBox comboBox)
    {
        return (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Queue";
    }

    private static IDestination BuildStompDestination(ISession session, ComboBox comboBox, string destinationName)
    {
        return GetSelectedStompDestinationType(comboBox) == "Topic"
            ? session.GetTopic(destinationName)
            : session.GetQueue(destinationName);
    }

    private static string DecodeStompMessageBody(IBytesMessage message)
    {
        byte[]? content = message.Content;
        if (content is null || content.Length == 0)
        {
            return "(空消息体)";
        }

        return Encoding.UTF8.GetString(content);
    }

    private void AppendStompLog(string source, string message)
    {
        string time = DateTime.Now.ToString("HH:mm:ss");
        string logLine = $"[{time}] {source}: {message}";
        _stompLogItems.Add(logLine);

        if (_stompLogItems.Count > MaxLogItems)
        {
            // STOMP broker 推送频繁时也可能刷满日志，因此和 MQTT 一样保留最近窗口。
            _stompLogItems.RemoveRange(0, _stompLogItems.Count - MaxLogItems);
            StompLogTextBox.Text = string.Join(Environment.NewLine, _stompLogItems);
        }
        else
        {
            StompLogTextBox.AppendText($"{logLine}{Environment.NewLine}");
        }

        StompLogTextBox.ScrollToEnd();
    }
}
