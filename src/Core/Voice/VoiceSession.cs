using System.Text.RegularExpressions;
using System.Threading.Channels;
using NAudio.Wave;

namespace DesktopAssistant.Core.Voice;

public sealed class VoiceSession(VoiceRouter router) : IAsyncDisposable
{
    private enum Mode { Automatic, Manual, Test }
    private static WaveFormat Pcm => new(VoiceAudio.SampleRate, 16, 1);
    private readonly SemaphoreSlim _transition = new(1);
    private readonly SemaphoreSlim _speechGate = new(1);
    private readonly object _speechLock = new();
    private Capture? _capture;
    private Playback? _playback;
    private CancellationTokenSource? _speech;
    private VoiceOptions _options = new();
    private bool _paused;
    private bool _disposed;

    public event Action<string>? Status;
    public event Action<string>? TextReady;
    public event Action<bool>? ManualChanged;

    public async Task ConfigureAsync(VoiceOptions options)
    {
        await _transition.WaitAsync();
        try
        {
            VoiceOptions previous = _options;
            await StopAsync(finish: false);
            _options = options;
            try { await RestoreAutomaticAsync(); }
            catch
            {
                _options = previous;
                try { await RestoreAutomaticAsync(); }
                catch (Exception restoreError) { Report(restoreError.Message); }
                throw;
            }
        }
        finally { _transition.Release(); }
    }

    public async Task PauseAsync(bool paused)
    {
        await _transition.WaitAsync();
        try
        {
            _paused = paused;
            if (paused)
            {
                await StopAsync(finish: false);
                Report("麦克风已暂停");
            }
            else
            {
                try { await RestoreAutomaticAsync(); }
                catch
                {
                    _paused = true;
                    throw;
                }
            }
        }
        finally { _transition.Release(); }
    }

    public async Task ToggleTalkAsync()
    {
        await _transition.WaitAsync();
        try
        {
            if (_paused)
                throw new VoiceUnavailableException("请先恢复麦克风。");
            if (_capture?.Kind == Mode.Manual)
            {
                await StopAsync(finish: true);
                await RestoreAutomaticAsync();
                return;
            }
            Interrupt();
            await StopAsync(finish: false);
            try { await StartAsync(Mode.Manual, _options); }
            catch
            {
                try { await RestoreAutomaticAsync(); }
                catch (Exception restoreError) { Report(restoreError.Message); }
                throw;
            }
        }
        finally { _transition.Release(); }
    }

    public async Task TestWakeAsync(VoiceOptions options)
    {
        await _transition.WaitAsync();
        try
        {
            if (_paused)
                throw new VoiceUnavailableException("请先恢复麦克风。");
            Interrupt();
            await StopAsync(finish: false);
            try { await StartAsync(Mode.Test, options); }
            catch
            {
                try { await RestoreAutomaticAsync(); }
                catch (Exception restoreError) { Report(restoreError.Message); }
                throw;
            }
        }
        finally { _transition.Release(); }
    }

    private async Task RestoreAutomaticAsync()
    {
        if (!_disposed && !_paused && _options.AlwaysListen)
            await StartAsync(Mode.Automatic, _options);
        else if (!_paused && !_disposed)
            Report("麦克风待命");
    }

    private async Task StartAsync(Mode mode, VoiceOptions options)
    {
        if (_disposed)
            return;
        Capture capture = await Task.Run(async () =>
        {
            if (mode != Mode.Test)
                await router.ProbeAsync(VoiceSlot.Asr, options);
            if (mode != Mode.Manual)
                await router.ProbeAsync(VoiceSlot.Wake, options);
            if (WaveInEvent.DeviceCount == 0)
                throw new VoiceUnavailableException("找不到麦克风。请连接设备并检查 Windows 麦克风权限。");
            Capture next = new(mode, options);
            try
            {
                next.Device.StartRecording();
                return next;
            }
            catch
            {
                next.Dispose();
                throw;
            }
        });
        _capture = capture;
        if (mode == Mode.Manual)
            ManualChanged?.Invoke(true);
        Report(mode switch
        {
            Mode.Manual => "正在录音 · 点击结束",
            Mode.Test => $"请试读「{options.Keyword}」",
            _ => $"等待唤醒 · {options.Keyword}"
        });
        capture.Worker = Task.Run(() => CaptureAsync(capture));
        _ = ObserveAsync(capture);
    }

    private async Task ObserveAsync(Capture capture)
    {
        string? outcome = await capture.Worker;
        await _transition.WaitAsync();
        try
        {
            if (_capture != capture)
                return;
            _capture = null;
            capture.Dispose();
            if (capture.Kind == Mode.Manual)
                ManualChanged?.Invoke(false);
            if (capture.Kind != Mode.Automatic && capture.Failure is null)
            {
                try { await RestoreAutomaticAsync(); }
                catch (Exception error) { Report(error.Message); }
            }
            if (outcome is not null)
                Report(outcome);
        }
        finally { _transition.Release(); }
    }

