using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BigBlueFish.Windows;

internal sealed record VoiceServiceLaunch(string FileName, string Arguments, string WorkingDirectory, string Endpoint, string Engine);

internal enum VoiceServiceState { Disabled, External, Starting, Running, Failed }

// A child process and its console tail. Tests swap in a fake so no python is needed.
internal interface IVoiceServiceProcess : IDisposable
{
    int Id { get; }
    bool HasExited { get; }
    int? ExitCode { get; }
    string Tail { get; }
    void Kill();
    bool WaitForExit(int milliseconds);
}

internal interface IVoiceServiceHost
{
    VoiceServiceState State { get; }
    bool Owned { get; }
    int? Pid { get; }
    string Detail { get; }
    Task<VoiceServiceState> EnsureStartedAsync(CompanionPreferences preferences, CancellationToken token = default);
    Task<VoiceServiceState> RestartAsync(CompanionPreferences preferences, CancellationToken token = default);
    void Stop();
}

// Decides whether this installation may run its own speech service, and how. Pure: no process is started here.
internal static class VoiceServicePlan
{
    static readonly string[] LoopbackHosts = ["127.0.0.1", "localhost", "::1"];

    internal static string? ScriptFor(string engine) => engine switch
    {
        "f5" => "tts_server.py",
        "kokoro" => "tts_kokoro_server.py",
        "gsv" => "tts_gsv_server.py",
        _ => null
    };

    internal static bool IsLoopback(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && LoopbackHosts.Contains(uri.Host.Trim('[', ']').ToLowerInvariant());

    // A bare name like "python" is left to PATH; anything with a separator is anchored to the voice folder.
    internal static string ResolvePython(string configured, string voiceDirectory, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        var pick = configured?.Trim() ?? "";
        if (pick.Length > 0)
        {
            var relative = !Path.IsPathRooted(pick) && !pick.Contains('\\') && !pick.Contains('/');
            return relative ? pick : Path.GetFullPath(Path.IsPathRooted(pick) ? pick : Path.Combine(voiceDirectory, pick));
        }
        // A venv is platform specific: Scripts/python.exe on Windows, bin/python on Linux and macOS.
        var candidates = OperatingSystem.IsWindows()
            ? new[]
            {
                Path.Combine(voiceDirectory, ".venv", "Scripts", "python.exe"),
                Path.Combine(voiceDirectory, "venv", "Scripts", "python.exe"),
                Path.Combine(voiceDirectory, "python.exe")
            }
            : new[]
            {
                Path.Combine(voiceDirectory, ".venv", "bin", "python3"),
                Path.Combine(voiceDirectory, ".venv", "bin", "python"),
                Path.Combine(voiceDirectory, "venv", "bin", "python3"),
                Path.Combine(voiceDirectory, "venv", "bin", "python")
            };
        foreach (var candidate in candidates)
            if (exists(candidate)) return candidate;
        return OperatingSystem.IsWindows() ? "python" : "python3";
    }

    internal static VoiceServiceLaunch? TryDescribe(CompanionPreferences preferences, string voiceDirectory, Func<string, bool>? exists = null)
    {
        if (!preferences.SpeechEnabled || !preferences.SpeechAutoStart) return null;
        var engine = preferences.SpeechEngine;
        var script = ScriptFor(engine);
        if (script == null) return null;
        var endpoint = preferences.EndpointFor(engine);
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        if (!IsLoopback(endpoint) || uri.Port <= 0) return null;
        var folder = Path.GetFullPath(voiceDirectory);
        var python = ResolvePython(preferences.SpeechPython, folder, exists);
        return new VoiceServiceLaunch(python, $"{script} --host 127.0.0.1 --port {uri.Port}", folder, endpoint, engine);
    }
}

/// <summary>
/// Owns the local python synthesis service: starts it when speech needs it and takes the whole process tree down
/// on exit, because the venv python is a shim that spawns the interpreter really holding the weights.
/// A service that is already answering on the loopback address belongs to whoever started it and is never killed.
/// </summary>
internal sealed class CompanionVoiceService : IVoiceServiceHost, IDisposable
{
    static readonly TimeSpan StartDeadline = TimeSpan.FromSeconds(60);
    static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);
    const int KillWaitMilliseconds = 3000;

