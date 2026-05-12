using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using CFX;
using CFX.ResourcePerformance;
using CFX.Structures;
using CFX.Transport;

namespace ProtocolTestBench
{
    public partial class MainWindow
    {
        // CFX 页面的日志缓存，和 Socket 页的 _logItems 独立。
        // 两个页面互不清空，方便用户在不同协议间切换对比调试记录。
        private readonly List<string> _cfxLogItems = new List<string>();

        // CFX SDK 的 AMQP 1.0 端点对象。
        // 它负责打开本机 CFX endpoint、维护发布/订阅通道并触发消息事件。
        private AmqpCFXEndpoint _cfxEndpoint;

        // 标记 CFX 端点是否已经打开。UI 按钮文案、输入框启停和发送按钮都依赖它。
        private bool _isCfxOpen;

        // 用户可以只订阅不发布；这种情况下端点打开但发送按钮应保持不可用。
        private bool _cfxHasPublishChannel;

        // 记录已添加到 CFX SDK 的 AMQP 通道。关闭时显式关闭这些通道，再释放 endpoint。
        private readonly List<AmqpChannelAddress> _cfxPublishChannels = new List<AmqpChannelAddress>();
        private readonly List<AmqpChannelAddress> _cfxSubscribeChannels = new List<AmqpChannelAddress>();

        private void InitializeCfxDefaults()
        {
            // 窗口创建后立即放入一个合法的 CFX Envelope 示例，用户可直接修改后发送。
            LoadCfxSampleMessage();
            LoadCfxRequestResponseSamples();
            SetCfxRunningState(false, "未打开：填写 AMQP 1.0 broker、发布地址和订阅地址后打开端点");
        }

        private void CfxOpenButton_Click(object sender, RoutedEventArgs e)
        {
            // 同一个按钮承担打开/关闭两种动作，避免界面上堆太多控制按钮。
            if (_isCfxOpen)
            {
                CloseCfxEndpoint();
                return;
            }

            OpenCfxEndpoint();
        }

