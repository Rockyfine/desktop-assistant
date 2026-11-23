using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using DesktopAssistant.Core;
using DesktopAssistant.Core.Voice;

if (args.Length < 3)
    throw new ArgumentException("Smoke <scratch directory> <Chinese model directory> <published SoftGain directory>");

string scratch = Path.Combine(Path.GetFullPath(args[0]), Guid.NewGuid().ToString("N"));
CheckStorage(scratch);
await CheckModelAsync();
await SubscriptionSmoke.CheckAsync(scratch);
await UpdateSmoke.CheckAsync(scratch);
await CheckVoiceAsync(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), scratch);
Console.WriteLine("All smoke checks passed.");

static void CheckStorage(string scratch)
{
    SettingsFile settings = new(scratch);
    Check(settings.Read().DisplayName is null, "isolated settings");
    settings.Write(new AppSettings(DisplayName: "新名字", Voice: new VoiceOptions(Keyword: "你好助手")));
    settings.Write(settings.Read() with { DisplayName = "新的显示名" });
    Check(settings.Read() is { DisplayName: "新的显示名", Voice.Keyword: "你好助手" }, "rename preserves wake word");
    LocalStore store = new(scratch);
    DateTimeOffset now = DateTimeOffset.Now;
    store.AddMessage("a", new Message("user", "测试会话", now));
    store.AddMessage("a", new Message("assistant", "中文搜索词", now.AddSeconds(1)));
    Conversation conversation = store.Conversations("搜索词").Single();
    Check(store.Export(conversation, false).Contains("中文搜索词"), "Markdown export");
    using JsonDocument exported = JsonDocument.Parse(store.Export(conversation, true));
    Check(exported.RootElement.GetProperty("messages").GetArrayLength() == 2, "JSON export");
    store.DeleteConversation("a");
    Check(store.Messages("a").Count == 0, "history cascade");

    DateTimeOffset start = now.AddMinutes(4);
    DateOnly day = DateOnly.FromDateTime(start.LocalDateTime);
    TimeOnly time = new(start.Hour, start.Minute);
    Appointment item = new("event", day, time, null, "验收日程", "");
    store.SaveAppointment(item);
    Check(store.Appointments(day).Single() == item, "schedule read after write");
    Check(store.ClaimReminders(now).Single().Id == item.Id, "five-minute reminder");
    Check(new LocalStore(scratch).ClaimReminders(now).Count == 0, "reminder persists");
    store.SaveAppointment(item with { Done = true });
    Check(store.Appointments(day).Single().Done, "schedule edit");
    store.DeleteAppointment(item.Id);
    Check(store.Appointments(day).Count == 0, "schedule delete");
    Console.WriteLine("Settings, search, export, calendar and reminders OK.");
}

static async Task CheckModelAsync()
{
    var provider = new ProviderSettings("http://127.0.0.1:11434/v1", "local-test");
    Check(provider.ChatUri().PathAndQuery == "/v1/chat/completions", "provider route");
    try
    {
        _ = new ProviderSettings("http://example.com/v1").ChatUri();
        throw new Exception("accepted remote unencrypted provider");
    }
    catch (ArgumentException) { }
    DateTimeOffset now = DateTimeOffset.Now;
    Message[] history = [new("user", "明天下午三点开会", now)];
    using ChatClient client = new(new ModelFixture());
    await client.TestAsync(provider, null, CancellationToken.None);
    ChatReply answer = await client.ReplyAsync(provider, null, history, now, CancellationToken.None);
    Check(answer.Draft is { Title: "开会", Day: { Year: 2030 }, Start.Hour: 15 }, "valid draft");
    ChatReply fallback = await client.ReplyAsync(provider with { Model = "no-tools" }, null, history, now, CancellationToken.None);
    Check(fallback is { Text: "模型已连接", Draft: null }, "compatibility with a text-only model");
    Console.WriteLine("Model request, tool validation and text-only fallback OK.");
}

