using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace BigBlueFish.Windows;

// The synthesis code ships inside the app (it is open source); only the weights come from a download.
internal sealed record VoicePackFile(string Relative, string Url, long Size)
{
    public string MirrorUrl => Url.Replace("https://huggingface.co/", "https://hf-mirror.com/");
}

internal static class CompanionVoicePack
{
    private const string Model = "https://huggingface.co/SWivid/F5-TTS/resolve/main/F5TTS_v1_Base/";
    private const string Vocos = "https://huggingface.co/charactr/vocos-mel-24khz/resolve/main/";

    // Sizes come from the repository listings and are used both for progress and for "is it complete".
    internal static readonly VoicePackFile[] Files =
    [
        new(Path.Combine("f5", "model_1250000.safetensors"), Model + "model_1250000.safetensors", 1348435761),
        new(Path.Combine("f5", "vocab.txt"), Model + "vocab.txt", 13800),
        new(Path.Combine("f5", "vocos", "pytorch_model.bin"), Vocos + "pytorch_model.bin", 54365991),
        new(Path.Combine("f5", "vocos", "config.yaml"), Vocos + "config.yaml", 461),
    ];

    internal static readonly string[] Scripts = ["tts_server.py", "f5_tts_run.py"];

    internal static long TotalBytes => Files.Sum(file => file.Size);
    internal static string ModelsDirectory(string voiceDirectory) => Path.Combine(voiceDirectory, "models");

    internal static long InstalledBytes(string voiceDirectory)
    {
        long total = 0;
        foreach (var file in Files)
        {
            var path = Path.Combine(ModelsDirectory(voiceDirectory), file.Relative);
            if (!File.Exists(path)) continue;
            total += Math.Min(new FileInfo(path).Length, file.Size);
        }
        return total;
    }

    internal static bool IsInstalled(string voiceDirectory) => InstalledBytes(voiceDirectory) == TotalBytes;

    /// <summary>Writes the bundled synthesis scripts next to the weights so the folder is runnable.</summary>
    internal static void ExtractScripts(string voiceDirectory)
    {
        Directory.CreateDirectory(voiceDirectory);
        var assembly = typeof(CompanionVoicePack).Assembly;
        foreach (var script in Scripts)
        {
            var name = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("." + script, StringComparison.Ordinal));
            if (name == null) continue;
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            var path = Path.Combine(voiceDirectory, script);
            // Keep the copy current without touching a script the owner may have edited by hand.
            if (!File.Exists(path) || new FileInfo(path).Length != text.Length) File.WriteAllText(path, text);
        }
    }

    internal static async Task InstallAsync(string voiceDirectory, IProgress<(long Done, long Total, string File)> progress, CancellationToken token)
    {
        Directory.CreateDirectory(ModelsDirectory(voiceDirectory));
        var done = InstalledBytes(voiceDirectory);
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        foreach (var file in Files)
        {
            token.ThrowIfCancellationRequested();
            var path = Path.Combine(ModelsDirectory(voiceDirectory), file.Relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var have = File.Exists(path) ? new FileInfo(path).Length : 0;
            if (have == file.Size)
            {
                done += 0;
                progress.Report((done, TotalBytes, file.Relative));
                continue;
            }
            if (have > file.Size) { File.Delete(path); have = 0; }

            var failed = false;
            foreach (var url in new[] { file.Url, file.MirrorUrl })
            {
                try
                {
                    await DownloadAsync(http, url, path, have, file.Size, done, progress, file.Relative, token).ConfigureAwait(false);
                    done += file.Size - have;
                    progress.Report((done, TotalBytes, file.Relative));
                    failed = false;
                    break;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    failed = true;
                }
            }
            if (failed) throw new IOException("下载失败：" + file.Relative);
        }
        progress.Report((TotalBytes, TotalBytes, ""));
    }

    private static async Task DownloadAsync(HttpClient http, string url, string path, long have, long size, long done,
        IProgress<(long, long, string)> progress, string label, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (have > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (response.StatusCode is not System.Net.HttpStatusCode.OK and not System.Net.HttpStatusCode.PartialContent)
            throw new IOException("HTTP " + (int)response.StatusCode);
        // A server that ignores Range restarts the file rather than appending to it.
        if (have > 0 && response.StatusCode == System.Net.HttpStatusCode.OK) have = 0;
        await using var target = new FileStream(path, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None);
        await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var buffer = new byte[81920];
        long written = have;
        while (true)
        {
            var read = await source.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) break;
            await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            written += read;
            progress.Report((done + written, TotalBytes, label));
        }
        if (written != size) throw new IOException($"大小不符：{Path.GetFileName(path)} {written}/{size}");
    }
}
