# RemoteController.Client — 控制端约定

Avalonia UI 控制端。本文件优先于根 AGENTS.md 的通用约定。

## 帧渲染

- 每帧从 JPEG 负载解码新 `Avalonia.Media.Imaging.Bitmap`（Skia 解码）并替换 `Image.Source`；**禁止**回退到 `WriteableBitmap` 原地写入——Avalonia 12 中该方式不会使已上传纹理失效。
- 旧位图延迟一帧 `Dispose`：渲染管线可能仍持有上一帧引用。
- 每帧 `byte[]` 负载属 Gen0 级分配，勿主动池化；如 profiling 证实热点，改造点隔离在接收循环一处。

## 鼠标映射

- 显示用 `Stretch.Fill`，窗口尺寸按 Hello 的帧尺寸调整，使图像区与控制区重合。
- 坐标映射 = 指针在图像区的相对位置 × 帧尺寸 + 区域原点（Hello 的 X/Y），结果为虚拟屏幕绝对物理像素。

## 连接生命周期

- 连接仅由「连接」按钮触发，断开即停在断线状态，不做自动重连（重连属 M3）。
- 帧计数器显示在状态栏，是区分「帧未到达」与「渲染未刷新」的第一诊断手段。
