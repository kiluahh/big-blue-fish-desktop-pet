using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace BigBlueFish.Windows;

internal sealed partial class LinuxPetWindow : Window
{
    internal readonly CompanionPreferences Preferences = CompanionPreferences.Read();
    readonly CompanionAnimationPolicy policy = CompanionAnimationPolicy.Load(Path.Combine(AppContext.BaseDirectory, "assets/fish"));
    readonly CompanionBrain brain;
    readonly Random random = new();
    readonly Image sprite = new() { Stretch = Stretch.Fill };
    readonly LinuxVideoPlayer player;
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    readonly Window bubbleWindow;
    readonly TextBlock bubbleText = new() { TextWrapping = TextWrapping.Wrap };
    readonly LinuxBubble bubbleFrame;
    readonly LinuxLoadingRing loading = new() { IsVisible = false };
    readonly Stopwatch typingClock = new();
    readonly Queue<string> externalActions = new();
    readonly Mutex mutex;
    // The two palm states ship as PNGs derived from the Windows .cur files; X11 shows them
    // only where the input shape lets the window receive the pointer.
    static readonly Cursor Palm = LoadCursor("Palm"), PalmGrab = LoadCursor("PalmGrab");
#if COMPANION_LINUX
    // Speech: one HTTP client for all three local engines, an external player for low latency,
    // and the service host that owns the python process tree.
    readonly LinuxVoice voice;
    IVoiceServiceHost? voiceService = new CompanionVoiceService();
    DateTime voiceHoldUntil = DateTime.MinValue;
#endif
    double typingSeconds;
    string currentAction = "", phase = "Startup", fullText = "";
    string? nextExternalText;
    DateTime nextSpeech = DateTime.UtcNow.AddSeconds(45), nextEvent = DateTime.MaxValue, nextWarmup;
    DateTime bubbleUntil = DateTime.MaxValue;
    IntPtr lastForeground;
    bool closed, exitReady, hidden, fullscreenHidden, dragging, dragged, generating, streamed, externalSpeech, typing, modelReply, greetingNoticeActive;
    DispatcherTimer? exitWatchdog;
    long speechVersion;
    int[] textBoundaries = [];
    PixelPoint dragOrigin, windowOrigin, moveOrigin;
    PixelPoint lastClickPoint;
    long lastClickAt;
    double moveStart, moveEnd;
    CompanionAnimationRule? moveRule;
    bool moveMirrored;
    Window? settings;
    bool modelInstalling;
    bool agentWeatherPending;
    static readonly IBrush Hair = new SolidColorBrush(Color.FromRgb(80, 96, 152));
    internal bool Puppet => McpAvailable && Preferences.McpEnabled && Preferences.PuppetMode;
    double SpriteSize => 280 * Preferences.Scale;

