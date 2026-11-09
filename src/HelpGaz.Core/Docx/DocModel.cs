namespace HelpGaz.Core.Docx;

/// <summary>Итоговое оформление символа после применения стилей и прямого форматирования.</summary>
public sealed record CharFormat(string Font, int HalfPoints, bool Bold, bool Italic, string Underline);

/// <summary>Итоговое оформление абзаца. Значения в твипах, как в самом .docx.</summary>
public sealed record ParaFormat(string Align, int Left, int Right, int FirstLine, int Before, int After, int Line, string LineRule);

/// <summary>Параметры страницы одного раздела документа.</summary>
public sealed record SectionLayout(int Width, int Height, string Orient, int Top, int Right, int Bottom, int Left, int Header, int Footer, int Gutter);

/// <summary>Результат поля Word внутри текста абзаца.</summary>
public sealed record FieldSpan(int Start, int Length, string Instruction);

public sealed class DocPara
{
    public required int Index { get; init; }
    public required string Location { get; init; }

    /// <summary>Область документа: основной текст, столбец таблицы, колонтитул или надпись.</summary>
    public required string Container { get; init; }

    /// <summary>Номера строки и ячейки таблицы, -1 вне таблицы.</summary>
    public int RowId { get; init; } = -1;
    public int CellId { get; init; } = -1;
    public required string Text { get; init; }
    public required CharFormat[] Formats { get; init; }
    public required ParaFormat Format { get; init; }
    public required bool HasPageBreak { get; init; }
    public required IReadOnlyList<FieldSpan> Fields { get; init; }
}

public sealed class DocModel
{
    public required IReadOnlyList<DocPara> Paragraphs { get; init; }
    public required IReadOnlyList<SectionLayout> Sections { get; init; }
}
