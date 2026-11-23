using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Web;

namespace DesktopAssistant.Core;

public sealed record ChatGptSignIn(Uri BrowserUrl, Action<string> SubmitCallback);
internal sealed record AuthorizationCode(string Code, string ClientId);

internal sealed class ChatGptCallback : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly byte[] _state;
    private readonly string? _savedClientId;
    private readonly TaskCompletionSource<AuthorizationCode> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ChatGptCallback(string state, string? savedClientId)
    {
        _state = Encoding.UTF8.GetBytes(state);
        _savedClientId = savedClientId;
        _listener.Start();
        Uri = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/auth/callback");
    }

    public Uri Uri { get; }
    public Task<AuthorizationCode> Result => _result.Task;

    public void Accept(string address)
    {
        if (address.Length > 8192 || !Uri.TryCreate(address.Trim(), UriKind.Absolute, out Uri? callback) ||
            callback.GetLeftPart(UriPartial.Path) != Uri.AbsoluteUri || callback.Fragment.Length > 0)
            throw new ArgumentException("请粘贴浏览器地址栏中本次登录的完整回调地址。");
        var query = HttpUtility.ParseQueryString(callback.Query);
        string? Read(string field)
        {
            string[]? values = query.GetValues(field);
            return values switch
            {
                null => null,
                [string value] => value,
                _ => throw new ArgumentException("回调参数重复，请重新登录。")
            };
        }

        string? returnedState = Read("state");
        if (returnedState is null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(returnedState), _state))
            throw new ArgumentException("回调与本次登录不匹配。");
        if (Read("error") is string error)
        {
            _result.TrySetException(new InvalidOperationException(error == "access_denied"
                ? "浏览器中的授权已取消。"
                : "ChatGPT 未完成授权，请在浏览器中核对后重试。"));
            return;
        }
        string? code = Read("code");
        string? returnedClient = Read("client_id");
        if (_savedClientId is not null && returnedClient is not null && returnedClient != _savedClientId)
            throw new ArgumentException("回调中的客户端与所选账号不匹配。");
        string? client = _savedClientId ?? returnedClient;
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(client) || client == "dynamic_agent_client")
            throw new ArgumentException("回调缺少授权码或已注册的客户端。");
        _result.TrySetResult(new AuthorizationCode(code, client));
    }

    public async Task ListenAsync(CancellationToken cancellation)
    {
        try
        {
            while (!_result.Task.IsCompleted)
            {
                using TcpClient client = await _listener.AcceptTcpClientAsync(cancellation);
                using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                request.CancelAfter(TimeSpan.FromSeconds(2));
                try { await HandleAsync(client.GetStream(), request.Token); }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { }
                catch (IOException) { }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) { _result.TrySetException(error); }
    }

    private async Task HandleAsync(NetworkStream stream, CancellationToken cancellation)
    {
        byte[] buffer = new byte[8192];
        int filled = 0;
        int end = -1;
        while (filled < buffer.Length && end < 0)
        {
            int count = await stream.ReadAsync(buffer.AsMemory(filled), cancellation);
            if (count == 0)
                return;
            end = Array.IndexOf(buffer, (byte)'\n', filled, count);
            filled += count;
        }
        string line = end >= 0 ? Encoding.ASCII.GetString(buffer, 0, end).TrimEnd('\r') : "";
        string[] fields = line.Split(' ');
        bool accepted = false;
        if (fields is ["GET", string target, "HTTP/1.1" or "HTTP/1.0"] &&
            target.StartsWith("/auth/callback?", StringComparison.Ordinal))
        {
            try
            {
                Accept(Uri.GetLeftPart(UriPartial.Authority) + target);
                accepted = true;
            }
            catch (ArgumentException) { }
        }
        string status = accepted ? "200 OK" : "400 Bad Request";
        string message = accepted ? "已收到授权回调。请返回桌面助手查看结果。" : "这不是当前登录请求的有效回调。";
        byte[] body = Encoding.UTF8.GetBytes("<!doctype html><meta charset=utf-8><title>桌面助手</title><p>" + message + "</p>");
        byte[] headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\n"
            + $"Content-Length: {body.Length}\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(headers);
        await stream.WriteAsync(body);
    }

    public void Dispose() => _listener.Stop();
}
