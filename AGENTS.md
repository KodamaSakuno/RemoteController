# AGENTS.md

## 项目概述

局域网远程控制（画面推流 + 键鼠转发），.NET 10，Windows 专用。三个项目：

| 项目 | 说明 |
| --- | --- |
| `RemoteController.Shared` | 二进制协议（长度前缀分帧），被双方引用 |
| `RemoteController.Host` | 被控端（控制台）：DXGI Desktop Duplication 抓屏 → MediaFoundation H.264 编码 → TCP 推流；`SendInput` 注入键鼠 |
| `RemoteController.Client` | 控制端（Avalonia + ReactiveUI）：MF 解码 → `WriteableBitmap` 乒乓双缓冲显示；采集键鼠转发 |

协议 v2：`[int32 bodyLength][byte type][payload]`，小端。`VideoFrame(timestamp, keyframe, data)` 为 H.264 Annex B 访问单元，首帧必为关键帧且前置 SPS/PPS（编码器输出类型里读不到 `MF_MT_MPEG_SEQUENCE_HEADER` 的 MFT 靠码流内嵌）。

## 构建与运行

```bash
dotnet build RemoteController.slnx          # 全量构建
dotnet run --project RemoteController.Host -- --port 47800 --fps 20 --bitrate 10
dotnet run --project RemoteController.Client
```

- Host 参数：`--port`（默认 47800）、`--fps`（默认 30）、`--bitrate`（Mbps，默认 8）。
- 修改 Host 代码后重跑前，先杀掉旧 `RemoteController.Host` 进程，否则 exe 被锁、构建失败。

## 本地验证回路（重要）

- **裸协议探针**（`.scratch/probe`，控制台，需自建）：TcpClient + `MessageStream` 握手收帧 → MF 解码校验帧数。用于不依赖 GUI 的快速回归。
- **无头基准**：`Avalonia.Headless` 起无窗客户端跑真实接收-解码-显示路径（初始化后必须 `SynchronizationContext.SetSynchronizationContext(null)`，否则 await 全部挂死）。
- 调试经验：**凡是跨端/驱动相关问题，先打"阶段标签"日志**（每个可疑调用一段 `stage` 字符串，异常时带出来），一次定位，拒绝盲猜。

## 平台知识库（全是踩过的坑，症状 → 根因 → 修法）

### MediaFoundation / MFT 通用

1. **硬件（async）MFT 的类型协商报 `MF_E_TRANSFORM_ASYNC_LOCKED`** → 必须先解锁：通过 `IMFTransform::GetAttributes()`（**不是** `QueryInterface<IMFAttributes>`）设置 `MF_TRANSFORM_ASYNC_UNLOCK=1`。微软软编 MFT 不需要。
2. **async MFT 光解锁不够**：`ProcessInput/ProcessOutput` 必须由 `METransformNeedInput/HaveOutput` 事件门控（ffmpeg h264_mf 的做法）。直接调会 `MF_E_NOTACCEPTING`（0xC00D36B5，注意不是 B4）或 `E_UNEXPECTED`。同步（软编）MFT 直接泵即可。
3. **`ICodecAPI` 的真实 vtable 在 `strmif.h`**（8 个方法，**没有** QueryCapability/IsAvailable，顺序：IsSupported, IsModifiable, GetParameterRange, GetParameterValues, GetDefaultValue, GetValue, SetValue, RegisterForEvent, UnregisterForEvent, SetAllDefaults, …）。按网上常见版本写会错位两槽，全部 `E_NOTIMPL`。
4. **VARIANT 参数用 VT_UI4**：布尔属性（如 `AVLowLatencyMode`）也必须传 `uint`（0/1），VT_BOOL 会 `E_INVALIDARG`。
5. **每个 MF 对象都要确定性 Dispose**：sample/buffer/media type/event/activate，尤其 `ProcessOutput` 里 `MFCreateMemoryBuffer` 创建的 buffer——只靠终结器会因 GC 压力小而堆积（本项目曾因这个每分钟涨 GB 级内存）。
6. **`MF_E_TRANSFORM_STREAM_CHANGE`**：编码器/解码器都可能报（见到首帧后改输出格式）。处理：重新 `SetOutputType(GetOutputAvailableType(0,0))`，刷新派生状态后继续。
7. **解码端延迟的隐藏来源**：微软软解默认扣住 ~36 帧管线（变化驱动的流在低帧率下放大成十几秒）。`CODECAPI_AVLowLatencyMode=true` 后扣 0 帧。这是远程桌面延迟的头号来源，不是网络。
8. **编码器可用属性（已验证可设）**：`AVLowLatencyMode`、`AVEncMPVDefaultBPictureCount=0`、`AVEncMPVGOPSize`。`AVEncCommonRateControlMode=LowDelayVBR` 会让 QCOM 后续 `SetOutputType` 报 `E_INVALIDARG`，不要设。
9. **序列头**：Intel/QCOM 的输出类型上经常读不到 `MF_MT_MPEG_SEQUENCE_HEADER`（`MF_E_ATTRIBUTENOTFOUND`）——轮询 ~80ms 后放弃即可，关键帧码流内嵌 SPS/PPS。

