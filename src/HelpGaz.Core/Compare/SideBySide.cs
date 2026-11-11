using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using HelpGaz.Core.Docx;

namespace HelpGaz.Core.Compare;

public enum VisualMark { None, Tag, Value, Removed, Added, Format, Optional }

public enum RowKind { Same, Changed, Added, Missing, Optional }

public sealed record VisualRun(string Text, CharFormat? Format, VisualMark Mark);

public sealed class VisualPara
{
    public required DocPara Source { get; init; }
    public required List<VisualRun> Runs { get; init; }
    public bool FormatChanged { get; init; }
    public bool PageBreakChanged { get; init; }
}

/// <summary>Строка сравнения: абзац шаблона слева, абзац файла справа. Пустая сторона — абзаца там нет.</summary>
public sealed class VisualRow
{
    public VisualPara? Left { get; init; }
    public VisualPara? Right { get; init; }
    public required RowKind Kind { get; init; }
    public bool Focus { get; set; }
}

public sealed record LayoutLine(string Name, string Template, string File);

public sealed class VisualComparison
{
    /// <summary>Строки по порядку документа; null — пропущенный кусок между фрагментами.</summary>
    public List<VisualRow?> Rows { get; } = new();
    public List<LayoutLine> Layout { get; } = new();
    public string Note { get; set; } = "";
}

/// <summary>
/// Готовит наглядное сравнение места находки: абзацы шаблона и файла рядом, с их шрифтами и пометками отличий.
/// </summary>
public static class SideBySide
{
    private static readonly Regex TagRx = new(@"\{[^{}]*\}", RegexOptions.Compiled);
    private static readonly Regex OnlyTags = new(@"^\s*(\{[^{}]*\}\s*)+$", RegexOptions.Compiled);
    private const int MaxFocus = 12;

    private sealed record Pair(int F, int T, RowKind Kind, List<SegMatch>? Segs);

    public static VisualComparison Build(TemplateModel template, DocModel file, FindingKind kind, string where, string expected, string actual, int context = 3)
    {
        var result = new VisualComparison();
        if (kind == FindingKind.PageLayout)
        {
            FillLayout(template.Doc.Sections, file.Sections, result);
            return result;
        }

        var tpl = template.Paragraphs;
        var doc = file.Paragraphs;
        var pairs = Pairs(Aligner.Align(doc, tpl), tpl);
        var rows = pairs.Select(p => MakeRow(p, doc, tpl)).ToList();
        var focus = FindFocus(pairs, rows, doc, tpl, kind, where, expected, actual);

        if (focus.Count == 0)
        {
            result.Note = where.Length > 0
                ? $"Место «{where}» в файле не найдено: возможно, файл изменили после проверки. Проверьте папку заново."
                : "У этой находки нет места в тексте документа.";
            return result;
        }

        var show = new bool[rows.Count];
        foreach (var i in focus)
        {
            rows[i].Focus = true;
            for (var k = Math.Max(0, i - context); k <= Math.Min(rows.Count - 1, i + context); k++) show[k] = true;
        }
        for (var i = 0; i < rows.Count; i++)
        {
            if (show[i]) result.Rows.Add(rows[i]);
            else if (result.Rows.Count == 0 || result.Rows[^1] is not null) result.Rows.Add(null);
        }
        if (focus.Count >= MaxFocus) result.Note = $"Показаны первые {MaxFocus} мест.";
        return result;
    }

    /// <summary>Все абзацы обоих документов по порядку. Абзацы шаблона без пары встают перед следующим по шаблону.</summary>
    private static List<Pair> Pairs(AlignResult a, IReadOnlyList<TplPara> tpl)
    {
        var list = a.Matched.Select(m => new Pair(m.File, m.Tpl, RowKind.Same, m.Segs))
            .Concat(a.Changed.Select(c => new Pair(c.File, c.Tpl, RowKind.Changed, null)))
            .Concat(a.Extra.Select(e => new Pair(e, -1, RowKind.Added, null)))
            .OrderBy(p => p.F)
            .ToList();

        var used = list.Where(p => p.T >= 0).Select(p => p.T).ToHashSet();
        var missing = a.Missing.ToHashSet();
        for (var t = 0; t < tpl.Count; t++)
        {
            if (used.Contains(t)) continue;
            var at = list.FindIndex(p => p.T > t);
            var pair = new Pair(-1, t, missing.Contains(t) ? RowKind.Missing : RowKind.Optional, null);
            if (at < 0) list.Add(pair);
            else list.Insert(at, pair);
        }
        return list;
    }

