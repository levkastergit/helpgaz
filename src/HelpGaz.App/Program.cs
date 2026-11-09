using System.Security.Cryptography;
using System.Text;

namespace HelpGaz.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

internal static class AppPaths
{
    public static readonly string Root = Environment.GetEnvironmentVariable("HELPGAZ_DATA")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HelpGaz");
    public static readonly string Findings = Path.Combine(Root, "findings.json");
    public static readonly string Session = Path.Combine(Root, "session.bin");
}

/// <summary>Токен сайта хранится зашифрованным средствами Windows. Пароль на диск не записывается.</summary>
internal static class SessionStore
{
    public static (string Token, string Email)? Load()
    {
        try
        {
            if (!File.Exists(AppPaths.Session)) return null;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(AppPaths.Session), null, DataProtectionScope.CurrentUser);
            var parts = Encoding.UTF8.GetString(plain).Split('\n', 2);
            return parts.Length == 2 ? (parts[1], parts[0]) : null;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public static void Save(string token, string email)
    {
        Directory.CreateDirectory(AppPaths.Root);
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(email + "\n" + token), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(AppPaths.Session, data);
    }

    public static void Clear()
    {
        if (File.Exists(AppPaths.Session)) File.Delete(AppPaths.Session);
    }
}
