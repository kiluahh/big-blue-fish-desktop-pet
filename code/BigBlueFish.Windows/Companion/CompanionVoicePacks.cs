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
    /// <summary>hf-mirror serves the same paths, so a blocked HuggingFace still has a second route.</summary>
    public string MirrorUrl => Url.Contains("huggingface.co/", StringComparison.Ordinal)
        ? Url.Replace("https://huggingface.co/", "https://hf-mirror.com/", StringComparison.Ordinal)
        : Url;
}

/// <summary>One engine's weights: what to fetch, where it belongs, and how it is started.</summary>
internal sealed record VoicePack(string Id, string Name, string Licence, string Start, string[] Scripts, VoicePackFile[] Files)
{
    public long TotalBytes => Files.Sum(file => file.Size);
    public string Folder(string voiceDirectory) => Path.Combine(CompanionVoicePacks.ModelsDirectory(voiceDirectory), Id);
    public string PathOf(string voiceDirectory, VoicePackFile file) => Path.Combine(Folder(voiceDirectory), file.Relative);

    public long InstalledBytes(string voiceDirectory)
    {
        long total = 0;
        foreach (var file in Files)
        {
            var path = PathOf(voiceDirectory, file);
            if (File.Exists(path)) total += Math.Min(new FileInfo(path).Length, file.Size);
        }
        return total;
    }

    /// <summary>Files that are absent or the wrong size; a partial download shows up here.</summary>
    public int Missing(string voiceDirectory) => Files.Count(file =>
    {
        var path = PathOf(voiceDirectory, file);
        return !File.Exists(path) || new FileInfo(path).Length != file.Size;
    });

    public bool IsInstalled(string voiceDirectory) => Missing(voiceDirectory) == 0;

    public void Delete(string voiceDirectory)
    {
        var folder = Folder(voiceDirectory);
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }
}

internal static class CompanionVoicePacks
{
    private const string F5 = "https://huggingface.co/SWivid/F5-TTS/resolve/main/F5TTS_v1_Base/";
    private const string Vocos = "https://huggingface.co/charactr/vocos-mel-24khz/resolve/main/";
    private const string Kokoro = "https://huggingface.co/hexgrad/Kokoro-82M-v1.1-zh/resolve/main/";
    private const string Gsv = "https://huggingface.co/lj1995/GPT-SoVITS/resolve/main/";

    // The voices the Kokoro page offers, highest pitch first. Each one is a 511 KB style vector.
    private static readonly string[] Voices = ["zf_038", "zf_072", "zf_074", "zf_092", "zf_017", "zf_073", "zf_076", "zf_085"];

    // Sizes come from the repository listings and are used both for progress and for "is it complete".
    internal static readonly VoicePack F5Pack = new("f5", "F5-TTS v1 Base", "CC BY-NC 4.0（仅非商业）",
        "启动：由程序自动启动", ["tts_server.py", "f5_tts_run.py"],
        [
            new("model_1250000.safetensors", F5 + "model_1250000.safetensors", 1348435761),
            new("vocab.txt", F5 + "vocab.txt", 13800),
            new(Path.Combine("vocos", "pytorch_model.bin"), Vocos + "pytorch_model.bin", 54365991),
            new(Path.Combine("vocos", "config.yaml"), Vocos + "config.yaml", 461),
        ]);

    internal static readonly VoicePack KokoroPack = new("kokoro", "Kokoro v1.1-zh", "Apache-2.0",
        "启动：由程序自动启动", ["tts_kokoro_server.py"],
        [
            new("config.json", Kokoro + "config.json", 3228),
            new("kokoro-v1_1-zh.pth", Kokoro + "kokoro-v1_1-zh.pth", 327247856),
            .. Voices.Select(voice => new VoicePackFile(Path.Combine("voices", voice + ".pt"), Kokoro + "voices/" + voice + ".pt", 523331)),
        ]);

