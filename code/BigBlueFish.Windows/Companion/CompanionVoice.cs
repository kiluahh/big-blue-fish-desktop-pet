using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
#if COMPANION_OCR
using Windows.Media.SpeechSynthesis;
#endif

namespace BigBlueFish.Windows;

internal sealed record VoiceClip(byte[] Wav, double Seconds);

// A speech backend turns text into a wav. Implementations must be safe to call off the UI thread.
internal interface IVoiceEngine
{
    string Id { get; }
    Task<VoiceClip?> SynthesizeAsync(string text, CompanionPreferences preferences, CancellationToken token);
}

// Playback seam. The shipped implementation drives WPF's MediaPlayer; tests substitute a recorder.
internal interface IAudioSink : IDisposable
{
    bool IsPlaying { get; }
    // Raised once the clip is actually audible, so the text can start with the voice.
    event Action? Started;
    void Play(byte[] wav, double volume);
    void Stop();
}

internal sealed class MediaAudioSink : IAudioSink
{
    private readonly MediaPlayer player = new();
    private readonly Func<string> directory;
    private string? file;
    private int generation;

    public MediaAudioSink(Func<string>? directory = null)
    {
        this.directory = directory ?? (() => Path.GetTempPath());
        player.Volume = 1;
        player.MediaOpened += (_, _) => Started?.Invoke();
        player.MediaEnded += (_, _) => { };
        player.MediaFailed += (_, _) => Stop();
    }

    public event Action? Started;

    public bool IsPlaying { get; private set; }

    public void Play(byte[] wav, double volume)
    {
        Stop();
        var folder = directory();
        System.IO.Directory.CreateDirectory(folder);
        Prune(folder);
        var name = Path.Combine(folder, $"clip-{Environment.ProcessId}-{++generation}.wav");
        File.WriteAllBytes(name, wav);
        file = name;
        player.Volume = Math.Clamp(volume, 0, 1);
        player.Open(new Uri(name));
        player.Play();
        IsPlaying = true;
    }

    // Clips are transient; a killed process leaves a few behind, so drop anything old on the next play.
    private static void Prune(string folder)
    {
        try
        {
            foreach (var stale in System.IO.Directory.EnumerateFiles(folder, "clip-*.wav"))
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(stale) > TimeSpan.FromHours(1))
                    File.Delete(stale);
        }
        catch
        {
        }
    }

    public void Stop()
    {
        if (!IsPlaying && file == null) return;
        try { player.Stop(); } catch { }
        try { player.Close(); } catch { }
        IsPlaying = false;
        var path = file;
        file = null;
        if (path == null) return;
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    public void Dispose() => Stop();
}

// Local F5-TTS service (D:\AI-Workspace\voice\tts_server.py). Kept as a service so the 12 s model load is paid once.
internal sealed class F5VoiceEngine : IVoiceEngine
{
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };
    public string Id => "f5";

    public async Task<VoiceClip?> SynthesizeAsync(string text, CompanionPreferences preferences, CancellationToken token)
    {
        var payload = JsonSerializer.Serialize(new
        {
            text,
            voice = preferences.SpeechVoice,
            speed = preferences.SpeechRate,
            pitch = preferences.SpeechPitch,
            nfe = preferences.SpeechNfe
        });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(preferences.SpeechEndpoint + "/speak", content, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        var wav = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
        if (wav.Length < 64) return null;
        double seconds = response.Headers.TryGetValues("X-Audio-Seconds", out var values) && double.TryParse(First(values), out var parsed)
            ? parsed
            : 0;
        return new VoiceClip(wav, seconds);
    }

    public async Task<bool> ProbeAsync(string endpoint, CancellationToken token)
    {
        try
        {
            using var response = await http.GetAsync(endpoint + "/health", token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static string? First(System.Collections.Generic.IEnumerable<string> values)
    {
        foreach (var value in values) return value;
        return null;
    }
}

// In-box Windows voices: instant, no GPU, but the only Chinese female voice on a stock machine is Huihui.
// The Windows natural voices are not exposed through SpeechSynthesizer, so this path stays a fallback.
internal sealed class SystemVoiceEngine : IVoiceEngine
{
    public string Id => "system";

    public async Task<VoiceClip?> SynthesizeAsync(string text, CompanionPreferences preferences, CancellationToken token)
    {
#if COMPANION_OCR
        using var synthesizer = new SpeechSynthesizer();
        using var stream = await synthesizer.SynthesizeTextToStreamAsync(text).AsTask(token).ConfigureAwait(false);
        if (stream.Size == 0) return null;
        var bytes = new byte[stream.Size];
        using var reader = new global::Windows.Storage.Streams.DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size).AsTask(token).ConfigureAwait(false);
        reader.ReadBytes(bytes);
        return new VoiceClip(bytes, 0);
#else
        await Task.CompletedTask;
        return null;
#endif
    }
}

internal sealed class CompanionVoice : IDisposable
{
    private readonly IAudioSink sink;
    private readonly IVoiceEngine? engineOverride;
    private F5VoiceEngine f5 = new();
    private readonly SystemVoiceEngine system = new();
    private CancellationTokenSource? speaking;
    private Action? startedHandler;
    private double seconds;

    public CompanionVoice() : this(new MediaAudioSink(), null) { }

    internal CompanionVoice(IAudioSink audioSink, IVoiceEngine? engine = null)
    {
        sink = audioSink;
        engineOverride = engine;
    }

    public IAudioSink Sink => sink;
    internal F5VoiceEngine F5 => f5;
    public bool IsPlaying => sink.IsPlaying;
    public double LastClipSeconds => seconds;

    internal IVoiceEngine Engine(CompanionPreferences preferences) =>
        engineOverride ?? (preferences.SpeechEngine == "system" ? system : f5);

    internal async Task<VoiceClip?> SynthesizeAsync(string text, CompanionPreferences preferences, CancellationToken token = default)
    {
        if (!preferences.SpeechEnabled || string.IsNullOrWhiteSpace(text)) return null;
        // MCP's say can be very long; synthesis cost grows with length, so cap what reaches the engine.
        var spoken = text.Length > 240 ? text[..240] : text;
        try
        {
            var clip = await Engine(preferences).SynthesizeAsync(spoken, preferences, token).ConfigureAwait(false);
            if (clip != null) seconds = clip.Seconds;
            return clip;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    internal void Play(VoiceClip clip, CompanionPreferences preferences, Action? started = null)
    {
        if (clip.Wav.Length == 0)
        {
            started?.Invoke();
            return;
        }
        if (started == null)
        {
            sink.Play(clip.Wav, preferences.SpeechVolume);
            return;
        }
        Action handler = () => { sink.Started -= startedHandler!; startedHandler = null; started(); };
        startedHandler = handler;
        sink.Started += handler;
        sink.Play(clip.Wav, preferences.SpeechVolume);
    }

    internal Task<bool> ProbeAsync(string endpoint, CancellationToken token) => f5.ProbeAsync(endpoint, token);

    internal void Stop()
    {
        try { speaking?.Cancel(); } catch { }
        if (startedHandler != null) { sink.Started -= startedHandler; startedHandler = null; }
        sink.Stop();
    }

    internal CancellationToken BeginSpeaking()
    {
        Stop();
        speaking?.Dispose();
        speaking = new CancellationTokenSource();
        return speaking.Token;
    }

    public void Dispose()
    {
        Stop();
        speaking?.Dispose();
        sink.Dispose();
        f5 = new F5VoiceEngine();
    }
}
