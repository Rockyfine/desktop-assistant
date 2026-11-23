using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Speech.AudioFormat;
using System.Speech.Recognition;
using System.Speech.Synthesis;
using System.Text.Json;
using Vosk;

namespace DesktopAssistant.Core.Voice;

internal static class BuiltInEngines
{
    public static IVoiceEngine Create(VoiceSlot slot, string modelPath) => slot switch
    {
        VoiceSlot.Vad => new VoiceActivity(),
        VoiceSlot.Asr => new VoskSpeech(modelPath),
        VoiceSlot.Wake => new WindowsKeyword(),
        VoiceSlot.Tts => new WindowsVoice(),
        _ => new PassThrough()
    };

    public static string[] Speakers() =>
        ReadInstalledVoices().Select(voice => voice.Name).ToArray();

    public static string[] KeywordLanguages() => SpeechRecognitionEngine.InstalledRecognizers()
        .Select(info => info.Culture.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static VoiceInfo[] ReadInstalledVoices()
    {
        using SpeechSynthesizer synthesizer = new();
        return synthesizer.GetInstalledVoices().Where(voice => voice.Enabled).Select(voice => voice.VoiceInfo).ToArray();
    }

    private static SpeechAudioFormatInfo Format() =>
        new(VoiceAudio.SampleRate, AudioBitsPerSample.Sixteen, AudioChannel.Mono);

    private sealed class PassThrough : IVoiceEngine
    {
        public ValueTask<EngineReply> ExecuteAsync(EngineRequest request, CancellationToken cancellation) =>
            ValueTask.FromResult(new EngineReply(Audio: request.Audio));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class VoiceActivity : IVoiceEngine
    {
        public ValueTask<EngineReply> ExecuteAsync(EngineRequest request, CancellationToken cancellation)
        {
            if (request.Audio is not { Length: > 0 } audio)
                return ValueTask.FromResult(new EngineReply(Active: false));
            double sum = 0;
            for (int i = 0; i < audio.Length; i += sizeof(short))
            {
                double sample = BinaryPrimitives.ReadInt16LittleEndian(audio.AsSpan(i)) / 32768d;
                sum += sample * sample;
            }
            return ValueTask.FromResult(new EngineReply(Active: Math.Sqrt(sum / (audio.Length / 2)) >= 0.012));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class VoskSpeech(string modelPath) : IVoiceEngine
    {
        private Model? _model;
        private VoskRecognizer? _recognizer;
        private readonly List<string> _parts = [];

        public ValueTask<EngineReply> ExecuteAsync(EngineRequest request, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (request.Op == "probe")
            {
                EnsureModel();
                return ValueTask.FromResult(new EngineReply());
            }
            if (request.Op == "reset")
            {
                Reset();
                return ValueTask.FromResult(new EngineReply());
            }
            EnsureModel();
            _recognizer ??= new VoskRecognizer(_model, VoiceAudio.SampleRate);
            if (request.Op == "finish")
            {
                Save(_recognizer.FinalResult());
                string text = string.Join(" ", _parts);
                Reset();
                return ValueTask.FromResult(new EngineReply(Text: text));
            }
            if (request.Audio is { } audio && _recognizer.AcceptWaveform(audio, audio.Length))
                Save(_recognizer.Result());
            return ValueTask.FromResult(new EngineReply());
        }

        private void EnsureModel()
        {
            if (_model is not null)
                return;
            if (!File.Exists(Path.Combine(modelPath, "am", "final.mdl")))
                throw new VoiceUnavailableException("缺少普通话识别模型。请使用完整安装包，或按说明配置模型。");
            global::Vosk.Vosk.SetLogLevel(-1);
            _model = new Model(modelPath);
        }

        private void Save(string json)
        {
            using JsonDocument result = JsonDocument.Parse(json);
            if (result.RootElement.TryGetProperty("text", out JsonElement text) &&
                text.GetString() is { Length: > 0 } part)
                _parts.Add(part);
        }

        private void Reset()
        {
            _recognizer?.Dispose();
            _recognizer = null;
            _parts.Clear();
        }

        public ValueTask DisposeAsync()
        {
            Reset();
            _model?.Dispose();
            _model = null;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class WindowsKeyword : IVoiceEngine
    {
        private SpeechRecognitionEngine? _recognizer;
        private PushStream? _stream;
        private (string? Phrase, string? Culture) _configuration;
        private int _detected;
        private bool _running;

        public ValueTask<EngineReply> ExecuteAsync(EngineRequest request, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (request.Op == "reset")
            {
                Close();
                return ValueTask.FromResult(new EngineReply());
            }
            if (_recognizer is null || _configuration != (request.Keyword, request.Culture))
                Configure(request.Keyword ?? "", request.Culture ?? "");
            if (request.Op == "probe")
                return ValueTask.FromResult(new EngineReply());
            if (!_running)
            {
                _stream = new PushStream();
                _recognizer!.SetInputToAudioStream(_stream, Format());
                _recognizer.RecognizeAsync(RecognizeMode.Multiple);
                _running = true;
            }
            if (request.Audio is { } audio)
                _stream!.Push(audio);
            return ValueTask.FromResult(new EngineReply(Active: Interlocked.Exchange(ref _detected, 0) == 1));
        }

        private void Configure(string keyword, string culture)
        {
            Close();
            if (string.IsNullOrWhiteSpace(keyword) || keyword.Length > 48 || keyword.Any(char.IsControl))
                throw new ArgumentException("请输入 1–48 个字符的唤醒词。");
            RecognizerInfo? info = SpeechRecognitionEngine.InstalledRecognizers().FirstOrDefault(item =>
                item.Culture.Name.Equals(culture, StringComparison.OrdinalIgnoreCase));
            if (info is null)
                throw new VoiceUnavailableException($"Windows 尚未安装 {culture} 识别器，可继续使用按键说话。");
            var recognizer = new SpeechRecognitionEngine(info);
            try
            {
                var phrase = new GrammarBuilder { Culture = info.Culture };
                phrase.Append(keyword);
                recognizer.LoadGrammar(new Grammar(phrase));
                recognizer.SpeechRecognized += (sender, args) =>
                {
                    if (ReferenceEquals(sender, _recognizer) && args.Result.Confidence >= 0.65)
                        Interlocked.Exchange(ref _detected, 1);
                };
                _recognizer = recognizer;
                _configuration = (keyword, culture);
            }
            catch
            {
                recognizer.Dispose();
                throw;
            }
        }

        private void Close()
        {
            _stream?.Complete();
            _recognizer?.Dispose();
            _recognizer = null;
            _stream?.Dispose();
            _stream = null;
            _running = false;
            Interlocked.Exchange(ref _detected, 0);
        }

        public ValueTask DisposeAsync()
        {
            Close();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class WindowsVoice : IVoiceEngine
    {
        public async ValueTask<EngineReply> ExecuteAsync(EngineRequest request, CancellationToken cancellation)
        {
            if (request.Op == "reset")
                return new EngineReply();
            using SpeechSynthesizer synthesizer = new();
            VoiceInfo[] voices = synthesizer.GetInstalledVoices().Where(voice => voice.Enabled)
                .Select(voice => voice.VoiceInfo).ToArray();
            if (voices.Length == 0)
                throw new VoiceUnavailableException("Windows 尚未安装可用的朗读音色。");
            VoiceInfo voice = string.IsNullOrWhiteSpace(request.Speaker)
                ? voices.FirstOrDefault(item => item.Culture.Name == "zh-CN") ?? voices[0]
                : voices.FirstOrDefault(item => item.Name == request.Speaker)
                    ?? throw new VoiceUnavailableException("所选音色不可用。请在设置中选择其他音色。");
            synthesizer.SelectVoice(voice.Name);
            if (request.Op == "probe")
                return new EngineReply();
            using MemoryStream audio = new();
            synthesizer.SetOutputToAudioStream(audio, Format());
            TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            synthesizer.SpeakCompleted += (_, args) =>
            {
                if (args.Error is Exception error)
                    completion.TrySetException(error);
                else if (args.Cancelled)
                    completion.TrySetCanceled();
                else
                    completion.TrySetResult();
            };
            synthesizer.SpeakAsync(request.Text ?? "");
            try { await completion.Task.WaitAsync(cancellation); }
            finally
            {
                if (cancellation.IsCancellationRequested)
                    synthesizer.SpeakAsyncCancelAll();
            }
            return new EngineReply(Audio: audio.ToArray());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class PushStream : Stream
    {
        private readonly BlockingCollection<byte[]> _queue = new(48);
        private byte[] _current = [];
        private int _offset;
        private long _position;

        public void Push(byte[] data)
        {
            if (!_queue.TryAdd(data))
            {
                _queue.TryTake(out _);
                _queue.TryAdd(data);
            }
        }

        public void Complete() => _queue.CompleteAdding();

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = 0;
            while (read < count)
            {
                if (_offset == _current.Length)
                {
                    if (_queue.IsCompleted || !_queue.TryTake(out byte[]? next, Timeout.Infinite))
                        break;
                    _current = next;
                    _offset = 0;
                }
                int copy = Math.Min(count - read, _current.Length - _offset);
                Buffer.BlockCopy(_current, _offset, buffer, offset + read, copy);
                read += copy;
                _offset += copy;
            }
            _position += read;
            return read;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        // System.Speech snapshots Length before reading. A live stream ends when Read returns zero.
        public override long Length => long.MaxValue;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin)
        {
            if ((origin == SeekOrigin.Current && offset == 0) || (origin == SeekOrigin.Begin && offset == _position))
                return _position;
            throw new NotSupportedException();
        }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _queue.Dispose();
            base.Dispose(disposing);
        }
    }
}
