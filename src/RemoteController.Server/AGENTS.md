# RemoteController.Server — 被控端约定

被控端运行在 Windows（含 ARM64 平板）。本文件优先于根 AGENTS.md 的通用约定。

## Win32 / P/Invoke

- **一律使用 CsWin32 源生成**（`Microsoft.Windows.CsWin32`），禁止手写 `DllImport`。所需的 Win32 API 追加到 `NativeMethods.txt`，每行一个；相关结构体/常量由生成器自动带出。
- 生成的类型位于 `Windows.Win32` 命名空间。不得把生成代码复制粘贴成手写副本。
- M2 引入 DXGI 采集时添加 `Vortice.Direct3D11`；在此之前不引入其他第三方依赖。

## DPI 与坐标

- 进程启动时调用 `SetProcessDpiAwarenessContext(PER_MONITOR_DPI_AWARE)`，禁止靠 manifest 声明后忘记验证。
- 所有屏幕坐标、区域尺寸必须以**物理像素**为口径：用 `GetDpiForSystem` / `GetSystemMetricsForDpi` 换算，禁止混用 GDI 逻辑像素与物理像素。
- 裁剪区域坐标相对虚拟屏幕原点（多显示器时 `GetSystemMetrics(SM_XVIRTUALSCREEN)`）。

## 鼠标注入

- 只通过 `SendInput` 注入，禁止 `mouse_event`。
- 移动事件使用绝对坐标（`MOUSEEVENTF_ABSOLUTE` 已按虚拟屏幕归一化），按下/抬起事件必须配对发送。
- 注入前校验目标坐标在裁剪区域内，防止协议错误把指针抛出区域。

## 资源管理

- GDI 对象（DC、Bitmap）严格配对释放：`DeleteDC` / `DeleteObject`，用 `SafeHandle` 封装避免泄漏；采集循环内不重复创建 DC，建立一次复用。