    internal static readonly VoicePack GsvPack = new("gsv", "GPT-SoVITS v2", "MIT",
        "启动：由程序自动启动（另需源码与参考音频）", ["tts_gsv_server.py"],
        [
            new(Path.Combine("gsv-v2final-pretrained", "s1bert25hz-5kh-longer-epoch=12-step=369668.ckpt"),
                Gsv + "gsv-v2final-pretrained/s1bert25hz-5kh-longer-epoch=12-step=369668.ckpt", 155315150),
            new(Path.Combine("gsv-v2final-pretrained", "s2G2333k.pth"), Gsv + "gsv-v2final-pretrained/s2G2333k.pth", 106035259),
            new(Path.Combine("chinese-hubert-base", "config.json"), Gsv + "chinese-hubert-base/config.json", 1449),
            new(Path.Combine("chinese-hubert-base", "preprocessor_config.json"), Gsv + "chinese-hubert-base/preprocessor_config.json", 212),
            new(Path.Combine("chinese-hubert-base", "pytorch_model.bin"), Gsv + "chinese-hubert-base/pytorch_model.bin", 188811417),
            new(Path.Combine("chinese-roberta-wwm-ext-large", "config.json"), Gsv + "chinese-roberta-wwm-ext-large/config.json", 963),
            new(Path.Combine("chinese-roberta-wwm-ext-large", "pytorch_model.bin"), Gsv + "chinese-roberta-wwm-ext-large/pytorch_model.bin", 651225145),
            new(Path.Combine("chinese-roberta-wwm-ext-large", "tokenizer.json"), Gsv + "chinese-roberta-wwm-ext-large/tokenizer.json", 268962),
        ]);

    internal static readonly VoicePack[] All = [F5Pack, KokoroPack, GsvPack];

    internal static VoicePack? ForEngine(string engine) => engine switch
    {
        "f5" => F5Pack,
        "kokoro" => KokoroPack,
        "gsv" => GsvPack,
        _ => null
    };

    internal static string ModelsDirectory(string voiceDirectory) => Path.Combine(voiceDirectory, "models");

    internal static IEnumerable<string> Scripts => All.SelectMany(pack => pack.Scripts).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>Writes the bundled synthesis scripts next to the weights so the folder is runnable.</summary>
    internal static void ExtractScripts(string voiceDirectory)
    {
        Directory.CreateDirectory(voiceDirectory);
        var assembly = typeof(CompanionVoicePacks).Assembly;
        foreach (var script in Scripts)
        {
            var name = assembly.GetManifestResourceNames()
                .FirstOrDefault(candidate => candidate.EndsWith("." + script, StringComparison.Ordinal));
            if (name == null) continue;
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            var path = Path.Combine(voiceDirectory, script);
            // Keep the copy current without touching a script the owner may have edited by hand.
            if (!File.Exists(path) || new FileInfo(path).Length != text.Length) File.WriteAllText(path, text);
        }
    }

    internal static async Task InstallAsync(VoicePack pack, string voiceDirectory,
        IProgress<(long Done, long Total, string File)> progress, CancellationToken token)
    {
        Directory.CreateDirectory(pack.Folder(voiceDirectory));
        var total = pack.TotalBytes;
        var done = pack.InstalledBytes(voiceDirectory);
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        foreach (var file in pack.Files)
        {
            token.ThrowIfCancellationRequested();
            var path = pack.PathOf(voiceDirectory, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var have = File.Exists(path) ? new FileInfo(path).Length : 0;
            if (have == file.Size)
            {
                progress.Report((done, total, file.Relative));
                continue;
            }
            // A file longer than expected is a broken earlier attempt; start it over.
            if (have > file.Size) { File.Delete(path); have = 0; }

            var failed = false;
            foreach (var url in new[] { file.Url, file.MirrorUrl }.Distinct(StringComparer.Ordinal))
            {
                try
                {
                    await DownloadAsync(http, url, path, have, file.Size, done, progress, file.Relative, token).ConfigureAwait(false);
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
            done += file.Size - have;
            progress.Report((done, total, file.Relative));
        }
        if (!pack.IsInstalled(voiceDirectory)) throw new IOException("下载的文件大小不符，请重试。");
        progress.Report((total, total, ""));
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
        var reported = Environment.TickCount64;
        while (true)
        {
            var read = await source.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) break;
            await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            written += read;
            // Throttle: a 1.3 GB file is 16k buffers and every report crosses to the UI thread.
            if (Environment.TickCount64 - reported < 250) continue;
            reported = Environment.TickCount64;
            progress.Report((done + written, done + size, label));
        }
        if (written != size) throw new IOException($"大小不符：{Path.GetFileName(path)} {written}/{size}");
    }
}
