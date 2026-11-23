using System.Security.Cryptography;
using System.Text.Json;

namespace DesktopAssistant.Core;

internal sealed record PlanTokens(string Access, string? Refresh, string IdToken, DateTimeOffset Expires,
    string Scopes, DateTimeOffset? EarliestRefresh);

internal sealed record PlanAccount(Guid Id, string ClientId, string Subject, string Label, string? Email,
    PlanTokens? Tokens);

public sealed record SubscriptionAccount(Guid Id, string Label, bool SignedIn, bool PlanEnabled);
public sealed record SubscriptionOption(string Id, string Label);

internal sealed record VaultContents(Guid DeviceId, PlanAccount[] Accounts);

internal sealed class SubscriptionVault(string directory)
{
    private static readonly byte[] Entropy = "DesktopAssistant/ChatGPT/1"u8.ToArray();
    private readonly string _file = Path.Combine(directory, "chatgpt-accounts.dat");

    public VaultContents Read()
    {
        if (!File.Exists(_file))
            return new VaultContents(Guid.Empty, []);
        byte[] plaintext = ProtectedData.Unprotect(File.ReadAllBytes(_file), Entropy, DataProtectionScope.CurrentUser);
        try
        {
            return JsonSerializer.Deserialize<VaultContents>(plaintext, JsonDefaults.Options)
                ?? throw new InvalidDataException("无法读取 ChatGPT 本地账号。");
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public void Write(VaultContents contents)
    {
        Directory.CreateDirectory(directory);
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(contents, JsonDefaults.Options);
        try
        {
            byte[] encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            string temporary = _file + ".tmp";
            File.WriteAllBytes(temporary, encrypted);
            File.Move(temporary, _file, overwrite: true);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
