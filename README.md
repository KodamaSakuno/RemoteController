# RemoteController

局域网远程控制方案（画面传输 + 键鼠转发），.NET 10。

> **安全提示**：当前版本无鉴权、无加密，仅限可信局域网使用。

## 项目结构

| 项目 | 说明 |
| --- | --- |
| `RemoteController.Shared` | 二进制协议（长度前缀分帧）与消息读写，被双方引用 |
| `RemoteController.Host` | 被控端（仅 Windows）：DXGI Desktop Duplication 抓屏 → JPEG 推流，`SendInput` 注入键鼠 |
| `RemoteController.Client` | 控制端（Avalonia + ReactiveUI）：连接、显示画面、采集键鼠转发 |

## 使用

被控端（Windows 机器）：

```bash
dotnet run --project RemoteController.Host -- --port 47800
```

控制端：

```bash
dotnet run --project RemoteController.Client
```

在界面中输入被控端 IP 和端口（默认 47800），点击「连接」。点击画面区域后，鼠标移动/点击/滚轮与键盘输入都会转发到被控端。

## 协议（TCP，小端，长度前缀）

`[int32 bodyLength][byte messageType][payload]`

- 握手：Client → `ClientHello(version)`；Host → `ServerHello(version, width, height)`
- 推流：Host 推送 `Frame(width, height, jpeg)`，约 30 FPS 上限；基于 DXGI Desktop Duplication，画面无变化时不推帧（空闲零带宽、零编码开销）
- 输入：`MouseMove(x,y)` / `MouseButton(btn,down)` / `MouseWheel(steps)` / `KeyEvent(vk,down)`，坐标为远端物理像素，键盘为 Windows VK 码

## 已知限制 / 后续方向

- 仅主显示器；无鉴权加密；无剪贴板/文件传输
- 编码可升级差量帧/H.264；跨公网需中继或打洞
