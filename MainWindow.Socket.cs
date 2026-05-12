using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ProtocolTestBench
{
    public partial class MainWindow
    {
        // 服务端模式下会同时维护多个客户端连接。
        // Socket 接收任务运行在后台线程，UI 勾选发送目标运行在主线程，因此访问列表时必须加锁。
        private readonly object _socketClientsLock = new object();
        private readonly List<SocketClientConnection> _socketServerClients = new List<SocketClientConnection>();

        // Socket 页面的日志缓存。TextBox 负责显示，列表负责保留可裁剪的真实数据源。
        private readonly List<string> _socketLogItems = new List<string>();

        // Socket 页面的运行资源。
        // 服务端使用 _socketListener 接收客户端，客户端模式使用 _socketClient 连接远端。
        // _socketCancellationTokenSource 用于通知 Accept/Receive 循环退出，StopSocketEndpoint 会统一释放这些资源。
        private CancellationTokenSource _socketCancellationTokenSource;
        private Socket _socketListener;
        private Socket _socketClient;

        // 当前 Socket 页是否处于启动状态。按钮文案、输入框启停和发送能力都依赖这个状态。
        private bool _isSocketRunning;

        // ComboBoxItem 的 Tag 是实际逻辑值，Content 只是显示给用户看的中文。
        private bool IsSocketServerMode
        {
            get { return (ModeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "Server"; }
        }

        // 启动按钮在“未运行”时启动服务端/客户端，在“运行中”时执行停止。
        private async void SocketStartButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isSocketRunning)
            {
                StopSocketEndpoint();
                return;
            }

            int port;
            if (!TryReadSocketPort(out port))
            {
                return;
            }

            try
            {
                if (IsSocketServerMode)
                {
                    StartSocketServer(port);
                }
                else
                {
                    await StartSocketClientAsync(RemoteHostTextBox.Text.Trim(), port);
                }
            }
            catch (Exception ex)
            {
                AppendSocketLog("错误", ex.Message);
                StopSocketEndpoint();
            }
        }

        // 服务端：绑定本机端口并启动后台接受连接循环。
        private void StartSocketServer(int port)
        {
            // 每次启动都创建新的取消源，避免复用已经 Cancel 的 token。
            _socketCancellationTokenSource = new CancellationTokenSource();

            // 当前工具只做 IPv4 TCP 调试；ReuseAddress 方便频繁停止/启动同一个端口。
            _socketListener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _socketListener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _socketListener.Bind(new IPEndPoint(IPAddress.Any, port));
            _socketListener.Listen(20);

            SetSocketRunningState(true, string.Format("服务端已监听 0.0.0.0:{0}", port));
            AppendSocketLog("系统", string.Format("服务端启动，监听端口 {0}", port));

            // 接收连接是长时间运行的后台循环，不能 await 阻塞 UI 线程。
            _ = AcceptSocketClientsLoopAsync(_socketCancellationTokenSource.Token);
        }

        // 客户端：解析地址、连接服务端，并启动后台接收循环。
        private async Task StartSocketClientAsync(string host, int port)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                MessageBox.Show("请输入服务端地址。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _socketCancellationTokenSource = new CancellationTokenSource();
            _socketClient = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            // 支持用户输入 IP 或域名，统一解析成 IPv4 后再连接。
            IPAddress address = await ResolveSocketHostAsync(host);
            await _socketClient.ConnectAsync(new IPEndPoint(address, port), _socketCancellationTokenSource.Token);

            SetSocketRunningState(true, string.Format("客户端已连接 {0}:{1}", address, port));
            AppendSocketLog("系统", string.Format("已连接到服务端 {0}:{1}", address, port));

            // 客户端模式只有一个对端，也就是服务端；false 表示断开时不走服务端客户端列表清理逻辑。
            _ = ReceiveSocketLoopAsync(_socketClient, "服务端", _socketCancellationTokenSource.Token, false);
            UpdateSocketConnectionCount();
        }

        // 服务端循环接受新客户端；每个客户端独立开启一个接收任务。
        private async Task AcceptSocketClientsLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && _socketListener != null)
            {
                try
                {
                    Socket client = await _socketListener.AcceptAsync(cancellationToken);
                    string remoteName = client.RemoteEndPoint == null ? "未知客户端" : client.RemoteEndPoint.ToString();
                    SocketClientConnection connection = new SocketClientConnection(client, remoteName);

                    // 新客户端默认选中，服务端发送时可以直接群发。
                    lock (_socketClientsLock)
                    {
                        _socketServerClients.Add(connection);
                    }

                    // WPF 控件只能在 UI 线程访问，所以后台循环里所有界面更新都通过 Dispatcher。
                    Dispatcher.Invoke(() =>
                    {
                        AppendSocketLog("系统", string.Format("客户端已连接：{0}", remoteName));
                        RefreshSocketClientTargets();
                        UpdateSocketConnectionCount();
                    });

                    _ = ReceiveSocketLoopAsync(client, remoteName, cancellationToken, true);
                }
                catch (OperationCanceledException)
                {
                    // StopSocketEndpoint 取消 token 后会进入这里，这是正常停止流程。
                    break;
                }
                catch (ObjectDisposedException)
                {
                    // StopSocketEndpoint 关闭 listener 时 AcceptAsync 可能抛出对象已释放，也属于正常停止流程。
                    break;
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => AppendSocketLog("错误", string.Format("接受连接失败：{0}", ex.Message)));
                }
            }
        }

        // 从指定 Socket 持续读取数据。网络线程收到数据后，通过 Dispatcher 回到 UI 线程写日志。
        private async Task ReceiveSocketLoopAsync(
            Socket socket,
            string peerName,
            CancellationToken cancellationToken,
            bool isServerClient)
        {
            byte[] buffer = new byte[4096];

            // GKG/设备通讯中可能存在 ALIVE_REQ 心跳包。
            // 这里自动回复 ALIVE_RES，避免对端因为调试工具不回应心跳而主动断开。
            int heartbeatPrintCount = 0;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    int received = await socket.ReceiveAsync(buffer, SocketFlags.None, cancellationToken);
                    if (received == 0)
                    {
                        // ReceiveAsync 返回 0 表示对端正常关闭连接。
                        break;
                    }

                    string message = Encoding.UTF8.GetString(buffer, 0, received);
                    if (message.Contains("ALIVE_REQ"))
                    {
                        string response = "<ASYS>\n  <ALIVE_RES />\n</ASYS>";
                        byte[] responseBytes = Encoding.UTF8.GetBytes(response);
                        await SendToSocketAsync(socket, responseBytes);

                        // 心跳通常很频繁，只在累计到一定次数后打印一次，避免日志被心跳淹没。
                        if (++heartbeatPrintCount >= 100)
                        {
                            Dispatcher.Invoke(() =>
                                AppendSocketLog(string.Format("收到 <- {0}次心跳包，此次心跳包为:{1}", heartbeatPrintCount, Environment.NewLine),
                                    message.TrimEnd()));
                            heartbeatPrintCount = 0;
                        }
                    }

                    Dispatcher.Invoke(() => AppendSocketLog(string.Format("收到 <- {0}:{1}", peerName, Environment.NewLine), message.TrimEnd()));
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (SocketException ex)
            {
                Dispatcher.Invoke(() => AppendSocketLog("网络", string.Format("{0} 连接中断：{1}", peerName, ex.Message)));
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => AppendSocketLog("错误", string.Format("{0} 接收失败：{1}", peerName, ex.Message)));
            }
            finally
            {
                if (isServerClient)
                {
                    // 服务端下某个客户端断开，只移除这个客户端，不影响 listener 和其他客户端。
                    RemoveSocketServerClient(socket);
                    Dispatcher.Invoke(() =>
                    {
                        AppendSocketLog("系统", string.Format("{0} 已断开", peerName));
                        RefreshSocketClientTargets();
                        UpdateSocketConnectionCount();
                    });
                }
                else if (_isSocketRunning)
                {
                    // 客户端模式下服务端断开，当前连接已经不可用，直接回到未启动状态。
                    Dispatcher.Invoke(() =>
                    {
                        AppendSocketLog("系统", "服务端已断开");
                        StopSocketEndpoint();
                    });
                }
            }
        }

        // 发送按钮点击后发送输入框中的内容。
        private async void SocketSendButton_Click(object sender, RoutedEventArgs e)
        {
            await SendSocketCurrentMessageAsync();
        }

        // 根据当前模式选择发送目标：服务端群发给全部客户端，客户端发送给服务端。
        private async Task SendSocketCurrentMessageAsync()
        {
            string message = MessageTextBox.Text;
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            byte[] payload = Encoding.UTF8.GetBytes(message + Environment.NewLine);

            try
            {
                if (IsSocketServerMode)
                {
                    SocketClientConnection[] clients;
                    SocketClientConnection[] targets;
                    lock (_socketClientsLock)
                    {
                        // 复制快照后再发送，避免发送过程中客户端列表被接收线程修改。
                        clients = _socketServerClients.ToArray();
                        targets = clients.Where(client => client.IsSelected).ToArray();
                    }

                    if (clients.Length == 0)
                    {
                        AppendSocketLog("提示", "当前没有已连接客户端。");
                        return;
                    }

                    if (targets.Length == 0)
                    {
                        AppendSocketLog("提示", "请选择至少一个客户端作为发送目标。");
                        return;
                    }

                    foreach (SocketClientConnection target in targets)
                    {
                        // 服务端模式按勾选目标逐个发送；某个目标发送失败会进入外层 catch。
                        await SendToSocketAsync(target.Socket, payload);
                    }

                    AppendSocketLog(string.Format("发送 -> {0}", GetSocketTargetLogName(targets, clients.Length)), message);
                }
                else
                {
                    if (_socketClient == null || !_socketClient.Connected)
                    {
                        AppendSocketLog("提示", "客户端尚未连接。");
                        return;
                    }

                    await SendToSocketAsync(_socketClient, payload);
                    AppendSocketLog("发送 -> 服务端", message);
                }

                MessageTextBox.Clear();
            }
            catch (Exception ex)
            {
                AppendSocketLog("错误", string.Format("发送失败：{0}", ex.Message));
            }
        }

        // Socket.SendAsync 不保证一次发完全部字节，因此循环发送到 payload 全部写出。
        private static async Task SendToSocketAsync(Socket socket, byte[] payload)
        {
            int sent = 0;
            while (sent < payload.Length)
            {
                sent += await socket.SendAsync(payload.AsMemory(sent), SocketFlags.None);
            }
        }

        // 停止当前运行状态：取消后台任务、关闭监听 Socket、关闭客户端连接并刷新 UI。
        private void StopSocketEndpoint()
        {
            // 先取消后台循环，再关闭 Socket；这样等待中的 Accept/Receive 会尽快醒来并退出。
            if (_socketCancellationTokenSource != null)
            {
                _socketCancellationTokenSource.Cancel();
            }

            CloseSocket(_socketListener);
            CloseSocket(_socketClient);

            SocketClientConnection[] clients;
            lock (_socketClientsLock)
            {
                // 先取快照再清空列表，释放锁后再逐个关闭 Socket，避免锁内做耗时 I/O。
                clients = _socketServerClients.ToArray();
                _socketServerClients.Clear();
            }

            foreach (SocketClientConnection client in clients)
            {
                CloseSocket(client.Socket);
            }

            _socketListener = null;
            _socketClient = null;

            if (_socketCancellationTokenSource != null)
            {
                _socketCancellationTokenSource.Dispose();
            }

            _socketCancellationTokenSource = null;

            SetSocketRunningState(false, "未启动");
            RefreshSocketClientTargets();
            UpdateSocketConnectionCount();
            AppendSocketLog("系统", "已停止");
        }

        // 切换服务端/客户端模式时，同步更新输入项和按钮文案。
        private void SocketModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateSocketModeUi();
        }

        // Ctrl+Enter 快捷发送；普通 Enter 仍然用于输入换行。
        private async void SocketMessageTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                await SendSocketCurrentMessageAsync();
            }
        }

        // 清空 Socket 页收发记录。
        private void SocketClearLogButton_Click(object sender, RoutedEventArgs e)
        {
            _socketLogItems.Clear();
            LogTextBox.Clear();
        }

        // 根据当前模式控制服务端地址输入框是否显示，并设置默认状态提示。
        private void UpdateSocketModeUi()
        {
            if (StatusTextBlock == null)
            {
                return;
            }

            bool serverMode = IsSocketServerMode;

            // 服务端不需要远端地址；客户端需要填写服务端地址。
            RemoteHostLabel.Visibility = serverMode ? Visibility.Collapsed : Visibility.Visible;
            RemoteHostTextBox.Visibility = serverMode ? Visibility.Collapsed : Visibility.Visible;

            // 只有服务端模式才有“发送给哪些客户端”的选择。
            ServerTargetPanel.Visibility = serverMode ? Visibility.Visible : Visibility.Collapsed;
            StartButton.Content = _isSocketRunning ? "停止" : (serverMode ? "启动" : "连接");
            PortLabel.Text = serverMode ? "监听端口" : "端口";
            StatusTextBlock.Text = serverMode ? "服务端模式：监听端口后等待客户端连接" : "客户端模式：连接指定服务端地址和端口";
            RefreshSocketClientTargets();
        }

        // 运行状态变化时统一启停输入控件，避免连接期间修改关键参数。
        private void SetSocketRunningState(bool isRunning, string status)
        {
            _isSocketRunning = isRunning;
            ModeComboBox.IsEnabled = !isRunning;
            RemoteHostTextBox.IsEnabled = !isRunning;
            PortTextBox.IsEnabled = !isRunning;
            SendButton.IsEnabled = isRunning;
            TargetDropDownButton.IsEnabled = isRunning && IsSocketServerMode;
            StartButton.Content = isRunning ? "停止" : (IsSocketServerMode ? "启动" : "连接");
            StatusTextBlock.Text = status;
        }

        // 显示当前连接数：服务端显示已连接客户端数量，客户端显示是否连上服务端。
        private void UpdateSocketConnectionCount()
        {
            if (IsSocketServerMode)
            {
                lock (_socketClientsLock)
                {
                    // 连接数展示的是当前仍在列表中的客户端数量。
                    ConnectionCountTextBlock.Text = string.Format("连接数：{0}", _socketServerClients.Count);
                }
            }
            else
            {
                ConnectionCountTextBlock.Text = _socketClient != null && _socketClient.Connected ? "连接数：1" : "连接数：0";
            }
        }

        // 下拉列表中的“全选”按钮。
        private void SocketSelectAllTargetsButton_Click(object sender, RoutedEventArgs e)
        {
            SetAllSocketClientTargets(true);
            TargetPopup.IsOpen = true;
        }

        // 下拉列表中的“清空”按钮，方便只勾选少量客户端。
        private void SocketClearTargetsButton_Click(object sender, RoutedEventArgs e)
        {
            SetAllSocketClientTargets(false);
            TargetPopup.IsOpen = true;
        }

        private void SocketClientTargetCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            // 勾选状态绑定在 SocketClientConnection.IsSelected 上，这里只刷新按钮摘要文案。
            UpdateSocketTargetSummary();
        }

        private void SetAllSocketClientTargets(bool isSelected)
        {
            lock (_socketClientsLock)
            {
                foreach (SocketClientConnection client in _socketServerClients)
                {
                    client.IsSelected = isSelected;
                }
            }

            UpdateSocketTargetSummary();
        }

        // 连接变化后刷新下拉列表，同时保留每个客户端对象上的选中状态。
        private void RefreshSocketClientTargets()
        {
            if (ClientTargetsListBox == null)
            {
                return;
            }

            SocketClientConnection[] clients;
            lock (_socketClientsLock)
            {
                // ItemsSource 使用数组快照，避免 List 在后台线程变更时影响 WPF 枚举。
                clients = _socketServerClients.ToArray();
            }

            ClientTargetsListBox.ItemsSource = clients;
            NoClientsTextBlock.Visibility = clients.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            ClientTargetsListBox.Visibility = clients.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            TargetDropDownButton.IsEnabled = _isSocketRunning && IsSocketServerMode;
            UpdateSocketTargetSummary(clients);
        }

        private void UpdateSocketTargetSummary()
        {
            SocketClientConnection[] clients;
            lock (_socketClientsLock)
            {
                clients = _socketServerClients.ToArray();
            }

            UpdateSocketTargetSummary(clients);
        }

        private void UpdateSocketTargetSummary(SocketClientConnection[] clients)
        {
            if (TargetDropDownButton == null)
            {
                return;
            }

            int selectedCount = clients.Count(client => client.IsSelected);

            // 发送目标按钮既是下拉入口，也承担当前目标摘要展示。
            if (clients.Length == 0)
            {
                TargetDropDownButton.Content = "暂无客户端";
            }
            else if (selectedCount == clients.Length)
            {
                TargetDropDownButton.Content = string.Format("所有客户端（{0}）", clients.Length);
            }
            else if (selectedCount == 0)
            {
                TargetDropDownButton.Content = "未选择客户端";
            }
            else if (selectedCount == 1)
            {
                TargetDropDownButton.Content = clients.First(client => client.IsSelected).DisplayName;
            }
            else
            {
                TargetDropDownButton.Content = string.Format("已选择 {0}/{1} 个客户端", selectedCount, clients.Length);
            }
        }

        private static string GetSocketTargetLogName(SocketClientConnection[] targets, int totalClientCount)
        {
            // 日志里尽量显示“所有客户端”或具体数量，方便回看发送范围。
            if (targets.Length == totalClientCount)
            {
                return "所有客户端";
            }

            return targets.Length == 1 ? targets[0].DisplayName : string.Format("{0} 个客户端", targets.Length);
        }

        // 读取并校验端口范围。
        private bool TryReadSocketPort(out int port)
        {
            if (!int.TryParse(PortTextBox.Text.Trim(), out port) || port < 1 || port > 65535)
            {
                MessageBox.Show("请输入 1-65535 之间的端口号。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }

            return true;
        }

        // 支持直接输入 IP，也支持输入域名；这里只取 IPv4 地址。
        private static async Task<IPAddress> ResolveSocketHostAsync(string host)
        {
            IPAddress address;
            if (IPAddress.TryParse(host, out address))
            {
                return address;
            }

            IPAddress[] addresses = await Dns.GetHostAddressesAsync(host);
            return addresses.First(item => item.AddressFamily == AddressFamily.InterNetwork);
        }

        // 将收发记录追加到只读 TextBox，并限制最多保留 MaxLogItems 条，避免日志无限增长。
        private void AppendSocketLog(string source, string message)
        {
            string time = DateTime.Now.ToString("HH:mm:ss");
            string logLine = string.Format("[{0}]{1}{2}", time, source, message);
            _socketLogItems.Add(logLine);

            if (_socketLogItems.Count > MaxLogItems)
            {
                // 超出上限时先裁剪内存缓存，再重建 TextBox，避免 TextBox 留下旧内容。
                _socketLogItems.RemoveRange(0, _socketLogItems.Count - MaxLogItems);
                RefreshSocketLogTextBox();
            }
            else
            {
                LogTextBox.AppendText(string.Format("{0}{1}", logLine, Environment.NewLine));
            }

            LogTextBox.ScrollToEnd();
        }

        // 当旧日志被裁剪后，重建 TextBox 内容，让界面和内存中的最近日志保持一致。
        private void RefreshSocketLogTextBox()
        {
            LogTextBox.Text = string.Join(Environment.NewLine, _socketLogItems);
        }

        // 服务端移除断开的客户端，并关闭对应 Socket。
        private void RemoveSocketServerClient(Socket socket)
        {
            lock (_socketClientsLock)
            {
                SocketClientConnection connection = _socketServerClients.FirstOrDefault(client => client.Socket == socket);
                if (connection != null)
                {
                    _socketServerClients.Remove(connection);
                }
            }

            CloseSocket(socket);
        }

        // 关闭 Socket 时忽略 Shutdown 期间的异常，因为连接可能已经被对端断开。
        private static void CloseSocket(Socket socket)
        {
            if (socket == null)
            {
                return;
            }

            try
            {
                socket.Shutdown(SocketShutdown.Both);
            }
            catch
            {
            }

            socket.Close();
            socket.Dispose();
        }

        private sealed class SocketClientConnection : INotifyPropertyChanged
        {
            // 每个服务端客户端在发送目标下拉框里都有一个勾选状态。
            // 默认 true 是为了让刚连入的客户端能被服务端群发覆盖。
            private bool _isSelected = true;

            public SocketClientConnection(Socket socket, string displayName)
            {
                Socket = socket;
                DisplayName = displayName;
            }

            // 与这个 UI 目标绑定的真实 Socket。
            public Socket Socket { get; private set; }

            // 下拉框中展示的客户端名称，通常是 RemoteEndPoint。
            public string DisplayName { get; private set; }

            public bool IsSelected
            {
                get { return _isSelected; }
                set
                {
                    if (_isSelected == value)
                    {
                        return;
                    }

                    _isSelected = value;
                    // 通知 WPF 绑定刷新 CheckBox 状态和发送目标摘要。
                    OnPropertyChanged();
                }
            }

            public event PropertyChangedEventHandler PropertyChanged;

            private void OnPropertyChanged([CallerMemberName] string propertyName = null)
            {
                PropertyChangedEventHandler handler = PropertyChanged;
                if (handler != null)
                {
                    handler(this, new PropertyChangedEventArgs(propertyName));
                }
            }
        }
    }
}
