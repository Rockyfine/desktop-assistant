using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopAssistant.Core;

public sealed record ChatReply(string Text, Appointment? Draft);

public sealed class ChatClient : IDisposable
{
    internal static string Instructions(DateTimeOffset now) =>
        $"你是中文桌面助手。用户本地日期和时间为 {now:yyyy-MM-dd HH:mm zzz}。"
        + "为新日程提供标题、日期和时间。若要创建日程且时间完整，请调用 propose_appointment 生成草稿，等用户确认。"
        + "从未调用工具时不要说已经保存日程。";

    internal static readonly object AppointmentParameters = new
    {
        type = "object",
        properties = new
        {
            title = new { type = "string" },
            date = new { type = "string", description = "本地日期 yyyy-MM-dd" },
            start = new { type = "string", description = "开始时间 HH:mm，24 小时制" },
            end = new { type = "string", description = "可选结束时间 HH:mm" },
            note = new { type = "string" }
        },
        required = new[] { "title", "date", "start" },
        additionalProperties = false
    };

    internal const string DraftTool = "propose_appointment";
    internal const string DraftDescription = "生成一个待用户确认的本地日程草稿。";
    internal const string DraftSuccess = "已生成日程草稿，请核对后保存。";
    internal const string DraftInvalid = "日程草稿缺少有效的标题、日期或时间。请补充后重试。";

    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _client;
    private readonly HttpClient _localClient;

    public ChatClient(HttpMessageHandler? handler = null)
    {
        _client = CreateClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false });
        _localClient = handler is null
            ? CreateClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
            : _client;
    }

    private static HttpClient CreateClient(HttpMessageHandler handler) => new(handler)
    {
        Timeout = TimeSpan.FromSeconds(90),
        MaxResponseContentBufferSize = 2 * 1024 * 1024
    };

    public async Task TestAsync(ProviderSettings provider, string? key, CancellationToken cancellation)
    {
        using JsonDocument response = await SendAsync(provider, key, new
        {
            model = provider.Model.Trim(),
            messages = new[] { new WireMessage("user", "请回复 OK。") },
            stream = false
        }, cancellation);
        _ = ReadMessage(response);
    }

    public async Task<ChatReply> ReplyAsync(ProviderSettings provider, string? key,
        IReadOnlyList<Message> history, DateTimeOffset now, CancellationToken cancellation)
    {
        List<WireMessage> messages =
        [
            new("system", Instructions(now))
        ];
        messages.AddRange(history.TakeLast(30).Select(message => new WireMessage(message.Role, message.Text)));
        object[] tools = [new
        {
            type = "function",
            function = new
            {
                name = DraftTool,
                description = DraftDescription,
                parameters = AppointmentParameters
            }
        }];
        using JsonDocument response = await SendAsync(provider, key, new
        {
            model = provider.Model.Trim(), messages, stream = false, tools
        }, cancellation, new { model = provider.Model.Trim(), messages, stream = false });
        JsonElement message = ReadMessage(response);
        string text = ReadString(message, "content")?.Trim() ?? "";
        Appointment? draft = ReadDraft(message);
        if (draft is not null)
            return new ChatReply(DraftSuccess, draft);
        if (message.TryGetProperty("tool_calls", out JsonElement calls) &&
            calls.ValueKind == JsonValueKind.Array && calls.GetArrayLength() > 0)
            return new ChatReply(DraftInvalid, null);
        return new ChatReply(text.Length == 0 ? "模型未返回文字。" : text, null);
    }

    private async Task<JsonDocument> SendAsync(ProviderSettings provider, string? key, object payload,
        CancellationToken cancellation, object? simplerPayload = null)
    {
        if (string.IsNullOrWhiteSpace(provider.Model))
            throw new ArgumentException("请输入模型名称。");
        Uri uri = provider.ChatUri();
        using HttpResponseMessage first = await PostAsync(uri, key, payload, cancellation);
        if (simplerPayload is not null && (int)first.StatusCode is 400 or 422)
        {
            string body = await first.Content.ReadAsStringAsync(cancellation);
            if (body.Contains("tool", StringComparison.OrdinalIgnoreCase) ||
                body.Contains("function", StringComparison.OrdinalIgnoreCase))
            {
                using HttpResponseMessage retry = await PostAsync(uri, key, simplerPayload, cancellation);
                return await ParseAsync(retry, cancellation);
            }
        }
        return await ParseAsync(first, cancellation);
    }

    private async Task<HttpResponseMessage> PostAsync(Uri uri, string? key, object body, CancellationToken cancellation)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, uri)
        {
            Content = JsonContent.Create(body, options: Wire)
        };
        if (!string.IsNullOrWhiteSpace(key))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return await (uri.IsLoopback ? _localClient : _client).SendAsync(request, cancellation);
    }

    private static async Task<JsonDocument> ParseAsync(HttpResponseMessage response, CancellationToken cancellation)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"模型服务返回 HTTP {(int)response.StatusCode}。", null, response.StatusCode);
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellation);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellation);
    }

    private static JsonElement ReadMessage(JsonDocument response)
    {
        if (response.RootElement.ValueKind != JsonValueKind.Object ||
            !response.RootElement.TryGetProperty("choices", out JsonElement choices) ||
            choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0 ||
            choices[0].ValueKind != JsonValueKind.Object ||
            !choices[0].TryGetProperty("message", out JsonElement message) || message.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("模型响应缺少消息，请检查服务地址和模型名称。");
        }
        return message;
    }

    private static Appointment? ReadDraft(JsonElement message)
    {
        if (!message.TryGetProperty("tool_calls", out JsonElement calls) || calls.ValueKind != JsonValueKind.Array)
            return null;
        foreach (JsonElement call in calls.EnumerateArray())
        {
            if (call.ValueKind != JsonValueKind.Object || !call.TryGetProperty("function", out JsonElement fn) ||
                fn.ValueKind != JsonValueKind.Object || ReadString(fn, "name") != DraftTool ||
                ReadString(fn, "arguments") is not string arguments)
                continue;
            if (DraftFromArguments(arguments) is Appointment draft)
                return draft;
        }
        return null;
    }

    internal static Appointment? DraftFromArguments(string arguments)
    {
        try
        {
            using JsonDocument data = JsonDocument.Parse(arguments);
            JsonElement fields = data.RootElement;
            if (fields.ValueKind != JsonValueKind.Object ||
                !DateOnly.TryParseExact(ReadString(fields, "date"), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out DateOnly date) ||
                !TimeOnly.TryParseExact(ReadString(fields, "start"), "HH:mm", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out TimeOnly start))
                return null;
            string? endText = ReadString(fields, "end");
            TimeOnly? end = null;
            if (!string.IsNullOrEmpty(endText))
            {
                if (!TimeOnly.TryParseExact(endText, "HH:mm", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out TimeOnly parsed))
                    return null;
                end = parsed;
            }
            Appointment draft = new(Guid.NewGuid().ToString("N"), date, start, end,
                ReadString(fields, "title") ?? "", ReadString(fields, "note") ?? "");
            draft.Validate();
            return draft;
        }
        catch (JsonException) { }
        catch (ArgumentException) { }
        return null;
    }

    private static string? ReadString(JsonElement node, string key) =>
        node.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public void Dispose()
    {
        if (!ReferenceEquals(_localClient, _client))
            _localClient.Dispose();
        _client.Dispose();
    }

    private sealed record WireMessage(string Role, string Content);
}