static async Task CheckVoiceAsync(string model, string sample, string scratch)
{
    await using VoiceRouter router = new(model);
    string pluginDirectory = Path.Combine(scratch, "soft-gain");
    Directory.CreateDirectory(pluginDirectory);
    foreach (string file in Directory.GetFiles(sample))
        File.Copy(file, Path.Combine(pluginDirectory, Path.GetFileName(file)));
    Console.WriteLine("Reading sample manifest.");
    VoicePlugin plugin = VoicePlugin.Read(Path.Combine(pluginDirectory, "plugin.json"));
    VoiceOptions options = new();
    SlotState selected = await router.SelectAsync(VoiceSlot.Fx, plugin, options);
    Check(selected.External, "sample plugin selected");
    Console.WriteLine("Sample plugin connected.");
    byte[] audio = new byte[VoiceAudio.FrameBytes];
    BinaryPrimitives.WriteInt16LittleEndian(audio, 12000);
    byte[] softened = await router.ApplyEffectAsync(audio, CancellationToken.None);
    Check(BinaryPrimitives.ReadInt16LittleEndian(softened) == 6600, "plugin changed audio");
    Console.WriteLine("Sample processed audio.");
    Process child = Process.GetProcessesByName("SoftGain").Single(process =>
        string.Equals(process.MainModule?.FileName, plugin.Executable, StringComparison.OrdinalIgnoreCase));
    using (child)
    {
        child.Kill();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(5));
        await child.WaitForExitAsync(deadline.Token);
    }
    Console.WriteLine("Sample process stopped.");
    byte[] recovered = await router.ApplyEffectAsync(audio, CancellationToken.None);
    Check(BinaryPrimitives.ReadInt16LittleEndian(recovered) == 12000, "fallback after sample process crash");
    Console.WriteLine("Effect recovered. Checking the synthesis boundary.");
    await router.BeginAsync(VoiceSlot.Tts, CancellationToken.None);
    SlotState pending = await router.SelectAsync(VoiceSlot.Tts, null, options);
    Check(pending.Pending == "builtin", "synthesis changes queue during a sentence");
    await router.EndAsync(VoiceSlot.Tts);
    Console.WriteLine("External audio effect, crash recovery and sentence boundary OK.");

    CancellationToken token = CancellationToken.None;
    byte[] spoken = await router.SynthesizeAsync("你好助手，今天天气很好。", options);
    Check(spoken.Length > VoiceAudio.FrameBytes && !spoken.AsSpan().StartsWith("RIFF"u8), "Windows TTS produces PCM");
    await router.ProbeAsync(VoiceSlot.Asr, options);
    foreach (byte[] frame in Frames(spoken))
        await router.ExecuteAsync(VoiceSlot.Asr, new("frame", Audio: frame), token);
    EngineReply transcription = await router.ExecuteAsync(VoiceSlot.Asr, new("finish"), token);
    Check(!string.IsNullOrWhiteSpace(transcription.Text), "Vosk transcribes Windows-generated Chinese speech offline");
    Console.WriteLine("Native Chinese TTS and offline Vosk ASR OK.");

    if (!VoiceRouter.KeywordLanguages().Contains("zh-CN", StringComparer.OrdinalIgnoreCase))
    {
        Console.WriteLine("Skipping the Windows wake grammar fixture because zh-CN is unavailable.");
        return;
    }
    byte[] keyword = await router.SynthesizeAsync("你好助手", options);
    Check(await DetectAsync(router, options, keyword), "Windows wake grammar recognizes a spoken configured phrase");
    Check(!await DetectAsync(router, options with { Keyword = "明天见面" }, keyword), "changed wake word rejects the old phrase");
    Console.WriteLine("Custom wake word and old-word rejection OK.");
}

static async Task<bool> DetectAsync(VoiceRouter router, VoiceOptions options, byte[] spoken)
{
    CancellationToken token = CancellationToken.None;
    await router.ExecuteAsync(VoiceSlot.Wake, new("reset"), token);
    await router.ProbeAsync(VoiceSlot.Wake, options);
    byte[] silence = new byte[VoiceAudio.FrameBytes * 110];
    foreach (byte[] frame in Frames(silence[..(VoiceAudio.FrameBytes * 12)].Concat(spoken).Concat(silence).ToArray()))
    {
        EngineReply reply = await router.ExecuteAsync(VoiceSlot.Wake,
            new("frame", Audio: frame, Keyword: options.Keyword, Culture: options.Culture), token);
        if (reply.Active == true)
            return true;
        await Task.Delay(20);
    }
    return false;
}

static IEnumerable<byte[]> Frames(byte[] pcm)
{
    for (int offset = 0; offset < pcm.Length; offset += VoiceAudio.FrameBytes)
    {
        byte[] frame = new byte[VoiceAudio.FrameBytes];
        Buffer.BlockCopy(pcm, offset, frame, 0, Math.Min(frame.Length, pcm.Length - offset));
        yield return frame;
    }
}

static void Check(bool condition, string description)
{
    if (!condition)
        throw new InvalidOperationException("Failed: " + description);
}

sealed class ModelFixture : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri?.AbsolutePath != "/v1/chat/completions")
            throw new InvalidOperationException("Unexpected API route.");
        using JsonDocument body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
        bool tools = body.RootElement.TryGetProperty("tools", out _);
        if (tools && body.RootElement.GetProperty("model").GetString() == "no-tools")
            return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("tools unsupported") };

        string response = tools
            ? """
                {"choices":[{"message":{"content":null,"tool_calls":[{"function":{"name":"propose_appointment","arguments":"{\"title\":\"开会\",\"date\":\"2030-04-12\",\"start\":\"15:00\"}"}}]}}]}
                """
            : """
                {"choices":[{"message":{"content":"模型已连接"}}]}
                """;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
    }
}
