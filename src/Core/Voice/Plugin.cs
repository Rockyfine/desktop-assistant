using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopAssistant.Core.Voice;

public sealed record VoicePlugin(string Id, string Name, string ManifestPath, string Executable, string Fingerprint,
    IReadOnlyList<VoiceSlot> Slots)
{
    private sealed record Manifest(int Protocol, string Id, string Name, string Executable, string[] Slots);

    public static VoicePlugin Read(string manifestFile)
    {
        const string badManifest = "语音插件清单无效。";
        manifestFile = Path.GetFullPath(manifestFile);
        using FileStream stream = File.OpenRead(manifestFile);
        if (stream.Length > 16_384)
            throw new InvalidDataException(badManifest);
        byte[] content = new byte[checked((int)stream.Length)];
        stream.ReadExactly(content);
        Manifest? manifest = JsonSerializer.Deserialize<Manifest>(content, JsonDefaults.Options);
        if (manifest is not { Protocol: 1 } || string.IsNullOrWhiteSpace(manifest.Name) ||
            manifest.Name.Length > 80 || string.IsNullOrWhiteSpace(manifest.Id) || manifest.Id.Length > 64 ||
            manifest.Id.Equals("builtin", StringComparison.OrdinalIgnoreCase) ||
            !manifest.Id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') ||
            string.IsNullOrWhiteSpace(manifest.Executable) || Path.IsPathRooted(manifest.Executable) ||
            manifest.Slots is not { Length: > 0 and <= 6 })
        {
            throw new InvalidDataException(badManifest);
        }
        string directory = Path.GetDirectoryName(manifestFile)!;
        string executable = Path.GetFullPath(Path.Combine(directory, manifest.Executable));
        if (!executable.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(executable))
        {
            throw new InvalidDataException("请选择清单目录内的 Windows 插件程序。");
        }
        List<VoiceSlot> slots = [];
        foreach (string name in manifest.Slots)
        {
            if (!Enum.TryParse(name, true, out VoiceSlot slot) ||
                !slot.ToString().Equals(name, StringComparison.OrdinalIgnoreCase) || slots.Contains(slot))
                throw new InvalidDataException(badManifest);
            slots.Add(slot);
        }
        using FileStream binary = File.OpenRead(executable);
        using IncrementalHash digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        digest.AppendData(content);
        digest.AppendData(SHA256.HashData(binary));
        return new VoicePlugin(manifest.Id, manifest.Name.Trim(), manifestFile, executable,
            Convert.ToHexString(digest.GetHashAndReset()), slots);
    }
}

internal sealed class PluginEngine : IVoiceEngine
{
    private const int ReplyLimit = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly Process _process;
    private readonly VoiceSlot _slot;
    private readonly char[] _readBuffer = new char[4096];
    private int _readStart;
    private int _readEnd;
    private long _sequence;
    private bool _disposed;

    private PluginEngine(VoicePlugin plugin, VoiceSlot slot)
    {
        _slot = slot;
        ProcessStartInfo start = new(plugin.Executable)
        {
            WorkingDirectory = Path.GetDirectoryName(plugin.Executable)!,
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new System.Text.UTF8Encoding(false),
            StandardOutputEncoding = System.Text.Encoding.UTF8
        };
        _process = Process.Start(start) ?? throw new IOException("无法启动语音插件。");
        _process.ErrorDataReceived += (_, _) => { };
        _process.BeginErrorReadLine();
    }

    public static async Task<PluginEngine> ConnectAsync(VoicePlugin selected, VoiceSlot slot, CancellationToken cancellation)
    {
        VoicePlugin current = VoicePlugin.Read(selected.ManifestPath);
        if (current.Fingerprint != selected.Fingerprint || !current.Slots.Contains(slot))
            throw new InvalidDataException("插件已变更，请在设置中重新确认插件。");
        PluginEngine engine = new(current, slot);
        try
        {
            Reply reply = await engine.CallAsync(new WireRequest(++engine._sequence, "hello", 1,
                slot.ToString().ToLowerInvariant(), VoiceAudio.SampleRate), TimeSpan.FromSeconds(4), cancellation);
            if (reply.Protocol != 1)
                throw new InvalidDataException("语音插件接口版本不匹配。");
            return engine;
        }
        catch
        {
            await engine.DisposeAsync();
            throw;
        }
    }

