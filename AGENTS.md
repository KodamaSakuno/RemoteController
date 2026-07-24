# AGENTS.md

## 项目概述

局域网远程控制（画面推流 + 键鼠转发），.NET 10，Windows 专用。三个项目：

| 项目 | 说明 |
| --- | --- |
| `RemoteController.Shared` | 二进制协议（长度前缀分帧），被双方引用 |
| `RemoteController.Host` | 被控端（控制台）：DXGI Desktop Duplication 抓屏 → MediaFoundation H.264 编码 → TCP 推流；`SendInput` 注入键鼠 |
| `RemoteController.Client` | 控制端（Avalonia + ReactiveUI）：MF 解码 → `WriteableBitmap` 三缓冲轮换显示（绝不写入合成器可能在读的两块）；采集键鼠转发 |

协议 v2：`[int32 bodyLength][byte type][payload]`，小端。`VideoFrame(timestamp, keyframe, data)` 为 H.264 Annex B 访问单元，首帧必为关键帧且前置 SPS/PPS（编码器输出类型里读不到 `MF_MT_MPEG_SEQUENCE_HEADER` 的 MFT 靠码流内嵌）。

## 构建与运行

```bash
dotnet build RemoteController.slnx          # 全量构建
dotnet run --project RemoteController.Host -- --port 47800 --fps 20 --bitrate 10 --raw-port 47801
dotnet run --project RemoteController.Client
```

- Host 参数：`--port`（默认 47800）、`--fps`（默认 30）、`--bitrate`（Mbps，默认 8）、`--raw-port`（默认关；开启后在该端口输出**无协议的 Annex B H.264 裸流**，首帧前置 SPS/PPS，供外部播放器使用）。
- 用 ffplay 看画面：`ffplay -f h264 -fflags nobuffer -flags low_delay tcp://<被控端IP>:47801`（裸流无时间戳，节奏不对就加 `-framerate 20`）。
- raw 端口与协议端口各自独立起会话（独立的抓屏+编码实例），互不干扰；黑帧抑制在两条路径上都生效（休眠时画面定格）。
- 修改 Host 代码后重跑前，先杀掉旧 `RemoteController.Host` 进程，否则 exe 被锁、构建失败。

## 本地验证回路（重要）

- **裸协议探针**（`.scratch/probe`，控制台，已 gitignore）：`--stream [port] [秒]` 连 Host 握手收帧、校验首帧 SPS/PPS/IDR 结构并报 fps/码率；`--dump [目录]` 各抓一帧 `CaptureNv12Gpu`/`CaptureNv12` 写成 PNG 并比对两路 NV12 逐字节差（转换正确性回归）；`--monitor [N]` 连续 N 帧统计黑帧（max Y<24）；`--panel-test [秒] [es]` 每秒一帧分类，观察息屏黑帧转折点及 ES 保持效果；`--display on|off` 广播 SC_MONITORPOWER。用于不依赖 GUI 的快速回归。
- **无头基准**：`Avalonia.Headless` 起无窗客户端跑真实接收-解码-显示路径（初始化后必须 `SynchronizationContext.SetSynchronizationContext(null)`，否则 await 全部挂死）。
- **表面接受矩阵探针**（`tools/SurfaceProbe`，已入库）：对首个硬件 H.264 编码 MFT 跑"完整 MFT 仪式"（async 解锁 → 协商 → D3D 管理器 → BeginStreaming → NeedInput 门控 → ProcessInput → drain）下的纹理形态矩阵（bind/misc 标志 × NV12/P010 × 采样创建方式 × 设备变体）加判别实验（manager 对内存输入的影响、LockDevice、feature level）。改捕获/编码路径前先跑它。
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
16. **NV12 读回别走 planar staging**：NV12 staging 纹理配 keyed-mutex 源拷贝是 `E_INVALIDARG` 雷区。ffmpeg（hwcontext_d3d11va）也只 map 子资源 0 当整块 blob 读。本项目用两个单平面 staging（R8 + R8G8）分别 map，最稳。
17. **平板无人值守 → 显示管线休眠 → 采集全黑帧**：超时后 DXGI 抓屏**不报错、照常用帧返回，内容全黑（Y 恒为 16）**——客户端表现为"时不时黑屏闪烁"，且管线会以数 Hz 在休眠/唤醒间振荡（任何可见桌面更新都唤醒它，控制台打印也算），所以黑帧是成串突发的而非持续。`SendInput` 注入的远程输入**不重置显示空闲计时器**。**`SetThreadExecutionState(ES_DISPLAY_REQUIRED)` 在本机实测防不住**（录制会话中 2/3 帧是黑的，ES 全程持有）。有效对策是**黑帧抑制**：`IsBlackFrame`（Y 平面抽样 max<24）命中的帧不编码不发送——休眠时桌面本来就是冻结的，客户端留住最后一帧真实画面即正确体验；抑制期加 100ms 节流，否则采集-转换-丢弃空转成热循环（实测曾 ~74 帧/秒空转）。`DisplayPower.KeepAwake()`（ES + `SC_MONITORPOWER` 尽力唤醒）仍保留——对其他机器可能有效，且零成本。锁屏后 `DuplicateOutput` 报 `E_ACCESSDENIED`（会话报错断开）。验证回路：`--record` 录码流 + `--check` 逐帧解码统计黑帧。