    internal LinuxPetWindow()
    {
        mutex = new Mutex(true, "BigBlueFish.Linux." + Environment.UserName, out var first);
#if COMPANION_LINUX
        voice = new(() => Path.Combine(Preferences.SpeechDirectory, "cache"));
#endif
        if (!first) Environment.Exit(0);
        DiagnosticLog.ConfigureDirectories(AppContext.BaseDirectory, Environment.CurrentDirectory);
        DiagnosticLog.Write(DiagnosticEvent.AppStarted, kind: "Linux", directory: Environment.CurrentDirectory);
        DiagnosticLog.ObserveSettings(Preferences);
        brain = new() { UseGpu = Preferences.GpuEnabled, StopOnBattery = Preferences.StopModelOnBattery, WeatherRegionCode = Preferences.WeatherRegionCode };
        Title = "蓝色大肥鱼"; WindowDecorations = WindowDecorations.None; ShowInTaskbar = false; ShowActivated = false;
        Topmost = true; CanResize = false; Background = Brushes.Transparent; TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        var canvas = new Grid(); canvas.Children.Add(sprite); canvas.Children.Add(loading); Content = canvas; Cursor = Palm;
        loading.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left; loading.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        player = new(sprite); player.FramePresented += MoveWithAnimation;
        PositionChanged += (_, _) => MoveBubble();
        player.PixelsPresented += (pixels,width,height,mirror) => CompanionNative.SetInputShape(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero,pixels,width,height,Width*DesktopScaling/width,mirror);
        player.Failed += error => { DiagnosticLog.Write(DiagnosticEvent.AnimationFailed, state: "failed"); if (phase == "Exit") CloseImmediately(); };
        bubbleText.FontFamily = new FontFamily("avares://" + typeof(App).Assembly.GetName().Name + "/Assets#LemiXiaoNaiPaoTi");
        bubbleText.Foreground = Hair;
        var close = new Button { Content = "×", Padding = new Thickness(3), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top, Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        close.Click += (_, _) => { typing = false; bubbleWindow!.Hide(); };
        var bubbleGrid = new Grid(); bubbleGrid.Children.Add(bubbleText); bubbleGrid.Children.Add(close); bubbleText.Margin = new Thickness(0, 0, 24, 0);
        bubbleFrame = new LinuxBubble { Child = bubbleGrid, Padding = new Thickness(16,16,16,30) };
        bubbleWindow = new Window { Title = "蓝色大肥鱼 · 对话", WindowDecorations = WindowDecorations.None, ShowInTaskbar = false, ShowActivated = false,
            Topmost = true, CanResize = false, Background = Brushes.Transparent, TransparencyLevelHint = [WindowTransparencyLevel.Transparent], Content = bubbleFrame };
        Rescale();
        var screen = Screens.Primary?.WorkingArea ?? new PixelRect(0, 0, 1280, 720);
        Position = new((int)(Preferences.Left ?? screen.Right - Width * DesktopScaling), (int)(Preferences.Top ?? screen.Bottom - Height * DesktopScaling)); Clamp();
        Opened += async (_, _) => {
            if (OperatingSystem.IsMacOS()) { Rescale(); Clamp(); } // Avalonia's macOS backend opens a borderless window at 0×0, ignoring the size set before Show
            if (Puppet) Play(policy.Find("待机呼吸休闲"), "PuppetIdle");
            else { Play(policy.Find("女仆屈膝礼仪"), "Startup"); Welcome(); }
            InitializeMcp(); InitializeTray(); timer.Start();
#if COMPANION_LINUX
            if (Preferences.SpeechEnabled && Preferences.SpeechAutoStart) EnsureVoiceService();
#endif
            if (!File.Exists("model-selection.json") && !brain.ModelAvailable && !App.Args.Contains("--test-mode")) await InstallModel();
            if (Preferences.CheckUpdatesOnStartup && !App.Args.Contains("--test-mode")) _ = CheckUpdates(false);
        };
        PointerPressed += (_, e) => {
            var point = e.GetCurrentPoint(this);
            if (point.Properties.IsRightButtonPressed) { PublishInteraction("click", new { button = "right", Position.X, Position.Y }); OpenMenu(); e.Handled = true; return; }
            if (!point.Properties.IsLeftButtonPressed) return;
            if (e.ClickCount == 2 && StartSpeechTest()) { lastClickAt = 0; PublishInteraction("click", new { button = "left", count = 2, Position.X, Position.Y }); e.Handled = true; return; }
            dragging = true; dragged = false; moveRule = null; windowOrigin = Position; Cursor = PalmGrab;
            var screenPoint = this.PointToScreen(point.Position); dragOrigin = screenPoint;
            e.Pointer.Capture(this); e.Handled = true;
        };
        PointerMoved += (_, e) => {
            if (!dragging) return;
            var screenPoint = this.PointToScreen(e.GetPosition(this)); int dx = screenPoint.X - dragOrigin.X, dy = screenPoint.Y - dragOrigin.Y;
            if (!dragged && Math.Abs(dx) + Math.Abs(dy) > 4 * DesktopScaling) {
                dragged = true; PublishInteraction("drag_start", new { Position.X, Position.Y }); if (!Puppet) Play(policy.Find("被鼠标拖拽悬空反馈"), "Drag");
            }
            Position = new(windowOrigin.X + dx, windowOrigin.Y + dy); MoveBubble();
        };
        PointerReleased += (_, e) => {
            Cursor = Palm;
            if (!dragging) return; dragging = false; e.Pointer.Capture(null); Clamp(); Save();
            if (dragged) { lastClickAt = 0; PublishInteraction("drag_end", new { Position.X, Position.Y, cancelled = false }); if (!Puppet && !PlayQueued()) Idle(); }
            else {
                PublishInteraction("click", new { button = "left", Position.X, Position.Y });
                var click = this.PointToScreen(e.GetPosition(this)); long at = Environment.TickCount64;
                bool twice = lastClickAt != 0 && at - lastClickAt <= 500 && Math.Abs(click.X-lastClickPoint.X)+Math.Abs(click.Y-lastClickPoint.Y) <= 8;
                lastClickAt = at; lastClickPoint = click;
                if (twice && StartSpeechTest()) lastClickAt = 0;
                else if (!Puppet) Play(policy.Choose("Click", Context(), random), "Click");
            }
            e.Handled = true;
        };
        PointerCaptureLost += (_, _) => { Cursor = Palm; if (dragging) { dragging = false; if (dragged) PublishInteraction("drag_end", new { Position.X, Position.Y, cancelled = true }); if (!Puppet) Idle(); } };
        Closing += (_, e) => {
            try { Save(); CloseModelInstaller(); } catch { }
            if (exitReady || closed || Puppet) return;
            e.Cancel = true; if (phase == "Exit") return;
            try {
                speechVersion++; brain.Stop(); moveRule = null; settings?.Close(); hidden = false; fullscreenHidden = false; Show(); player.Paused = false;
                Play(policy.Find("女仆屈膝礼仪"), "Exit"); ShowText("主人再见~鱼鱼喜欢主人哦", false, true);
            } catch {
                // 道别动作播放不出来就直接退出，别把窗口留在桌面上。
                CloseImmediately();
                return;
            }
            // 播放器偶尔不回报播放结束（视频解码卡住或窗口被系统收起），兜底几秒后照样关闭。
            exitWatchdog?.Stop();
            exitWatchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            exitWatchdog.Tick += (_, _) => { exitWatchdog?.Stop(); CloseImmediately(); };
            exitWatchdog.Start();
        };
        Closed += (_, _) => {
            closed = true;
            // 清理里可能卡在 GStreamer、语音服务或退出动画上，先挂一个硬退出兜底，保证进程一定结束。
            new Thread(() => { Thread.Sleep(4000); Environment.Exit(0); }) { IsBackground = true, Name = "shutdown-watchdog" }.Start();
            try { DiagnosticLog.Write(DiagnosticEvent.AppStopped, kind: "Linux"); } catch { }
            try { Save(); } catch { }
            try { timer.Stop(); } catch { }
            try { StopMcp(); } catch { }
            try { brain.Dispose(); } catch { }
            try { player.Dispose(); } catch { }
#if COMPANION_LINUX
            try { StopVoice(); } catch { }
            try { voiceService?.Stop(); } catch { }
            try { voice.Dispose(); } catch { }
#endif
            try { bubbleWindow.Close(); } catch { }
            try { settings?.Close(); } catch { }
            try { mutex.Dispose(); } catch { }
            try { (Avalonia.Application.Current!.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)!.Shutdown(); } catch { }
            Environment.Exit(0);
        };
        timer.Tick += async (_, _) => await Tick();
    }
    CompanionAnimationContext Context() => new(DateTime.Now, BreakfastHour: Preferences.BreakfastHour, LunchHour: Preferences.LunchHour, DinnerHour: Preferences.DinnerHour);
    void Rescale()
    {
        Width = SpriteSize * 640 / 360; Height = SpriteSize; sprite.Width = Width; sprite.Height = Height;
        loading.Margin = new Thickness(SpriteSize * 412 / 360, SpriteSize * 50 / 360, 0, 0); MoveBubble();
    }
    PixelPoint Clamp(PixelPoint? requested = null)
    {
        var area = (Screens.ScreenFromWindow(this) ?? Screens.Primary)?.WorkingArea ?? new PixelRect(0, 0, 1280, 720);
        double s = DesktopScaling, left = SpriteSize * 208 / 360 * s, top = SpriteSize * 58 / 360 * s, right = SpriteSize * 428 / 360 * s, bottom = SpriteSize * 336 / 360 * s;
        var target = requested ?? Position;
        var result = new PixelPoint((int)Math.Clamp(target.X, area.X - left, Math.Max(area.X - left, area.Right - right)), (int)Math.Clamp(target.Y, area.Y - top, Math.Max(area.Y - top, area.Bottom - bottom)));
        Position = result; MoveBubble(); return result;
    }
    void Save() { if (closed) return; Preferences.Left = Position.X; Preferences.Top = Position.Y; Preferences.Save(); DiagnosticLog.ObserveSettings(Preferences); }
    static Cursor LoadCursor(string name)
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri($"avares://{typeof(App).Assembly.GetName().Name}/Res/Cursor/{name}.png"));
            return new Cursor(new Bitmap(stream), new PixelPoint(17, 18));
        }
        catch (Exception)
        {
            // A trimmed build without the resource stays usable with the system hand.
            return new Cursor(StandardCursorType.Hand);
        }
    }
    void Play(CompanionAnimationRule rule, string nextPhase, bool mirror = false)
    {
        if (closed) return;
        if (phase == "Startup" && nextPhase != "Startup") EndWelcome();
        DiagnosticLog.Write(DiagnosticEvent.AnimationStarted, kind: nextPhase, action: rule.Id);
        currentAction = rule.Id; phase = nextPhase;
        player.Play(Path.Combine(AppContext.BaseDirectory, "assets/fish", rule.File), mirror, Finished,
            nextPhase == "Drag" ? 1.5 : 0, nextPhase == "Drag" ? 6 : null, rule.Pool == "Greeting" ? 1.5 : 1);
    }
    void Idle() => Play(Puppet ? policy.Find("待机呼吸休闲") : policy.Choose("Idle", Context(), random), Puppet ? "PuppetIdle" : "Idle");
    bool PlayQueued() { if (externalActions.Count == 0) return false; moveRule = null; Play(policy.Find(externalActions.Dequeue()), "External"); return true; }
    void Finished()
    {
        DiagnosticLog.Write(DiagnosticEvent.AnimationCompleted, kind: phase, action: currentAction);
        if (closed) return;
        if (phase == "Exit") { CloseImmediately(); return; }
        if (!dragging && !hidden && !fullscreenHidden && PlayQueued()) return;
        if (Puppet) { Idle(); return; }
        if (phase == "Drag") return;
        if (phase == "Outbound" && moveRule != null) { Position = new((int)moveEnd, Position.Y); Play(policy.Choose("Idle", Context(), random), "BetweenMoves"); return; }
        if (phase == "BetweenMoves" && moveRule != null) { moveStart = Position.X; moveEnd = moveOrigin.X; Play(moveRule, "Return", !moveMirrored); return; }
        if (phase == "Return") { Position = new(moveOrigin.X, Position.Y); moveRule = null; }
        if (phase == "Startup") { EndWelcome(); nextEvent = DateTime.UtcNow.AddSeconds(Preferences.AnimationIntervalSeconds); }
        if (dragging || hidden || fullscreenHidden || settings != null) { Idle(); return; }
        var now = DateTime.Now;
        if (now.Hour == 23 && Preferences.LastBrushDate != now.ToString("yyyy-MM-dd")) { Preferences.LastBrushDate = now.ToString("yyyy-MM-dd"); Save(); Play(policy.Find("晨间刷牙"), "Daily"); return; }
        if (DateTime.UtcNow >= nextEvent) { nextEvent = DateTime.UtcNow.AddSeconds(Preferences.AnimationIntervalSeconds); Event(policy.Choose("Event", Context(), random)); } else Idle();
    }
    void Event(CompanionAnimationRule rule)
    {
        if (rule.MoveKind == null) { Play(rule, "Event"); return; }
        moveRule = rule; moveOrigin = Position; moveStart = Position.X;
        Position = new(Position.X - (int)(70 * Preferences.Scale * DesktopScaling), Position.Y); Clamp(); moveEnd = Position.X;
        if (Math.Abs(moveEnd - moveStart) < 15 * DesktopScaling) { Position = new((int)(moveStart + 70 * Preferences.Scale * DesktopScaling), Position.Y); Clamp(); moveEnd = Position.X; }
        Position = moveOrigin; moveMirrored = moveEnd > moveStart; Play(rule, "Outbound", moveMirrored);
    }
    void MoveWithAnimation(double seconds)
    {
        if (moveRule == null || phase is not ("Outbound" or "Return")) return;
        double lead = moveRule.MoveKind == "Run" ? 1.75 : 2, tail = moveRule.MoveKind == "Run" ? 4.8 : 2;
        double fraction = Math.Clamp((seconds - lead) / (10 - lead - tail), 0, 1);
        Position = new((int)(moveStart + (moveEnd - moveStart) * fraction), Position.Y); MoveBubble();
    }
    void Welcome()
    {
        string period = DateTime.Now.Hour switch { >= 5 and < 11 => "早上", >= 11 and < 14 => "中午", >= 14 and < 18 => "下午", _ => "晚上" };
        ShowText($"主人{period}好，鱼鱼给主人请安啦~", false, true);
        greetingNoticeActive = true;
    }
    void EndWelcome() { if (greetingNoticeActive) bubbleWindow.Hide(); greetingNoticeActive = false; }
    void ShowText(string text, bool external, bool immediate = false, bool fromModel = false, double audioSeconds = 0)
    {
        greetingNoticeActive = false;
        DiagnosticLog.Write(DiagnosticEvent.BubbleShown, kind: external ? "agent" : fromModel ? "local" : "notice", chars: text.Length);
        fullText = text; textBoundaries = StringInfo.ParseCombiningCharacters(text); externalSpeech = external; modelReply = fromModel;
        typing = !immediate && textBoundaries.Length > 1; typingClock.Restart(); bubbleUntil = DateTime.MaxValue;
        typingSeconds = typing && audioSeconds > 0 ? audioSeconds : 0;
        bubbleText.Text = typing ? text[..textBoundaries[1]] : text;
        MoveBubble(); if (!hidden && !fullscreenHidden) ShowBubble(); if (!typing && phase is not ("Startup" or "Exit")) ResetDwell();
    }
    void Partial(string text, long expectedVersion, bool agentTriggered = false)
    {
        Dispatcher.UIThread.Post(() => {
            if (expectedVersion != speechVersion || Puppet && !agentTriggered || closed || hidden || fullscreenHidden || !Preferences.ModelEnabled || !brain.CanRunOnCurrentPower) return;
            greetingNoticeActive = false; streamed = true; fullText = text; typing = false; typingSeconds = 0; externalSpeech = false; modelReply = true;
            // 开了“气泡等声音开始再出现”就先把流式文字攒着，等音频真的开始再一起出现。
            if (Speaks(agentTriggered ? VoiceScope.Interactive : VoiceScope.Automatic) && Preferences.SpeechBubbleWithAudio) return;
            bubbleText.Text = text; bubbleUntil = DateTime.MaxValue;
            MoveBubble(); ShowBubble();
        });
    }
    void ShowBubble()
    {
        if (bubbleWindow.IsVisible) return;
        bubbleWindow.Show(this);
        MoveBubble(); // re-measure after mapping: the first measure before the window is shown yields zero-width bubble text
        // Some X11 WMs discard ABOVE when a window is withdrawn. Reassert it
        // after mapping, without activating the bubble or raising it per letter.
        bubbleWindow.Topmost = false;
        bubbleWindow.Topmost = true;
        bubbleWindow.ShowInTaskbar = true;
        bubbleWindow.ShowInTaskbar = false;
    }
    void ResetDwell()
    {
        int duration = CompanionPreferences.BubbleDurations[Preferences.BubbleDurationIndex];
        var until = duration == 0 ? DateTime.MaxValue : DateTime.UtcNow.AddSeconds(duration);
#if COMPANION_LINUX
        // A sentence is often longer than the chosen dwell time; keep the bubble while it is still being spoken.
        if (DateTime.UtcNow < voiceHoldUntil && voiceHoldUntil > until) until = voiceHoldUntil;
#endif
        bubbleUntil = until;
    }
    void StopVoice()
    {
#if COMPANION_LINUX
        voice.Stop(); voiceHoldUntil = DateTime.MinValue;
#endif
    }
