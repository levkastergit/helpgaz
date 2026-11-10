using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using HelpGaz.Core.Diagnostics;

namespace HelpGaz.Core.Scan;

public sealed record ReleaseInfo(Version Version, string Tag, string ExeUrl, string HashUrl);

/// <summary>
/// Автообновление с GitHub Releases. Новый exe ставится только после сверки SHA-256 с файлом из того же релиза.
/// Запущенный exe Windows разрешает переименовать, поэтому подмена не требует отдельной программы.
/// </summary>
public sealed class Updater : IDisposable
{
    private const string LatestRelease = "https://api.github.com/repos/levkastergit/helpgaz/releases/latest";
    private const string ExeName = "HelpGaz.exe";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public Updater()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("HelpGaz-Updater");
    }

    public static Version CurrentVersion
    {
        get
        {
            var info = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
            return Version.TryParse(info.Split('+')[0], out var v) ? v : new Version(0, 0, 0);
        }
    }

    /// <summary>Сборка без номера версии (из IDE) и запуск с отключением не обновляются.</summary>
    public static bool Enabled =>
        CurrentVersion > new Version(0, 0, 0) && Environment.GetEnvironmentVariable("HELPGAZ_NO_UPDATE") is null;

    public async Task<ReleaseInfo?> FindNewerAsync(CancellationToken ct)
    {
        using var resp = await _http.GetAsync(LatestRelease, ct);
        if (!resp.IsSuccessStatusCode)
        {
            AppLog.Warn($"Обновление: GitHub ответил {(int)resp.StatusCode}");
            return null;
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
        AppLog.Info($"Обновление: сейчас v{CurrentVersion.ToString(3)}, последний релиз {tag}");
        if (!Version.TryParse(tag.TrimStart('v'), out var version) || version <= CurrentVersion) return null;

        string? exe = null, hash = null;
        foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString();
            var url = a.GetProperty("browser_download_url").GetString();
            if (name == ExeName) exe = url;
            else if (name == ExeName + ".sha256") hash = url;
        }
        if (exe is null || hash is null)
        {
            AppLog.Warn($"Обновление: в релизе {tag} нет {ExeName} или контрольной суммы");
            return null;
        }
        return new ReleaseInfo(version, tag, exe, hash);
    }

    /// <summary>Скачивает новую версию рядом с текущей и возвращает путь к ней после проверки контрольной суммы.</summary>
    public async Task<string> DownloadAsync(ReleaseInfo release, string currentExe, CancellationToken ct)
    {
        var expected = (await _http.GetStringAsync(release.HashUrl, ct))
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];

        var target = currentExe + ".new";
        await using (var source = await _http.GetStreamAsync(release.ExeUrl, ct))
        await using (var file = File.Create(target))
            await source.CopyToAsync(file, ct);

        string actual;
        await using (var file = File.OpenRead(target))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(file, ct));

        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(target);
            AppLog.Error($"Обновление {release.Tag}: SHA-256 {actual}, ожидалась {expected}");
            throw new InvalidDataException("Контрольная сумма скачанного обновления не совпала.");
        }
        AppLog.Info($"Обновление {release.Tag} скачано, SHA-256 совпала");
        return target;
    }

    public static void InstallAndRestart(string newExe, string currentExe)
    {
        var old = currentExe + ".old";
        if (File.Exists(old)) File.Delete(old);

        File.Move(currentExe, old);
        try
        {
            File.Move(newExe, currentExe);
        }
        catch
        {
            File.Move(old, currentExe);
            throw;
        }
        AppLog.Info($"Обновление установлено в «{currentExe}», перезапуск");
        Process.Start(new ProcessStartInfo(currentExe) { UseShellExecute = true });
    }

    /// <summary>Убирает файлы, оставшиеся от прошлого обновления. Старый процесс мог ещё не завершиться.</summary>
    public static void Cleanup(string currentExe)
    {
        foreach (var path in new[] { currentExe + ".old", currentExe + ".new" })
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public void Dispose() => _http.Dispose();
}