### DXGI Desktop Duplication / D3D11

10. **抓到的帧是面板原生扫描方向**：横竖屏旋转场景要按 `DXGI_OUTDUPL_DESC.Rotation` 的具名角度顺时针矫正（当前实现：GPU 全屏 quad 按角点重排 UV；`SetDirtyVert` 的微软官方示例是权威依据）。
11. **抓帧追赶**：桌面更新快于消费时帧在队列里变老。用 `DXGI_OUTDUPL_FRAME_INFO.AccumulatedFrames > 2` 触发"释放当前帧→0 超时重取"循环；**必须先 ReleaseFrame 再 AcquireNextFrame**（持帧时 acquire 报 `DXGI_ERROR_INVALID_CALL`）。
12. **`AcquireNextFrame` 持帧规则**：释放后才能取下一帧。
13. **自建纹理要 SRV 就必须有 `D3D11_BIND_SHADER_RESOURCE`**：Intel 宽松放行，Adreno（骁龙平板）严格校验报 `E_INVALIDARG`。旋转用的 `_rotated` 需要 `RenderTarget|ShaderResource`。
14. **Adreno 不接受 NV12 作为绘制目标**：改为渲到 R8/R8G8 平面纹理再 `CopySubresourceRegion` 进 NV12 平面。
15. **设过 D3D 管理器的 MFT 不再接受系统内存输入**（native 0xC0000005 崩溃）。GPU 路径失败回退 CPU 时必须**新建一个不带管理器的编码器**，不能只翻标志位。

### 设备差异（实测）

- **Intel（本机）**：编码/解码一切正常；接受 ARGB32 输入（直接喂 BGRA，零色彩转换，已走通）。NVIDIA H.264 编码 MFT 在 `SetOutputType` 报 `E_UNEXPECTED`，不可用（跳过即可）。
- **QCOM（骁龙平板）**：编码器 "QCOM Hardware Encoder - H264"，async、接受 D3D 管理器、接受低延迟/B=0/GOP 属性；输入只有 NV12 和 P010（无 RGB32）；**`MFCreateDXGISurfaceBuffer`/`ProcessInput` 对我们的 NV12 纹理报 `E_INVALIDARG`（未解决）**，已排除：ShaderResource 标志、纯 RenderTarget、VideoEncoder 标志（反而有害）、Shared 标志。OBS 在同设备 h264_mf 硬编 2560×1600@60 正常，理论上 QCOM 接受正确形态的 NV12 surface，差异点待查（见下）。
- **客户端（x64 PC）**：微软软解（无硬解 MFT 可用）+ `AVLowLatencyMode` 后零滞留；NV12→BGRA 转换已 `Parallel.For` 行并行（~3ms/帧@1080p）。

## 当前状态

- **CPU 路径全链路工作**（所有设备）：抓屏 → GPU 旋转 → 读回并行转 NV12 → 编码推流 → 解码显示，30fps，亚秒延迟（平板建议 `--fps 20 --bitrate 10`）。
- **GPU 路径（零拷贝喂纹理）**：Intel 上走通（RGB32 直接喂）；QCOM 上 `surface sample feed failed: E_INVALIDARG` 未解决，自动安全回退 CPU 路径（重建编码器，不崩）。
- QCOM 纹理排查现场：`H264Encoder.Encode(ID3D11Texture2D)`（标签 "surface sample feed failed"），纹理在 `DxgiScreenCapture.EnsureBuffers` 的 `_nv12Pool`（NV12、Default、RenderTarget、SharedKeyedMutex）。下一步候选：调查 OBS 实际喂给 MFT 的纹理形态（`obs-d3d11` 源码/RenderDoc 抓包）；`MFCreateVideoSampleFromSurface` 或由管理器分配 surface；或确认用户 OBS 测试时是否真的走了纹理输入（可能实际是内存输入）。

## 约定

- 代码注释用英文、解释"为什么"（不是复述代码）；提交信息英文、单一主题、带根因说明。
- 修改 Host 相关行为后更新本文件；协议变更双方同步并 bump `ProtocolInfo.Version`。
