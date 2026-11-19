using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace BigBlueFish.Windows;

internal enum VoiceScope { Automatic, Interactive, External }

internal sealed record VoiceClip(byte[] Wav, double Seconds);

// Playback seam. The shipped implementation drives ffplay; the self-check substitutes a recorder.
internal interface IAudioSink : IDisposable
{
    bool IsPlaying { get; }
    // Raised once the clip is handed to the audio device, so the text can start with the voice.
    event Action? Started;
    void Play(byte[] wav, double volume);
    void Stop();
}

/// <summary>
/// Plays the clip with the lightest available external player. A fresh process is the only
/// per-sentence cost: pw-play starts in about 24 ms on PipeWire, paplay in about 40 ms, and
/// ffplay (the guaranteed fallback shipped with ffmpeg) in about 100 ms.
/// </summary>
internal sealed class PlayerAudioSink : IAudioSink
{
    static readonly string[] Candidates = ["pw-play", "paplay", "ffplay"];
    readonly Func<string> directory;
    readonly string executable;
    Process? player;
    string? file;
    int generation;

    public PlayerAudioSink(Func<string>? directory = null)
    {
        this.directory = directory ?? (() => Path.GetTempPath());
        executable = Candidates.FirstOrDefault(OnPath) ?? "ffplay";
    }

    public event Action? Started;

    internal string Player => executable;

    // Only a full path in PATH counts; a desktop session may inherit a shorter PATH than a shell.
    static bool OnPath(string name)
    {
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (folder.Length > 0 && File.Exists(Path.Combine(folder, name))) return true;
        return false;
    }

    public bool IsPlaying
    {
        get
        {
            try { return player != null && !player.HasExited; }
            catch { return false; }
        }
    }

