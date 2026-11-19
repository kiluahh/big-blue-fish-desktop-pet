using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;
#if !COMPANION_SAFE
using BigBlueFish.Control;
#endif

namespace BigBlueFish.Windows;

internal sealed partial class LinuxPetWindow
{
    bool consentOpen;
    Window? consentWindow;
    Action? refreshLinuxSettings;
    Action? disposeLogConsole;
    internal Action? openVoicePage;
#if COMPANION_LINUX
    // Voice page state survives page switches so downloads keep showing their last note.
    CancellationTokenSource? voiceRefresh;
    CancellationTokenSource? voiceDownload;
    string? voiceDownloading;
    readonly Dictionary<string, string> voiceNotes = new();
#endif
    static string AutostartFile => OperatingSystem.IsMacOS() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library/LaunchAgents/io.github.bai-cute.bigbluefish.plist")
        : Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"), "autostart/bigbluefish.desktop");
    bool StartupEnabled => File.Exists(AutostartFile);
    void SetStartup(bool on)
    {
        if (!on) { File.Delete(AutostartFile); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(AutostartFile)!);
        if (OperatingSystem.IsMacOS()) File.WriteAllText(AutostartFile, "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n<plist version=\"1.0\"><dict><key>Label</key><string>io.github.bai-cute.bigbluefish</string><key>ProgramArguments</key><array><string>"
            + System.Security.SecurityElement.Escape(Path.Combine(AppContext.BaseDirectory, "BigBlueFish.Mac")) + "</string></array><key>RunAtLoad</key><true/><key>ProcessType</key><string>Interactive</string></dict></plist>\n");
        else File.WriteAllText(AutostartFile, "[Desktop Entry]\nType=Application\nName=蓝色大肥鱼\nExec=\"" + Path.Combine(AppContext.BaseDirectory, "BigBlueFish.Linux").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"\nTerminal=false\n");
    }
    void ApplySettings(PixelPoint? requestedPosition = null)
    {
        speechVersion++; brain.Stop(); brain.UseGpu = Preferences.GpuEnabled; brain.StopOnBattery = Preferences.StopModelOnBattery; brain.WeatherRegionCode = Preferences.WeatherRegionCode;
        ConfigureAgent();
        Rescale(); var appliedPosition = Clamp(requestedPosition); nextSpeech = DateTime.UtcNow.Add(Preferences.NextSpeechDelay(random)); nextEvent = DateTime.UtcNow.AddSeconds(Preferences.AnimationIntervalSeconds); nextWarmup = DateTime.MinValue;
        if (!Preferences.HideOnFullscreen && fullscreenHidden) { fullscreenHidden = false; if (!hidden) Show(); }
        if (bubbleWindow.IsVisible) ResetDwell();
        Preferences.Left = appliedPosition.X; Preferences.Top = appliedPosition.Y; Preferences.Save(); DiagnosticLog.ObserveSettings(Preferences);
    }
    static Button ActionButton(string label, Action action)
    {
        var button = new Button { Content = label, Padding = new Thickness(18, 9), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 4, 10, 4), Background = Brushes.White, Foreground = Hair, BorderBrush = new SolidColorBrush(Color.FromRgb(222,226,237)), BorderThickness = new Thickness(1) };
        button.Click += (_, _) => action(); return button;
    }
    static TextBlock Paragraph(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 12), Foreground = Brushes.SlateGray };
    static Window Dialog(string title, Avalonia.Controls.Control content, double width = 630, double height = 460) => new() {
        Title = title, Width = width, Height = height, MinWidth = 480, MinHeight = 360,
        FontFamily = new FontFamily("Noto Sans CJK SC"), FontSize = 14,
        WindowStartupLocation = WindowStartupLocation.CenterScreen, Background = new SolidColorBrush(Color.FromRgb(247, 248, 251)),
        Content = new Border { Padding = new Thickness(28), Child = content } };
    async Task<bool> Confirm(string title, string message, string accept = "确定", CancellationToken token = default, string reject = "取消")
    {
        if (consentOpen) throw new InvalidOperationException("请先处理已打开的提示窗口。");
        consentOpen = true; bool accepted = false;
        try {
            var panel = new StackPanel { Spacing = 15 };
            panel.Children.Add(new TextBlock { Text = title, FontSize = 23, FontWeight = FontWeight.SemiBold }); panel.Children.Add(Paragraph(message));
            var dialog = Dialog(title, panel); var buttons = new WrapPanel();
            buttons.Children.Add(ActionButton(accept, () => { accepted = true; dialog.Close(); })); buttons.Children.Add(ActionButton(reject, dialog.Close)); panel.Children.Add(buttons);
            using var registration = token.Register(() => Dispatcher.UIThread.Post(() => { accepted = false; dialog.Close(); }));
            var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            dialog.Closed += (_, _) => finished.TrySetResult(accepted && !token.IsCancellationRequested);
            consentWindow = dialog;
            if (settings?.IsVisible == true) dialog.Show(settings); else dialog.Show();
            return await finished.Task;
        } finally { consentWindow = null; consentOpen = false; }
    }
    void OpenSettings()
    {
        if (settings != null) { if (settings.WindowState == WindowState.Minimized) settings.WindowState = WindowState.Normal; settings.Activate(); return; }
        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("180,*") };
        var navigation = new StackPanel { Spacing = 10, Margin = new Thickness(0, 0, 20, 0) };
        var body = new StackPanel { Spacing = 8 }; var scroll = new ScrollViewer { Content = body }; Grid.SetColumn(scroll, 1); root.Children.Add(navigation); root.Children.Add(scroll);
        settings = Dialog("蓝色大肥鱼 · 设置", root, 880, 700); settings.MinWidth = 780; settings.MinHeight = 600;
        settings.Closed += (_, _) => {
            openVoicePage = null;
#if COMPANION_LINUX
            voiceRefresh?.Cancel(); voiceRefresh = null; voiceDownload?.Cancel(); voiceDownload = null; voiceDownloading = null;
#endif
            disposeLogConsole?.Invoke(); disposeLogConsole = null; settings = null; refreshLinuxSettings = null; DiagnosticLog.Write(DiagnosticEvent.SettingsClosed); Save(); nextEvent = DateTime.UtcNow.AddSeconds(Preferences.AnimationIntervalSeconds); };
        Action selectedPage = General;
        void Navigate(Action page) { selectedPage = page; page(); scroll.Offset = new Vector(0, 0); }
        void Title(string text) {
#if COMPANION_LINUX
            voiceRefresh?.Cancel(); voiceRefresh = null; voiceDownload?.Cancel(); voiceDownload = null; voiceDownloading = null;
#endif
            disposeLogConsole?.Invoke(); disposeLogConsole = null; body.Children.Clear(); body.Children.Add(new TextBlock { Text = text, FontSize = 25, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 16) }); }
        void Toggle(string text, bool value, Action<bool> set) { var box = new CheckBox { Content = text, IsChecked = value, Margin = new Thickness(0, 4) }; box.IsCheckedChanged += (_, _) => { set(box.IsChecked == true); Save(); }; body.Children.Add(box); }
        void Slider(string text, double value, double low, double high, Action<double> set, double tick = 1) {
            var label = new TextBlock { Text = text + "：" + value.ToString("0.##"), Margin = new Thickness(0, 12, 0, 0) };
            var slider = new Avalonia.Controls.Slider { Minimum = low, Maximum = high, Value = value, TickFrequency = tick, IsSnapToTickEnabled = true };
            slider.ValueChanged += (_, _) => { label.Text = text + "：" + slider.Value.ToString("0.##"); set(slider.Value); Save(); }; body.Children.Add(label); body.Children.Add(slider);
        }
        ComboBox Select(string label, string[] choices, int selected, Action<int> set) {
            body.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 10, 0, 4) }); var combo = new ComboBox { ItemsSource = choices, SelectedIndex = selected, HorizontalAlignment = HorizontalAlignment.Stretch };
            combo.SelectionChanged += (_, _) => { if (combo.SelectedIndex >= 0) { set(combo.SelectedIndex); Save(); } }; body.Children.Add(combo); return combo;
        }
        void General() {
            Title("桌宠与显示"); Slider("桌宠大小（%）", Preferences.Scale * 100, 50, 200, value => { Preferences.Scale = value / 100; Rescale(); Clamp(); }, 5);
            Slider("文字气泡大小（%）", Preferences.BubbleScale * 100, 50, 200, value => { Preferences.BubbleScale = value / 100; MoveBubble(); }, 5);
            Toggle("开机启动", StartupEnabled, SetStartup); Toggle("全屏时自动隐藏桌宠", Preferences.HideOnFullscreen, value => { Preferences.HideOnFullscreen = value; ApplySettings(); });
            Select("气泡驻留时间", CompanionPreferences.BubbleDurationLabels, Preferences.BubbleDurationIndex, value => { Preferences.BubbleDurationIndex = value; ResetDwell(); });
        }
        void Model() {
            Title("模型与信息"); Toggle("启用本地大模型", Preferences.ModelEnabled, value => { Preferences.ModelEnabled = value; ApplySettings(); });
            Toggle("使用 GPU 加速", Preferences.GpuEnabled, value => { Preferences.GpuEnabled = value; ApplySettings(); });
            var deviceStatus = Paragraph(""); body.Children.Add(deviceStatus);
            var deviceClock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            void UpdateDevice() => deviceStatus.Text = "推理设备：" + brain.ExecutionDevice;
            deviceClock.Tick += (_, _) => UpdateDevice();
            deviceStatus.AttachedToVisualTree += (_, _) => { UpdateDevice(); deviceClock.Start(); };
            deviceStatus.DetachedFromVisualTree += (_, _) => deviceClock.Stop();
            Toggle("笔记本离电时暂停模型", Preferences.StopModelOnBattery, value => { Preferences.StopModelOnBattery = value; ApplySettings(); });
            Toggle("联网获取天气和科技新闻", Preferences.PublicInfo, value => Preferences.PublicInfo = value);
            Toggle("感知当前前台应用", Preferences.ForegroundEnabled, value => Preferences.ForegroundEnabled = value);
#if !COMPANION_SAFE
            body.Children.Add(Paragraph("本地吐字在本机读取屏幕；由 Agent 编写日常关心时，会向它提供前台截图。"));
#endif
            body.Children.Add(ActionButton(brain.ModelAvailable ? "管理本地模型" : "安装模型", async () => await InstallModel()));
            var frequency = Select("自动说话间隔", CompanionPreferences.SpeechRanges.Select(r => $"{r.Min}–{r.Max} 分钟").ToArray(), Preferences.SpeechRangeIndex, value => { Preferences.SpeechMinMinutes = CompanionPreferences.SpeechRanges[value].Min; nextSpeech = DateTime.UtcNow.Add(Preferences.NextSpeechDelay(random)); });
            frequency.IsEnabled = !Puppet;
            body.Children.Add(Paragraph(Puppet ? "日常关心的触发时机已交给 Agent，此时说话间隔不可调节。关闭接管触发后恢复使用此设置。" : AgentDailyActive ? "鱼鱼按此间隔触发日常关心，由 Agent 编写吐字。" : "鱼鱼按此间隔触发日常关心，由本地模型编写吐字。"));
            body.Children.Add(new TextBlock { Text = "天气地区", Margin = new Thickness(0,12,0,4) });
            var regions = new StackPanel { Spacing = 8 }; body.Children.Add(regions);
            var country = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = CompanionRegions.Countries.Select(c => c.Name).ToArray() };
            regions.Children.Add(country);
            var levels = Enumerable.Range(0, 3).Select(_ => new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch }).ToArray();
            foreach (var level in levels) regions.Children.Add(level);
            var options = new CompanionRegion[levels.Length][];
            var selected = CompanionRegions.District(Preferences.WeatherRegionCode); var current = CompanionRegions.Country(selected); bool changing = true;
            void Fill(int from, string parentCode, CompanionRegion[] keep) {
                for (int i = from; i < levels.Length; i++) {
                    levels[i].IsVisible = i <= current.LeafLevel;
                    options[i] = i <= current.LeafLevel ? CompanionRegions.Children(parentCode) : [];
                    levels[i].ItemsSource = options[i].Select(r => r.Name).ToArray();
                    levels[i].SelectedIndex = options[i].Length == 0 ? -1 : Math.Max(0, Array.FindIndex(options[i], r => i < keep.Length && r.Code == keep[i].Code));
                    if (levels[i].SelectedIndex >= 0) parentCode = options[i][levels[i].SelectedIndex].Code;
                }
            }
            country.SelectedIndex = Array.IndexOf(CompanionRegions.Countries, current); Fill(0, current.Root, CompanionRegions.Chain(selected)); changing = false;
            void RegionChanged() { var leaf = levels[current.LeafLevel]; if (leaf.SelectedIndex >= 0 && leaf.SelectedIndex < options[current.LeafLevel].Length) { Preferences.WeatherRegionCode = options[current.LeafLevel][leaf.SelectedIndex].Code; ApplySettings(); } }
            country.SelectionChanged += (_,_) => { if (changing || country.SelectedIndex < 0 || CompanionRegions.Countries[country.SelectedIndex] == current) return; changing = true; current = CompanionRegions.Countries[country.SelectedIndex]; Fill(0, current.Root, current == CompanionRegions.China ? CompanionRegions.Chain(CompanionRegions.District(CompanionRegions.DefaultCode)) : []); changing = false; RegionChanged(); };
            for (int level = 0; level < levels.Length; level++) { var at = level; levels[at].SelectionChanged += (_,_) => { if (changing || levels[at].SelectedIndex < 0) return; changing = true; if (at < current.LeafLevel) Fill(at + 1, options[at][levels[at].SelectedIndex].Code, []); changing = false; RegionChanged(); }; }
            body.Children.Add(Paragraph("天气来自 Open-Meteo（Weather data by Open-Meteo.com）；美国地点使用华氏度和英里/小时。海外城市数据来自 GeoNames（CC BY 4.0）。"));
        }
        void Animation() {
            Title("动作与作息"); Slider("动作触发间隔（秒）", Preferences.AnimationIntervalSeconds, 30, 300, value => { Preferences.AnimationIntervalSeconds = (int)value; nextEvent = DateTime.UtcNow.AddSeconds(value); });
            foreach (var meal in new[] { ("早餐", "BreakfastHour"), ("午餐", "LunchHour"), ("晚餐", "DinnerHour") }) {
                var property = typeof(CompanionPreferences).GetProperty(meal.Item2)!;
                Slider(meal.Item1 + "时间（时）", (int)property.GetValue(Preferences)!, 0, 23, value => property.SetValue(Preferences, (int)value));
            }
        }