#if COMPANION_LINUX
    bool Speaks(VoiceScope scope) => Preferences.SpeechEnabled && scope switch
    {
        VoiceScope.Automatic => Preferences.SpeechAutoCare,
        VoiceScope.Interactive => Preferences.SpeechInteraction,
        _ => Preferences.SpeechExternal
    };

    // The local service belongs to the pet: started in the background and taken down on exit.
    void EnsureVoiceService() => _ = EnsureVoiceServiceAsync();

    internal async Task<VoiceServiceState> EnsureVoiceServiceAsync(CancellationToken token = default)
    {
        var host = voiceService;
        if (host == null) return VoiceServiceState.Disabled;
        try { return await host.EnsureStartedAsync(Preferences, token).ConfigureAwait(true); }
        catch (Exception error)
        {
            DiagnosticLog.Write(DiagnosticEvent.VoiceServiceFailed, kind: "voice", state: "failed", error: error);
            return VoiceServiceState.Failed;
        }
    }

    /// <summary>Shows the bubble, then speaks it if the scope allows it. Text waits for audio only when asked.</summary>
    internal void ShowVoiceText(string text, VoiceScope scope, bool external = false, bool fromModel = false)
    {
        if (Speaks(scope) && Preferences.SpeechBubbleWithAudio) { _ = SpeakThenShowAsync(text, scope, external, fromModel); return; }
        ShowText(text, external, fromModel: fromModel);
        if (Speaks(scope)) _ = SpeakAsync(text);
    }

    // Bubble-delay mode: synthesize first, then reveal the text and start the clip together.
    async Task SpeakThenShowAsync(string text, VoiceScope scope, bool external, bool fromModel)
    {
        var token = voice.BeginSpeaking();
        var started = DateTime.UtcNow;
        DiagnosticLog.Write(DiagnosticEvent.SpeechStarted, kind: "voice", chars: text.Length);
        VoiceClip? clip;
        try { clip = await voice.SynthesizeAsync(text, Preferences, token).ConfigureAwait(true); }
        catch (OperationCanceledException) { return; }
        if (token.IsCancellationRequested) return;
        if (clip == null)
        {
            // Nothing to hear, so do not keep the owner waiting: show the text as usual.
            DiagnosticLog.Write(DiagnosticEvent.SpeechFailed, kind: "voice");
            ShowText(text, external, fromModel: fromModel);
            return;
        }
        voiceHoldUntil = DateTime.UtcNow.AddSeconds(clip.Seconds + 2);
        var revealed = false;
        var fallback = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        void Reveal()
        {
            if (revealed) return;
            revealed = true;
            fallback.Stop();
            ShowText(text, external, false, fromModel, clip.Seconds);
        }
        fallback.Tick += (_, _) => Reveal();
        fallback.Start();
        voice.Play(clip, Preferences, () => Dispatcher.UIThread.Post(() => { fallback.Stop(); Reveal(); }));
        DiagnosticLog.Write(DiagnosticEvent.SpeechCompleted, kind: "voice", chars: text.Length,
            elapsedMs: (DateTime.UtcNow - started).TotalMilliseconds);
    }

    // Text-first mode: the bubble is already up, so the clip only extends its dwell time.
    async Task SpeakAsync(string text)
    {
        var token = voice.BeginSpeaking();
        // 气泡已经在屏幕上时，合成这段语音要几秒，别让气泡提前收走，等声音一起走完。
        if (bubbleWindow.IsVisible && Preferences.SpeechBubbleWithAudio) { voiceHoldUntil = DateTime.UtcNow.AddSeconds(30); ResetDwell(); }
        VoiceClip? clip;
        try { clip = await voice.SynthesizeAsync(text, Preferences, token).ConfigureAwait(true); }
        catch (OperationCanceledException) { return; }
        if (clip == null) { DiagnosticLog.Write(DiagnosticEvent.SpeechFailed, kind: "voice"); return; }
        if (token.IsCancellationRequested || fullText != text) return;
        voice.Play(clip, Preferences);
        voiceHoldUntil = DateTime.UtcNow.AddSeconds(clip.Seconds + 2);
        if (bubbleWindow.IsVisible) ResetDwell();
        DiagnosticLog.Write(DiagnosticEvent.SpeechCompleted, kind: "voice", chars: text.Length);
    }
