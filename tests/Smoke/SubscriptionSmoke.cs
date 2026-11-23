using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using DesktopAssistant.Core;

internal static class SubscriptionSmoke
{
    public static async Task CheckAsync(string scratch)
    {
        string directory = Path.Combine(scratch, "subscription");
        Fixture transport = new();
        using ChatGptSubscription subscription = new(directory, transport);
        using var loginCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        CancellationToken token = loginCancellation.Token;
        SubscriptionAccount first = await subscription.SignInAsync(null, "桌面助手", false,
            flow => transport.Accept(flow, "alpha", "fixture-alpha", "alpha@example.test", true, viaBrowser: true), token);
        Assert(first is { SignedIn: true, PlanEnabled: true }, "initial sign-in and grant");
        var catalog = await Task.WhenAll(subscription.ListModelsAsync(first.Id, token), subscription.ListModelsAsync(first.Id, token));
        Assert(catalog[0].Single() == new SubscriptionOption("eligible-model", "账号可用模型"), "account-specific model discovery");
        Assert(transport.Refreshes == 1, "concurrent requests share rotating refresh");
        var selection = new SubscriptionModel(first.Id, "eligible-model", "账号可用模型");
        var message = new Message("user", "请安排日程", DateTimeOffset.UtcNow);
        ChatReply answer = await subscription.ReplyAsync(selection, [message], DateTimeOffset.Now, token);
        Assert(answer.Draft?.Title == "测试日程", "subscription request creates a draft via namespace");
        await subscription.TestAsync(selection, token);
        transport.InterruptNextResponse = true;
        try
        {
            await subscription.TestAsync(selection, token);
            throw new Exception("accepted subscription stream without response.completed");
        }
        catch (EndOfStreamException) { }

        SubscriptionAccount second = await subscription.SignInAsync(null, "桌面助手", false,
            flow => transport.Accept(flow, "beta", "fixture-beta", "beta@example.test", false), token);
        Assert(second is { SignedIn: true, PlanEnabled: false } && second.Id != first.Id, "distinct identity-only account");
        try
        {
            await subscription.ListModelsAsync(second.Id, token);
            throw new Exception("allowed inference credentials before subscription consent");
        }
        catch (InvalidOperationException error) when (error.Message.Contains("尚未启用")) { }
        Assert(await subscription.SignOutAsync(first.Id, token), "remote revocation confirmed");
        byte[] encrypted = File.ReadAllBytes(Path.Combine(directory, "chatgpt-accounts.dat"));
        Assert(!Encoding.UTF8.GetString(encrypted).Contains("token-beta"), "vault encryption");
        using ChatGptSubscription reopened = new(directory, new Fixture());
        Assert(reopened.Accounts().Count == 2 && reopened.Accounts().Single(account => account.Id == first.Id).SignedIn == false,
            "signed-out registration and other account survive restart");
        SubscriptionAccount reused = await subscription.SignInAsync(first.Id, "桌面助手", false,
            flow => transport.Accept(flow, "alpha", "fixture-alpha", "alpha@example.test", true, existing: true), token);
        Assert(reused.Id == first.Id, "reauthorization retains registration");

        Console.WriteLine("ChatGPT login callbacks, encrypted accounts, renewal, model list, Responses and sign-out OK.");
    }

    private static void Assert(bool condition, string feature)
    {
        if (!condition)
            throw new InvalidOperationException("Failed subscription check: " + feature);
    }

    private sealed class Fixture : HttpMessageHandler
    {
        private readonly RSA _key = RSA.Create(2048);
        private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
        private Grant? _grant;
        private string? _device;
        private int _refreshes;
        public int Refreshes => _refreshes;
        public bool InterruptNextResponse { get; set; }