#if COMPANION_LINUX
        void Voice() {
            Title("语音朗读");
            var token = (voiceRefresh = new CancellationTokenSource()).Token;
            var status = new TextBlock { Text = "正在检测语音服务…", Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6) };
            // Declared before the toggles because Refresh() closes over it and the toggles can call Refresh.
            var voiceCombo = new ComboBox { ItemsSource = new[] { Preferences.SpeechVoice }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
            voiceCombo.SelectionChanged += (_, _) => { if (voiceCombo.SelectedItem is string id && id.Length > 0 && id != Preferences.SpeechVoice) { Preferences.SpeechVoice = id; Save(); } };
            void ToggleVoice(string text, bool value, Action<bool> set) { var box = new CheckBox { Content = text, IsChecked = value, Margin = new Thickness(0, 4) }; box.IsCheckedChanged += (_, _) => { set(box.IsChecked == true); Save(); }; body.Children.Add(box); }
            void ValueSlider(string label, double value, double low, double high, double tick, Func<double, string> format, Action<double> set) {
                var caption = new TextBlock { Text = label + "：" + format(value), Margin = new Thickness(0, 12, 0, 0) };
                var slider = new Avalonia.Controls.Slider { Minimum = low, Maximum = high, Value = value, TickFrequency = tick, IsSnapToTickEnabled = true };
                slider.ValueChanged += (_, _) => { caption.Text = label + "：" + format(slider.Value); set(slider.Value); Save(); }; body.Children.Add(caption); body.Children.Add(slider);
            }
            static string Size(long bytes) => bytes >= 1L << 30 ? $"{bytes / 1024.0 / 1024 / 1024:0.00} GB" : $"{bytes / 1024.0 / 1024:0.0} MB";

            ToggleVoice("启用语音朗读", Preferences.SpeechEnabled, value => { Preferences.SpeechEnabled = value; if (!value) { StopVoice(); voiceService?.Stop(); } else EnsureVoiceService(); _ = Refresh(); });
            ToggleVoice("气泡等声音开始再出现", Preferences.SpeechBubbleWithAudio, value => Preferences.SpeechBubbleWithAudio = value);
            body.Children.Add(Paragraph("开启后气泡等到声音真正开始播放才出现，字与声音一起开始；关闭则文字立刻出现、声音随后跟上，感知延迟更小。"));

            var engineIds = new[] { "f5", "kokoro", "gsv" };
            body.Children.Add(new TextBlock { Text = "引擎", Margin = new Thickness(0, 14, 0, 4) });
            var engine = new ComboBox { ItemsSource = new[] { "F5-TTS（默认，音色自然，需要显卡）", "Kokoro v1.1-zh（体积小，CPU 就能实时）", "GPT-SoVITS v2（MIT 权重，质量最好）" }, SelectedIndex = Math.Max(0, Array.IndexOf(engineIds, Preferences.SpeechEngine)), HorizontalAlignment = HorizontalAlignment.Stretch };
            body.Children.Add(engine);

            body.Children.Add(new TextBlock { Text = "音色（由本地服务提供）", Margin = new Thickness(0, 10, 0, 4) });
            body.Children.Add(voiceCombo);
            body.Children.Add(status);

            void ApplyVoicePicker() { voiceCombo.IsVisible = Preferences.SpeechEngine != "gsv"; }
            engine.SelectionChanged += (_, _) => { if (engine.SelectedIndex < 0) return; Preferences.SpeechEngine = engineIds[engine.SelectedIndex]; ApplyVoicePicker(); Save(); _ = Refresh(); };
            ApplyVoicePicker();

            ToggleVoice("使用显卡加速（关闭则用 CPU）", Preferences.SpeechUseGpu, value => { Preferences.SpeechUseGpu = value; status.Text = value ? "已切到显卡：下一句会让语音服务重新加载模型。" : "已切到 CPU：下一句会让语音服务重新加载模型。"; });

            body.Children.Add(new TextBlock { Text = "本地服务", Margin = new Thickness(0, 14, 0, 4) });
            body.Children.Add(Paragraph("程序自己拉起本机的语音服务，退出时连同子进程一起收走；如果这个地址上已经有服务在跑，就只连接、不接管。"));
            var python = new TextBox { Text = Preferences.SpeechPython, Watermark = "留空则用语音文件夹里的 .venv/bin/python3，再退回 PATH 里的 python3" };
            python.LostFocus += (_, _) => { Preferences.SpeechPython = python.Text?.Trim() ?? ""; Save(); };
            body.Children.Add(python);
            ToggleVoice("跟着程序启动和退出", Preferences.SpeechAutoStart, value => { Preferences.SpeechAutoStart = value; if (value) EnsureVoiceService(); else voiceService?.Stop(); _ = Refresh(); });

            var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 4) };
            void AddAction(string label, Action action) => buttons.Children.Add(ActionButton(label, action));
            AddAction("检测服务", () => _ = Refresh());
            AddAction("试听", () => _ = PlaySample());
            AddAction("重启服务", () => _ = RestartService());
            AddAction("打开语音目录", () => {
                try { var folder = Preferences.SpeechDirectory; Directory.CreateDirectory(folder); Process.Start(new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open", folder) { UseShellExecute = true }); }
                catch (Exception error) { status.Text = "打不开目录：" + error.Message; }
            });
            body.Children.Add(buttons);

            ValueSlider("语速", Preferences.SpeechRate, .7, 1.4, .05, value => value.ToString("0.00"), value => Preferences.SpeechRate = Math.Round(value, 2));
            ValueSlider("音调（半音，正数更年轻）", Preferences.SpeechPitch, -6, 6, .1, value => value.ToString("+0.0;-0.0;0.0") + " 半音", value => Preferences.SpeechPitch = Math.Round(value, 1));
            ValueSlider("合成步数（越低越快）", Preferences.SpeechNfe, 8, 40, 2, value => ((int)Math.Round(value)).ToString(), value => Preferences.SpeechNfe = (int)Math.Round(value));
            ValueSlider("音量", Preferences.SpeechVolume, 0, 2, .05, value => value.ToString("P0"), value => Preferences.SpeechVolume = Math.Round(value, 2));

            body.Children.Add(new TextBlock { Text = "朗读范围", Margin = new Thickness(0, 14, 0, 2) });
            ToggleVoice("日常关心与问候", Preferences.SpeechAutoCare, value => Preferences.SpeechAutoCare = value);
            ToggleVoice("手动点击与测试", Preferences.SpeechInteraction, value => Preferences.SpeechInteraction = value);
            ToggleVoice("Agent 与 MCP", Preferences.SpeechExternal, value => Preferences.SpeechExternal = value);

            body.Children.Add(new TextBlock { Text = "语音模型", Margin = new Thickness(0, 18, 0, 4) });
            body.Children.Add(Paragraph("三个引擎的权重分开下载，各自放进语音文件夹的 models 子目录；支持断点续传，失败自动换 hf-mirror 镜像。合成脚本随程序分发，下载时释放。"));
            var folder = Preferences.SpeechDirectory;
            foreach (var pack in CompanionVoicePacks.All) {
                var have = pack.InstalledBytes(folder);
                var missing = pack.Missing(folder);
                body.Children.Add(new TextBlock { Text = $"{pack.Name} · {Size(pack.TotalBytes)}", FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 2) });
                body.Children.Add(new TextBlock { Text = pack.Licence + " · " + pack.Start, FontSize = 11, Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap });
                var line = new TextBlock { Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4) };
                line.Text = voiceNotes.TryGetValue(pack.Id, out var note) ? note : missing == 0 ? "已就绪" : have == 0 ? "未下载" : $"已下载 {have * 100.0 / pack.TotalBytes:0}%，还差 {missing} 个文件";
                body.Children.Add(line);
                var bar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 6, Margin = new Thickness(0, 0, 0, 6), IsVisible = false };
                body.Children.Add(bar);
                var rowButtons = new WrapPanel();
                var action = new Button { Content = voiceDownloading == pack.Id ? "取消" : "下载", Padding = new Thickness(12, 6), Margin = new Thickness(0, 0, 8, 0) };
                var remove = new Button { Content = "删除", Padding = new Thickness(12, 6) };
                remove.IsVisible = have > 0 && voiceDownloading != pack.Id;
                action.IsEnabled = voiceDownloading == null || voiceDownloading == pack.Id;
                action.Click += (_, _) => {
                    if (voiceDownloading == pack.Id) { voiceDownload?.Cancel(); return; }
                    if (voiceDownloading != null) return;
                    StartDownload(pack, line, bar);
                };
                remove.Click += (_, _) => {
                    try { pack.Delete(folder); voiceNotes.Remove(pack.Id); }
                    catch (Exception error) { voiceNotes[pack.Id] = "删除失败：" + error.Message; }
                    refreshLinuxSettings?.Invoke();
                };
                rowButtons.Children.Add(action); rowButtons.Children.Add(remove); body.Children.Add(rowButtons);
            }

            body.Children.Add(Paragraph("合成代码已内置，下载时释放到语音文件夹。运行还需要 Python 3.10–3.12 与依赖：F5 用 pip install f5-tts（带显卡更快）；Kokoro 用 pip install kokoro soundfile numpy librosa；GPT-SoVITS 要把源码放到语音文件夹的 vendor/GPT-SoVITS 并装它自己的 requirements。参考音色不随程序分发：F5 放 refs/kids，GPT-SoVITS 放同名目录，每段 .wav 配一个 .txt。"));
            body.Children.Add(new TextBlock { Text = "语音文件夹：" + Preferences.SpeechDirectory, FontSize = 11, Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap });

            string Summary(VoiceServiceState state, string endpoint) { var pid = voiceService?.Pid; return state switch {
                VoiceServiceState.Running => $"服务由程序启动{(pid == null ? "" : $"（PID {pid}）")}，已连接（{endpoint}）",
                VoiceServiceState.External => $"外部服务已连接（{endpoint}）",
                VoiceServiceState.Starting => "正在启动语音服务…（模型加载需要十几秒）",
                VoiceServiceState.Failed => "语音服务启动失败：" + (voiceService?.Detail is { Length: > 0 } detail ? detail : "原因未知"),
                _ => "语音朗读已关闭" }; }

            async Task Refresh() {
                if (token.IsCancellationRequested) return;
                var endpoint = Preferences.EndpointFor(Preferences.SpeechEngine);
                status.Text = "正在检测语音服务…";
                var state = await EnsureVoiceServiceAsync(token);
                if (token.IsCancellationRequested) return;
                if (!await voice.ProbeAsync(endpoint, token)) {
                    status.Text = state == VoiceServiceState.Failed ? "语音服务启动失败：" + (voiceService?.Detail is { Length: > 0 } detail ? detail : "原因未知")
                        : Preferences.SpeechAutoStart ? "语音服务没有应答：" + endpoint
                        : "自动启动已关闭：请手动运行 " + (VoiceServicePlan.ScriptFor(Preferences.SpeechEngine) ?? "语音服务") + "。";
                    return;
                }
                var ids = await LinuxVoice.VoicesAsync(endpoint, token);
                if (token.IsCancellationRequested) return;
                // With auto-start off, EnsureStarted reports Disabled even when a service answers.
                var summary = state == VoiceServiceState.Disabled ? $"外部服务已连接（{endpoint}）" : Summary(state, endpoint);
                status.Text = summary + (ids.Count == 0 ? "" : $"，可用音色 {ids.Count} 个");
                if (ids.Count > 0) {
                    voiceCombo.ItemsSource = ids;
                    var index = ids.IndexOf(Preferences.SpeechVoice);
                    if (index < 0) { Preferences.SpeechVoice = ids[0]; Save(); index = 0; }
                    voiceCombo.SelectedIndex = index;
                }
            }

            async Task PlaySample() {
                if (!Preferences.SpeechEnabled) { status.Text = "先打开上面的开关。"; return; }
                var endpoint = Preferences.EndpointFor(Preferences.SpeechEngine);
                status.Text = "正在准备语音服务…";
                var state = await EnsureVoiceServiceAsync(token);
                if (state == VoiceServiceState.Failed) { status.Text = "语音服务启动失败：" + (voiceService?.Detail ?? ""); return; }
                if (!await voice.ProbeAsync(endpoint, token)) { status.Text = "未连接语音服务：" + endpoint; return; }
                status.Text = "正在合成…";
                var speakToken = voice.BeginSpeaking();
                var clip = await voice.SynthesizeAsync("主人，我是鱼鱼，这样说话好听吗？", Preferences, speakToken);
                if (clip == null) { status.Text = "合成失败：检查语音服务是否在运行。"; return; }
                voice.Play(clip, Preferences);
                status.Text = "试听开始。";
            }

            async Task RestartService() { status.Text = "正在重启语音服务…"; try { if (voiceService != null) await voiceService.RestartAsync(Preferences, token); } catch { } await Refresh(); }

            void StartDownload(VoicePack pack, TextBlock line, ProgressBar bar) {
                voiceDownload?.Cancel();
                var cancel = voiceDownload = new CancellationTokenSource();
                voiceDownloading = pack.Id;
                voiceNotes.Remove(pack.Id);
                try { CompanionVoicePacks.ExtractScripts(folder); } catch (Exception error) { voiceNotes[pack.Id] = "无法准备语音文件夹：" + error.Message; refreshLinuxSettings?.Invoke(); return; }
                bar.IsVisible = true; bar.Value = 0;
                var progress = new Progress<(long Done, long Total, string File)>(report => {
                    if (cancel.IsCancellationRequested) return;
                    var ratio = Math.Clamp((double)report.Done / Math.Max(report.Total, 1), 0, 1); bar.Value = ratio;
                    line.Text = $"正在下载 {ratio:P0}" + (string.IsNullOrEmpty(report.File) ? "" : " · " + Path.GetFileName(report.File));
                });
                _ = Task.Run(async () => {
                    string note;
                    try { await CompanionVoicePacks.InstallAsync(pack, folder, progress, cancel.Token); note = $"下载完成：{Size(pack.TotalBytes)}。{pack.Start}"; }
                    catch (OperationCanceledException) { note = "已取消，下次接着下。"; }
                    catch (Exception error) { note = "下载失败：" + error.Message; }
                    await Dispatcher.UIThread.InvokeAsync(() => { voiceNotes[pack.Id] = note; voiceDownload = null; voiceDownloading = null; refreshLinuxSettings?.Invoke(); });
                });
            }

            _ = Refresh();
        }
