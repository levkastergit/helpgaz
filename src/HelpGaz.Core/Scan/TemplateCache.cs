using System.Security.Cryptography;
using System.Text;

namespace HelpGaz.Core.Scan;

/// <summary>
/// Копии шаблонов, скачанных при проверке. По ним наглядное сравнение строится без обращения к сайту.
/// Лежат только на этом компьютере, в папке данных программы.
/// </summary>
public sealed class TemplateCache(string directory)
{
    public string Directory { get; } = directory;

    public string PathFor(string templateName) =>
        Path.Combine(Directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(templateName)))[..32].ToLowerInvariant() + ".docx");

    public void Save(string templateName, byte[] bytes)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = PathFor(templateName);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }

    public byte[]? Load(string templateName)
    {
        var path = PathFor(templateName);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }
}
