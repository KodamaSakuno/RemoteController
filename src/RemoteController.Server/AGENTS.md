# RemoteController.Server — 被控端约定

被控端运行在 Windows（含 ARM64 平板）。本文件优先于根 AGENTS.md 的通用约定。

## Win32 / P/Invoke

- **一律使用 CsWin32 源生成**（`Microsoft.Windows.CsWin32`），禁止手写 `DllImport`。所需的 Win32 API 追加到 `NativeMethods.txt`，每行一个；相关结构体/常量由生成器自动带出。
- 生成的类型位于 `Windows.Win32` 命名空间。不得把生成代码复制粘贴成手写副本。
- DXGI/D3D11 通过 `Vortice.Direct3D11` 接入，禁止手写 COM vtable 互操作。

## DPI 与坐标

- 进程启动时调用 `SetProcessDpiAwarenessContext(PER_MONITOR_DPI_AWARE)`，禁止靠 manifest 声明后忘记验证。
- 所有屏幕坐标、区域尺寸必须以**物理像素**为口径：用 `GetDpiForSystem` / `GetSystemMetricsForDpi` 换算，禁止混用 GDI 逻辑像素与物理像素。
- 裁剪区域坐标相对虚拟屏幕原点（多显示器时 `GetSystemMetrics(SM_XVIRTUALSCREEN)`）。

## 鼠标注入

- 只通过 `SendInput` 注入，禁止 `mouse_event`。
- 移动事件使用绝对坐标（`MOUSEEVENTF_ABSOLUTE` 已按虚拟屏幕归一化），按下/抬起事件必须配对发送。
- 注入前校验目标坐标在裁剪区域内，防止协议错误把指针抛出区域。

## 资源管理

- D3D 对象（device、duplication、staging texture）建立一次复用，Dispose 逆序释放。
- `AcquireNextFrame` 成功后在 finally 中必须 `ReleaseFrame`，包括「仅指针移动、无桌面变化」（`AccumulatedFrames == 0`）的分支。
- GDI 采集已移除，禁止重新引入 BitBlt 路径。
