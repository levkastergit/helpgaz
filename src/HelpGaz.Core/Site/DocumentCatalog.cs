using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace HelpGaz.Core.Site;

/// <summary>Как получить шаблон и документ сайта для файла из папки.</summary>
public sealed record ResolvedDocument(string TemplateFile, int TemplateId, string GenerateUrlPrefix, bool Binary, bool CheckValues);

public static partial class DocumentCatalog
{
    private static readonly Regex BrowserCopySuffix = new(@"\s*\(\d+\)(?=\.[^.]+$)", RegexOptions.Compiled);

    public static string Normalize(string fileName)
    {
        var s = BrowserCopySuffix.Replace(fileName.Trim(), "");
        s = s.ToLowerInvariant().Replace('ё', 'е');
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    public static string Hash(string fileName) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(fileName))))[..24].ToLowerInvariant();

    /// <summary>Ищет документ по имени файла среди штатных и пользовательских документов ИТД.</summary>
    public static ResolvedDocument? Resolve(string fileName, IReadOnlyList<SiteTemplate> templates, IReadOnlyList<SiteTemplate> customItd)
    {
        var normalized = Normalize(fileName);
        foreach (var c in customItd)
            if (Normalize(c.FileName) == normalized)
                return new ResolvedDocument(c.FileName, c.Id, $"/api/templates/render/{c.Id}", false, true);

        var hash = Hash(fileName);
        var entry = Array.Find(Entries, e => e.DownloadHash == hash);
        if (entry is null) return null;

        var template = templates.FirstOrDefault(t => Hash(t.FileName) == entry.TemplateHash);
        return template is null
            ? null
            : new ResolvedDocument(template.FileName, template.Id, entry.Url, entry.Binary, entry.CheckValues);
    }
}
