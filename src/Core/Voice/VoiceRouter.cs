namespace DesktopAssistant.Core.Voice;

public sealed class VoiceRouter(string modelDirectory) : IAsyncDisposable
{
    private sealed class Route
    {
        public readonly SemaphoreSlim Gate = new(1);
        public IVoiceEngine? Engine;
        public string Id = "builtin";
        public (IVoiceEngine Engine, string Id)? Pending;
        public bool Busy;
    }

    private readonly Dictionary<VoiceSlot, Route> _routes = Enum.GetValues<VoiceSlot>()
        .ToDictionary(slot => slot, _ => new Route());

    public event Action<SlotState>? Changed;
    public event Action<string>? Notice;

    public static string[] Speakers() => BuiltInEngines.Speakers();
    public static string[] KeywordLanguages() => BuiltInEngines.KeywordLanguages();
    internal bool HasExternalAec => Volatile.Read(ref _routes[VoiceSlot.Aec].Id) != "builtin";

    public async Task<SlotState> SelectAsync(VoiceSlot slot, VoicePlugin? plugin, VoiceOptions options,
        CancellationToken cancellation = default)
    {
        Route route = _routes[slot];
        string id = plugin?.Id ?? "builtin";
        IVoiceEngine candidate = plugin is null
            ? BuiltInEngines.Create(slot, modelDirectory)
            : await PluginEngine.ConnectAsync(plugin, slot, cancellation);
        bool adopted = false;
        try
        {
            if (plugin is not null || slot is VoiceSlot.Asr or VoiceSlot.Tts || slot == VoiceSlot.Wake && options.AlwaysListen)
                await candidate.ExecuteAsync(Options("probe", options), cancellation);
            await route.Gate.WaitAsync(cancellation);
            try
            {
                if (route.Pending is { } previous)
                    await previous.Engine.DisposeAsync();
                route.Pending = null;
                if (route.Busy && slot is VoiceSlot.Asr or VoiceSlot.Tts)
                    route.Pending = (candidate, id);
                else
                    await ReplaceAsync(route, candidate, id);
                adopted = true;
                SlotState state = State(slot, route);
                Changed?.Invoke(state);
                return state;
            }
            finally { route.Gate.Release(); }
        }
        finally
        {
            if (!adopted)
                await candidate.DisposeAsync();
        }
    }

    internal async Task BeginAsync(VoiceSlot slot, CancellationToken cancellation)
    {
        Route route = _routes[slot];
        await route.Gate.WaitAsync(cancellation);
        try { route.Busy = true; }
        finally { route.Gate.Release(); }
    }

    internal async Task EndAsync(VoiceSlot slot)
    {
        Route route = _routes[slot];
        await route.Gate.WaitAsync();
        try
        {
            route.Busy = false;
            if (route.Pending is { } pending)
            {
                route.Pending = null;
                await ReplaceAsync(route, pending.Engine, pending.Id);
                Changed?.Invoke(State(slot, route));
            }
        }
        finally { route.Gate.Release(); }
    }

    public async Task ProbeAsync(VoiceSlot slot, VoiceOptions options, CancellationToken cancellation = default) =>
        _ = await ExecuteAsync(slot, Options("probe", options), cancellation);

    internal async ValueTask<EngineReply> ExecuteAsync(VoiceSlot slot, EngineRequest request,
        CancellationToken cancellation)
    {
        Route route = _routes[slot];
        await route.Gate.WaitAsync(cancellation);
        try
        {
            route.Engine ??= BuiltInEngines.Create(slot, modelDirectory);
            try
            {
                return await route.Engine.ExecuteAsync(request, cancellation);
            }
            catch (Exception failure) when (route.Id != "builtin" && failure is not OutOfMemoryException)
            {
                string notice = $"{slot} 插件未完成请求，已切回内置引擎。";
                await ReplaceAsync(route, BuiltInEngines.Create(slot, modelDirectory), "builtin");
                Changed?.Invoke(State(slot, route));
                Notice?.Invoke(notice);
                cancellation.ThrowIfCancellationRequested();
                if (slot == VoiceSlot.Asr && route.Busy)
                    throw new VoiceInterruptedException(notice + " 请重新说话。");
                return await route.Engine!.ExecuteAsync(request, cancellation);
            }
        }
        finally { route.Gate.Release(); }
    }

    public async Task<byte[]> SynthesizeAsync(string text, VoiceOptions options, CancellationToken cancellation = default)
    {
        await BeginAsync(VoiceSlot.Tts, cancellation);
        try
        {
            EngineReply reply = await ExecuteAsync(VoiceSlot.Tts,
                Options("synthesize", options) with { Text = text }, cancellation);
            return reply.Audio ?? [];
        }
        finally { await EndAsync(VoiceSlot.Tts); }
    }

    internal async Task<byte[]> ApplyEffectAsync(byte[] pcm, CancellationToken cancellation) =>
        (await ExecuteAsync(VoiceSlot.Fx, new("effect", Audio: pcm), cancellation)).Audio ?? pcm;

    private static EngineRequest Options(string op, VoiceOptions options) => new(op,
        Keyword: options.Keyword, Culture: options.Culture, Speaker: options.Speaker);

    private static SlotState State(VoiceSlot slot, Route route) =>
        new(slot, route.Id, route.Pending?.Id, route.Id != "builtin");

    private static async ValueTask ReplaceAsync(Route route, IVoiceEngine engine, string id)
    {
        IVoiceEngine? old = route.Engine;
        route.Engine = engine;
        route.Id = id;
        if (old is not null)
            await old.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (Route route in _routes.Values)
        {
            await route.Gate.WaitAsync();
            try
            {
                if (route.Pending is { } pending)
                    await pending.Engine.DisposeAsync();
                if (route.Engine is { } engine)
                    await engine.DisposeAsync();
            }
            finally { route.Gate.Release(); }
        }
    }
}