    private static List<int> FindFocus(List<Pair> pairs, List<VisualRow> rows, IReadOnlyList<DocPara> doc, IReadOnlyList<TplPara> tpl,
        FindingKind kind, string where, string expected, string actual)
    {
        IEnumerable<int> All() => Enumerable.Range(0, pairs.Count);
        IEnumerable<int> InFile(params RowKind[] kinds) =>
            All().Where(i => pairs[i].F >= 0 && doc[pairs[i].F].Location == where && (kinds.Length == 0 || kinds.Contains(pairs[i].Kind)));
        IEnumerable<int> InTemplate(params RowKind[] kinds) =>
            All().Where(i => pairs[i].T >= 0 && tpl[pairs[i].T].Source.Location == where && (kinds.Length == 0 || kinds.Contains(pairs[i].Kind)));

        static List<int> Prefer(IEnumerable<int> candidates, Func<int, bool> best)
        {
            var list = candidates.ToList();
            var hit = list.FirstOrDefault(i => best(i), -1);
            return hit >= 0 ? new List<int> { hit } : list.Take(1).ToList();
        }

        bool FileHas(int i, string s) => s.Length > 0 && doc[pairs[i].F].Text.Contains(s, StringComparison.Ordinal);

        return kind switch
        {
            FindingKind.TemplateText => Prefer(InFile(RowKind.Changed), _ => true),
            FindingKind.ParagraphAdded => Prefer(InFile(RowKind.Added).Where(i => doc[pairs[i].F].Text.Trim().Length > 0),
                i => doc[pairs[i].F].Text == actual),
            FindingKind.ParagraphMissing => Prefer(InTemplate(RowKind.Missing), i => tpl[pairs[i].T].DisplayText == expected),
            FindingKind.CharFormatting => Prefer(InFile(RowKind.Same), i => rows[i].Right!.Runs.Any(r => r.Mark == VisualMark.Format)),
            FindingKind.ParaFormatting => Prefer(InFile(RowKind.Same), i => rows[i].Right!.FormatChanged),
            FindingKind.PageBreak => Prefer(InFile(RowKind.Same), i => rows[i].Right!.PageBreakChanged),
            FindingKind.ValueMismatch or FindingKind.FolderConflict or FindingKind.Unfilled => Prefer(InFile(), i => FileHas(i, actual)),
            FindingKind.EmptyField => Prefer(InFile(), _ => true),
            FindingKind.RowCount or FindingKind.FrozenField => Prefer(InTemplate(), _ => true),
            FindingKind.BlankLines => All().Where(i =>
                    (pairs[i].Kind == RowKind.Added && doc[pairs[i].F].Text.Trim().Length == 0)
                    || (pairs[i].Kind == RowKind.Missing && tpl[pairs[i].T].DisplayText.Trim().Length == 0))
                .Take(MaxFocus).ToList(),
            _ => new List<int>(),
        };
    }

    private static VisualRow MakeRow(Pair p, IReadOnlyList<DocPara> doc, IReadOnlyList<TplPara> tpl)
    {
        var t = p.T >= 0 ? tpl[p.T].Source : null;
        var f = p.F >= 0 ? doc[p.F] : null;
        var left = t is null ? null : new VisualMark[t.Text.Length];
        var right = f is null ? null : new VisualMark[f.Text.Length];

        if (left is not null)
            foreach (Match m in TagRx.Matches(t!.Text))
                Array.Fill(left, VisualMark.Tag, m.Index, m.Length);

        switch (p.Kind)
        {
            case RowKind.Same:
                MarkSame(p.Segs!, t!, f!, left!, right!);
                break;
            case RowKind.Changed:
                MarkChanged(t!.Text, f!.Text, left!, right!);
                break;
            case RowKind.Added:
                Array.Fill(right!, VisualMark.Added);
                break;
            case RowKind.Missing:
                Array.Fill(left!, VisualMark.Removed);
                break;
            case RowKind.Optional:
                Array.Fill(left!, VisualMark.Optional);
                break;
        }

        return new VisualRow
        {
            Kind = p.Kind,
            Left = t is null ? null : Para(t, left!, false, false),
            Right = f is null ? null : Para(f, right!,
                p.Kind == RowKind.Same && t!.Format != f.Format,
                p.Kind == RowKind.Same && t!.HasPageBreak != f.HasPageBreak),
        };
    }

