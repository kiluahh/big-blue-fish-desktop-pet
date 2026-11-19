# Linux 语音朗读设计（三个本地引擎）

日期：2026-11-19
状态：已实现（Linux x64）

## 目标

Linux x64 版提供与 Windows 标准版一致的本地语音朗读：

- 三个本地引擎都能用：F5-TTS（默认、核心）、Kokoro v1.1-zh、GPT-SoVITS v2。
- 引擎、音色、语速、音调、合成步数、音量可在设置页调整，权重在设置页下载。
- 自动关心、手动测试、Agent/MCP 三类发言按范围朗读；气泡与声音同步（可关）。
- 服务随程序启停，退出时连同子进程收走。

## 非目标

- macOS 不引入语音（沿用既有平台范围），相关代码用 `#if COMPANION_LINUX` 隔离。
- 程序不自动安装 Python 与依赖，不自动下载模型；沿用 Windows 语音页的约定。
- 不改三个 Python 服务的合成算法与协议。

## 复用

| 组件 | 来源 | 复用方式 |
| --- | --- | --- |
| 服务生命周期 | `CompanionVoiceService.cs` | 链接；补 Linux 的 venv 路径与 taskkill 平台判断 |
| 权重清单/下载 | `CompanionVoicePacks.cs` | 链接；脚本改为 Linux 程序集嵌入 |
| 语音偏好字段 | `CompanionPreferences.cs` | 已含全部 Speech* 字段 |
| 三个 Python 服务 | `Res/Voice/*.py` | 原样嵌入，运行时释放到语音文件夹 |

三个服务共用 `POST /speak`（`text, voice, speed, pitch, gpu, nfe`）与 `GET /health`（`voices`）协议，客户端只按引擎切换端点。

## 新增

- `code/BigBlueFish.Linux/LinuxVoice.cs`
  - `LinuxVoice`：统一 HTTP 客户端，按 `SpeechEngine` 选择端点；`X-Audio-Seconds` 作为时长。
  - `PlayerAudioSink`：外部播放器链路 `pw-play → paplay → ffplay`；音量 >100% 先在 WAV 样本上增益。
  - `VoiceClip`、`IAudioSink`、`VoiceScope`。
- `LinuxPetWindow`：朗读接入。
  - 启动时若开启朗读且允许自启，后台预热语音服务（模型只加载一次）。
  - `SayExternal`（Agent/MCP）、`Speak` 生成完成走 `ShowVoiceText`。
  - 气泡等音频模式：合成完成后等播放器真正开始再显示，700ms 兜底。
  - 非等待模式：文字立即显示，音频到达后延长气泡驻留。
  - 隐藏、退出、切换设置时停止播放；退出时停止服务。
- `LinuxSettings` 新增“语音朗读”页：开关、气泡延迟、引擎、音色（/health 动态）、GPU、Python 路径、自启、检测/试听/重启/打开目录、语速/音调/步数/音量、三类范围、三引擎权重下载与删除、依赖说明。

## 延迟设计（实测）

本机 PipeWire，播放 1 秒片段的进程总耗时：

| 播放器 | 0.2 秒片段 | 1 秒片段 | 启动开销 |
| --- | --- | --- | --- |
| `pw-play` | 0.224 s | 1.024 s | 约 24 ms |
| `ffplay` | 0.341 s | 1.101 s | 约 101 ms |

因此播放选 `pw-play`，`paplay`、`ffplay` 依次兜底（ffplay 由已依赖的 ffmpeg 提供，保证可用）。

端到端延迟主要是 f5 合成（约 1–2 秒/句），播放器开销可忽略。因此：

- 启动即预热服务，避免首句额外等待模型加载。
- 合成步数可调（默认 24）；调低可换速度。
- “气泡延迟”开关保留：开启时文字等声音一起开始；关闭时文字立刻出现、声音随后，感知延迟更小。

## 生命周期

1. 启动：`SpeechEnabled && SpeechAutoStart` → `EnsureStartedAsync`（60 秒就绪超时）。
2. 发言：`SynthesizeAsync` → `Play` → `Started` 事件驱动气泡显示。
3. 停止：隐藏、退出、重新配置时 `voice.Stop()`；退出 `voiceService.Stop()` 杀进程树。
4. 外部已有服务：只连接不接管，退出不杀。

## 测试

- 桩服务（Python 标准库 `http.server`）实现 `/health`、`/speak` 返回 WAV：验证三引擎切换、音色列表、试听、气泡时序、服务启停。
- 播放用真实 WAV 验证（本机 PipeWire 可用）。
- 真合成需要 Python 3.10–3.12、依赖与权重，由用户环境提供；程序不做自动安装。

## 风险

- 发行版不一定有 `pw-play`/`paplay`，链路会退回 `ffplay`（+70ms 左右）。
- GPT-SoVITS 需要参考音频（同名 `.wav` 与 `.txt`），设置页说明。
- 权重体积大（F5 约 1.4 GB，GSV 约 1.1 GB），下载依赖网络；已有 hf-mirror 兜底与断点续传。
