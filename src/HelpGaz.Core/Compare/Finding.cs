using System.Security.Cryptography;
using System.Text;

namespace HelpGaz.Core.Compare;

public enum FindingKind
{
    TemplateText,
    ParagraphAdded,
    ParagraphMissing,
    BlankLines,
    ValueMismatch,
    RowCount,
    CharFormatting,
    ParaFormatting,
    PageLayout,
    PageBreak,
    Unfilled,
    EmptyField,
    FrozenField,
    FolderConflict,
    UnknownDocument,
    Error,
}

public static class FindingKinds
{
    public static string Title(FindingKind k) => k switch
    {
        FindingKind.TemplateText => "Изменён текст шаблона",
        FindingKind.ParagraphAdded => "Добавлен абзац",
        FindingKind.ParagraphMissing => "Удалён абзац",
        FindingKind.BlankLines => "Пустые строки",
        FindingKind.ValueMismatch => "Значение не как в договоре",
        FindingKind.RowCount => "Другое число строк",
        FindingKind.CharFormatting => "Оформление текста",
        FindingKind.ParaFormatting => "Оформление абзаца",
        FindingKind.PageLayout => "Разметка страницы",
        FindingKind.PageBreak => "Разрыв страницы",
        FindingKind.Unfilled => "Незаполненный тег",
        FindingKind.EmptyField => "Пустое поле",
        FindingKind.FrozenField => "Застывшее поле Word",
        FindingKind.FolderConflict => "Расхождение внутри папки",
        FindingKind.UnknownDocument => "Неизвестный документ",
        FindingKind.Error => "Ошибка проверки",
        _ => k.ToString(),
    };
}

/// <summary>Одна находка в одном файле.</summary>
public sealed class Finding
{
    public required FindingKind Kind { get; init; }
    public required string Template { get; init; }
    public required string File { get; init; }
    public string Where { get; init; } = "";
    public string Tag { get; init; } = "";
    public string Expected { get; init; } = "";
    public string Actual { get; init; } = "";
    public string Summary { get; init; } = "";

    /// <summary>Что должно совпасть, чтобы находки из разных папок считались одной.</summary>
    public required string GroupSource { get; init; }

    public string GroupKey => Hash($"{Kind}\u0001{Template}\u0001{GroupSource}");

    private static string Hash(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..24].ToLowerInvariant();
}

/// <summary>Значение, подставленное вместо тега.</summary>
public sealed record TagValue(string Tag, string Value, string Where);

/// <summary>Результат сверки одного файла.</summary>
public sealed class DocumentResult
{
    public required string File { get; init; }
    public required string Template { get; init; }
    public List<Finding> Findings { get; } = new();
    public List<TagValue> FileValues { get; } = new();
    public List<TagValue> SiteValues { get; } = new();
    public bool HasSiteDocument { get; set; }
}