    private static void MarkSame(List<SegMatch> segs, DocPara t, DocPara f, VisualMark[] left, VisualMark[] right)
    {
        foreach (var s in segs)
        {
            if (s.Seg is HoleSeg)
            {
                Array.Fill(right, VisualMark.Value, s.Start, s.Length);
                continue;
            }
            var l = (LiteralSeg)s.Seg;
            for (var i = 0; i < s.Length; i++)
            {
                var a = l.SourceStart + i;
                var b = s.Start + i;
                if (a >= t.Formats.Length || b >= f.Formats.Length || char.IsWhiteSpace(f.Text[b])) continue;
                if (t.Formats[a] == f.Formats[b]) continue;
                left[a] = VisualMark.Format;
                right[b] = VisualMark.Format;
            }
        }
    }

    /// <summary>Пословное сравнение. Текст файла на месте удалённого {тега} считается подставленным значением.</summary>
    private static void MarkChanged(string a, string b, VisualMark[] left, VisualMark[] right)
    {
        int x = 0, y = 0;
        var afterTag = false;
        foreach (var op in TextDiff.Diff(a, b))
        {
            switch (op.Kind)
            {
                case DiffKind.Equal:
                    x += op.Text.Length;
                    y += op.Text.Length;
                    afterTag = false;
                    break;
                case DiffKind.Delete:
                    for (var i = 0; i < op.Text.Length; i++)
                        if (left[x + i] != VisualMark.Tag) left[x + i] = VisualMark.Removed;
                    afterTag = OnlyTags.IsMatch(op.Text);
                    x += op.Text.Length;
                    break;
                case DiffKind.Insert:
                    Array.Fill(right, afterTag ? VisualMark.Value : VisualMark.Added, y, op.Text.Length);
                    y += op.Text.Length;
                    afterTag = false;
                    break;
            }
        }
    }

    private static VisualPara Para(DocPara p, VisualMark[] marks, bool formatChanged, bool pageBreakChanged)
    {
        var runs = new List<VisualRun>();
        var start = 0;
        for (var i = 1; i <= p.Text.Length; i++)
        {
            if (i < p.Text.Length && marks[i] == marks[start] && Equals(FormatAt(p, i), FormatAt(p, start))) continue;
            runs.Add(new VisualRun(p.Text[start..i], FormatAt(p, start), marks[start]));
            start = i;
        }
        return new VisualPara { Source = p, Runs = runs, FormatChanged = formatChanged, PageBreakChanged = pageBreakChanged };
    }

    private static CharFormat? FormatAt(DocPara p, int i) => i < p.Formats.Length ? p.Formats[i] : null;

    private static void FillLayout(IReadOnlyList<SectionLayout> tpl, IReadOnlyList<SectionLayout> file, VisualComparison r)
    {
        static string Mm(int twips) => (twips / 56.6929).ToString("0.#") + " мм";
        if (tpl.Count != file.Count) r.Layout.Add(new LayoutLine("Разделов", tpl.Count.ToString(), file.Count.ToString()));
        for (var i = 0; i < Math.Min(tpl.Count, file.Count); i++)
        {
            var a = tpl[i];
            var b = file[i];
            var prefix = tpl.Count > 1 ? $"Раздел {i + 1}: " : "";
            void Add(string name, int x, int y) => r.Layout.Add(new LayoutLine(prefix + name, Mm(x), Mm(y)));
            Add("ширина страницы", a.Width, b.Width);
            Add("высота страницы", a.Height, b.Height);
            r.Layout.Add(new LayoutLine(prefix + "ориентация", a.Orient, b.Orient));
            Add("поле сверху", a.Top, b.Top);
            Add("поле снизу", a.Bottom, b.Bottom);
            Add("поле слева", a.Left, b.Left);
            Add("поле справа", a.Right, b.Right);
            Add("колонтитул сверху", a.Header, b.Header);
            Add("колонтитул снизу", a.Footer, b.Footer);
            Add("переплёт", a.Gutter, b.Gutter);
        }
    }
}