    readonly Func<VoiceServiceLaunch, IVoiceServiceProcess?> spawn;
    readonly Func<string, CancellationToken, Task<bool>> probe;
    readonly HttpClient http = new() { Timeout = Timeout.InfiniteTimeSpan };
    readonly SemaphoreSlim gate = new(1, 1);
    readonly object sync = new();
    IVoiceServiceProcess? process;
    VoiceServiceLaunch? launch;
    bool disposed;

    internal CompanionVoiceService(Func<VoiceServiceLaunch, IVoiceServiceProcess?>? spawn = null,
        Func<string, CancellationToken, Task<bool>>? probe = null)
    {
        this.spawn = spawn ?? VoiceServiceProcess.Start;
        this.probe = probe ?? ProbeAsync;
    }

    public VoiceServiceState State { get; private set; }
    public bool Owned { get { lock (sync) return process != null; } }
    public int? Pid { get { lock (sync) return process?.Id; } }
    public string Detail { get; private set; } = "";

    public async Task<VoiceServiceState> EnsureStartedAsync(CompanionPreferences preferences, CancellationToken token = default)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (disposed) return State = VoiceServiceState.Disabled;
            var plan = VoiceServicePlan.TryDescribe(preferences, preferences.SpeechDirectory);
            if (plan == null) { StopOwned(null); Detail = ""; return State = VoiceServiceState.Disabled; }

            var running = Current();
            if (running != null && !running.HasExited && launch?.Arguments == plan.Arguments)
                return State = await WaitHealthyAsync(plan, running, token).ConfigureAwait(false);

            if (await probe(plan.Endpoint, token).ConfigureAwait(false))
            {
                StopOwned("external");
                Detail = "";
                return State = VoiceServiceState.External;
            }

            StopOwned("stale");
            Detail = "";
            try
            {
                Directory.CreateDirectory(plan.WorkingDirectory);
                CompanionVoicePacks.ExtractScripts(plan.WorkingDirectory);
            }
            catch (Exception error)
            {
                Detail = "无法准备语音文件夹：" + error.Message;
                DiagnosticLog.Write(DiagnosticEvent.VoiceServiceFailed, kind: "voice", state: "failed", error: error, directory: plan.WorkingDirectory);
                return State = VoiceServiceState.Failed;
            }

            IVoiceServiceProcess? started;
            try
            {
                started = spawn(plan);
            }
            catch (Exception error)
            {
                Detail = "启动语音服务失败：" + error.Message;
                DiagnosticLog.Write(DiagnosticEvent.VoiceServiceFailed, kind: "voice", state: "failed", error: error, directory: plan.WorkingDirectory);
                return State = VoiceServiceState.Failed;
            }
            if (started == null)
            {
                Detail = "找不到 python 解释器：" + plan.FileName;
                DiagnosticLog.Write(DiagnosticEvent.VoiceServiceFailed, kind: "voice", state: "failed", directory: plan.WorkingDirectory);
                return State = VoiceServiceState.Failed;
            }

