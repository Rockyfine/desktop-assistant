using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DesktopAssistant.Core;

public sealed class ChatGptSubscription : IDisposable
{
    private const string Issuer = "https://auth.openai.com";
    internal const string Api = "https://api.openai.com/v1";
    private const string Authorize = Issuer + "/api/accounts/authorize";
    private const string Token = Issuer + "/api/accounts/oauth/token";
    private const string Revoke = Issuer + "/api/accounts/oauth/revoke";
    private const string Keys = Issuer + "/.well-known/jwks.json";
    private const string PlanPermission = "chatgpt.tokens.use.direct";
    private const string RequestedScopes = "openid profile email offline_access resource.invoke " + PlanPermission;
    private readonly SubscriptionVault _vault;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1);

    public ChatGptSubscription(string directory, HttpMessageHandler? transport = null)
    {
        _vault = new SubscriptionVault(directory);
        _http = new HttpClient(transport ?? new HttpClientHandler { AllowAutoRedirect = false })
        {
            MaxResponseContentBufferSize = 4 * 1024 * 1024,
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    public IReadOnlyList<SubscriptionAccount> Accounts() => _vault.Read().Accounts.Select(Summary).ToArray();

    public async Task<SubscriptionAccount> SignInAsync(Guid? accountId, string agentName, bool askForPlanConsent,
        Action<ChatGptSignIn> openBrowser, CancellationToken cancellation)
    {
        PlanAccount? existing;
        Guid device;
        await _gate.WaitAsync(cancellation);
        try
        {
            VaultContents vault = _vault.Read();
            if (vault.DeviceId == Guid.Empty)
            {
                vault = vault with { DeviceId = Guid.NewGuid() };
                _vault.Write(vault);
            }
            device = vault.DeviceId;
            existing = accountId.HasValue ? Find(vault, accountId.Value) : null;
        }
        finally { _gate.Release(); }

        string state = RandomValue();
        string nonce = RandomValue();
        string verifier = RandomValue();
        using ChatGptCallback callback = new(state, existing?.ClientId);
        var query = new Dictionary<string, string>
        {
            ["client_id"] = existing?.ClientId ?? "dynamic_agent_client",
            ["ext_agent_host_id"] = "urn:uuid:" + device.ToString("D"),
            ["response_type"] = "code",
            ["redirect_uri"] = callback.Uri.AbsoluteUri,
            ["scope"] = RequestedScopes,
            ["resource"] = Api,
            ["state"] = state,
            ["nonce"] = nonce,
            ["code_challenge_method"] = "S256",
            ["code_challenge"] = Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
        };
        if (existing is null)
            query["agent_name_hint"] = agentName;
        else
        {
            if (existing.Tokens?.IdToken is { Length: > 0 } retained)
                query["id_token_hint"] = retained;
            if (!string.IsNullOrWhiteSpace(existing.Email))
                query["login_hint"] = existing.Email;
            if (askForPlanConsent)
                query["prompt"] = "consent";
        }
        Uri browser = new(Authorize + "?" + string.Join("&", query.Select(pair =>
            Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))));

        AuthorizationCode result;
        using (var listener = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
        {
            Task listening = callback.ListenAsync(listener.Token);
            try
            {
                openBrowser(new ChatGptSignIn(browser, callback.Accept));
                result = await callback.Result.WaitAsync(cancellation);
            }
            finally
            {
                await listener.CancelAsync();
                await listening;
            }
        }

        using JsonDocument response = await TokenRequestAsync(new()
        {
            ["grant_type"] = "authorization_code", ["code"] = result.Code, ["client_id"] = result.ClientId,
            ["redirect_uri"] = callback.Uri.AbsoluteUri, ["code_verifier"] = verifier, ["resource"] = Api
        }, cancellation);
        PlanTokens tokens = ReadTokens(response.RootElement, null);
        string? idToken = Text(response.RootElement, "id_token");
        if (string.IsNullOrEmpty(idToken))
            throw new InvalidDataException("ChatGPT 登录缺少身份凭据。");
        (string subject, string? email) = await VerifyIdentityAsync(idToken, result.ClientId, nonce, cancellation);
        if (existing is not null && existing.Subject != subject)
            throw new InvalidOperationException("登录账号与所选账号不一致。要切换账号，请使用“添加账号”。");
        tokens = tokens with { IdToken = idToken };
        await _gate.WaitAsync(cancellation);
        try
        {
            VaultContents vault = _vault.Read();
            PlanAccount? registered = vault.Accounts.FirstOrDefault(item => item.ClientId == result.ClientId);
            if (registered is not null && registered.Subject != subject)
                throw new InvalidOperationException("ChatGPT 注册与已保存的账号身份不匹配。");
            Guid id = existing?.Id ?? registered?.Id ?? Guid.NewGuid();
            string label = registered?.Label ?? $"{email ?? "ChatGPT"} · {id.ToString("N")[..6]}";
            var account = new PlanAccount(id, result.ClientId, subject, label, email, tokens);
            Save(vault, account);
            return Summary(account);
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> SignOutAsync(Guid id, CancellationToken cancellation)
    {
        await _gate.WaitAsync(cancellation);
        try
        {
            VaultContents vault = _vault.Read();
            PlanAccount account = Find(vault, id);
            bool confirmed = true;
            if (account.Tokens?.Refresh is { Length: > 0 } refresh)
            {
                confirmed = false;
                for (int attempt = 0; attempt < 2; ++attempt)
                {
                    try
                    {
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                        deadline.CancelAfter(TimeSpan.FromSeconds(5));
                        using HttpResponseMessage revoked = await _http.PostAsync(Revoke, new FormUrlEncodedContent(new Dictionary<string, string>
                        {
                            ["token"] = refresh, ["token_type_hint"] = "refresh_token", ["client_id"] = account.ClientId
                        }), deadline.Token);
                        if (revoked.IsSuccessStatusCode)
                            confirmed = true;
                        if ((int)revoked.StatusCode < 500)
                            break;
                    }
                    catch (HttpRequestException) { }
                    catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { }
                    if (attempt == 0)
                        await Task.Delay(250, cancellation);
                }
            }
            Save(vault, account with { Tokens = null });
            return confirmed;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<SubscriptionOption>> ListModelsAsync(Guid account, CancellationToken cancellation)
    {
        string access = await AccessAsync(account, cancellation);
        using var request = Authorized(HttpMethod.Get, Api + "/models", access);
        using HttpResponseMessage response = await _http.SendAsync(request, cancellation);
        using JsonDocument catalog = await ReadResponseAsync(response, cancellation);
        if (!catalog.RootElement.TryGetProperty("models", out JsonElement models) || models.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("ChatGPT 未返回订阅模型列表。");
        return models.EnumerateArray()
            .Where(item => Text(item, "visibility") == "list" && !string.IsNullOrWhiteSpace(Text(item, "slug")))
            .Select(item => new SubscriptionOption(Text(item, "slug")!, Text(item, "display_name") ?? Text(item, "slug")!))
            .DistinctBy(model => model.Id, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task TestAsync(SubscriptionModel selection, CancellationToken cancellation)
    {
        var history = new[] { new Message("user", "请只回复 OK。", DateTimeOffset.UtcNow) };
        _ = await CompleteAsync(selection, history, null, cancellation);
    }

    public Task<ChatReply> ReplyAsync(SubscriptionModel selection, IReadOnlyList<Message> history,
        DateTimeOffset now, CancellationToken cancellation) =>
        CompleteAsync(selection, history, ChatClient.Instructions(now), cancellation);

    private async Task<ChatReply> CompleteAsync(SubscriptionModel selection, IReadOnlyList<Message> history,
        string? instructions, CancellationToken cancellation)
    {
        string access = await AccessAsync(selection.AccountId, cancellation);
        return await ChatGptResponses.CompleteAsync(_http, access, selection.Model, history, instructions, cancellation);
    }

    private async Task<string> AccessAsync(Guid id, CancellationToken cancellation)
    {
        await _gate.WaitAsync(cancellation);
        try
        {
            VaultContents vault = _vault.Read();
            PlanAccount account = Find(vault, id);
            if (account.Tokens is not { } tokens)
                throw new InvalidOperationException("请先在设置中登录该 ChatGPT 账号。");
            if (!PlanEnabled(tokens))
                throw new InvalidOperationException("该账号尚未启用 ChatGPT 方案用量。请在设置中授权订阅用量。");
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (tokens.Expires > now.AddMinutes(2) || tokens.Expires > now && tokens.EarliestRefresh > now)
                return tokens.Access;
            if (tokens.Refresh is not { Length: > 0 } refresh)
                throw new InvalidOperationException("ChatGPT 登录已过期，请在设置中重新登录。");
            try
            {
                using JsonDocument result = await TokenRequestAsync(new()
                {
                    ["grant_type"] = "refresh_token", ["client_id"] = account.ClientId,
                    ["refresh_token"] = refresh, ["resource"] = Api
                }, cancellation);
                PlanTokens replacement = ReadTokens(result.RootElement, tokens);
                Save(vault, account with { Tokens = replacement });
                if (!PlanEnabled(replacement))
                    throw new InvalidOperationException("ChatGPT 方案授权已变更。请在设置中重新授权订阅用量。");
                return replacement.Access;
            }
            catch (ChatGptRequestException error) when (error.Code is "invalid_grant" or "invalid_refresh_token" or
                "token_expired" or "refresh_token_expired" or "refresh_token_invalidated" or "refresh_token_reused")
            {
                Save(vault, account with { Tokens = null });
                throw new InvalidOperationException("ChatGPT 登录已失效，请在设置中重新登录。", error);
            }
        }
        finally { _gate.Release(); }
    }

    private async Task<JsonDocument> TokenRequestAsync(Dictionary<string, string> fields, CancellationToken cancellation)
    {
        using HttpResponseMessage response = await _http.PostAsync(Token, new FormUrlEncodedContent(fields), cancellation);
        return await ReadResponseAsync(response, cancellation);
    }

    private async Task<(string Subject, string? Email)> VerifyIdentityAsync(string jwt, string client, string nonce,
        CancellationToken cancellation)
    {
        string[] segments = jwt.Split('.');
        if (segments.Length != 3)
            throw new InvalidDataException("ChatGPT 返回无效的身份凭据。");
        using JsonDocument header = JsonDocument.Parse(Decode(segments[0]));
        using JsonDocument claims = JsonDocument.Parse(Decode(segments[1]));
        JsonElement data = claims.RootElement;
        if (Text(header.RootElement, "alg") != "RS256" || Text(header.RootElement, "kid") is not string keyId ||
            Text(data, "iss") != Issuer || !HasAudience(data, client) || Text(data, "nonce") != nonce ||
            Text(data, "sub") is not { Length: > 0 } subject ||
            Integer(data, "exp") is not long expires || expires <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            throw new InvalidDataException("ChatGPT 身份声明验证失败。请重试登录。");
        using HttpResponseMessage keyResponse = await _http.GetAsync(Keys, cancellation);
        using JsonDocument keys = await ReadResponseAsync(keyResponse, cancellation);
        JsonElement jwk = keys.RootElement.GetProperty("keys").EnumerateArray().FirstOrDefault(key =>
            Text(key, "kid") == keyId && Text(key, "kty") == "RSA" &&
            Text(key, "use") is null or "sig" && Text(key, "alg") is null or "RS256");
        if (Text(jwk, "n") is not string modulus || Text(jwk, "e") is not string exponent)
            throw new InvalidDataException("找不到 ChatGPT 身份签名的校验密钥。");
        using RSA rsa = RSA.Create();
        rsa.ImportParameters(new RSAParameters { Modulus = Decode(modulus), Exponent = Decode(exponent) });
        if (rsa.KeySize < 2048 || !rsa.VerifyData(Encoding.ASCII.GetBytes(segments[0] + "." + segments[1]),
                Decode(segments[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new InvalidDataException("ChatGPT 身份签名验证失败。");
        return (subject, Text(data, "email"));
    }

    private static bool HasAudience(JsonElement data, string client)
    {
        if (!data.TryGetProperty("aud", out JsonElement aud))
            return false;
        if (aud.ValueKind == JsonValueKind.String)
            return aud.GetString() == client;
        return aud.ValueKind == JsonValueKind.Array &&
            (aud.GetArrayLength() == 1 || Text(data, "azp") == client) &&
            aud.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String && item.GetString() == client);
    }

    private static PlanTokens ReadTokens(JsonElement data, PlanTokens? previous)
    {
        if (Text(data, "access_token") is not { Length: > 0 } access ||
            Integer(data, "expires_in") is not (>= 1 and <= 86_400) ||
            !string.Equals(Text(data, "token_type"), "Bearer", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("ChatGPT 返回的凭据不完整。");
        string scopes = Text(data, "scope") ?? previous?.Scopes ?? "";
        string? refresh = Text(data, "refresh_token");
        if (previous is not null && string.IsNullOrWhiteSpace(refresh))
            throw new InvalidDataException("ChatGPT 未返回更新后的续期凭据。");
        DateTimeOffset? earliest = Integer(data, "earliest_refresh_at") is long unix && unix is >= 0 and < 4_102_444_800
            ? DateTimeOffset.FromUnixTimeSeconds(unix)
            : DateTimeOffset.TryParse(Text(data, "earliest_refresh_at"), out DateTimeOffset instant) ? instant : null;
        return new PlanTokens(access, refresh, previous?.IdToken ?? "", DateTimeOffset.UtcNow.AddSeconds(Integer(data, "expires_in")!.Value),
            scopes, earliest);
    }

    private void Save(VaultContents vault, PlanAccount account) => _vault.Write(vault with
    {
        Accounts = [.. vault.Accounts.Where(item => item.Id != account.Id), account]
    });

    private static PlanAccount Find(VaultContents vault, Guid id) =>
        vault.Accounts.FirstOrDefault(item => item.Id == id)
        ?? throw new InvalidOperationException("找不到所选 ChatGPT 账号。请重新配置模型。");

    private static SubscriptionAccount Summary(PlanAccount account) =>
        new(account.Id, account.Label, account.Tokens is not null, account.Tokens is { } tokens && PlanEnabled(tokens));

    private static bool PlanEnabled(PlanTokens tokens) =>
        tokens.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(PlanPermission, StringComparer.Ordinal);

    private static string RandomValue() => Encode(RandomNumberGenerator.GetBytes(32));
    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] Decode(string value) =>
        Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));

    private static long? Integer(JsonElement item, string property) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(property, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number) ? number : null;

    internal static string? Text(JsonElement item, string property) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(property, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal static HttpRequestMessage Authorized(HttpMethod method, string url, string access)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        return request;
    }

    internal static async Task<JsonDocument> ReadResponseAsync(HttpResponseMessage response, CancellationToken cancellation)
    {
        if (!response.IsSuccessStatusCode)
            throw await ChatGptRequestException.FromResponseAsync(response, cancellation);
        return JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellation));
    }

    public void Dispose() => _http.Dispose();
}