/// <summary>Страница со сравнением для встроенного браузера.</summary>
public static class ComparisonHtml
{
    private const string Css = """
        body { font-family: 'Segoe UI', Arial, sans-serif; font-size: 9pt; margin: 8px; color: #1b1b1b; background: #ffffff; }
        h1 { font-size: 11pt; margin: 0 0 2px 0; }
        .sub { color: #666666; margin-bottom: 6px; }
        .legend span { display: inline-block; margin: 0 10px 4px 0; padding: 1px 6px; border: 1px solid #d0d0d0; }
        .brief { margin: 6px 0 10px 0; }
        .brief td { padding: 2px 8px 2px 0; vertical-align: top; }
        table.cmp { width: 100%; border-collapse: collapse; table-layout: fixed; }
        table.cmp th { text-align: left; background: #f0f0f0; border: 1px solid #c8c8c8; padding: 4px 6px; }
        table.cmp td { border: 1px solid #dcdcdc; padding: 3px 8px; vertical-align: top; word-wrap: break-word; }
        tr.focus td { background: #fffbe0; border-top: 2px solid #e0a000; border-bottom: 2px solid #e0a000; }
        tr.gap td { text-align: center; color: #999999; background: #fafafa; padding: 0; }
        td.none { background: #f4f4f4; color: #999999; font-style: italic; }
        .loc { font-family: 'Segoe UI', Arial, sans-serif; font-size: 8pt; color: #888888; margin-bottom: 2px; }
        .pb { font-family: 'Segoe UI', Arial, sans-serif; font-size: 8pt; color: #b00000; border-top: 1px dashed #b00000; margin-bottom: 2px; }
        .pf { border-left: 4px solid #e0a000; padding-left: 4px; }
        .pil { color: #b0b0b0; }
        .m-tag { color: #1a56c4; background: #e4edff; }
        .m-value { background: #e4edff; }
        .m-removed { background: #ffc8c8; text-decoration: line-through; }
        .m-added { background: #bff0bf; }
        .m-format { background: #ffe98a; }
        .m-optional { color: #9a9a9a; }
        .note { margin: 8px 0; padding: 6px 8px; background: #fff4e5; border: 1px solid #f0c890; }
        table.lay { border-collapse: collapse; }
        table.lay td, table.lay th { border: 1px solid #d0d0d0; padding: 3px 10px; text-align: left; }
        table.lay tr.diff td { background: #ffe98a; }
        """;

    /// <summary>Если место находки не помещается в окно, страница прокручивается к нему.</summary>
    private const string ScrollToFocus =
        "<script>(function(){var f=document.getElementById('focus');if(!f)return;var y=0,e=f;"
        + "while(e){y+=e.offsetTop;e=e.offsetParent;}"
        + "var h=document.documentElement.clientHeight||document.body.clientHeight;"
        + "if(y+f.offsetHeight>h)window.scrollTo(0,Math.max(0,y-h/3));})();</script>";

    public static string Message(string title, string text) =>
        Page($"<h1>{E(title)}</h1><div class=\"sub\">{E(text)}</div>");

    /// <param name="brief">Пары «подпись — текст» над сравнением; пустые пропускаются.</param>
    public static string Render(string title, string subtitle, IReadOnlyList<(string Label, string Text)> brief, string fileName, VisualComparison? cmp, string? unavailable)
    {
        var sb = new StringBuilder();
        sb.Append($"<h1>{E(title)}</h1><div class=\"sub\">{E(subtitle)}</div>");

        var lines = brief.Where(b => b.Text.Length > 0).ToList();
        if (lines.Count > 0)
        {
            sb.Append("<table class=\"brief\">");
            foreach (var (label, text) in lines) sb.Append($"<tr><td><b>{E(label)}</b></td><td>{E(text)}</td></tr>");
            sb.Append("</table>");
        }

        if (unavailable is not null) sb.Append($"<div class=\"note\">{E(unavailable)}</div>");
        if (cmp is null) return Page(sb.ToString());

        if (cmp.Layout.Count > 0)
        {
            sb.Append("<table class=\"lay\"><tr><th>Параметр</th><th>Шаблон на сайте</th><th>Ваш файл</th></tr>");
            foreach (var l in cmp.Layout)
                sb.Append($"<tr{(l.Template != l.File ? " class=\"diff\"" : "")}><td>{E(l.Name)}</td><td>{E(l.Template)}</td><td>{E(l.File)}</td></tr>");
            sb.Append("</table>");
        }

        if (cmp.Rows.Count > 0)
        {
            sb.Append("<div class=\"legend\">"
                + "<span class=\"m-removed\">есть в шаблоне, нет в файле</span>"
                + "<span class=\"m-added\">есть в файле, нет в шаблоне</span>"
                + "<span class=\"m-format\">другое оформление</span>"
                + "<span class=\"m-tag\">{тег} — место для данных</span><span class=\"m-value\">данные, подставленные сайтом</span>"
                + "<span class=\"m-optional\">может отсутствовать</span>"
                + "</div>");
            sb.Append("<table class=\"cmp\"><tr><th>Шаблон на сайте</th>");
            sb.Append($"<th>Ваш файл: {E(fileName)}</th></tr>");
            var first = true;
            foreach (var row in cmp.Rows)
            {
                if (row is null)
                {
                    sb.Append("<tr class=\"gap\"><td colspan=\"2\">⋯</td></tr>");
                    continue;
                }
                sb.Append(!row.Focus ? "<tr>" : first ? "<tr class=\"focus\" id=\"focus\">" : "<tr class=\"focus\">");
                if (row.Focus) first = false;
                Cell(sb, row.Left, row, "нет абзаца");
                Cell(sb, row.Right, row, row.Kind == RowKind.Optional ? "не нужен в этом документе" : "абзаца нет");
                sb.Append("</tr>");
            }
            sb.Append("</table>");
        }