#endif

        void About() {
            Title("关于与更新"); body.Children.Add(Paragraph("蓝色大肥鱼 " + System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(LinuxPetWindow).Assembly)!.InformationalVersion.Split('+')[0] + (McpAvailable ? "" : " · 无MCP版")));
            Toggle("每次启动自动检查更新", Preferences.CheckUpdatesOnStartup, value => Preferences.CheckUpdatesOnStartup = value);
            body.Children.Add(ActionButton("检查更新", async () => await CheckUpdates(true)));
        }
        navigation.Children.Add(ActionButton("桌宠与显示", () => Navigate(General))); navigation.Children.Add(ActionButton("模型与信息", () => Navigate(Model))); navigation.Children.Add(ActionButton("动作与作息", () => Navigate(Animation)));
#if COMPANION_LINUX
        navigation.Children.Add(ActionButton("语音朗读", () => Navigate(Voice)));
#endif
#if !COMPANION_SAFE
        void Agent() { Title("连接 Agent"); BuildAgentSettings(body); }
        navigation.Children.Add(ActionButton("连接 Agent", () => Navigate(Agent)));
#endif
        navigation.Children.Add(ActionButton("关于与更新", () => Navigate(About)));
        refreshLinuxSettings = () => { var offset = scroll.Offset; selectedPage(); scroll.Offset = offset; };
#if COMPANION_LINUX
        openVoicePage = () => Navigate(Voice);
#endif
        void Logs() { Title("日志控制台"); disposeLogConsole = BuildLogConsole(body); }
        navigation.Children.Add(ActionButton("日志控制台", () => Navigate(Logs)));
        DiagnosticLog.Write(DiagnosticEvent.SettingsOpened);
        General(); settings.Show(); settings.Activate();
    }
}