#endif
    void MoveBubble()
    {
        if (bubbleFrame == null || bubbleWindow == null) return;
        double scale = Preferences.BubbleScale;
        bubbleText.FontSize = 17 * scale; bubbleText.Margin = new(0, 0, 24 * scale, 0); bubbleText.MaxWidth = 270 * scale;
        bubbleFrame.Padding = new(16 * scale, 16 * scale, 16 * scale, 30 * scale);
        string prefix = bubbleText.Text ?? ""; bubbleText.Text = fullText;
        bubbleFrame.Measure(new Size(320 * scale, double.PositiveInfinity)); var size = bubbleFrame.DesiredSize; bubbleText.Text = prefix;
        var area = (Screens.ScreenFromWindow(this) ?? Screens.Primary)?.WorkingArea ?? new PixelRect(0, 0, 1280, 720);
        bubbleWindow.Width = Math.Min(size.Width, area.Width / DesktopScaling - 16); bubbleWindow.Height = Math.Min(size.Height, area.Height / DesktopScaling - 16);
        int width = (int)(bubbleWindow.Width * DesktopScaling), height = (int)(bubbleWindow.Height * DesktopScaling);
        int x = Position.X + (int)(Width * DesktopScaling / 2) - width / 2, y = Position.Y + (int)(SpriteSize * 58 / 360 * DesktopScaling) - height - 10;
        if (y < area.Y) y = Position.Y + (int)(SpriteSize * 336 / 360 * DesktopScaling) + 10;
        bubbleWindow.Position = new(Math.Clamp(x, area.X + 8, Math.Max(area.X + 8, area.Right - width - 8)), Math.Clamp(y, area.Y + 8, Math.Max(area.Y + 8, area.Bottom - height - 8)));
    }
    async Task Tick()
    {
        if (closed || phase == "Exit") return;
        await TestCommands(); if (closed || phase == "Exit") return;
        var now = DateTime.UtcNow;
        lastForeground = CompanionNative.ResolveForeground(CompanionNative.GetForegroundWindow(), lastForeground);
        if (Preferences.PublicInfo) brain.RefreshWeather();
        if (!brain.CanRunOnCurrentPower) {
            if (brain.RunnerPid != null || brain.Busy) { speechVersion++; brain.Stop(); loading.IsVisible = false; }
            if (modelReply) bubbleWindow.Hide();
        }
        else if (!AgentDailyActive && !modelInstalling && Preferences.ModelEnabled && brain.ModelAvailable && !generating && !brain.Busy && brain.RunnerPid == null && now >= nextWarmup) { nextWarmup = now.AddMinutes(2); _ = brain.WarmUp(); }
        bool full = Preferences.HideOnFullscreen && CompanionNative.IsFullscreen();
        if (full && !fullscreenHidden && !hidden) { fullscreenHidden = true; Hide(); bubbleWindow.Hide(); speechVersion++; brain.CancelGeneration(); StopVoice(); }
        else if (!full && fullscreenHidden) { fullscreenHidden = false; if (!hidden) { Show(); Clamp(); } }
        player.Paused = hidden || fullscreenHidden;
        TickAgent(now);
        if (hidden || fullscreenHidden) return;
        if (typing)
        {
            double rate = typingSeconds > 0 && textBoundaries.Length > 1 ? (textBoundaries.Length - 1) / typingSeconds : 28;
            int count = Math.Min(textBoundaries.Length, 1 + (int)(typingClock.Elapsed.TotalSeconds * rate));
            bubbleText.Text = count >= textBoundaries.Length ? fullText : fullText[..textBoundaries[count]];
            if (count == textBoundaries.Length) { typing = false; ResetDwell(); }
        }
        if (now >= bubbleUntil) bubbleWindow.Hide();
        if (Puppet || dragging || settings != null || modelInstalling || now < nextSpeech) return;
        if (nextExternalText == null && !AgentDailyActive && (generating || brain.Busy || !Preferences.ModelEnabled || !brain.ModelAvailable || !brain.CanRunOnCurrentPower)) return;
        if (CompanionNative.IdleSeconds < 8 || CompanionNative.IdleSeconds > 600) { nextSpeech = now.AddSeconds(30); return; }
        if (nextExternalText != null) { string text = nextExternalText; nextExternalText = null; SayExternal(text); nextSpeech = now.Add(Preferences.NextSpeechDelay(random)); }
        else if (!DispatchAgentDaily()) await Speak(false);
    }
    async Task Speak(bool weather, bool agentTriggered = false)
    {
        DiagnosticLog.Write(DiagnosticEvent.SpeechRequested, kind: weather ? "weather" : "local", state: agentTriggered ? "manual" : "automatic");
        if (Puppet && !agentTriggered || generating || closed || phase == "Exit" || modelInstalling) { DiagnosticLog.Write(DiagnosticEvent.SpeechSkipped, state: "blocked"); return; }
        if (!brain.ModelAvailable) { await InstallModel(); return; }
        if (!Preferences.ModelEnabled) { ShowText("主人，请先在设置里启用本地大模型哦~", false); return; }
        if (!brain.CanRunOnCurrentPower) { ShowText("主人，当前设置为笔记本离电暂停模型，可以在设置里调节哦。", false); return; }
        generating = true; long version = ++speechVersion; streamed = false; loading.IsVisible = true;
        try
        {
            var until = DateTime.UtcNow.AddSeconds(100);
            while (brain.Busy && DateTime.UtcNow < until && version == speechVersion) await Task.Delay(100);
            if (brain.Busy || version != speechVersion || Puppet && !agentTriggered || hidden || fullscreenHidden) return;
            nextSpeech = DateTime.UtcNow.Add(Preferences.NextSpeechDelay(random));
            var text = weather ? await brain.GenerateWeather(p => Partial(p, version, agentTriggered)) : await brain.Generate(Preferences.PublicInfo, Preferences.ForegroundEnabled, lastForeground, p => Partial(p, version, agentTriggered));
            if (version != speechVersion || Puppet && !agentTriggered || hidden || fullscreenHidden || closed || !Preferences.ModelEnabled || !brain.CanRunOnCurrentPower) return;
            if (text.Length > 0) {
#if COMPANION_LINUX
                var scope = agentTriggered ? VoiceScope.Interactive : VoiceScope.Automatic;
                // 等声音的模式下即使模型是流式吐字，也只在音频开始那一刻把字放出来，避免字说完才出声。
                if (streamed && !(Speaks(scope) && Preferences.SpeechBubbleWithAudio)) {
                    fullText = text; bubbleText.Text = text; ResetDwell();
                    if (Speaks(scope)) _ = SpeakAsync(text);
                }
                else ShowVoiceText(text, scope, fromModel: true);
#else
                if (streamed) { fullText = text; bubbleText.Text = text; ResetDwell(); }
                else ShowText(text, false, fromModel: true);
#endif
            }
            else if (streamed && !externalSpeech) bubbleWindow.Hide();
        }
        finally { generating = false; loading.IsVisible = false; }
    }
    void SayExternal(string text)
    {
        speechVersion++; brain.CancelGeneration(); loading.IsVisible = false;
#if COMPANION_LINUX
        ShowVoiceText(text, VoiceScope.External, external: true);
#else
        ShowText(text, true);
#endif
    }
    // 左键双击 = 吐字测试（原先是打开聊天输入框；聊天改到右键菜单）。正在生成时也吃掉这次双击，免得变成拖拽。
    bool StartSpeechTest()
    {
        if (closed || phase == "Exit") return false;
        _ = RunSpeechTest();
        return true;
    }
    async Task RunSpeechTest()
    {
        try { await SpeakManual(false); }
        catch (Exception error) { DiagnosticLog.Write(DiagnosticEvent.SpeechFailed, kind: "test", error: error); }
    }
    async Task SpeakManual(bool weather)
    {
        if (closed || phase == "Exit") return;
        if (weather && agentWeatherPending) return;
        DiagnosticLog.Write(DiagnosticEvent.SpeechRequested, kind: weather ? "weather" : "test");
        if (hidden || fullscreenHidden) { hidden = false; fullscreenHidden = false; Show(); }
#if !COMPANION_SAFE
        if (AgentDailyActive)
        {
            try
            {
                if (weather) { agentWeatherPending = true; loading.IsVisible = true; }
                else ShowText("主人，正在请 Agent 回应这次吐字测试～", false);
                string context = weather ? await brain.GetWeatherContext(CancellationToken.None) : "";
                bool accepted = agent?.ManualSpeech(weather, context) == true;
                DiagnosticLog.Write(DiagnosticEvent.AgentMaterials, kind: weather ? "weather" : "test", state: accepted ? "success" : "blocked");
                if (!accepted && !weather) ShowText("主人，Agent 请求尚未就绪或队列已满，请查看连接状态后重试～", false);
            }
            catch (Exception error) { DiagnosticLog.Write(DiagnosticEvent.AgentFailed, kind: weather ? "weather" : "test", error: error); if (!weather) ShowText("主人，" + error.Message, false); }
            finally { agentWeatherPending = false; if (agent?.AwaitingSpeech != true) loading.IsVisible = false; }
            return;
        }
#endif
        await Speak(weather, true);
    }
    void OpenMenu()
    {
        var menu = new ContextMenu();
        MenuItem Item(string title, Action action, bool available = true) { var item = new MenuItem { Header = title, IsEnabled = available }; item.Click += (_, _) => action(); return item; }
        menu.ItemsSource = new[] { Item("设置", OpenSettings), Item("天气", async () => await SpeakManual(true)), Item("聊天", () => { TryOpenMcpInput(); }, McpInputAvailable),
            Item(hidden ? "显示桌宠" : "隐藏桌宠", () => { if (hidden) { hidden = false; Show(); Clamp(); } else { hidden = true; Hide(); bubbleWindow.Hide(); speechVersion++; brain.CancelGeneration(); StopVoice(); } }), Item("关闭桌宠", Close) };
        ContextMenu = menu; menu.Open(this);
    }
    void CloseImmediately() { exitReady = true; Close(); }
}