        if (cmp.Note.Length > 0) sb.Append($"<div class=\"note\">{E(cmp.Note)}</div>");
        sb.Append(ScrollToFocus);
        return Page(sb.ToString());
    }

    private static void Cell(StringBuilder sb, VisualPara? p, VisualRow row, string empty)
    {
        if (p is null)
        {
            sb.Append($"<td class=\"none\">{E(empty)}</td>");
            return;
        }

        sb.Append("<td>");
        if (row.Focus)
        {
            // Оформление дублируется словами: встроенный браузер может подменить шрифт, которого нет в системе.
            var changed = p.Runs.FirstOrDefault(r => r.Mark == VisualMark.Format)?.Format;
            sb.Append($"<div class=\"loc\">{E(p.Source.Location)}{(changed is null ? "" : " · " + E(Describe(changed)))}</div>");
        }
        if (p.Source.HasPageBreak || p.PageBreakChanged)
            sb.Append($"<div class=\"pb\">{(p.Source.HasPageBreak ? "разрыв страницы перед абзацем" : "здесь в шаблоне был разрыв страницы")}</div>");

        var f = p.Source.Format;
        var style = $"text-align:{Align(f.Align)};margin-left:{Px(f.Left)}px;margin-right:{Px(f.Right)}px;text-indent:{Px(f.FirstLine)}px";
        sb.Append($"<div class=\"{(p.FormatChanged ? "pf" : "")}\" style=\"{style}\">");

        if (p.Runs.Count == 0 || p.Runs.All(r => r.Text.Trim().Length == 0 && r.Mark == VisualMark.None))
        {
            // Пустой абзац рисуем знаком абзаца, как в Word с включёнными непечатаемыми символами.
            var mark = row.Kind switch { RowKind.Added => "m-added", RowKind.Missing => "m-removed", _ => "pil" };
            sb.Append($"<span class=\"{mark}\">¶ пустая строка</span>");
        }
        else
        {
            foreach (var r in p.Runs) Run(sb, r);
        }
        sb.Append("</div></td>");
    }

    private static string Describe(CharFormat f)
    {
        var parts = new List<string> { f.Font, (f.HalfPoints / 2.0).ToString("0.#") + " пт" };
        if (f.Bold) parts.Add("жирный");
        if (f.Italic) parts.Add("курсив");
        if (f.Underline is not ("" or "none")) parts.Add("подчёркнутый");
        return string.Join(", ", parts.Where(x => x.Length > 0));
    }

    private static void Run(StringBuilder sb, VisualRun r)
    {
        var css = new StringBuilder();
        if (r.Format is { } f)
        {
            if (f.Font.Length > 0) css.Append($"font-family:'{f.Font.Replace("'", "")}',serif;");
            if (f.HalfPoints > 0) css.Append($"font-size:{(f.HalfPoints / 2.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}pt;");
            if (f.Bold) css.Append("font-weight:bold;");
            if (f.Italic) css.Append("font-style:italic;");
            if (f.Underline is not ("" or "none") && r.Mark != VisualMark.Removed) css.Append("text-decoration:underline;");
        }
        var cls = r.Mark switch
        {
            VisualMark.Tag => "m-tag",
            VisualMark.Value => "m-value",
            VisualMark.Removed => "m-removed",
            VisualMark.Added => "m-added",
            VisualMark.Format => "m-format",
            VisualMark.Optional => "m-optional",
            _ => "",
        };
        var text = E(r.Text).Replace("\t", "&nbsp;&nbsp;&nbsp;&nbsp;").Replace("\n", "<br>");
        sb.Append($"<span class=\"{cls}\" style=\"{css}\">{text}</span>");
    }

    private static string Align(string a) => a switch
    {
        "center" => "center",
        "right" or "end" => "right",
        "both" or "distribute" => "justify",
        _ => "left",
    };

    /// <summary>Отступы уменьшены вдвое: колонка сравнения уже страницы.</summary>
    private static int Px(int twips) => Math.Clamp(twips / 30, -60, 200);

    private static string E(string s) => WebUtility.HtmlEncode(s);

    private static string Page(string body) =>
        "<!DOCTYPE html><html><head><meta http-equiv=\"X-UA-Compatible\" content=\"IE=edge\"><meta charset=\"utf-8\">"
        + $"<style>{Css}</style></head><body>{body}</body></html>";
}