    public async ValueTask<EngineReply> ExecuteAsync(EngineRequest request, CancellationToken cancellation)
    {
        TimeSpan timeout = TimeSpan.FromMilliseconds(request.Op switch
        {
            "synthesize" => 12_000,
            "probe" => 4000,
            "finish" => 3000,
            _ => 300
        });
        Reply result = await CallAsync(new WireRequest(++_sequence, request.Op, Audio: request.Audio,
            Reference: request.Reference, Text: request.Text, Keyword: request.Keyword,
            Culture: request.Culture, Speaker: request.Speaker), timeout, cancellation);
        if ((request.Op == "effect" || request.Op == "synthesize" || request.Op == "frame" && _slot == VoiceSlot.Aec) &&
            result.Audio is not { Length: > 0 } ||
            request.Op == "finish" && result.Text is null ||
            request.Op == "frame" && _slot is VoiceSlot.Wake or VoiceSlot.Vad && result.Active is null)
            throw new InvalidDataException("插件响应缺少当前环节需要的结果。");
        return new EngineReply(result.Audio, result.Text, result.Active);
    }

    private async Task<Reply> CallAsync(WireRequest request, TimeSpan timeout, CancellationToken cancellation)
    {
        if (_process.HasExited)
            throw new EndOfStreamException("语音插件已退出。");
        using CancellationTokenSource signal = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        try
        {
            return await ExchangeAsync(request, signal.Token).WaitAsync(timeout, cancellation);
        }
        finally
        {
            signal.Cancel();
        }
    }

    private async Task<Reply> ExchangeAsync(WireRequest request, CancellationToken cancellation)
    {
        string line = JsonSerializer.Serialize(request, Wire);
        await _process.StandardInput.WriteLineAsync(line.AsMemory(), cancellation);
        await _process.StandardInput.FlushAsync(cancellation);
        string response = await ReadLineAsync(cancellation);
        Reply? reply = JsonSerializer.Deserialize<Reply>(response, Wire);
        if (reply is null || reply.Id != request.Id || !reply.Ok)
            throw new InvalidDataException("插件返回了无效响应。");
        if (reply.Audio is { Length: var length } && (length % 2 != 0 ||
            ((request.Op == "frame" || request.Op == "effect") && length != request.Audio?.Length)))
            throw new InvalidDataException("插件返回了无效的音频长度。");
        return reply;
    }

    private async Task<string> ReadLineAsync(CancellationToken cancellation)
    {
        System.Text.StringBuilder line = new();
        while (line.Length <= ReplyLimit)
        {
            if (_readStart == _readEnd)
            {
                _readEnd = await _process.StandardOutput.ReadAsync(_readBuffer.AsMemory(), cancellation);
                _readStart = 0;
                if (_readEnd == 0)
                    throw new EndOfStreamException("语音插件已退出。");
            }
            int end = Array.IndexOf(_readBuffer, '\n', _readStart, _readEnd - _readStart);
            int count = (end < 0 ? _readEnd : end) - _readStart;
            if (line.Length + count > ReplyLimit)
                break;
            line.Append(_readBuffer, _readStart, count);
            _readStart += count;
            if (end >= 0)
            {
                ++_readStart;
                return line.ToString().TrimEnd('\r');
            }
        }
        throw new InvalidDataException("语音插件响应过大。");
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;
        _disposed = true;
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        finally { _process.Dispose(); }
        return ValueTask.CompletedTask;
    }

    private sealed record WireRequest(long Id, string Op, int? Protocol = null, string? Slot = null,
        int? SampleRate = null, byte[]? Audio = null, byte[]? Reference = null, string? Text = null,
        string? Keyword = null, string? Culture = null, string? Speaker = null);

    private sealed record Reply(long Id, bool Ok, int? Protocol, byte[]? Audio, string? Text, bool? Active);
}