    public void Play(byte[] wav, double volume)
    {
        Stop();
        var folder = directory();
        Directory.CreateDirectory(folder);
        Prune(folder);
        var name = Path.Combine(folder, $"clip-{Environment.ProcessId}-{++generation}.wav");
        // Players stop at 100%; anything louder is applied to the samples first.
        var gain = Math.Clamp(volume, 0, 2);
        if (gain > 1.001)
        {
            wav = Amplify(wav, gain);
            gain = 1;
        }
        File.WriteAllBytes(name, wav);
        file = name;
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (executable == "pw-play")
        {
            info.ArgumentList.Add("--volume");
            info.ArgumentList.Add(gain.ToString("0.###", CultureInfo.InvariantCulture));
        }
        else if (executable == "paplay")
        {
            info.ArgumentList.Add("--volume");
            info.ArgumentList.Add(((int)Math.Round(gain * 65536, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            foreach (var argument in new[] { "-nodisp", "-autoexit", "-loglevel", "quiet", "-volume", Math.Round(gain * 100, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture) })
                info.ArgumentList.Add(argument);
        }
        info.ArgumentList.Add(name);
        try
        {
            player = Process.Start(info);
            if (player != null)
            {
                player.EnableRaisingEvents = true;
                // Drain both pipes so a chatty player can never block on a full buffer.
                _ = player.StandardOutput.ReadToEndAsync();
                _ = player.StandardError.ReadToEndAsync();
            }
        }
        catch (Exception error)
        {
            DiagnosticLog.Write(DiagnosticEvent.SpeechFailed, kind: "voice", state: executable, error: error);
        }
        Started?.Invoke();
    }

    // Scales 16-bit PCM in place; hard clipping is acceptable for the small boosts the slider allows.
    internal static byte[] Amplify(byte[] wav, double gain)
    {
        int position = 12, start = -1, length = 0;
        while (position + 8 <= wav.Length)
        {
            var id = Encoding.ASCII.GetString(wav, position, 4);
            var size = BitConverter.ToInt32(wav, position + 4);
            if (id == "data")
            {
                start = position + 8;
                length = Math.Min(size, wav.Length - start);
                break;
            }
            position += 8 + size + (size & 1);
        }
        if (start < 0) return wav;
        var copy = (byte[])wav.Clone();
        for (var i = start; i + 1 < start + length; i += 2)
        {
            var scaled = (int)Math.Round(BitConverter.ToInt16(copy, i) * gain);
            var clamped = Math.Clamp(scaled, short.MinValue, short.MaxValue);
            copy[i] = (byte)(clamped & 0xFF);
            copy[i + 1] = (byte)((clamped >> 8) & 0xFF);
        }
        return copy;
    }

    // Clips are transient; a killed process leaves a few behind, so drop anything old on the next play.
    static void Prune(string folder)
    {
        try
        {
            foreach (var stale in Directory.EnumerateFiles(folder, "clip-*.wav"))
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(stale) > TimeSpan.FromHours(1))
                    File.Delete(stale);
        }
        catch
        {
        }
    }

    public void Stop()
    {
        var running = player;
        player = null;
        if (running != null)
        {
            try { if (!running.HasExited) running.Kill(entireProcessTree: true); } catch { }
            try { running.Dispose(); } catch { }
        }
        var path = file;
        file = null;
        if (path == null) return;
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    public void Dispose() => Stop();
}

/// <summary>
/// The three local engines speak the same JSON protocol, so one client serves f5, kokoro and gsv;
/// only the endpoint and the voice list differ. The service lifecycle lives in CompanionVoiceService.
/// </summary>
internal sealed class LinuxVoice : IDisposable
{
    readonly IAudioSink sink;
    readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };
    CancellationTokenSource? speaking;
    Action? startedHandler;
    double seconds;

    internal LinuxVoice(Func<string>? clipDirectory = null) : this(new PlayerAudioSink(clipDirectory)) { }

    internal LinuxVoice(IAudioSink audioSink) => sink = audioSink;

    public bool IsPlaying => sink.IsPlaying;
    public double LastClipSeconds => seconds;

    internal async Task<VoiceClip?> SynthesizeAsync(string text, CompanionPreferences preferences, CancellationToken token)
    {
        if (!preferences.SpeechEnabled || string.IsNullOrWhiteSpace(text)) return null;
        // MCP's say can be very long; synthesis cost grows with length, so cap what reaches the engine.
        var spoken = text.Length > 240 ? text[..240] : text;
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                text = spoken,
                voice = preferences.SpeechVoice,
                speed = preferences.SpeechRate,
                pitch = preferences.SpeechPitch,
                gpu = preferences.SpeechUseGpu,
                nfe = preferences.SpeechNfe
            });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await http.PostAsync(preferences.EndpointFor(preferences.SpeechEngine) + "/speak", content, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var wav = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
            if (wav.Length < 64) return null;
            seconds = response.Headers.TryGetValues("X-Audio-Seconds", out var values) && double.TryParse(First(values), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0;
            return new VoiceClip(wav, seconds);
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

    internal async Task<bool> ProbeAsync(string endpoint, CancellationToken token = default)
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

    internal static async Task<List<string>> VoicesAsync(string endpoint, CancellationToken token = default)
    {
        var ids = new List<string>();
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var json = await client.GetStringAsync(endpoint + "/health", token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("voices", out var voices))
                foreach (var item in voices.EnumerateArray())
                    if (item.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } value)
                        ids.Add(value);
        }
        catch
        {
        }
        return ids;
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

    internal CancellationToken BeginSpeaking()
    {
        Stop();
        speaking?.Dispose();
        speaking = new CancellationTokenSource();
        return speaking.Token;
    }

    internal void Stop()
    {
        try { speaking?.Cancel(); } catch { }
        if (startedHandler != null) { sink.Started -= startedHandler; startedHandler = null; }
        sink.Stop();
    }

    public void Dispose()
    {
        Stop();
        speaking?.Dispose();
        sink.Dispose();
        http.Dispose();
    }

    static string? First(IEnumerable<string> values)
    {
        foreach (var value in values) return value;
        return null;
    }
}
