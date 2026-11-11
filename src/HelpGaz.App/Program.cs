using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HelpGaz.Core.Diagnostics;
using HelpGaz.Core.Scan;

namespace HelpGaz.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        AppLog.Init(AppPaths.Logs);
        AppLog.Info($"Запуск HelpGaz v{Updater.CurrentVersion.ToString(3)}: «{Environment.ProcessPath}», данные «{AppPaths.Root}», "
            + $"{Environment.OSVersion}, .NET {Environment.Version}, язык {CultureInfo.CurrentCulture.Name}");

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            AppLog.Error("Необработанная ошибка", e.Exception);
            MessageBox.Show("Произошла ошибка: " + e.Exception.Message + "\n\nПодробности записаны в лог программы.", "HelpGaz",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => AppLog.Error("Необработанная ошибка, программа закрывается", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => AppLog.Error("Ошибка фоновой задачи", e.Exception);

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        AppLog.Info("Выход");
    }
}

internal static class AppPaths
{
    public static readonly string Root = Environment.GetEnvironmentVariable("HELPGAZ_DATA")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HelpGaz");
    public static readonly string Findings = Path.Combine(Root, "findings.json");
    public static readonly string Session = Path.Combine(Root, "session.bin");
    public static readonly string Logs = Path.Combine(Root, "logs");
    public static readonly string Templates = Path.Combine(Root, "templates");
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
