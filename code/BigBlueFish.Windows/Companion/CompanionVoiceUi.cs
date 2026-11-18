using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace BigBlueFish.Windows;

internal enum VoiceScope { Automatic, Interactive, External }

public partial class MainWindow
{
    // Clips live in the installation's data directory, never in a system temp path or a fixed absolute path.
    private string VoiceCacheDirectory => Path.Combine(preferences.SpeechDirectory, "cache");
    private CompanionVoice voice = new();
    private DateTime voiceHoldUntil = DateTime.MinValue;

    private bool Speaks(VoiceScope scope) => preferences.SpeechEnabled && scope switch
    {
        VoiceScope.Automatic => preferences.SpeechAutoCare,
        VoiceScope.Interactive => preferences.SpeechInteraction,
        _ => preferences.SpeechExternal
    };

    /// <summary>Shows the bubble, then speaks it if the voice packs allow it. Text never waits for audio unless asked.</summary>
    private void ShowVoiceText(string text, VoiceScope scope = VoiceScope.Automatic)
    {
        if (Speaks(scope) && preferences.SpeechBubbleWithAudio)
        {
            _ = SpeakThenShowAsync(text);
            return;
        }
        speech.Show(text, true);
        if (Speaks(scope)) _ = SpeakAsync(text);
    }

