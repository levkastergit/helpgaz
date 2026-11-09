using System.Text;
using System.Text.RegularExpressions;
using HelpGaz.Core.Docx;

namespace HelpGaz.Core.Compare;

public enum HoleKind { Value, Conditional, AutoField }

public abstract record Seg;
public sealed record LiteralSeg(string Text, int SourceStart) : Seg;
public sealed record HoleSeg(string Tag, HoleKind Kind, CharFormat? Format, int Ordinal) : Seg;

/// <summary>Абзац шаблона: постоянный текст вперемешку с «дырками» на месте тегов.</summary>
public sealed class TplPara
{
    public required int Index { get; init; }
    public required DocPara Source { get; init; }
    public required IReadOnlyList<Seg> Segs { get; init; }
    public required bool Removable { get; set; }
    public required string DisplayText { get; init; }
    public required int LiteralLength { get; init; }
    public required HashSet<string> Words { get; init; }

    /// <summary>Абзац внутри цикла или условия: может повторяться или отсутствовать.</summary>
    public bool Optional { get; set; }

    public bool StartsWithClose { get; init; }
    public bool EndsWithOpen { get; init; }

    /// <summary>Абзац только с маркером, который после заполнения остаётся пустой строкой.</summary>
    public bool KeepsBlank { get; set; }

    /// <summary>Начало самого внешнего цикла, в который входит абзац, либо -1.</summary>
    public int SectionStart { get; set; } = -1;

    /// <summary>Индексы начала циклов, которые заканчиваются на этом абзаце.</summary>
    public List<int> LoopStarts { get; } = new();
}

public sealed record FrozenField(string Name, string Value, string Location);

public sealed class TemplateModel
{
    public required DocModel Doc { get; init; }
    public required IReadOnlyList<TplPara> Paragraphs { get; init; }
    public required IReadOnlyList<FrozenField> Frozen { get; init; }

