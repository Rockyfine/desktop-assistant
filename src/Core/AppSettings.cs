using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DesktopAssistant.Core;

public static class AppIdentity
{
    public const string Id = "DesktopAssistant.56ec080e";
    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Id);
    public static string ModelDirectory => Path.Combine(AppContext.BaseDirectory, "models", "vosk-model-small-cn-0.22");
}

public sealed record Branding(string Name, string InstallerName, string Version, string? ReleaseRepository = null)
{
    public static Branding Load() => JsonSerializer.Deserialize<Branding>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "branding.json")), JsonDefaults.Options)
        ?? throw new InvalidDataException("程序品牌配置无效。");
}

public sealed record ProviderSettings(string Endpoint = "", string Model = "")
{
    public Uri ChatUri()
    {
        if (!Uri.TryCreate(Endpoint.Trim(), UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != "https" && (uri.Scheme != "http" || !uri.IsLoopback)) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
        {
            throw new ArgumentException("请输入 HTTPS 服务地址，或本机 HTTP 地址，例如 http://127.0.0.1:11434/v1。");
        }
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/chat/completions");
    }
}

public sealed record VoiceOptions(
    string Keyword = "你好助手", string Culture = "zh-CN", string Speaker = "",
    bool AlwaysListen = false, bool SpeakReplies = true, bool ListenWhileSpeaking = false);

public sealed record SubscriptionModel(Guid AccountId, string Model, string Label);

public sealed record AppSettings(
    string? DisplayName = null, ProviderSettings? Provider = null, VoiceOptions? Voice = null,
    Dictionary<string, string>? Engines = null, Dictionary<string, string>? TrustedPlugins = null,
    SubscriptionModel? Subscription = null, bool PlanWelcomeShown = false);

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
}

public sealed class SettingsFile(string directory)
{
    private string PathToSettings => Path.Combine(directory, "settings.json");

    public AppSettings Read()
    {
        return File.Exists(PathToSettings)
            ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(PathToSettings), JsonDefaults.Options)
                ?? new AppSettings()
            : new AppSettings();
    }

    public void Write(AppSettings settings)
    {
        Directory.CreateDirectory(directory);
        string temporary = PathToSettings + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonDefaults.Options));
        File.Move(temporary, PathToSettings, overwrite: true);
    }
}

public static class Credentials
{
    private const uint Generic = 1;
    private const int NotFound = 1168;

    private static string Target(ProviderSettings provider)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(provider.ChatUri().AbsoluteUri));
        return AppIdentity.Id + "/provider/" + Convert.ToHexString(digest);
    }

    public static string? Read(ProviderSettings provider)
    {
        if (!CredRead(Target(provider), Generic, 0, out IntPtr pointer))
        {
            int error = Marshal.GetLastWin32Error();
            return error == NotFound ? null : throw new Win32Exception(error);
        }
        try
        {
            Credential credential = Marshal.PtrToStructure<Credential>(pointer);
            return Marshal.PtrToStringUni(credential.Blob, checked((int)credential.BlobSize / sizeof(char)));
        }
        finally { CredFree(pointer); }
    }

    public static void Write(ProviderSettings provider, string secret)
    {
        if (secret.Length > 1200)
            throw new ArgumentException("API 密钥过长。");
        IntPtr pointer = Marshal.StringToCoTaskMemUni(secret);
        try
        {
            Credential credential = new()
            {
                Type = Generic, TargetName = Target(provider), UserName = "API key",
                Blob = pointer, BlobSize = (uint)secret.Length * sizeof(char), Persist = 2
            };
            if (!CredWrite(ref credential, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Marshal.ZeroFreeCoTaskMemUnicode(pointer); }
    }

    public static void Remove(ProviderSettings provider)
    {
        if (!CredDelete(Target(provider), Generic, 0) && Marshal.GetLastWin32Error() != NotFound)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint BlobSize;
        public IntPtr Blob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref Credential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr credential);
}
