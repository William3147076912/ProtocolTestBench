# ProtocolTestBench

> 一款面向工业通信场景的多协议调试工具，基于 WPF（.NET 8）开发，支持 TCP Socket、MQTT、STOMP 和 IPC CFX（AMQP 1.0）四种协议的连通性测试与消息收发。

---

## 目录

- [功能概览](#功能概览)
- [技术栈](#技术栈)
- [模块说明](#模块说明)
  - [TCP Socket](#tcp-socket)
  - [MQTT](#mqtt)
  - [STOMP](#stomp)
  - [IPC CFX / AMQP 1.0](#ipc-cfx--amqp-10)
  - [JSON 美化工具](#json-美化工具)
- [快速上手](#快速上手)
- [项目结构](#项目结构)
- [依赖说明](#依赖说明)
- [构建与运行](#构建与运行)

---

## 功能概览

| 功能 | 描述 |
|------|------|
| **TCP Socket** | 支持服务端/客户端双模式，服务端可同时管理多客户端并选择性群发，自动识别并回复 ALIVE_REQ 心跳包 |
| **MQTT** | 连接 MQTT Broker，支持订阅（含 QoS 0/1/2、通配符）、发布消息和自动断线检测 |
| **STOMP** | 连接 STOMP Broker（支持 tcp/ssl），支持 Queue/Topic 目标、发送与订阅、多种 ACK 模式 |
| **IPC CFX** | 基于 AMQP 1.0 的 IPC CFX 端点，支持发布/订阅通道和点对点 Request/Response |
| **JSON 美化** | 独立 JSON 编辑器，带实时语法高亮、格式校验、一键美化和智能括号补全 |

---

## 技术栈

- **平台**：Windows Desktop（WPF）
- **运行时**：.NET 8（`net8.0-windows`）
- **语言**：C# 7.3
- **UI 框架**：WPF + XAML

---

## 模块说明

### TCP Socket

文件：`MainWindow.Socket.cs`

支持在同一界面切换 **服务端** 和 **客户端** 两种工作模式。

**服务端模式**
- 绑定本机任意 IPv4 地址，监听指定端口
- 支持多客户端并发接入，每个客户端独立接收循环
- 提供下拉多选框，可选择性向部分客户端或所有客户端群发消息
- 客户端断开后自动从列表移除，连接数实时刷新

**客户端模式**
- 支持直接填写 IP 地址或域名（自动 DNS 解析为 IPv4）
- 连接到远端 TCP 服务端，实时显示接收数据
- 服务端断开时自动回到未启动状态

**心跳保活**
- 自动识别 `ALIVE_REQ` 心跳包并回复 `<ASYS><ALIVE_RES /></ASYS>`
- 心跳累计 100 次后才在日志中打印一次，避免日志被心跳淹没

**快捷键**：`Ctrl+Enter` 快速发送

---

### MQTT

文件：`MainWindow.Mqtt.cs`

基于 [MQTTnet](https://github.com/dotnet/MQTTnet) v5 构建。

**连接配置**
- Broker 地址 / 端口 / ClientId / 用户名密码
- 可选 Clean Session
- 超时 10 秒，KeepAlive 30 秒

**订阅**
- 支持 QoS 0 / 1 / 2
- 自动把 `*` 转换为 MQTT 标准通配符 `+`（仅用于订阅过滤器）
- 切换订阅主题时自动取消旧订阅

**发布**
- 可配置 QoS 和 Retain 标志
- 内置示例 JSON 消息，方便连通性验证

**快捷键**：`Ctrl+Enter` 快速发布

---

### STOMP

文件：`MainWindow.Stomp.cs`

基于 [Stomp.Net](https://github.com/nicknisi/stomp.net) v2 构建。

**连接配置**
- 支持 `tcp://` 和 `ssl://` 两种 URI 方案
- 可配置 ClientId、用户名密码
- 支持自定义 Host Header（用于 RabbitMQ virtual host 场景）
- ACK 模式：Auto / Client / Individual

**订阅与发送**
- 目标类型：Queue / Topic
- 发送时自动附带 `content-type: application/json; charset=utf-8` 头
- 非自动 ACK 模式下收到消息后立即 ACK，避免 broker 重复投递

**快捷键**：`Ctrl+Enter` 快速发送

---

### IPC CFX / AMQP 1.0

文件：`MainWindow.Cfx.cs`

基于 [IPC CFX SDK](https://github.com/IPCConnectedFactoryExchange/CFX) v2.1.1（`CFX.CFXSDK`）构建，用于 IPC Connected Factory Exchange 标准消息的调试。

**端点配置**
- CFX Handle（设备标识符）
- AMQP Broker URI（支持 `amqp://` 和 `amqps://`）
- Virtual Host（可选，用于 RabbitMQ 等需要指定 vhost 的场景）
- 发布地址 / 订阅地址 / 本端请求 URI（均可选填）

**发布 / 订阅**
- 发送框支持：完整 `CFXEnvelope` JSON 或普通文本（自动包装为 `LogEntryRecorded`）
- 接收 broker 推送的 `CFXEnvelope`，异常格式消息单独标注

**点对点 Request / Response**
- 主动发起请求到指定目标 URI
- 自动响应框：收到 Request 时按预设 JSON 回复，解析失败时回退为默认 `AreYouThereResponse`
- 内置 `AreYouThereRequest` / `AreYouThereResponse` 示例，方便快速验证

**注意**：已禁用 CFX SDK 的 `KeepAliveEnabled`，避免在 RabbitMQ AMQP 1.0 插件下产生大量空闲连接。

**快捷键**：`Ctrl+Enter` 快速发送

---

### JSON 美化工具

文件：`JsonFormatterWindow.xaml.cs`

独立弹窗，可从主界面菜单或工具栏打开。

**编辑器特性**
- 实时 JSON 语法高亮（Key 红色、字符串绿色、数字橙色）
- 实时校验，显示错误行列位置
- 一键格式化（美化缩进）
- 智能括号自动补全：输入 `{`、`[`、`"` 自动补右侧
- 括号内回车自动展开为多行结构
- 空字符串 `""` 成对删除

---

## 快速上手

1. 克隆或下载仓库
2. 使用 Visual Studio 2022 或 JetBrains Rider 打开 `ProtocolTestBench.sln`
3. 还原 NuGet 包（首次打开时自动完成）
4. 按 `F5` 运行，选择对应协议标签页开始调试

> **系统要求**：Windows 10/11，[.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)

---

## 项目结构

```
ProtocolTestBench/
├── App.xaml / App.xaml.cs           # 应用入口
├── MainWindow.xaml                  # 主窗口 XAML（含四个协议标签页布局）
├── MainWindow.xaml.cs               # 主窗口代码入口（partial class 汇总）
├── MainWindow.Socket.cs             # TCP Socket 模块逻辑
├── MainWindow.Mqtt.cs               # MQTT 模块逻辑
├── MainWindow.Stomp.cs              # STOMP 模块逻辑
├── MainWindow.Cfx.cs                # IPC CFX / AMQP 1.0 模块逻辑
├── MainWindow.Navigation.cs         # 标签页导航逻辑
├── MainWindow.Tools.cs              # 工具栏/公共常量（MaxLogItems 等）
├── JsonFormatterWindow.xaml         # JSON 美化器 XAML
├── JsonFormatterWindow.xaml.cs      # JSON 美化器逻辑
├── Styles.xaml                      # 全局 WPF 样式
├── Resources/                       # 图标等静态资源
└── ProtocolTestBench.csproj         # 项目文件
```

---

## 依赖说明

| 包 | 版本 | 用途 |
|----|------|------|
| `CFX.CFXSDK` | 2.1.1 | IPC CFX 标准消息类型、AMQP 1.0 传输端点 |
| `MQTTnet` | 5.1.0.1559 | MQTT 客户端（连接、发布、订阅） |
| `Stomp.Net` | 2.4.0 | STOMP over TCP/SSL 客户端 |

---

## 构建与运行

```powershell
# 还原依赖
dotnet restore ProtocolTestBench.sln

# 调试运行
dotnet run --project ProtocolTestBench.csproj

# 发布（自包含，单文件）
dotnet publish ProtocolTestBench.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

> 发布时如需缩小体积，可移除 `--self-contained true` 改为依赖系统已安装的 .NET 8 运行时。