    private async Task StopAsync(bool finish)
    {
        if (_capture is not { } capture)
            return;
        _capture = null;
        capture.FinishRequested = finish;
        if (!finish)
            await capture.Cancel.CancelAsync();
        capture.Complete();
        string? outcome = await capture.Worker;
        capture.Dispose();
        if (capture.Kind == Mode.Manual)
            ManualChanged?.Invoke(false);
        if (outcome is not null)
            Report(outcome);
    }

    private async Task<string?> CaptureAsync(Capture capture)
    {
        CancellationToken token = capture.Cancel.Token;
        bool turn = false;
        bool heard = false;
        bool playbackPaused = false;
        int frames = 0;
        int silent = 0;
        int trial = 0;
        try
        {
            if (capture.Kind == Mode.Manual)
            {
                await BeginTurnAsync(token);
                turn = true;
            }
            await foreach (byte[] frame in capture.Frames.Reader.ReadAllAsync(token))
            {
                Playback? playback = Volatile.Read(ref _playback);
                if (capture.Kind == Mode.Automatic && playback is not null &&
                    (!capture.Options.ListenWhileSpeaking || !router.HasExternalAec))
                {
                    if (!turn && !playbackPaused)
                        await router.ExecuteAsync(VoiceSlot.Wake, new("reset"), token);
                    playbackPaused = true;
                    continue;
                }
                playbackPaused = false;
                try
                {
                    byte[] filtered = (await router.ExecuteAsync(VoiceSlot.Aec,
                        new("frame", Audio: frame, Reference: playback?.Reference()), token)).Audio ?? frame;
                    if (!turn)
                    {
                        bool woke = (await router.ExecuteAsync(VoiceSlot.Wake,
                            new("frame", Audio: filtered, Keyword: capture.Options.Keyword, Culture: capture.Options.Culture),
                            token)).Active == true;
                        if (capture.Kind == Mode.Test && (woke || ++trial >= 600))
                            return woke ? "已识别试读的唤醒词" : "未识别到唤醒词，请调整后再试。";
                        if (!woke)
                            continue;
                        Interrupt();
                        await BeginTurnAsync(token);
                        turn = true;
                        heard = false;
                        frames = silent = 0;
                        Report("已唤醒 · 请说话");
                        continue;
                    }
                    ++frames;
                    bool active = (await router.ExecuteAsync(VoiceSlot.Vad, new("frame", Audio: filtered), token)).Active == true;
                    heard |= active;
                    silent = active ? 0 : silent + 1;
                    await router.ExecuteAsync(VoiceSlot.Asr, new("frame", Audio: filtered), token);
                    if (capture.Kind == Mode.Manual && frames < 1000 ||
                        capture.Kind == Mode.Automatic && frames < 750 && (heard ? silent < 38 : frames < 225))
                        continue;
                    await FinishTurnAsync(heard, token);
                    turn = false;
                    if (capture.Kind == Mode.Manual)
                        return heard ? null : "没有听到清晰语音，请重试。";
                    await router.ExecuteAsync(VoiceSlot.Wake, new("reset"), token);
                    Report($"等待唤醒 · {capture.Options.Keyword}");
                }
                catch (VoiceInterruptedException error)
                {
                    if (turn)
                        await router.EndAsync(VoiceSlot.Asr);
                    turn = false;
                    if (capture.Kind == Mode.Manual)
                        return error.Message;
                    await router.ExecuteAsync(VoiceSlot.Wake, new("reset"), token);
                    Report(error.Message);
                }
            }
            if (capture.Failure is Exception failure)
                return $"麦克风连接中断。{failure.Message}";
            if (turn && capture.FinishRequested)
            {
                await FinishTurnAsync(heard, token);
                turn = false;
                if (!heard)
                    return "没有听到清晰语音，请重试。";
            }
            return null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return null; }
        catch (Exception error) { return error.Message; }
        finally
        {
            try
            {
                if (turn)
                    await router.EndAsync(VoiceSlot.Asr);
                if (capture.Kind != Mode.Manual)
                    await router.ExecuteAsync(VoiceSlot.Wake, new("reset"), CancellationToken.None);
            }
            catch (Exception error) { Report(error.Message); }
        }
    }

    private async Task BeginTurnAsync(CancellationToken cancellation)
    {
        await router.ExecuteAsync(VoiceSlot.Asr, new("reset"), cancellation);
        await router.BeginAsync(VoiceSlot.Asr, cancellation);
    }

    private async Task FinishTurnAsync(bool heard, CancellationToken cancellation)
    {
        EngineReply result = await router.ExecuteAsync(VoiceSlot.Asr, new(heard ? "finish" : "reset"), cancellation);
        await router.EndAsync(VoiceSlot.Asr);
        if (result.Text?.Trim() is { Length: > 0 } text)
            TextReady?.Invoke(text);
        else if (heard)
            Report("没有识别到文字，请重试。");
    }

    public Task SpeakAsync(string text, CancellationToken cancellation = default) =>
        SpeakAsync(text, _options, cancellation);

    public Task SpeakAsync(string text, VoiceOptions options, CancellationToken cancellation = default) =>
        Task.Run(() => SpeakCoreAsync(text, options, cancellation), cancellation);