        public void Accept(ChatGptSignIn flow, string subject, string client, string email, bool enablePlan,
            bool viaBrowser = false, bool existing = false)
        {
            var query = HttpUtility.ParseQueryString(flow.BrowserUrl.Query);
            Assert(flow.BrowserUrl.Host == "auth.openai.com", "browser pinned to OpenAI");
            Assert(query["client_id"] == (existing ? client : "dynamic_agent_client"), "correct OAuth client");
            Assert(existing ? query["agent_name_hint"] is null : query["agent_name_hint"] == "桌面助手", "own app registration identity");
            Assert(query["code_challenge_method"] == "S256" && query["resource"] == "https://api.openai.com/v1", "PKCE and resource");
            _device ??= query["ext_agent_host_id"];
            Assert(_device?.StartsWith("urn:uuid:") == true && _device == query["ext_agent_host_id"], "stable host ID");
            string redirect = query["redirect_uri"]!;
            string stable = redirect + "?code=fixture-code&state=" + Uri.EscapeDataString(query["state"]!);
            Assert(new Uri(redirect) is { IsLoopback: true, AbsolutePath: "/auth/callback" }, "loopback callback");
            _grant = new Grant(subject, client, email, query["nonce"]!, query["code_challenge"]!, redirect, enablePlan);
            try
            {
                flow.SubmitCallback(redirect + "?code=fixture-code&state=wrong");
                throw new Exception("accepted OAuth callback with wrong state");
            }
            catch (ArgumentException) { }
            string callback = existing ? stable : stable + "&client_id=" + client;
            if (viaBrowser)
            {
                using HttpClient browser = new(new HttpClientHandler { UseProxy = false });
                string html = browser.GetStringAsync(callback).GetAwaiter().GetResult();
                Assert(html.Contains("已收到授权回调"), "automatic browser callback");
            }
            else
                flow.SubmitCallback(callback);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path == "/.well-known/jwks.json")
            {
                RSAParameters key = _key.ExportParameters(false);
                return Json(new { keys = new[] { new { kid = "fixture", kty = "RSA", n = Encode(key.Modulus!), e = Encode(key.Exponent!) } } });
            }
            if (path == "/api/accounts/oauth/token")
            {
                var form = HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(cancellationToken));
                Assert(form["resource"] == ChatGptSubscription.Api, "token resource");
                if (form["grant_type"] == "refresh_token")
                {
                    Assert(form["client_id"] == "fixture-alpha" && form["refresh_token"] == "refresh-alpha", "refresh uses issued registration");
                    Interlocked.Increment(ref _refreshes);
                    return Json(new
                    {
                        token_type = "Bearer", access_token = "token-alpha-renewed", refresh_token = "refresh-alpha-renewed",
                        expires_in = 3600, scope = "chatgpt.tokens.use.direct"
                    });
                }
                Grant grant = _grant!;
                Assert(form["client_id"] == grant.Client && form["redirect_uri"] == grant.Redirect &&
                    form["code"] == "fixture-code" &&
                    Encode(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"]!))) == grant.Challenge,
                    "callback code exchange");
                string header = Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", kid = "fixture" }));
                string claims = Encode(JsonSerializer.SerializeToUtf8Bytes(new
                {
                    iss = "https://auth.openai.com", aud = grant.Client, sub = grant.Subject, nonce = grant.Nonce,
                    exp = _now.AddMinutes(5).ToUnixTimeSeconds(), email = grant.Email
                }));
                string signed = header + "." + claims;
                string jwt = signed + "." + Encode(_key.SignData(Encoding.ASCII.GetBytes(signed),
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
                return Json(new
                {
                    token_type = "Bearer", access_token = "token-" + grant.Subject, refresh_token = "refresh-" + grant.Subject,
                    id_token = jwt, expires_in = 45, scope = grant.EnablePlan ? "chatgpt.tokens.use.direct" : "openid email"
                });
            }
            if (path == "/api/accounts/oauth/revoke")
            {
                var form = HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(cancellationToken));
                Assert(form["client_id"] == "fixture-alpha" && form["token"] == "refresh-alpha-renewed",
                    "revoke the most recent refresh credential");
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            Assert(request.RequestUri.Host == "api.openai.com" && request.Headers.Authorization?.Parameter == "token-alpha-renewed",
                "use refreshed plan OAuth at official API");
            if (path == "/v1/models")
            {
                return Json(new
                {
                    models = new[]
                    {
                        new { slug = "eligible-model", display_name = "账号可用模型", visibility = "list" },
                        new { slug = "hidden-model", display_name = "Hidden model", visibility = "hidden" }
                    }
                });
            }
            Assert(path == "/v1/responses", "subscription Responses route");
            using JsonDocument prompt = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            JsonElement data = prompt.RootElement;
            Assert(data.GetProperty("stream").GetBoolean() && !data.GetProperty("store").GetBoolean() &&
                data.GetProperty("model").GetString() == "eligible-model" && !data.TryGetProperty("max_output_tokens", out _),
                "official streaming payload");
            bool tools = data.TryGetProperty("tools", out JsonElement toolList);
            if (tools)
                Assert(toolList[0].GetProperty("type").GetString() == "namespace", "subscription function tool namespace");
            if (InterruptNextResponse)
            {
                InterruptNextResponse = false;
                return EventStream("""{"type":"response.output_text.delta","delta":"partial"}""");
            }
            return EventStream(tools
                ? """{"type":"response.completed","response":{"output":[{"type":"function_call","namespace":"calendar","name":"propose_appointment","arguments":"{\"title\":\"测试日程\",\"date\":\"2030-01-04\",\"start\":\"09:30\"}"}]}}"""
                : """{"type":"response.completed","response":{"output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"OK"}]}]}}""");
        }

        private static HttpResponseMessage Json(object value) =>
            new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

        private static HttpResponseMessage EventStream(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent($"data: {json}\r\n\r\n", Encoding.UTF8, "text/event-stream") };

        private static string Encode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('/', '_').Replace('+', '-');

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _key.Dispose();
            base.Dispose(disposing);
        }

        private sealed record Grant(string Subject, string Client, string Email, string Nonce, string Challenge,
            string Redirect, bool EnablePlan);
    }
}
