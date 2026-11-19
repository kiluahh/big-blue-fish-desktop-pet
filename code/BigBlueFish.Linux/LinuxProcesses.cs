using System.Diagnostics;

namespace BigBlueFish.Windows;

internal static class LinuxProcesses
{
    const string SystemdRun = "/usr/bin/systemd-run";
    static readonly string guard = Path.Combine(AppContext.BaseDirectory, "runtime", "BigBlueFish.RunnerGuard");
    static readonly Lazy<bool> scopes = new(ProbeScopes);
    static int captures;

    internal static Process Start(ProcessStartInfo original)
    {
        if (!File.Exists(guard)) throw new IOException("缺少随程序安装的进程退出保障组件。");
        var command = original.FileName; original.FileName = guard;
        original.ArgumentList.Insert(0, command);
        return Process.Start(original) ?? throw new IOException("无法启动本地组件。");
    }

    // GNOME 48 的截图门户会给“带应用身份的窗口程序”弹一次系统授权框，而这个授权框只允许前台应用触发；
    // 桌宠几乎永远不是前台，于是每次截图要么被直接拒绝，要么卡到 D-Bus 超时（实测 23 秒）。
    // 放进名称不带 app- 前缀的 transient unit 后，门户按普通命令行工具对待，直接给图（实测 0.6 秒）。
    internal static Process StartScreenshot(ProcessStartInfo original)
    {
        if (!scopes.Value) return Start(original);
        if (!File.Exists(guard)) throw new IOException("缺少随程序安装的进程退出保障组件。");
        var start = new ProcessStartInfo(SystemdRun)
        {
            UseShellExecute = false,
            RedirectStandardOutput = original.RedirectStandardOutput,
            RedirectStandardError = original.RedirectStandardError,
        };
        foreach (var argument in new[]
        {
            "--user", "--pipe", "--quiet", "--wait", "--collect",
            $"--unit=bigbluefish-capture-{Environment.ProcessId}-{Interlocked.Increment(ref captures)}",
            "--", guard, original.FileName,
        }) start.ArgumentList.Add(argument);
        foreach (var argument in original.ArgumentList) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new IOException("无法启动本地组件。");
    }

    static bool ProbeScopes()
    {
        if (!File.Exists(SystemdRun)) return false;
        try
        {
            var probe = new ProcessStartInfo(SystemdRun) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[]
            {
                "--user", "--pipe", "--quiet", "--wait", "--collect",
                $"--unit=bigbluefish-probe-{Environment.ProcessId}", "--", "/bin/true",
            }) probe.ArgumentList.Add(argument);
            using var process = Process.Start(probe);
            return process != null && process.WaitForExit(3000) && process.ExitCode == 0;
        }
        catch { return false; }
    }
}
internal sealed class CompanionJob : IDisposable
{
    private readonly List<Process> children = [];
    internal bool Attach(Process process) { children.Add(process); return true; }
    public void Dispose() { foreach (var child in children) try { if (!child.HasExited) child.Kill(true); } catch { } children.Clear(); }
}
