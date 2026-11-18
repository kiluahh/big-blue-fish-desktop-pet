# 语音朗读设计

## 目标

鱼鱼说话时同时出声。声音走用户自己的本地语音服务，程序和安装包不附带模型、数据集和参考音色。

## 为什么不在安装包里带模型

F5-TTS 的 Python 运行时（torch + CUDA）约 11 GB，参考音色的授权也不属于本项目。把模型留在用户侧，安装包体积不变，授权责任也在使用者。程序只认一个本地 HTTP 地址。

## 组成

| 部分 | 位置 | 职责 |
| --- | --- | --- |
| `tts_server.py` | 语音文件夹，程序按需拉起 | 启动时加载一次 F5 模型，`POST /speak` 返回 wav，`GET /health` 返回可用音色 |
| `tts_kokoro_server.py` | 同上 | Kokoro v1.1-zh 服务，权重只读本地 `models/kokoro` |
| `tts_gsv_server.py` | 同上 | GPT-SoVITS v2 服务，权重读 `models/gsv`，没有时退回源码仓库自带的副本 |
| `CompanionVoice` | `code/BigBlueFish.Windows/Companion/CompanionVoice.cs` | 引擎选择、合成、播放、停止 |
| `CompanionVoiceService` | `code/BigBlueFish.Windows/Companion/CompanionVoiceService.cs` | 本机服务生命周期：按需启动、健康轮询、退出时连子进程一起收走（见 `2026-11-18-voice-service-lifecycle-design.md`） |
| `IVoiceEngine` | 同上 | `SystemVoiceEngine`（系统语音）与 `F5VoiceEngine`（HTTP，三个本地引擎共用）两个实现 |
| `IAudioSink` | 同上 | 播放接缝；正式实现用 WPF `MediaPlayer`，测试替换成记录器 |

不选 NAudio 的原因：仓库用 `RestoreLockedMode`，另有三份 `packages.lock.json`（常规 / Vision / Safe），加依赖要同时重新生成三份锁文件；`MediaPlayer` 已能满足音量、停止、失败事件。音量超过 100% 时程序把 16 位 PCM 样本按倍数缩放后交给播放器，因为 `MediaPlayer.Volume` 上限是 1.0。

## 设置页

设置窗口新增第 5 页「语音朗读」：

- 总开关、气泡延迟、引擎（F5 / Kokoro / GPT-SoVITS / 系统语音）、显卡加速开关、音色（F5 与 Kokoro 从 `/health` 拉取；GPT-SoVITS 自己挑参考音色，不显示选择器）、语速 0.7–1.4、音调 ±6 半音（0.1 半音一档）、合成步数 8–40、音量 0–200%
- 状态行显示服务是否连通、是谁拉起的，检测服务、试听、重启服务、打开语音目录按钮
- 「本地服务」：Python 解释器路径（留空自动查找语音文件夹里的 `.venv`，再退回 PATH）、跟随程序启动与退出开关
- 朗读范围：日常关心与问候 / 手动点击与测试 / Agent 与 MCP
- 「语音模型」区块按引擎列出体积、许可、就绪状态，可单独下载、取消、删除

`preferences.json` 新增 `SpeechEnabled`、`SpeechEngine`、`SpeechVoice`、`SpeechRate`、`SpeechPitch`、`SpeechVolume`、`SpeechNfe`、`SpeechUseGpu`、`SpeechEndpoint` / `SpeechEndpointKokoro` / `SpeechEndpointGsv`、`SpeechFolder`、`SpeechAutoCare` / `SpeechInteraction` / `SpeechExternal`、`SpeechBubbleWithAudio`。

## 文件路径

语音相关的文件只落在安装目录里，不写死任何绝对路径：

