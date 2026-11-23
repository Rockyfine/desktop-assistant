using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using static DesktopAssistant.Core.ChatGptSubscription;

namespace DesktopAssistant.Core;

internal static class ChatGptResponses
{
    public static async Task<ChatReply> CompleteAsync(HttpClient http, string access, string model,
        IReadOnlyList<Message> history, string? instructions, CancellationToken cancellation)
    {
        using HttpRequestMessage request = Authorized(HttpMethod.Post, Api + "/responses", access);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        var payload = new Dictionary<string, object>
        {
            ["model"] = model,
            ["input"] = history.TakeLast(30).Select(item => new { role = item.Role, content = item.Text }).ToArray(),
            ["store"] = false,
            ["stream"] = true
        };
        if (instructions is not null)
        {
            payload["instructions"] = instructions;
            payload["tools"] = new[] { new
            {
                type = "namespace",
                name = "calendar",
                description = "桌面助手的本地日程功能。",
                tools = new[] { new
                {
                    type = "function", name = ChatClient.DraftTool, description = ChatClient.DraftDescription,
                    parameters = ChatClient.AppointmentParameters, strict = false
                } }
            } };
        }
        request.Content = JsonContent.Create(payload);
        using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        if (!response.IsSuccessStatusCode)
            throw await ChatGptRequestException.FromResponseAsync(response, cancellation);
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellation);
        using StreamReader reader = new(stream);
        StringBuilder data = new();
        while (await reader.ReadLineAsync(cancellation) is { } line)
        {
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                data.AppendLine(line[5..].TrimStart());
                if (data.Length > 2 * 1024 * 1024)
                    throw new InvalidDataException("ChatGPT 返回的内容过大。");
                continue;
            }
            if (line.Length > 0 || data.Length == 0)
                continue;
            string json = data.ToString();
            data.Clear();
            if (json.Trim() == "[DONE]")
                break;
            using JsonDocument entry = JsonDocument.Parse(json);
            JsonElement frame = entry.RootElement;
            switch (Text(frame, "type"))
            {
                case "response.completed":
                    return ReadCompleted(frame.GetProperty("response"), allowDraft: instructions is not null);
                case "response.failed":
                    throw ChatGptRequestException.FromEvent(frame.GetProperty("response"), response);
                case "error":
                    throw ChatGptRequestException.FromEvent(frame, response);
                case "response.incomplete":
                    throw new InvalidDataException("ChatGPT 的回复未生成完整，请缩短问题或重试。");
            }
        }
        throw new EndOfStreamException("ChatGPT 的连接已中断，未收到完整回复。");
    }

    private static ChatReply ReadCompleted(JsonElement response, bool allowDraft)
    {
        if (!response.TryGetProperty("output", out JsonElement output) || output.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("ChatGPT 未返回可用的回复。");
        List<string> text = [];
        bool invalidDraft = false;
        foreach (JsonElement item in output.EnumerateArray())
        {
            switch (Text(item, "type"))
            {
                case "function_call" when allowDraft && Text(item, "name") == ChatClient.DraftTool
                    && Text(item, "namespace") is null or "calendar":
                    if (Text(item, "arguments") is string args && ChatClient.DraftFromArguments(args) is Appointment draft)
                        return new ChatReply(ChatClient.DraftSuccess, draft);
                    invalidDraft = true;
                    break;
                case "message" when Text(item, "role") == "assistant" && item.TryGetProperty("content", out JsonElement content)
                    && content.ValueKind == JsonValueKind.Array:
                    foreach (JsonElement part in content.EnumerateArray())
                    {
                        string? value = Text(part, "type") switch
                        {
                            "output_text" => Text(part, "text"),
                            "refusal" => Text(part, "refusal"),
                            _ => null
                        };
                        if (!string.IsNullOrWhiteSpace(value))
                            text.Add(value);
                    }
                    break;
            }
        }
        if (!allowDraft && text.Count == 0)
            throw new InvalidDataException("所选订阅模型未返回测试文字。");
        return new ChatReply(invalidDraft ? ChatClient.DraftInvalid : text.Count > 0
            ? string.Join("\n", text) : "ChatGPT 未返回文字。", null);
    }
}

internal sealed class ChatGptRequestException : HttpRequestException
{
    public string? Code { get; }

    private ChatGptRequestException(string? code, int? status, string? requestId) : base(MessageFor(code, status, requestId))
    {
        Code = code;
    }

    public static ChatGptRequestException FromEvent(JsonElement body, HttpResponseMessage response) =>
        new(ReadCode(body), null, RequestId(response));

    public static async Task<ChatGptRequestException> FromResponseAsync(HttpResponseMessage response, CancellationToken cancellation)
    {
        string? code = null;
        try
        {
            if (response.Content.Headers.ContentLength is null or <= 64_000)
            {
                await using Stream body = await response.Content.ReadAsStreamAsync(cancellation);
                byte[] buffer = new byte[64_001];
                int read = await body.ReadAtLeastAsync(buffer, buffer.Length, false, cancellation);
                if (read > 0 && read < buffer.Length)
                {
                    using JsonDocument result = JsonDocument.Parse(buffer.AsMemory(0, read));
                    code = ReadCode(result.RootElement);
                }
            }
        }
        catch (JsonException) { }
        return new ChatGptRequestException(code, (int)response.StatusCode, RequestId(response));
    }

    private static string? ReadCode(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
            return null;
        string? code = body.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.Object
            ? Text(error, "code") : Text(body, "code") ?? Text(body, "error");
        return Safe(code, 100);
    }

    private static string? RequestId(HttpResponseMessage response)
    {
        foreach (string header in new[] { "openai-request-id", "x-request-id" })
        {
            if (response.Headers.TryGetValues(header, out IEnumerable<string>? ids) && Safe(ids.FirstOrDefault(), 100) is string id)
                return id;
        }
        return null;
    }

    private static string? Safe(string? value, int max) =>
        value is { Length: > 0 } && value.Length <= max && value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')
            ? value : null;

    private static string MessageFor(string? code, int? status, string? requestId)
    {
        string detail = code switch
        {
            "subscription_sharing_user_not_eligible" => "当前账号或工作区无法使用 ChatGPT 订阅用量。",
            "subscription_sharing_usage_limit_exceeded" => "已达到 ChatGPT 方案或本应用的用量上限，请在 ChatGPT 的用量设置中查看。",
            "subscription_sharing_usage_unavailable" => "ChatGPT 暂时无法确认订阅用量，请稍后重试。",
            "subscription_sharing_unsupported_capability" => "所选订阅模型不支持此请求。可刷新模型列表并换一个模型。",
            "invalid_grant" => "ChatGPT 授权已过期，请重新登录。",
            _ when status == 401 => "ChatGPT 登录凭据未被接受，请重新登录或授权订阅用量。",
            _ when status == 403 => "当前账号、工作区或授权无法执行该请求。",
            _ when status == 429 => "ChatGPT 暂时拒绝请求，请在用量页面检查当前限制。",
            _ => "ChatGPT 未能完成请求。"
        };
        string[] metadata = [.. new string?[] { status.HasValue ? $"HTTP {status}" : null, code, requestId }.OfType<string>()];
        return metadata.Length == 0 ? detail : detail + "（" + string.Join(" · ", metadata) + "）";
    }
}
