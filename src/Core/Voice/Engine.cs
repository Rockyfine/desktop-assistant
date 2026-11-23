namespace DesktopAssistant.Core.Voice;

public enum VoiceSlot { Aec, Wake, Vad, Asr, Tts, Fx }

public static class VoiceAudio
{
    public const int SampleRate = 16000;
    public const int FrameBytes = 640;
}

internal sealed record EngineRequest(string Op, byte[]? Audio = null, byte[]? Reference = null,
    string? Text = null, string? Keyword = null, string? Culture = null, string? Speaker = null);

internal sealed record EngineReply(byte[]? Audio = null, string? Text = null, bool? Active = null);

internal interface IVoiceEngine : IAsyncDisposable
{
    ValueTask<EngineReply> ExecuteAsync(EngineRequest request, CancellationToken cancellation);
}

public sealed class VoiceUnavailableException(string message) : Exception(message);
internal sealed class VoiceInterruptedException(string message) : Exception(message);
public sealed record SlotState(VoiceSlot Slot, string Current, string? Pending, bool External);