        private void OpenCfxEndpoint()
        {
            if (_cfxEndpoint != null)
            {
                CloseCfxEndpoint();
            }

            // 读取 UI 输入时先 Trim，避免复制配置时带入空格导致连接失败。
            string handle = CfxHandleTextBox.Text.Trim();
            string brokerText = CfxBrokerTextBox.Text.Trim();
            string publishAddress = CfxPublishAddressTextBox.Text.Trim();
            string subscribeAddress = CfxSubscribeAddressTextBox.Text.Trim();
            string requestUriText = CfxRequestUriTextBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(handle))
            {
                MessageBox.Show("请输入 CFX Handle。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            Uri brokerUri;
            if (!Uri.TryCreate(brokerText, UriKind.Absolute, out brokerUri) ||
                (brokerUri.Scheme != "amqp" && brokerUri.Scheme != "amqps"))
            {
                // CFX SDK 走 AMQP 1.0；这里接受明文 amqp 和 TLS amqps 两种 URI。
                MessageBox.Show("请输入有效的 AMQP Broker URI，例如 amqp://127.0.0.1:5672。", "提示", MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            Uri requestUri;
            if (!string.IsNullOrWhiteSpace(requestUriText) &&
                (!Uri.TryCreate(requestUriText, UriKind.Absolute, out requestUri) ||
                 (requestUri.Scheme != "amqp" && requestUri.Scheme != "amqps")))
            {
                MessageBox.Show("请输入有效的本端请求 URI，例如 amqp://0.0.0.0:5673。", "提示", MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            AmqpCFXEndpoint endpoint = new AmqpCFXEndpoint();

            // CFX SDK 默认会周期性刷新订阅通道以保持连接活跃。
            // RabbitMQ 的 AMQP 1.0 插件下，这可能在管理页留下多条 running/0-channel 连接。
            AmqpCFXEndpoint.KeepAliveEnabled = false;

            // 先注册事件再打开通道，避免连接建立很快时错过连接事件或第一条消息。
            endpoint.OnCFXMessageReceived += CfxEndpoint_OnCFXMessageReceived;
            endpoint.OnMalformedMessageReceived += CfxEndpoint_OnMalformedMessageReceived;
            endpoint.OnRequestReceived += CfxEndpoint_OnRequestReceived;
            endpoint.OnConnectionEvent += CfxEndpoint_OnConnectionEvent;

            try
            {
                string virtualHost = GetCfxVirtualHost();

                // Open 创建本机 CFX endpoint。
                // 如果填写了本端请求 URI，SDK 会在这个 URI 上监听点对点 Request/Response 请求。
                if (string.IsNullOrWhiteSpace(requestUriText))
                {
                    endpoint.Open(handle);
                }
                else
                {
                    Uri endpointRequestUri = new Uri(requestUriText);
                    endpoint.Open(handle, endpointRequestUri);
                }

                if (!string.IsNullOrWhiteSpace(publishAddress))
                {
                    // 发布通道用于发送 CFXEnvelope。地址格式取决于 broker，例如 /exchange/cfx。
                    AmqpChannelAddress publishChannel = new AmqpChannelAddress
                    {
                        Uri = brokerUri,
                        Address = publishAddress
                    };

                    endpoint.AddPublishChannel(publishChannel, virtualHost);
                    _cfxPublishChannels.Add(publishChannel);
                }

                if (!string.IsNullOrWhiteSpace(subscribeAddress))
                {
                    // 订阅通道用于接收 broker 推来的 CFXEnvelope。地址格式例如 /queue/cfx。
                    AmqpChannelAddress subscribeChannel = new AmqpChannelAddress
                    {
                        Uri = brokerUri,
                        Address = subscribeAddress
                    };

                    endpoint.AddSubscribeChannel(subscribeChannel, virtualHost);
                    _cfxSubscribeChannels.Add(subscribeChannel);
                }

                // 所有通道添加成功后再保存 endpoint 引用，避免半初始化对象被发送逻辑使用。
                _cfxEndpoint = endpoint;
                _cfxHasPublishChannel = !string.IsNullOrWhiteSpace(publishAddress);
                SetCfxRunningState(true, string.Format("端点已打开：{0}", handle));
                AppendCfxLog("系统", string.Format("已打开 CFX 端点，Broker={0}", brokerUri));

                if (!string.IsNullOrWhiteSpace(publishAddress))
                {
                    AppendCfxLog("发布", publishAddress);
                }

                if (!string.IsNullOrWhiteSpace(subscribeAddress))
                {
                    AppendCfxLog("订阅", subscribeAddress);
                }

                if (!string.IsNullOrWhiteSpace(requestUriText))
                {
                    AppendCfxLog("点对点监听", requestUriText);
                }

                if (string.IsNullOrWhiteSpace(publishAddress) &&
                    string.IsNullOrWhiteSpace(subscribeAddress) &&
                    string.IsNullOrWhiteSpace(requestUriText))
                {
                    AppendCfxLog("系统", "未配置发布/订阅/本端请求 URI，当前端点可用于主动发起点对点请求。");
                }
            }
            catch (Exception ex)
            {
                // 打开失败时要撤销事件订阅并释放 endpoint，防止后续后台事件访问已失败的窗口状态。
                endpoint.OnCFXMessageReceived -= CfxEndpoint_OnCFXMessageReceived;
                endpoint.OnMalformedMessageReceived -= CfxEndpoint_OnMalformedMessageReceived;
                endpoint.OnRequestReceived -= CfxEndpoint_OnRequestReceived;
                endpoint.OnConnectionEvent -= CfxEndpoint_OnConnectionEvent;
                endpoint.Dispose();
                _cfxPublishChannels.Clear();
                _cfxSubscribeChannels.Clear();
                AppendCfxLog("错误", string.Format("打开 CFX 端点失败：{0}", ex.Message));
                SetCfxRunningState(false, "打开失败");
            }
        }

        private void CloseCfxEndpoint()
        {
            if (_cfxEndpoint == null)
            {
                // 关闭窗口时可能尚未打开 CFX 端点，此时无需做任何事。
                return;
            }

            // 先把字段清空，让并发中的发送逻辑看到“已关闭”状态。
            AmqpCFXEndpoint endpoint = _cfxEndpoint;
            _cfxEndpoint = null;
            _isCfxOpen = false;
            _cfxHasPublishChannel = false;

            // 取消事件订阅后再 Close，避免关闭过程中的连接事件继续写 UI。
            endpoint.OnCFXMessageReceived -= CfxEndpoint_OnCFXMessageReceived;
            endpoint.OnMalformedMessageReceived -= CfxEndpoint_OnMalformedMessageReceived;
            endpoint.OnRequestReceived -= CfxEndpoint_OnRequestReceived;
            endpoint.OnConnectionEvent -= CfxEndpoint_OnConnectionEvent;

            try
            {
                foreach (AmqpChannelAddress subscribeChannel in _cfxSubscribeChannels.ToArray())
                {
                    try
                    {
                        endpoint.CloseSubscribeChannel(subscribeChannel);
                    }
                    catch (Exception ex)
                    {
                        AppendCfxLog("错误", string.Format("关闭订阅通道失败：{0}，{1}", FormatCfxAddress(subscribeChannel), ex.Message));
                    }
                }

                foreach (AmqpChannelAddress publishChannel in _cfxPublishChannels.ToArray())
                {
                    try
                    {
                        endpoint.ClosePublishChannel(publishChannel);
                    }
                    catch (Exception ex)
                    {
                        AppendCfxLog("错误", string.Format("关闭发布通道失败：{0}，{1}", FormatCfxAddress(publishChannel), ex.Message));
                    }
                }

                endpoint.Close();
            }
            catch (Exception ex)
            {
                AppendCfxLog("错误", string.Format("关闭 CFX 端点时发生错误：{0}", ex.Message));
            }
            finally
            {
                // Dispose 释放 SDK 内部连接、sender/receiver 和相关后台资源。
                endpoint.Dispose();
                _cfxPublishChannels.Clear();
                _cfxSubscribeChannels.Clear();
                SetCfxRunningState(false, "已关闭");
                AppendCfxLog("系统", "CFX 端点已关闭");
            }
        }

        private async void CfxSendButton_Click(object sender, RoutedEventArgs e)
        {
            await SendCfxMessageAsync();
        }

        private async Task SendCfxMessageAsync()
        {
            if (_cfxEndpoint == null || !_isCfxOpen)
            {
                // 端点未打开时 Publish 没有可用通道，直接在日志提示而不是弹窗打断输入。
                AppendCfxLog("提示", "CFX 端点尚未打开。");
                return;
            }

            if (!_cfxHasPublishChannel)
            {
                // 允许“只订阅”场景，所以这里单独提示没有发布通道。
                AppendCfxLog("提示", "当前没有配置发布地址。");
                return;
            }

            string text = CfxMessageTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            try
            {
                // 输入框既支持完整 Envelope JSON，也支持普通文本快捷构造 LogEntryRecorded。
                CFXEnvelope envelope = CreateEnvelopeFromInput(text);
                AmqpCFXEndpoint endpoint = _cfxEndpoint;

                // CFX SDK 的 Publish 是同步方法，放到线程池避免 broker 慢时卡住 WPF UI。
                await Task.Run(() => endpoint.Publish(envelope));
                AppendCfxLog(string.Format("发送 -> {0}", CfxPublishAddressTextBox.Text.Trim()), envelope.ToJson(true));
            }
            catch (Exception ex)
            {
                AppendCfxLog("错误", string.Format("发送 CFX 消息失败：{0}", ex.Message));
            }
        }

        private CFXEnvelope CreateEnvelopeFromInput(string text)
        {
            if (text.StartsWith("{") || text.StartsWith("["))
            {
                // 用户输入 JSON 时按完整 CFXEnvelope 解析，保留其中 MessageName、Version、Body 等字段。
                CFXEnvelope envelope = CFXEnvelope.FromJson(text);
                if (string.IsNullOrWhiteSpace(envelope.Source))
                {
                    // Source 为空时补当前界面里的 CFX Handle，方便接收端识别来源。
                    envelope.Source = CfxHandleTextBox.Text.Trim();
                }

                return envelope;
            }

            // 用户只输入普通文本时，自动包装成 CFX.ResourcePerformance.LogEntryRecorded。
            // 这让工具既能做严格 Envelope 调试，也能快速发一条可识别的 CFX 标准消息。
            return new CFXEnvelope(new LogEntryRecorded
            {
                Importance = LogImportance.Information,
                Message = text
            })
            {
                Source = CfxHandleTextBox.Text.Trim()
            };
        }

        private void CfxSampleButton_Click(object sender, RoutedEventArgs e)
        {
            LoadCfxSampleMessage();
        }

        private void CfxRequestSampleButton_Click(object sender, RoutedEventArgs e)
        {
            LoadCfxRequestResponseSamples();
        }

        private void LoadCfxSampleMessage()
        {
            // 示例消息选用 LogEntryRecorded，因为它结构简单，适合作为连通性测试载荷。
            CFXEnvelope envelope = new CFXEnvelope(new LogEntryRecorded
            {
                Importance = LogImportance.Information,
                Message = "Hello from ProtocolTestBench CFX / AMQP"
            })
            {
                Source = CfxHandleTextBox.Text.Trim()
            };

            CfxMessageTextBox.Text = envelope.ToJson(true);
        }

        private void LoadCfxRequestResponseSamples()
        {
            string handle = CfxHandleTextBox.Text.Trim();

            // AreYouThereRequest/Response 是 CFX 标准里的典型点对点 Request/Response 消息。
            CFXEnvelope requestEnvelope = new CFXEnvelope(new AreYouThereRequest
            {
                CFXHandle = handle
            })
            {
                Source = handle
            };

            CFXEnvelope responseEnvelope = new CFXEnvelope(new AreYouThereResponse
            {
                Result = new RequestResult
                {
                    Result = StatusResult.Success,
                    ResultCode = 0,
                    Message = "OK"
                },
                CFXHandle = handle,
                RequestNetworkUri = CfxRequestUriTextBox.Text.Trim(),
                RequestTargetAddress = "/"
            })
            {
                Source = handle
            };

            CfxRequestMessageTextBox.Text = requestEnvelope.ToJson(true);
            CfxAutoResponseTextBox.Text = responseEnvelope.ToJson(true);
        }

        private async void CfxExecuteRequestButton_Click(object sender, RoutedEventArgs e)
        {
            await ExecuteCfxPointToPointRequestAsync();
        }

        private async Task ExecuteCfxPointToPointRequestAsync()
        {
            if (_cfxEndpoint == null || !_isCfxOpen)
            {
                AppendCfxLog("提示", "CFX 端点尚未打开。");
                return;
            }

            string targetUri = CfxTargetRequestUriTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(targetUri))
            {
                AppendCfxLog("提示", "请输入目标请求 URI。");
                return;
            }

            Uri parsedTargetUri;
            if (!Uri.TryCreate(targetUri, UriKind.Absolute, out parsedTargetUri) ||
                (parsedTargetUri.Scheme != "amqp" && parsedTargetUri.Scheme != "amqps"))
            {
                AppendCfxLog("提示", "目标请求 URI 必须是完整 amqp:// 或 amqps:// 地址。");
                return;
            }

            string requestText = CfxRequestMessageTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(requestText))
            {
                AppendCfxLog("提示", "请输入请求 Envelope JSON。");
                return;
            }

            try
            {
                CFXEnvelope requestEnvelope = CreateEnvelopeFromInput(requestText);
                AmqpCFXEndpoint endpoint = _cfxEndpoint;

                AppendCfxLog(string.Format("请求 -> {0}", targetUri), requestEnvelope.ToJson(true));
                CFXEnvelope responseEnvelope = await endpoint.ExecuteRequestAsync(targetUri, requestEnvelope);
                AppendCfxLog(string.Format("响应 <- {0}", targetUri), responseEnvelope.ToJson(true));
            }
            catch (Exception ex)
            {
                AppendCfxLog("错误", string.Format("执行点对点请求失败：{0}", ex.Message));
            }
        }

        private async void CfxMessageTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            // 多行 JSON 输入框需要保留普通 Enter 换行，所以发送快捷键使用 Ctrl+Enter。
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                await SendCfxMessageAsync();
            }
        }

        private void CfxClearLogButton_Click(object sender, RoutedEventArgs e)
        {
            // 只清空 CFX 页日志，不影响 Socket 页历史。
            _cfxLogItems.Clear();
            CfxLogTextBox.Clear();
        }

        private void CfxEndpoint_OnCFXMessageReceived(AmqpChannelAddress source, CFXEnvelope message)
        {
            // SDK 事件可能来自后台线程，WPF 控件更新必须切回 Dispatcher 所在线程。
            Dispatcher.Invoke(() => { AppendCfxLog(string.Format("收到 <- {0}", FormatCfxAddress(source)), message.ToJson(true)); });
        }

        private void CfxEndpoint_OnMalformedMessageReceived(AmqpChannelAddress source, string message)
        {
            // broker 中可能有非 CFX 格式消息；保留原始文本便于定位路由或编码配置问题。
            Dispatcher.Invoke(() => { AppendCfxLog(string.Format("异常消息 <- {0}", FormatCfxAddress(source)), message); });
        }

        private CFXEnvelope CfxEndpoint_OnRequestReceived(CFXEnvelope request)
        {
            // Request/Response 事件需要同步返回响应，因此使用 Dispatcher.Invoke 读取 UI 中的自动响应 JSON。
            return Dispatcher.Invoke(() =>
            {
                AppendCfxLog("请求 <- 点对点", request.ToJson(true));

                try
                {
                    string responseText = CfxAutoResponseTextBox.Text.Trim();
                    CFXEnvelope response = string.IsNullOrWhiteSpace(responseText)
                        ? CreateDefaultCfxResponseEnvelope(request)
                        : CreateEnvelopeFromInput(responseText);

                    if (string.IsNullOrWhiteSpace(response.Source))
                    {
                        response.Source = CfxHandleTextBox.Text.Trim();
                    }

                    if (string.IsNullOrWhiteSpace(response.Target))
                    {
                        response.Target = request.Source;
                    }

                    AppendCfxLog("响应 -> 点对点", response.ToJson(true));
                    return response;
                }
                catch (Exception ex)
                {
                    CFXEnvelope fallback = CreateDefaultCfxResponseEnvelope(request, string.Format("自动响应 JSON 解析失败：{0}", ex.Message));
                    AppendCfxLog("错误", fallback.ToJson(true));
                    return fallback;
                }
            });
        }

        private CFXEnvelope CreateDefaultCfxResponseEnvelope(CFXEnvelope request, string message = "OK")
        {
            string handle = CfxHandleTextBox.Text.Trim();

            return new CFXEnvelope(new AreYouThereResponse
            {
                Result = new RequestResult
                {
                    Result = message == "OK" ? StatusResult.Success : StatusResult.Failed,
                    ResultCode = message == "OK" ? 0 : 1,
                    Message = message
                },
                CFXHandle = handle,
                RequestNetworkUri = CfxRequestUriTextBox.Text.Trim(),
                RequestTargetAddress = "/"
            })
            {
                Source = handle,
                Target = request.Source
            };
        }

        private void CfxEndpoint_OnConnectionEvent(
            ConnectionEvent eventType,
            Uri uri,
            int spoolSize,
            string errorInformation,
            Exception errorException)
        {
            // 连接事件包括成功、失败、中断和关闭。Spool 是 SDK 内部等待发送/重连的消息数量。
            Dispatcher.Invoke(() =>
            {
                string detail = string.IsNullOrWhiteSpace(errorInformation) ? uri.ToString() : string.Format("{0}，{1}", uri, errorInformation);
                if (errorException != null)
                {
                    detail = string.Format("{0}，{1}", detail, errorException.Message);
                }

                AppendCfxLog("连接", string.Format("{0}，Spool={1}，{2}", eventType, spoolSize, detail));
            });
        }

        private void SetCfxRunningState(bool isOpen, string status)
        {
            _isCfxOpen = isOpen;

            // 端点打开后锁定连接参数，避免运行中改 UI 文本但 SDK 实际通道没有同步变更。
            CfxHandleTextBox.IsEnabled = !isOpen;
            CfxBrokerTextBox.IsEnabled = !isOpen;
            CfxVirtualHostTextBox.IsEnabled = !isOpen;
            CfxPublishAddressTextBox.IsEnabled = !isOpen;
            CfxSubscribeAddressTextBox.IsEnabled = !isOpen;
            CfxRequestUriTextBox.IsEnabled = !isOpen;
            CfxOpenButton.Content = isOpen ? "关闭" : "打开";
            CfxSendButton.IsEnabled = isOpen && _cfxHasPublishChannel;
            CfxExecuteRequestButton.IsEnabled = isOpen;
            CfxStatusTextBlock.Text = status;
            CfxConnectionTextBlock.Text = isOpen ? "端点：已打开" : "端点：未打开";
        }

        private string GetCfxVirtualHost()
        {
            string virtualHost = CfxVirtualHostTextBox.Text.Trim();

            // CFX SDK 用 null 表示默认虚拟主机；空字符串可能被 broker 当作真实 vhost 名称处理。
            return string.IsNullOrWhiteSpace(virtualHost) ? null : virtualHost;
        }

        private static string FormatCfxAddress(AmqpChannelAddress address)
        {
            // 日志中把网络 URI 和 AMQP source/target 地址拼在一起，更接近用户在界面里填的配置。
            return string.Format("{0}{1}", address.Uri, address.Address);
        }

        private void AppendCfxLog(string source, string message)
        {
            string time = DateTime.Now.ToString("HH:mm:ss");
            string logLine = string.Format("[{0}] {1}: {2}", time, source, message);
            _cfxLogItems.Add(logLine);

            if (_cfxLogItems.Count > MaxLogItems)
            {
                // CFX JSON 可能很长，裁剪旧日志能明显降低长时间运行后的 UI 压力。
                _cfxLogItems.RemoveRange(0, _cfxLogItems.Count - MaxLogItems);
                CfxLogTextBox.Text = string.Join(Environment.NewLine, _cfxLogItems);
            }
            else
            {
                CfxLogTextBox.AppendText(string.Format("{0}{1}", logLine, Environment.NewLine));
            }

            CfxLogTextBox.ScrollToEnd();
        }
    }
}