    private async Task SpeakCoreAsync(string text, VoiceOptions options, CancellationToken cancellation)
    {
        await _speechGate.WaitAsync(cancellation);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        lock (_speechLock) { _speech = linked; }
        try
        {
            Playback marker = new();
            Volatile.Write(ref _playback, marker);
            foreach (string sentence in Sentences(text))
            {
                linked.Token.ThrowIfCancellationRequested();
                await router.BeginAsync(VoiceSlot.Tts, linked.Token);
                try
                {
                    EngineReply speech = await router.ExecuteAsync(VoiceSlot.Tts,
                        new("synthesize", Text: sentence, Speaker: options.Speaker), linked.Token);
                    if (speech.Audio is not { Length: > 0 } pcm)
                        continue;
                    byte[] audio = await router.ApplyEffectAsync(pcm, linked.Token);
                    await marker.PlayAsync(audio, linked.Token);
                }
                finally { await router.EndAsync(VoiceSlot.Tts); }
            }
        }
        finally
        {
            Volatile.Write(ref _playback, null);
            lock (_speechLock) { _speech = null; }
            _speechGate.Release();
        }
    }

    private static IEnumerable<string> Sentences(string text)
    {
        foreach (string sentence in Regex.Split(text, "(?<=[。！？；.!?;\n])"))
        {
            string remaining = sentence.Trim();
            while (remaining.Length > 0)
            {
                int length = Math.Min(remaining.Length, 160);
                if (char.IsHighSurrogate(remaining[length - 1]) && length < remaining.Length)
                    --length;
                yield return remaining[..length];
                remaining = remaining[length..];
            }
        }
    }

    public void Interrupt()
    {
        lock (_speechLock) { _speech?.Cancel(); }
    }

    private void Report(string text) => Status?.Invoke(text);

    public async ValueTask DisposeAsync()
    {
        await _transition.WaitAsync();
        try
        {
            _disposed = true;
            Interrupt();
            await StopAsync(finish: false);
            await _speechGate.WaitAsync();
            _speechGate.Release();
        }
        finally { _transition.Release(); }
    }

    private sealed class Capture : IDisposable
    {
        public Channel<byte[]> Frames { get; } = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true
        });
        public CancellationTokenSource Cancel { get; } = new();
        public WaveInEvent Device { get; } = new()
        {
            DeviceNumber = -1, WaveFormat = Pcm, BufferMilliseconds = 20, NumberOfBuffers = 4
        };
        public Mode Kind { get; }
        public VoiceOptions Options { get; }
        public Task<string?> Worker { get; set; } = Task.FromResult<string?>(null);
        public Exception? Failure { get; private set; }
        public volatile bool FinishRequested;
        private byte[] _frame = new byte[VoiceAudio.FrameBytes];
        private int _filled;

        public Capture(Mode mode, VoiceOptions options)
        {
            Kind = mode;
            Options = options;
            Device.DataAvailable += Receive;
            Device.RecordingStopped += (_, stopped) =>
            {
                Failure = stopped.Exception;
                Frames.Writer.TryComplete();
            };
        }

        private void Receive(object? _, WaveInEventArgs e)
        {
            int cursor = 0;
            while (cursor < e.BytesRecorded)
            {
                int copy = Math.Min(_frame.Length - _filled, e.BytesRecorded - cursor);
                Buffer.BlockCopy(e.Buffer, cursor, _frame, _filled, copy);
                cursor += copy;
                _filled += copy;
                if (_filled != _frame.Length)
                    continue;
                Frames.Writer.TryWrite(_frame);
                _frame = new byte[VoiceAudio.FrameBytes];
                _filled = 0;
            }
        }

        public void Complete()
        {
            Device.StopRecording();
            Frames.Writer.TryComplete();
        }

        public void Dispose()
        {
            Complete();
            Device.Dispose();
            Cancel.Dispose();
        }
    }

    private sealed class Playback
    {
        private readonly object _gate = new();
        private WaveOutEvent? _device;
        private byte[]? _audio;

        public byte[]? Reference()
        {
            lock (_gate)
            {
                if (_device is null || _audio is null)
                    return null;
                int offset = (int)Math.Clamp(_device.GetPosition(), 0, _audio.Length) & ~1;
                byte[] reference = new byte[VoiceAudio.FrameBytes];
                Buffer.BlockCopy(_audio, offset, reference, 0, Math.Min(reference.Length, _audio.Length - offset));
                return reference;
            }
        }

        public async Task PlayAsync(byte[] pcm, CancellationToken cancellation)
        {
            using RawSourceWaveStream stream = new(new MemoryStream(pcm, writable: false), Pcm);
            using WaveOutEvent device = new() { DesiredLatency = 100 };
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            device.PlaybackStopped += (_, stopped) =>
            {
                if (stopped.Exception is Exception error)
                    completion.TrySetException(error);
                else
                    completion.TrySetResult();
            };
            device.Init(stream);
            lock (_gate) { _device = device; _audio = pcm; }
            try
            {
                device.Play();
                using CancellationTokenRegistration registration = cancellation.Register(device.Stop);
                await completion.Task.WaitAsync(cancellation);
            }
            finally { lock (_gate) { _device = null; _audio = null; } }
        }

    }
}
