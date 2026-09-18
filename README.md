# RemoteController

因挂机场景的需求诞生：平板挂着 KanColle 跑远征/演习，人坐在 PC 前想随时看一眼、点一下——不必起身去拿平板。

局域网远程控制工具：在 PC 上显示并操作 Windows 平板（高通 ARM64）屏幕的指定区域，平板跑游戏，PC 当显示器和键鼠。

## 架构

两端进程 + WebSocket over LAN：

| 项目 | 端 | 职责 |
|---|---|---|
| `src/RemoteController.Server` | 被控端（平板，ARM64/x64） | DXGI Desktop Duplication 变化驱动采集 → JPEG 推流；接收鼠标事件并 SendInput 注入 |
| `src/RemoteController.Client` | 控制端（PC） | Avalonia UI：解码显示（旋转自适应）、鼠标/按键转发、断线自动重连 |
| `src/RemoteController.Protocol` | 共享 | 帧头与消息协议，两端共用 |

特性：变化驱动（屏幕静止时零 CPU 零流量）、区域采集（`--region`）、JPEG 编码（quality 可配）、横竖屏自适应（旋转在客户端呈现）、鼠标回控（绝对坐标注入）。

架构决策详见 [docs/adr/0001-architecture.md](docs/adr/0001-architecture.md)。

## 要求

- .NET 10 SDK
- Server（被控端）：Windows（含 ARM64 平板；DXGI 依赖 Windows）
- Client（控制端）：跨平台，Windows / macOS / Linux 均可（Avalonia，macOS 已实机验证）
- 平板与 PC 同一局域网

## 运行

被控端（平板）：

```bash
dotnet run --project src/RemoteController.Server
# 可选：只采集指定区域（物理像素，相对虚拟屏幕原点）
dotnet run --project src/RemoteController.Server -- --region 0,0,1600,900
```

配置也可写进 `src/RemoteController.Server/appsettings.json`（`Urls` / `Region` / `Quality`），命令行优先。

控制端（PC）：

```bash
dotnet run --project src/RemoteController.Client -- <平板IP或主机名>
```

主机支持 `host:port` 写法；上次连接的主机会自动记住。

## 验证

```bash
dotnet build RemoteController.slnx   # 必须 0 错误 0 警告
dotnet test RemoteController.slnx    # 必须全部通过
```

## 目录

```
src/     Server / Client / Protocol
tests/   xUnit v3 + Microsoft.Testing.Platform v2
docs/    ADR（架构决策）、开发日志说明
```

## 备注

- `AGENTS.md` 是 AI 代理在本仓库工作的约定（注释纪律、提交粒度、双机开发回路），人类贡献者也可一读。
- `docs/dev-log.md` 是双机联调时的临时协作文件（已被 gitignore，不入库）。
- 平板无人值守自启动（任务计划程序 + 自包含发布）尚未实现，见 ADR 里程碑。