- `SpeechFolder` 留空表示 `<安装目录>\data\voice`（`CompanionPreferences.SpeechDirectory`）。填了才用自定义路径。
- 播放用的临时 wav 写在 `<语音文件夹>\cache\`，命名 `clip-<进程>-<序号>.wav`，播放结束即删；每次播放顺手清掉一小时前的残留。
- 启用语音时启动即建好这个文件夹，设置页的「打开语音目录」也指向它。

模型、参考音色和 `tts_server.py` 由使用者自己放进这个文件夹（或别的自选目录），程序只按 `SpeechEndpoint` 去连，不猜路径。地址在本机且没有服务应答时，程序用语音文件夹里的脚本自己拉起服务，退出时收走；地址在别处或已有服务应答时只连接、不接管。

## 内置代码与权重下载

三个合成脚本作为嵌入资源随程序发布，开始下载时释放到语音文件夹；它们是我们自己的脚本，三个引擎本身分别是 MIT / Apache-2.0 / MIT。**权重不进安装包**：体积大，许可证也各不相同（见下表），按需下载既减小安装包，也把许可责任留给使用者。

设置页「语音模型」为每个引擎列一条，互不影响：

| 引擎 | 目录 | 体积 | 许可 | 文件 |
| --- | --- | --- | --- | --- |
| F5-TTS v1 Base | `<语音文件夹>\models\f5` | 1.31 GB | CC BY-NC 4.0（仅非商业） | `model_1250000.safetensors`、`vocab.txt`、`vocos\pytorch_model.bin`、`vocos\config.yaml` |
| Kokoro v1.1-zh | `<语音文件夹>\models\kokoro` | 316 MB | Apache-2.0 | `config.json`、`kokoro-v1_1-zh.pth`、`voices\zf_*.pt`（8 个音色） |
| GPT-SoVITS v2 | `<语音文件夹>\models\gsv` | 1.03 GB | MIT | v2 的 s1/s2G 权重、`chinese-hubert-base`、`chinese-roberta-wwm-ext-large` |

下载从 HuggingFace 直连，失败自动换 `hf-mirror.com`；支持断点续传（Range）、取消、按精确字节数校验，完成后可单独删除某个引擎的目录。界面显示百分比、速度与剩余时间。三个脚本都会先找 `models\<engine>`，找不到才退回旧行为（F5 用 HuggingFace 缓存，GPT-SoVITS 用源码仓库自带的 `pretrained_models`），所以本地已有权重的开发目录不会被这条规则破坏。

F5 的权重是 CC BY-NC 4.0，只允许非商业用途；本项目的角色素材同样是 CC BY 协议、非商业使用，二者场景一致，因此保留 F5 作为默认引擎。Kokoro 用 Apache-2.0 的 v1.1-zh 权重，本地加载后不再访问 HuggingFace。GPT-SoVITS 的权重是 MIT，但还需要源码 `vendor\GPT-SoVITS`、它自己的 Python 依赖和参考音频，属于高级用法。

运行仍需要 Python 3.10–3.12 与对应依赖（`pip install f5-tts` / `pip install kokoro soundfile numpy librosa`），这一点写在页面说明里，程序不做自动安装。

## 音调

F5 模型本身没有音调参数。`/speak` 增加 `pitch`（半音，−12–12，界面限制 ±6），服务端在合成后用 `librosa.effects.pitch_shift` 变调，时长不变。实测同一句话 +4 半音：中位基频 349.6 Hz → 398.1 Hz，时长都是 3.744 秒。

## 时序

语音是异步的，文字不等声音：

1. 气泡立刻逐字显示（沿用 28 字/秒）。
2. 合成返回后，若这段话还在显示，按音频时长重排剩余字的节奏（`CompanionSpeech.Retime`，夹在 12–60 字/秒）。
3. 播放开始后，把气泡驻留时间抬到 `现在 + 音频时长 + 2 秒`（`voiceHoldUntil`），避免声音没播完气泡先消失。

设置里另有「气泡等语音准备好再出现」（`SpeechBubbleWithAudio`，默认开）。开启时不走上面第 1 步：先合成，拿到时长后把播放交给 `MediaPlayer`，**等 `MediaOpened` 真正开始出声的那一刻**才调用 `CompanionSpeech.Show(text, animate, seconds)`，逐字速度直接按音频长度定，两者同时开始、同时结束。播放器没有回报时 700 毫秒后兜底出字。本地模型流式生成的中间预览也会被压住，否则预览会早于声音好几秒。合成失败时立刻退回「只显示文字」，不让主人白等。

## 打断与降级

会停声的路径：`InvalidateSpeechRequests`、新的一句到来、气泡自然消失、`ClearCompanionModelReply`、退出。切换新句子前先停旧音频。

合成失败、服务没开、超时都只是没有声音：气泡照常显示，写一条 `SpeechFailed` 诊断日志，不弹窗、不改文字。

## 验证

- `AnimationUiVerification --speech-audio`：87 项。覆盖开关、引擎调用次数、音量透传、驻留延长、范围开关、停止、设置归一化、气泡延迟，以及三个模型清单的体积求和、目录映射、缺文件/尺寸不符判定与删除。用假引擎、假播放器、假语音进程与假探针，CI 无声卡、无 python 也能跑。
- `AnimationUiVerification --speech-download <engine> <folder>`：手动模式，走真实的 HTTP 下载，验证续传与校验；不参与常规测试。
- 实机：三个服务都启动过一次，`/health` 与 `/speak` 各跑通，Kokoro 本地权重 316 MB、合成 0.5 秒；GPT-SoVITS 本地权重 1.03 GB、合成 6.5 秒；F5 仍从缓存加载。

## 非目标

- 不做事级对齐（卡拉 OK 高亮）：本地模型没有时间戳。
- 不做夜间静音策略，先用手动开关。
- 不自动安装 Python 或依赖，也不托管远程服务地址。
- macOS / Linux 不接：两端的窗口层尚未实现播放。
