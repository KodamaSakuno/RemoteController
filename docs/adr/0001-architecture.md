# ADR 0001: 两端进程架构与通信方案

- 状态：已接受
- 日期：2026-09-17

## 背景

局域网内从 Windows PC 远程控制一台高通骁龙（ARM64）Windows 平板：PC 上显示平板屏幕的某一区域画面，用户在画面上做鼠标操作，操作回传到平板执行。

## 决策

### 进程划分

| 项目 | 端 | 职责 |
|---|---|---|
| `RemoteController.Server` | 被控端（平板，ARM64） | WebSocket 服务端；屏幕区域采集；接收鼠标事件并注入 |
| `RemoteController.Client` | 控制端（PC，x64） | WebSocket 客户端；显示画面；转发鼠标事件 |
| `RemoteController.Protocol` | 共享类库 | 两端共用的消息类型，防止协议漂移 |

### 通信：WebSocket over LAN

Server 侧用 Kestrel（`FrameworkReference=Microsoft.AspNetCore.App`）承载 WebSocket 端点。

理由：
- 无需自己实现帧分隔/粘包处理
- 浏览器原生支持 WebSocket，未来控制端可扩展为网页/手机端（对本项目场景有实际价值）
- 局域网带宽足够，暂不需要更底层的传输优化

### 画面采集：DXGI Desktop Duplication（已替换 GDI BitBlt）

变化驱动的屏幕采集：`AcquireNextFrame` 阻塞至屏幕更新才返回，空闲时零采集、零编码、零流量。区域通过 `CopySubresourceRegion` 源矩形裁剪，CPU 回读经 staging texture。经 `Vortice.Direct3D11`（3.8.3）接入。

**旋转在客户端呈现**（2026-09-18 决策）：服务端只发送纹理方向（面板原生）的帧，Hello 携带 `Rotation`；客户端用 `RenderTransform` 旋转渲染（GPU 零成本），指针坐标逆旋转映射回逻辑空间。理由：逐像素旋转重排是每帧 16MB 的 CPU gather，迁出平板后挂机/动画场景的功耗显著下降；服务端保留的只剩区域（逻辑）→纹理源矩形的换算（box 数学），旋转呈现逻辑放在可调试的 PC 侧。

光标不做合成（用户明确取舍），远程指针不可见于画面，以界面响应为反馈。

### 帧编码（M2 已落地）：JPEG via System.Drawing.Common

Server 端将 BGRA 帧编码为 JPEG（quality 75）后推流，Client 用 Avalonia `Bitmap(Stream)`（Skia）解码。带宽从 ~240MB/s 降至 ~5MB/s 量级。

理由：Server 为 Windows 专属进程，System.Drawing.Common 的 Windows 限制无影响；GDI+ 编码为原生速度，代码量小。

### Win32 互操作：CsWin32

Server 端的 Win32 API（SendInput、BitBlt、DPI 函数等）通过 `Microsoft.Windows.CsWin32` 源生成器接入，禁止手写 `DllImport`：签名直接来自 win32metadata，避免手写结构体布局错误；零运行时依赖。

### 鼠标注入：SendInput（P/Invoke）

零依赖，支持移动/按下/抬起，后续可扩展滚轮。

### 控制端 UI：Avalonia

用户指定。跨平台 UI 框架，保留控制端未来跑在非 Windows 平台的可能性。

## 后果

- 两端共享 `Protocol` 项目，任何消息格式变更必须同步考虑两端
- Server 依赖 Kestrel，平板端部署需携带 ASP.NET Core 运行时（后续可做 self-contained 发布）
- v1 原始帧带宽占用高，仅限局域网使用；广域网必须先做 M2 编码优化

## 里程碑

- M1：端到端最小链路（推帧、显示、鼠标回控）✅
- M1.1：区域收窄（`--region` 参数）✅
- M2：帧编码优化（JPEG，System.Drawing.Common）✅
- M3：平板自启动/服务化、连接配置
