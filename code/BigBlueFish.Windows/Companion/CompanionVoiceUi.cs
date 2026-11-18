using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

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
        speech.Show(text, true, clip.Seconds);
        voice.Play(clip, preferences);
        DiagnosticLog.Write(DiagnosticEvent.SpeechCompleted, kind: "voice", chars: text.Length,
            elapsedMs: (DateTime.UtcNow - started).TotalMilliseconds);
    }

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
        Toggle("气泡等语音准备好再出现", preferences.SpeechBubbleWithAudio,
            value => preferences.SpeechBubbleWithAudio = value);
        panel.Children.Add(new TextBlock
        {
            Text = "开启后先合成再出字，字与声音同步开始，代价是冒泡前多等一两秒；关闭则文字立刻出现，声音随后跟上。",
            FontSize = 11,
            Foreground = Brushes.SlateGray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6)
        });

        Label("引擎");
        var engines = new ComboBox { Margin = new Thickness(0, 0, 0, 6) };
        engines.Items.Add("F5-TTS 本地服务（音色自然，需要显卡）");
#if COMPANION_OCR
        engines.Items.Add("系统语音（即时，音色较机械）");
#endif
        engines.SelectedIndex = preferences.SpeechEngine == "system" && engines.Items.Count > 1 ? 1 : 0;
        engines.SelectionChanged += (_, _) =>
        {
            preferences.SpeechEngine = engines.SelectedIndex == 1 ? "system" : "f5";
            SaveCompanion();
        };
        panel.Children.Add(engines);

        Label("音色（由本地服务提供）", 8);
        var voices = new ComboBox { Margin = new Thickness(0, 0, 0, 4) };
        voices.Items.Add(preferences.SpeechVoice);
        voices.SelectedIndex = 0;
        voices.SelectionChanged += (_, _) =>
        {
            if (voices.SelectedItem is string id && !string.IsNullOrWhiteSpace(id)) { preferences.SpeechVoice = id; SaveCompanion(); }
        };
        panel.Children.Add(voices);

        var status = new TextBlock { Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap, Text = "正在检测语音服务…" };
        panel.Children.Add(status);

        var buttons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        void Action(string text, Action action)
        {
            var button = new Button { Content = text, Padding = new Thickness(8), Margin = new Thickness(0, 4, 6, 4) };
            button.Click += (_, _) => action();
            buttons.Children.Add(button);
        }

        async Task RefreshAsync()
        {
            var ids = await voice.ProbeAsync(preferences.SpeechEndpoint, fetched.Token);
            if (fetched.IsCancellationRequested) return;
            if (!ids)
            {
                status.Text = "语音服务未连接。请在语音目录里运行 tts_server.py，模型由本地服务自己加载；本程序不附带模型与音色。";
                return;
            }
            var listed = await VoiceIdsAsync(fetched.Token);
            status.Text = $"语音服务已连接（{preferences.SpeechEndpoint}）" + (listed.Count == 0 ? "" : $"，可用音色 {listed.Count} 个");
            if (listed.Count > 0)
            {
                voices.Items.Clear();
                foreach (var id in listed) voices.Items.Add(id);
                var index = listed.IndexOf(preferences.SpeechVoice);
                voices.SelectedIndex = index >= 0 ? index : 0;
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

        Label("音量");
        var volume = new TextBlock { Text = $"{preferences.SpeechVolume:P0}" };
        var volumeSlider = new Slider { Minimum = 0, Maximum = 1, Value = preferences.SpeechVolume, TickFrequency = .05, IsSnapToTickEnabled = true };
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
        Toggle("Agent 与 MCP 的 say", preferences.SpeechExternal, value => preferences.SpeechExternal = value);

        Label("语音包", 16);
        panel.Children.Add(new TextBlock
        {
            Text = "本程序不附带语音模型与音色。模型、数据集和参考音色请自行放进安装目录的语音文件夹，由本地服务加载；音色的使用授权由使用者自行确认。",
            FontSize = 11,
            Foreground = Brushes.SlateGray,
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new TextBlock
        {
            Text = "语音文件夹：" + preferences.SpeechDirectory + "\n（播放缓存写在 cache 子目录；模型与音色由本地服务读取）",
            FontSize = 11,
            Foreground = Brushes.SlateGray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 2)
        });

        _ = RefreshAsync();
        return () => fetched.Cancel();
    }

    private async Task<System.Collections.Generic.List<string>> VoiceIdsAsync(CancellationToken token)
    {
        var ids = new System.Collections.Generic.List<string>();
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var json = await http.GetStringAsync(preferences.SpeechEndpoint + "/health", token);
            using var document = System.Text.Json.JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("voices", out var voices))
                foreach (var item in voices.EnumerateArray())
                    if (item.TryGetProperty("id", out var id) && id.GetString() is string value && value.Length > 0) ids.Add(value);
        }
        catch { }
        return ids;
    }
}
