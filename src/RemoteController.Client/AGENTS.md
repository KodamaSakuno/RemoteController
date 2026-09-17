# RemoteController.Client — 控制端约定

Avalonia UI 控制端。本文件优先于根 AGENTS.md 的通用约定。

## 帧渲染

- 每帧从 JPEG 负载解码新 `Avalonia.Media.Imaging.Bitmap`（Skia 解码）并替换 `Image.Source`；**禁止**回退到 `WriteableBitmap` 原地写入——Avalonia 12 中该方式不会使已上传纹理失效。
- 旧位图延迟一帧 `Dispose`：渲染管线可能仍持有上一帧引用。
- 每帧 `byte[]` 负载属 Gen0 级分配，勿主动池化；如 profiling 证实热点，改造点隔离在接收循环一处。
- 帧是纹理方向（面板原生），旋转由 `Image.RenderTransform`（RotateTransform，中心原点）在渲染时完成，禁止在客户端做逐像素旋转。

## 鼠标映射（旋转感知）

- 图像控件显式按纹理尺寸设定（位图同向，`Stretch` 无失真），视觉经 RenderTransform 旋转。
- 指针映射：`GetPosition` 已逆变换到控件本地（纹理方向）坐标，直接归一化（守卫 [0,1]，超出=视觉黑区不注入）→ 纹理→逻辑（与服务端 logical→texture 公式互逆）→ + 区域原点 = 虚拟屏幕绝对物理像素。**禁止**再手动逆旋转（双重旋转会触发守卫导致零注入）。
- 窗口初始尺寸按**逻辑**宽高比适配屏幕（90/270 时逻辑宽高 = 纹理宽高互换）。

## 连接生命周期

- 连接仅由「连接」按钮触发，断开即停在断线状态，不做自动重连（重连属 M3）。
- 帧计数器显示在状态栏，是区分「帧未到达」与「渲染未刷新」的第一诊断手段。