### 设备差异（实测）

- **Intel（本机）**：编码/解码一切正常；接受 ARGB32 输入（直接喂 BGRA，零色彩转换，已走通）。NVIDIA H.264 编码 MFT 在 `SetOutputType` 报 `E_UNEXPECTED`，不可用（跳过即可）。
- **QCOM（骁龙平板）**：编码器 "QCOM Hardware Encoder - H264"，async、接受 D3D 管理器、接受低延迟/B=0/GOP 属性；输入只有 NV12 和 P010（无 RGB32）。**纹理输入已定论：不可用。** `tools/SurfaceProbe`（完整 MFT 仪式 + 事件门控）实测：17 种纹理形态（bind/misc 全组合、Shared/KeyedMutex/NTHandle、VideoEncoder、VideoSupport/宽 feature-level 设备）× 两种采样创建（`MFCreateDXGISurfaceBuffer`、`MFCreateVideoSampleFromSurface`）全部在 `ProcessInput` 报 `E_INVALIDARG`；`MF_SA_D3D11_AWARE=1` 形同虚设；无 `IMFVideoSampleAllocator`；manager 设备不被掉包；stream id 默认 0/0；LockDevice 无效。判别实验：同一 manager 下**内存输入正常**——manager 无毒，毒的就是 surface 输入本身。推论：OBS 的 h264_mf 走 ffmpeg obs-ffmpeg 路径（内存帧），"OBS 正常"从未证明过纹理输入可用。ffmpeg 源码佐证其 D3D11 纹理池 `BindFlags=0/RenderTarget、MiscFlags=0`（ddagrab），与探测矩阵一致。
- **客户端（x64 PC）**：微软软解（无硬解 MFT 可用）+ `AVLowLatencyMode` 后零滞留；NV12→BGRA 转换已 `Parallel.For` 行并行（~3ms/帧@1080p）。

## 当前状态

- **CPU 路径全链路工作**（所有设备）：抓屏 → GPU 旋转 → **GPU 渲染 NV12 + 单平面读回**（1.5 字节/像素、纯 memcpy，替代原 BGRA 读回 + CPU 逐像素转换）→ 编码推流 → 解码显示，亚秒延迟（平板建议 `--fps 20 --bitrate 10`）。GPU 转换失败时自动回退纯 CPU 转换（`CaptureNv12`）。
- **GPU 路径（零拷贝喂纹理）**：Intel 上走通（RGB32 直接喂）；QCOM 已由探针定论不支持表面输入，自动安全回退 CPU 路径（重建编码器，不崩）。
- RGB32 输入只在传入 D3D 设备时协商：无设备的回退编码器固定 NV12 输入，与内存喂帧路径保持一致。
- 会话期间 Host 通过 `DisplayPower`（SetThreadExecutionState + SC_MONITORPOWER 尽力唤醒）保持被控端屏幕常亮，并对采集到的休眠黑帧做抑制（`IsBlackFrame`，不编码不发送，客户端留住最后真实帧；抑制期 100ms 节流防空转），杜绝屏幕休眠导致的黑帧推流（见平台知识库 #17）。
- QCOM 纹理问题已结案（见"设备差异"）：非配置问题，是该 MFT 驱动根本不接受 DXGI 表面输入；零拷贝念想止步于此，GPU NV12 渲染 + 读回是该设备的最优形态。

## 约定

- 代码注释用英文、解释"为什么"（不是复述代码）；提交信息英文、单一主题、带根因说明。
- 修改 Host 相关行为后更新本文件；协议变更双方同步并 bump `ProtocolInfo.Version`。