            lock (sync) { process = started; launch = plan; }
            State = VoiceServiceState.Starting;
            DiagnosticLog.Write(DiagnosticEvent.VoiceServiceStarted, kind: "voice", state: "start", count: started.Id, directory: plan.WorkingDirectory);
            return State = await WaitHealthyAsync(plan, started, token).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<VoiceServiceState> RestartAsync(CompanionPreferences preferences, CancellationToken token = default)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            StopOwned("restart");
            State = VoiceServiceState.Disabled;
        }
        finally
        {
            gate.Release();
        }
        return await EnsureStartedAsync(preferences, token).ConfigureAwait(false);
    }

    // Exit path: kill first, wait briefly, then report. Safe to call more than once.
    public void Stop()
    {
        StopOwned("stop");
        Detail = "";
        State = VoiceServiceState.Disabled;
    }

    public void Dispose()
    {
        disposed = true;
        Stop();
        http.Dispose();
    }

    IVoiceServiceProcess? Current() { lock (sync) return process; }

    async Task<VoiceServiceState> WaitHealthyAsync(VoiceServiceLaunch plan, IVoiceServiceProcess running, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            if (running.HasExited)
            {
                var tail = running.Tail;
                Detail = "语音服务启动后退出（代码 " + (running.ExitCode?.ToString() ?? "?") + "）" + (tail.Length == 0 ? "" : "：" + tail);
                DiagnosticLog.Write(DiagnosticEvent.VoiceServiceFailed, kind: "voice", state: "failed", count: running.Id,
                    error: new InvalidOperationException(tail), directory: plan.WorkingDirectory);
                StopOwned("exited");
                return State = VoiceServiceState.Failed;
            }
            if (await probe(plan.Endpoint, token).ConfigureAwait(false)) return VoiceServiceState.Running;
            if (watch.Elapsed > StartDeadline)
            {
                Detail = "语音服务 60 秒内没有就绪";
                DiagnosticLog.Write(DiagnosticEvent.VoiceServiceFailed, kind: "voice", state: "failed", count: running.Id, directory: plan.WorkingDirectory);
                StopOwned("timeout");
                return State = VoiceServiceState.Failed;
            }
            try { await Task.Delay(300, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return State; }
        }
    }

    async Task<bool> ProbeAsync(string endpoint, CancellationToken token)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(ProbeTimeout);
            using var response = await http.GetAsync(endpoint + "/health", timeout.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    void StopOwned(string? reason)
    {
        IVoiceServiceProcess? stopped;
        VoiceServiceLaunch? where;
        lock (sync)
        {
            stopped = process;
            where = launch;
            process = null;
            launch = null;
        }
        if (stopped == null) return;
        var pid = stopped.Id;
        try { if (!stopped.HasExited) stopped.Kill(); } catch { }
        try { stopped.WaitForExit(KillWaitMilliseconds); } catch { }
        try { stopped.Dispose(); } catch { }
        DiagnosticLog.Write(DiagnosticEvent.VoiceServiceStopped, kind: "voice", state: "stopped", count: pid,
            directory: where?.WorkingDirectory);
    }
}

// Wraps System.Diagnostics.Process. stdout and stderr are drained into a short ring so a chatty child can never
// block on a full pipe, and the last lines ride along with a failure report.
internal sealed class VoiceServiceProcess : IVoiceServiceProcess
{
    readonly Process process;
    readonly Queue<string> lines = new();
    readonly object sync = new();

    VoiceServiceProcess(Process process) => this.process = process;

    internal static IVoiceServiceProcess? Start(VoiceServiceLaunch launch)
    {
        var info = new ProcessStartInfo(launch.FileName, launch.Arguments)
        {
            WorkingDirectory = launch.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        info.Environment["PYTHONUNBUFFERED"] = "1";
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        var wrapper = new VoiceServiceProcess(process);
        process.OutputDataReceived += (_, e) => wrapper.Capture(e.Data);
        process.ErrorDataReceived += (_, e) => wrapper.Capture(e.Data);
        try
        {
            if (!process.Start()) { process.Dispose(); return null; }
        }
        catch
        {
            process.Dispose();
            throw;
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return wrapper;
    }

    public int Id { get { try { return process.Id; } catch { return -1; } } }
    public bool HasExited { get { try { return process.HasExited; } catch { return true; } } }
    public int? ExitCode { get { try { return process.HasExited ? process.ExitCode : null; } catch { return null; } } }
    public string Tail { get { lock (sync) return string.Join(" ", lines); } }

    public void Kill()
    {
        // Kill(true) walks the children of the venv shim; taskkill is the fallback when that walk races.
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); return; }
        catch { }
        // taskkill only exists on Windows; on Unix the first kill is the whole story.
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var kill = Process.Start(new ProcessStartInfo("taskkill", $"/PID {process.Id} /T /F")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });
            kill?.WaitForExit(KillFallbackMilliseconds);
        }
        catch
        {
        }
    }

    const int KillFallbackMilliseconds = 3000;

    public bool WaitForExit(int milliseconds)
    {
        try { return process.WaitForExit(milliseconds); }
        catch { return HasExited; }
    }

    public void Dispose()
    {
        try { process.Dispose(); } catch { }
    }

    void Capture(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (sync)
        {
            lines.Enqueue(line.Length > 240 ? line[..240] : line);
            while (lines.Count > 8) lines.Dequeue();
        }
    }
}