    private static readonly Regex TagRx = new(@"\{([^{}]*)\}", RegexOptions.Compiled);
    internal static readonly Regex WordRx = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled);

    /// <summary>Поля, значение которых Word пересчитывает сам. Их результат сравнивать нельзя.</summary>
    private static readonly HashSet<string> VolatileFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "PAGE", "NUMPAGES", "SECTIONPAGES", "DATE", "TIME", "SAVEDATE", "PRINTDATE", "CREATEDATE",
        "EDITTIME", "LASTSAVEDBY", "FILENAME", "FILESIZE", "NUMWORDS", "NUMCHARS", "TOC", "PAGEREF", "REF", "SEQ",
    };

    private abstract record Item(int Start);
    private sealed record Lit(int Start, string Text) : Item(Start);
    private sealed record Tag(int Start, char Prefix, string Name) : Item(Start);
    private sealed record Cond(int Start, string Name) : Item(Start);
    private sealed record Auto(int Start) : Item(Start);

    public static TemplateModel Parse(DocModel doc)
    {
        var paragraphs = new List<TplPara>(doc.Paragraphs.Count);
        var frozen = new List<FrozenField>();
        var sections = new List<(int Start, int End)>();
        var openSections = new Stack<int>();

        foreach (var p in doc.Paragraphs)
        {
            var scanned = Scan(p, frozen);

            // Цикл от начала до конца абзаца повторяет абзац целиком, а не текст внутри него.
            var wholeParagraph = IsWholeParagraphSection(scanned);
            var items = CollapseInlineSections(wholeParagraph ? scanned.GetRange(1, scanned.Count - 2) : scanned);
            if (wholeParagraph) sections.Add((paragraphs.Count, paragraphs.Count));

            var segs = new List<Seg>();
            var hadMarker = wholeParagraph;
            var ordinal = 0;

            foreach (var item in items)
            {
                switch (item)
                {
                    case Lit l:
                        if (segs.Count > 0 && segs[^1] is LiteralSeg prev)
                            segs[^1] = prev with { Text = prev.Text + l.Text };
                        else
                            segs.Add(new LiteralSeg(l.Text, l.Start));
                        break;
                    case Tag { Prefix: '#' or '^' }:
                        hadMarker = true;
                        openSections.Push(paragraphs.Count);
                        break;
                    case Tag { Prefix: '/' }:
                        hadMarker = true;
                        if (openSections.Count > 0) sections.Add((openSections.Pop(), paragraphs.Count));
                        break;
                    case Tag t:
                        AddHole(segs, t.Name, HoleKind.Value, FormatAt(p, t.Start), ref ordinal);
                        break;
                    case Cond c:
                        AddHole(segs, c.Name, HoleKind.Conditional, null, ref ordinal);
                        break;
                    case Auto:
                        AddHole(segs, "поле Word", HoleKind.AutoField, null, ref ordinal);
                        break;
                }
            }

            paragraphs.Add(new TplPara
            {
                Index = paragraphs.Count,
                Source = p,
                Segs = segs,
                Removable = hadMarker && segs.Count == 0,
                StartsWithClose = items.Count > 0 && items[0] is Tag { Prefix: '/' },
                EndsWithOpen = items.Count > 0 && items[^1] is Tag { Prefix: '#' or '^' },
                DisplayText = Display(segs),
                LiteralLength = segs.OfType<LiteralSeg>().Sum(l => l.Text.Length),
                Words = WordsOf(segs),
            });
        }

        foreach (var (rawStart, rawEnd) in sections)
        {
            var start = rawStart;
            var end = rawEnd;
            var open = paragraphs[start].Source;
            var close = paragraphs[end].Source;

            // Цикл между разными ячейками повторяет строки таблицы целиком, включая ячейки вне тегов.
            var rowLoop = open.RowId >= 0 && close.RowId >= 0 && open.CellId != close.CellId;
            if (rowLoop)
            {
                while (start > 0 && paragraphs[start - 1].Source.RowId == open.RowId) start--;
                while (end < paragraphs.Count - 1 && paragraphs[end + 1].Source.RowId == close.RowId) end++;
            }
            // Абзацы с маркерами исчезают, только когда оба маркера стоят отдельно. Иначе в документе
            // остаётся пустая строка, и сравнивать её надо как обычную.
            else if (start < end && !(paragraphs[start].Removable && paragraphs[end].Removable))
            {
                paragraphs[start].KeepsBlank = true;
                paragraphs[end].KeepsBlank = true;
            }

            for (var k = start; k <= end; k++)
            {
                paragraphs[k].Optional = true;
                if (paragraphs[k].SectionStart < 0 || start < paragraphs[k].SectionStart)
                    paragraphs[k].SectionStart = start;
            }

            // Абзацы с одними маркерами в готовый документ не попадают, поэтому возврат к началу цикла
            // привязан к последнему настоящему абзацу тела.
            var first = start;
            var last = end;
            if (!rowLoop)
            {
                // Текст до открывающего и после закрывающего маркера не повторяется.
                if (first < end && paragraphs[first].EndsWithOpen) first++;
                if (last > start && paragraphs[last].StartsWithClose) last--;
                while (first < end && paragraphs[first].Removable) first++;
                while (last > start && paragraphs[last].Removable) last--;
            }
            if (rowLoop || !paragraphs[last].Removable) paragraphs[last].LoopStarts.Add(first);
        }

        return new TemplateModel { Doc = doc, Paragraphs = paragraphs, Frozen = frozen };
    }

    /// <summary>
    /// Соседние теги без текста между ними склеиваются: по готовому документу нельзя понять, где кончается
    /// одно значение и начинается другое.
    /// </summary>
    private static void AddHole(List<Seg> segs, string tag, HoleKind kind, CharFormat? format, ref int ordinal)
    {
        if (segs.Count > 0 && segs[^1] is HoleSeg prev)
        {
            var merged = prev.Kind == HoleKind.Value && kind == HoleKind.Value ? HoleKind.Value : HoleKind.Conditional;
            segs[^1] = prev with { Tag = prev.Tag + "}{" + tag, Kind = merged, Format = null };
            return;
        }
        segs.Add(new HoleSeg(tag, kind, format, ordinal++));
    }

    private static bool IsWholeParagraphSection(List<Item> items)
    {
        if (items.Count < 2 || items[0] is not Tag { Prefix: '#' or '^' } || items[^1] is not Tag { Prefix: '/' }) return false;
        var depth = 0;
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i] is Tag { Prefix: '#' or '^' }) depth++;
            else if (items[i] is Tag { Prefix: '/' }) depth--;
            if (depth == 0 && i < items.Count - 1) return false;
        }
        return depth == 0;
    }

    private static CharFormat? FormatAt(DocPara p, int i) => i < p.Formats.Length ? p.Formats[i] : null;

    private static List<Item> Scan(DocPara p, List<FrozenField> frozen)
    {
        var boundaries = new List<(int Start, int Length, Item Item)>();

        foreach (var f in p.Fields)
        {
            var parts = f.Instruction.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var name = parts.Length > 0 ? parts[0] : "";
            if (name.Equals("MERGEFIELD", StringComparison.OrdinalIgnoreCase))
            {
                var field = parts.Length > 1 ? parts[1].Trim('"') : "";
                frozen.Add(new FrozenField(field, p.Text.Substring(f.Start, f.Length), p.Location));
            }
            else if (VolatileFields.Contains(name) && f.Length > 0)
            {
                boundaries.Add((f.Start, f.Length, new Auto(f.Start)));
            }
        }

        foreach (Match m in TagRx.Matches(p.Text))
        {
            var raw = m.Groups[1].Value.Trim();
            var prefix = raw.Length > 0 && raw[0] is '#' or '^' or '/' or '@' ? raw[0] : '\0';
            var name = prefix == '\0' ? raw : raw[1..].Trim();
            boundaries.Add((m.Index, m.Length, new Tag(m.Index, prefix == '@' ? '\0' : prefix, name)));
        }

        boundaries.Sort((a, b) => a.Start.CompareTo(b.Start));

        var items = new List<Item>();
        var pos = 0;
        foreach (var (start, length, item) in boundaries)
        {
            if (start < pos) continue;
            if (start > pos) items.Add(new Lit(pos, p.Text[pos..start]));
            items.Add(item);
            pos = start + length;
        }
        if (pos < p.Text.Length) items.Add(new Lit(pos, p.Text[pos..]));
        return items;
    }

    /// <summary>Условие или цикл, открытые и закрытые в одном абзаце, превращаются в одну «дырку».</summary>
    private static List<Item> CollapseInlineSections(List<Item> items)
    {
        var output = new List<Item>();
        var stack = new Stack<int>();
        foreach (var item in items)
        {
            if (item is Tag { Prefix: '#' or '^' })
            {
                stack.Push(output.Count);
                output.Add(item);
            }
            else if (item is Tag { Prefix: '/' } && stack.Count > 0)
            {
                var pos = stack.Pop();
                var openTag = (Tag)output[pos];
                output.RemoveRange(pos, output.Count - pos);
                output.Add(new Cond(openTag.Start, openTag.Prefix + openTag.Name));
            }
            else
            {
                output.Add(item);
            }
        }
        return output;
    }

    private static string Display(List<Seg> segs)
    {
        var sb = new StringBuilder();
        foreach (var s in segs)
        {
            if (s is LiteralSeg l) sb.Append(l.Text);
            else if (s is HoleSeg h) sb.Append('{').Append(h.Tag).Append('}');
        }
        return sb.ToString();
    }

    private static HashSet<string> WordsOf(List<Seg> segs)
    {
        var words = new HashSet<string>();
        foreach (var l in segs.OfType<LiteralSeg>())
            foreach (Match m in WordRx.Matches(l.Text))
                words.Add(m.Value.ToLowerInvariant());
        return words;
    }
}