    /// <summary>Bubble-delay mode: synthesize first, then reveal the text and start the clip together.</summary>
    private async Task SpeakThenShowAsync(string text)
    {
        var token = voice.BeginSpeaking();
        var started = DateTime.UtcNow;
        DiagnosticLog.Write(DiagnosticEvent.SpeechStarted, kind: "voice", chars: text.Length);
        VoiceClip? clip;
        try
        {
            clip = await voice.SynthesizeAsync(text, preferences, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (token.IsCancellationRequested) return;
        if (clip == null)
        {
            // Nothing to hear, so do not keep the owner waiting: show the text as usual.
            DiagnosticLog.Write(DiagnosticEvent.SpeechFailed, kind: "voice");
            speech.Show(text, true);
            return;
        }
        voiceHoldUntil = DateTime.UtcNow.AddSeconds(clip.Seconds + 2);
        // Text starts with the audible clip, not with the call that opens the player.
        var revealed = false;
        var fallback = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        void Reveal()
        {
            if (revealed) return;
            revealed = true;
            fallback.Stop();
            speech.Show(text, true, clip.Seconds);
        }
        fallback.Tick += (_, _) => Reveal();
        fallback.Start();
        voice.Play(clip, preferences, Reveal);
        DiagnosticLog.Write(DiagnosticEvent.SpeechCompleted, kind: "voice", chars: text.Length,
            elapsedMs: (DateTime.UtcNow - started).TotalMilliseconds);
    }

    private bool HoldsBubbleForVoice(VoiceScope scope) => Speaks(scope) && preferences.SpeechBubbleWithAudio;

    private async Task SpeakAsync(string text)
    {
        var token = voice.BeginSpeaking();
        var started = DateTime.UtcNow;
        DiagnosticLog.Write(DiagnosticEvent.SpeechStarted, kind: "voice", chars: text.Length);
        VoiceClip? clip;
        try
        {
            clip = await voice.SynthesizeAsync(text, preferences, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (clip == null)
        {
            // Synthesis is best effort: the bubble already shows the sentence, so a failure only costs sound.
            DiagnosticLog.Write(DiagnosticEvent.SpeechFailed, kind: "voice");
            return;
        }
        if (token.IsCancellationRequested || speech.FullText != text) return;
        speech.Retime(clip.Seconds);
        voice.Play(clip, preferences);
        voiceHoldUntil = DateTime.UtcNow.AddSeconds(clip.Seconds + 2);
        if (bubble.Visibility == Visibility.Visible && bubbleUntil != DateTime.MaxValue) ResetBubbleDeadline();
        DiagnosticLog.Write(DiagnosticEvent.SpeechCompleted, kind: "voice", chars: text.Length,
            elapsedMs: (DateTime.UtcNow - started).TotalMilliseconds);
    }

    private void StopVoice()
    {
        voice.Stop();
        voiceHoldUntil = DateTime.MinValue;
    }

    /// <summary>Voice page. Ships no model or reference audio: the user downloads those and points the app at the service.</summary>
    private Action? AddVoiceSettings(StackPanel panel, Window owner)
    {
        var fetched = new CancellationTokenSource();
        void Toggle(string label, bool value, Action<bool> change)
        {
            var check = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 7, 0, 7) };
            check.Click += (_, _) => { change(check.IsChecked == true); SaveCompanion(); };
            panel.Children.Add(check);
        }

        void Label(string text, double top = 18) => panel.Children.Add(new TextBlock
        {
            Text = text,
            Margin = new Thickness(0, top, 0, 4),
            TextWrapping = TextWrapping.Wrap
        });

        Toggle("启用语音朗读", preferences.SpeechEnabled, value =>
        {
            preferences.SpeechEnabled = value;
            if (!value) StopVoice();
        });
        Toggle("气泡延迟", preferences.SpeechBubbleWithAudio,
            value => preferences.SpeechBubbleWithAudio = value);
        panel.Children.Add(new TextBlock
        {
            Text = "开启后气泡等到声音真正开始播放才出现，字与声音一起开始；关闭则文字立刻出现，声音随后跟上。",
            FontSize = 11,
            Foreground = Brushes.SlateGray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6)
        });

        Label("引擎");
        var status = new TextBlock { Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap, Text = "正在检测语音服务…" };
        var engineIds = new System.Collections.Generic.List<string> { "f5", "kokoro", "gsv" };
        var engines = new ComboBox { Margin = new Thickness(0, 0, 0, 6) };
        engines.Items.Add("F5-TTS 本地服务（音色自然，需要显卡）");
        engines.Items.Add("Kokoro v1.1-zh（体积小、CPU 就能实时）");
        engines.Items.Add("GPT-SoVITS v2（MIT 权重，质量最好）");
#if COMPANION_OCR
        engineIds.Add("system");
        engines.Items.Add("系统语音（即时，音色较机械）");
#endif
        engines.SelectedIndex = Math.Max(0, engineIds.IndexOf(preferences.SpeechEngine));
        panel.Children.Add(engines);
        var voiceLabel = new TextBlock { Text = "音色（由本地服务提供）", Margin = new Thickness(0, 8, 0, 4), TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(voiceLabel);
        var voices = new ComboBox { Margin = new Thickness(0, 0, 0, 4) };
        voices.Items.Add(preferences.SpeechVoice);
        voices.SelectedIndex = 0;
        voices.SelectionChanged += (_, _) =>
        {
            if (voices.SelectedItem is string id && !string.IsNullOrWhiteSpace(id)) { preferences.SpeechVoice = id; SaveCompanion(); }
        };
        panel.Children.Add(voices);
        panel.Children.Add(status);
        // GPT-SoVITS picks its own reference voice, so the picker would only mislead.
        void ApplyVoicePicker()
        {
            var show = preferences.SpeechEngine != "gsv" && preferences.SpeechEngine != "system";
            voiceLabel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            voices.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }
        ApplyVoicePicker();
        Toggle("使用显卡加速（关闭则用 CPU）", preferences.SpeechUseGpu, value =>
        {
            preferences.SpeechUseGpu = value;
            status.Text = value ? "已切到显卡：下一句会让语音服务重新加载模型。" : "已切到 CPU：下一句会让语音服务重新加载模型。";
        });

        engines.SelectionChanged += (_, _) =>
        {
            if (engines.SelectedIndex < 0 || engines.SelectedIndex >= engineIds.Count) return;
            preferences.SpeechEngine = engineIds[engines.SelectedIndex];
            ApplyVoicePicker();
            SaveCompanion();
            _ = RefreshAsync();
        };

        var buttons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        void Action(string text, Action action)
        {
            var button = new Button { Content = text, Padding = new Thickness(8), Margin = new Thickness(0, 4, 6, 4) };
            button.Click += (_, _) => action();
            buttons.Children.Add(button);
        }

        async Task RefreshAsync()
        {
            var endpoint = preferences.EndpointFor(preferences.SpeechEngine);
            var ok = await voice.ProbeAsync(endpoint, fetched.Token);
            if (fetched.IsCancellationRequested) return;
            if (!ok)
            {
                status.Text = preferences.SpeechEngine switch
                {
                    "kokoro" => "未连接 Kokoro 服务：运行 tts_kokoro_server.py --port 8766。",
                    "gsv" => "未连接 GPT-SoVITS 服务：运行 tts_gsv_server.py --port 8767。",
                    "system" => "系统语音不需要本地服务。",
                    _ => "未连接 F5 服务：运行 tts_server.py --port 8765。"
                };
                return;
            }
            var listed = await VoiceIdsAsync(endpoint, fetched.Token);
            status.Text = $"语音服务已连接（{endpoint}）" + (listed.Count == 0 ? "" : $"，可用音色 {listed.Count} 个");
            if (listed.Count > 0)
            {
                voices.Items.Clear();
                foreach (var id in listed) voices.Items.Add(id);
                var index = listed.IndexOf(preferences.SpeechVoice);
                if (index < 0) { preferences.SpeechVoice = listed[0]; SaveCompanion(); index = 0; }
                voices.SelectedIndex = index;
            }
        }

        Action("检测服务", () => _ = RefreshAsync());
        Action("试听", () =>
        {
            if (!preferences.SpeechEnabled) { status.Text = "先打开上面的开关。"; return; }
            var token = voice.BeginSpeaking();
            _ = Task.Run(async () =>
            {
                var clip = await voice.SynthesizeAsync("主人，我是鱼鱼，这样说话好听吗？", preferences, token);
                if (clip == null) { await Dispatcher.InvokeAsync(() => status.Text = "合成失败：检查语音服务是否在运行。"); return; }
                await Dispatcher.InvokeAsync(() => voice.Play(clip, preferences));
            });
        });
        Action("打开语音目录", () =>
        {
            try
            {
                var folder = preferences.SpeechDirectory;
                Directory.CreateDirectory(folder);
                Process.Start(new ProcessStartInfo("explorer.exe", folder) { UseShellExecute = true });
            }
            catch (Exception error) { status.Text = "打不开目录：" + error.Message; }
        });
        panel.Children.Add(buttons);

        Label("语速");
        var rate = new TextBlock { Text = preferences.SpeechRate.ToString("0.00") };
        var rateSlider = new Slider { Minimum = .7, Maximum = 1.4, Value = preferences.SpeechRate, TickFrequency = .05, IsSnapToTickEnabled = true };
        rateSlider.ValueChanged += (_, _) =>
        {
            preferences.SpeechRate = rateSlider.Value;
            rate.Text = preferences.SpeechRate.ToString("0.00");
            SaveCompanion();
        };
        panel.Children.Add(rate);
        panel.Children.Add(rateSlider);

        Label("音调（半音，正数更年轻）");
        var pitchText = new TextBlock { Text = preferences.SpeechPitch.ToString("+0.0;-0.0;0.0") + " 半音" };
        // 0.1 semitone steps: 0.5 was coarse enough to be heard as a jump.
        var pitchSlider = new Slider { Minimum = -6, Maximum = 6, Value = preferences.SpeechPitch, TickFrequency = .1, IsSnapToTickEnabled = true };
        pitchSlider.ValueChanged += (_, _) =>
        {
            preferences.SpeechPitch = Math.Round(pitchSlider.Value, 1);
            pitchText.Text = preferences.SpeechPitch.ToString("+0.0;-0.0;0.0") + " 半音";
            SaveCompanion();
        };
        System.Windows.Automation.AutomationProperties.SetName(pitchSlider, "音调");
        panel.Children.Add(pitchText);
        panel.Children.Add(pitchSlider);

        Label("合成步数（越低越快，音质略降）");
        var nfe = new TextBlock { Text = preferences.SpeechNfe + "（24 时每句约 2 秒，12–16 快将近一半）" };
        var nfeSlider = new Slider { Minimum = 8, Maximum = 40, Value = preferences.SpeechNfe, TickFrequency = 2, IsSnapToTickEnabled = true };
        nfeSlider.ValueChanged += (_, _) =>
        {
            preferences.SpeechNfe = (int)Math.Round(nfeSlider.Value);
            nfe.Text = preferences.SpeechNfe + "（24 时每句约 2 秒，12–16 快将近一半）";
            SaveCompanion();
        };
        panel.Children.Add(nfe);
        panel.Children.Add(nfeSlider);

        Label("音量");
        var volume = new TextBlock { Text = $"{preferences.SpeechVolume:P0}" };
        var volumeSlider = new Slider { Minimum = 0, Maximum = 2, Value = preferences.SpeechVolume, TickFrequency = .05, IsSnapToTickEnabled = true };
        volumeSlider.ValueChanged += (_, _) =>
        {
            preferences.SpeechVolume = volumeSlider.Value;
            volume.Text = $"{preferences.SpeechVolume:P0}";
            SaveCompanion();
        };
        panel.Children.Add(volume);
        panel.Children.Add(volumeSlider);

        Label("朗读范围");
        Toggle("日常关心与问候", preferences.SpeechAutoCare, value => preferences.SpeechAutoCare = value);
        Toggle("手动点击与测试", preferences.SpeechInteraction, value => preferences.SpeechInteraction = value);
        Toggle("Agent 与 MCP", preferences.SpeechExternal, value => preferences.SpeechExternal = value);

        Label("语音模型", 16);
        static string Size(long bytes) => bytes >= 1L << 30 ? $"{bytes / 1024.0 / 1024 / 1024:0.00} GB" : $"{bytes / 1024.0 / 1024:0.0} MB";
        static string Span(TimeSpan time) => time.TotalHours >= 1
            ? $"{(int)time.TotalHours} 小时 {time.Minutes} 分"
            : time.TotalMinutes >= 1 ? $"{(int)time.TotalMinutes} 分 {time.Seconds} 秒" : $"{Math.Max(1, time.Seconds)} 秒";

        panel.Children.Add(new TextBlock
        {
            Text = "三个引擎的权重分开下载，各自放进语音文件夹的 models 子目录；支持断点续传，失败自动换 hf-mirror 镜像。",
            FontSize = 11,
            Foreground = Brushes.SlateGray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6)
        });

        var folder = preferences.SpeechDirectory;
        var notes = new System.Collections.Generic.Dictionary<string, string>();
        var rows = new System.Collections.Generic.List<(VoicePack Pack, TextBlock Status, ProgressBar Bar, Button Action, Button Delete)>();
        var running = new CancellationTokenSource?[1];
        string? runningId = null;

        string Describe(VoicePack pack)
        {
            var missing = pack.Missing(folder);
            if (missing == 0) return "已就绪";
            var have = pack.InstalledBytes(folder);
            return have == 0 ? "未下载" : $"已下载 {have * 100.0 / pack.TotalBytes:0}%，还差 {missing} 个文件";
        }

        void Update((VoicePack Pack, TextBlock Status, ProgressBar Bar, Button Action, Button Delete) row)
        {
            var pack = row.Pack;
            var active = runningId == pack.Id;
            var have = pack.InstalledBytes(folder);
            row.Action.Content = active ? "取消" : "下载";
            row.Action.IsEnabled = active || (runningId == null && pack.Missing(folder) > 0);
            // A finished pack offers only "delete"; an untouched one offers only "download".
            row.Action.Visibility = !active && pack.IsInstalled(folder) ? Visibility.Collapsed : Visibility.Visible;
            row.Delete.IsEnabled = !active && runningId == null && have > 0;
            row.Delete.Visibility = have > 0 ? Visibility.Visible : Visibility.Collapsed;
            row.Bar.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            row.Status.Text = notes.TryGetValue(pack.Id, out var note) ? note : Describe(pack);
        }

        void UpdateAll() { foreach (var row in rows) Update(row); }

        void Start((VoicePack Pack, TextBlock Status, ProgressBar Bar, Button Action, Button Delete) row)
        {
            var pack = row.Pack;
            notes.Remove(pack.Id);
            var cancel = running[0] = new CancellationTokenSource();
            runningId = pack.Id;
            CompanionVoicePacks.ExtractScripts(folder);
            row.Bar.Value = 0;
            var watch = Stopwatch.StartNew();
            long seen = pack.InstalledBytes(folder);
            var at = TimeSpan.Zero;
            var progress = new Progress<(long Done, long Total, string File)>(report =>
            {
                var ratio = Math.Clamp((double)report.Done / Math.Max(report.Total, 1), 0, 1);
                row.Bar.Value = ratio;
                var now = watch.Elapsed;
                var step = now - at;
                if (step < TimeSpan.FromMilliseconds(400)) return;
                var speed = (report.Done - seen) / Math.Max(step.TotalSeconds, 0.001);
                seen = report.Done;
                at = now;
                row.Status.Text = $"正在下载 {ratio:P0} · {Size((long)Math.Max(speed, 0))}/s"
                    + (speed > 0.5 ? " · 剩 " + Span(TimeSpan.FromSeconds((report.Total - report.Done) / speed)) : "")
                    + (report.File.Length == 0 ? "" : " · " + Path.GetFileName(report.File));
            });
            UpdateAll();
            _ = Task.Run(async () =>
            {
                try
                {
                    await CompanionVoicePacks.InstallAsync(pack, folder, progress, cancel.Token);
                    await Dispatcher.InvokeAsync(() => notes[pack.Id] = $"下载完成：{Size(pack.TotalBytes)}。{pack.Start}");
                }
                catch (OperationCanceledException)
                {
                    await Dispatcher.InvokeAsync(() => notes[pack.Id] = "已取消，下次接着下。");
                }
                catch (Exception error)
                {
                    await Dispatcher.InvokeAsync(() => notes[pack.Id] = "下载失败：" + error.Message);
                }
                finally
                {
                    cancel.Dispose();
                    await Dispatcher.InvokeAsync(() =>
                    {
                        running[0] = null;
                        runningId = null;
                        UpdateAll();
                    });
                }
            });
        }

        void Remove((VoicePack Pack, TextBlock Status, ProgressBar Bar, Button Action, Button Delete) row)
        {
            var pack = row.Pack;
            if (MessageBox.Show(owner, $"删除 {pack.Name} 的权重（{Size(pack.TotalBytes)}）？以后可以重新下载。",
                    "删除语音模型", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            try
            {
                pack.Delete(folder);
                notes.Remove(pack.Id);
            }
            catch (Exception error) { notes[pack.Id] = "删除失败：" + error.Message; }
            UpdateAll();
        }

        foreach (var pack in CompanionVoicePacks.All)
        {
            var stack = new StackPanel { Margin = new Thickness(0, 6, 0, 8) };
            stack.Children.Add(new TextBlock
            {
                Text = $"{pack.Name} · {Size(pack.TotalBytes)}",
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });
            stack.Children.Add(new TextBlock
            {
                Text = pack.Licence + " · " + pack.Start,
                FontSize = 11,
                Foreground = Brushes.SlateGray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 2)
            });
            var line = new TextBlock { Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4) };
            stack.Children.Add(line);
            var bar = new ProgressBar { Height = 4, Margin = new Thickness(0, 0, 0, 6), Visibility = Visibility.Collapsed };
            stack.Children.Add(bar);
            var rowButtons = new WrapPanel();
            var action = new Button { Padding = new Thickness(8), Margin = new Thickness(0, 0, 6, 0) };
            var remove = new Button { Content = "删除", Padding = new Thickness(8) };
            rowButtons.Children.Add(action);
            rowButtons.Children.Add(remove);
            stack.Children.Add(rowButtons);
            panel.Children.Add(new Border
            {
                Child = stack,
                BorderBrush = Brushes.Gainsboro,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(0, 2, 0, 4)
            });
            var row = (pack, line, bar, action, remove);
            rows.Add(row);
            action.Click += (_, _) =>
            {
                if (runningId == pack.Id) running[0]?.Cancel();
                else if (runningId == null) Start(row);
            };
            remove.Click += (_, _) => Remove(row);
        }
        UpdateAll();

        panel.Children.Add(new TextBlock
        {
            Text = "合成代码已内置，下载时释放到语音文件夹。运行还需要 Python 3.10–3.12 与依赖："
                + "F5 用 pip install f5-tts（带显卡更快）；Kokoro 用 pip install kokoro soundfile numpy librosa；"
                + "GPT-SoVITS 要把源码放到 voice\\vendor\\GPT-SoVITS 并装它自己的 requirements。"
                + "参考音色不随程序分发：F5 放 voice\\refs\\kids，GPT-SoVITS 放同名目录，每段 .wav 配一个 .txt。",
            FontSize = 11,
            Foreground = Brushes.SlateGray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 2)
        });
        panel.Children.Add(new TextBlock
        {
            Text = "语音文件夹：" + preferences.SpeechDirectory,
            FontSize = 11,
            Foreground = Brushes.SlateGray,
            TextWrapping = TextWrapping.Wrap
        });

        _ = RefreshAsync();
        return () => fetched.Cancel();
    }

    private async Task<System.Collections.Generic.List<string>> VoiceIdsAsync(string endpoint, CancellationToken token)
    {
        var ids = new System.Collections.Generic.List<string>();
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var json = await http.GetStringAsync(endpoint + "/health", token);
            using var document = System.Text.Json.JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("voices", out var voices))
                foreach (var item in voices.EnumerateArray())
                    if (item.TryGetProperty("id", out var id) && id.GetString() is string value && value.Length > 0) ids.Add(value);
        }
        catch { }
        return ids;
    }
}
