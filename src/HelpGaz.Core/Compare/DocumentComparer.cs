using System.Text.RegularExpressions;
using HelpGaz.Core.Docx;

namespace HelpGaz.Core.Compare;

public sealed class CompareInput
{
    public required string FileName { get; init; }
    public required string TemplateName { get; init; }
    public required DocModel File { get; init; }
    public required TemplateModel Template { get; init; }

    /// <summary>Документ, который сайт выдаёт по договору сейчас. Null, если договор не привязан.</summary>
    public DocModel? SiteDocument { get; init; }

    /// <summary>False для документов, значения которых сайт вычисляет заново при каждом скачивании.</summary>
    public bool CheckValues { get; init; } = true;
}

/// <summary>Сверяет один файл с шаблоном и с документом сайта.</summary>
public static class DocumentComparer
{
    private static readonly Regex LeftoverTag = new(@"\{[#^/@]?\s*[\p{L}_][\p{L}\p{N}_.\s]*\}", RegexOptions.Compiled);
    private static readonly Regex JunkWord = new(@"(?<![\p{L}\p{N}])(undefined|NaN|null)(?![\p{L}\p{N}])|\[object Object\]", RegexOptions.Compiled);

    /// <summary>Сайт дописывает к значению пометку об истёкшем сертификате в момент генерации.</summary>
    private const string ExpiredSuffix = " (истёк)";

    public static DocumentResult Compare(CompareInput input)
    {
        var result = new DocumentResult { File = input.FileName, Template = input.TemplateName, HasSiteDocument = input.SiteDocument is not null };
        var tpl = input.Template.Paragraphs;
        var file = input.File.Paragraphs;
        var alignment = Aligner.Align(file, tpl);

        var fileFills = CollectFills(alignment, file, tpl, result.FileValues);
        var knownValues = result.FileValues
            .Where(v => v.Value.Length >= 4)
            .GroupBy(v => v.Value).Select(g => g.First())
            .OrderByDescending(v => v.Value.Length).ToList();

        ReportTextDifferences(input, alignment, file, tpl, knownValues, result);
        ReportFormatting(input, alignment, file, tpl, result);
        ReportLayout(input, result);

        if (input.SiteDocument is not null && input.CheckValues)
        {
            var siteAlignment = Aligner.Align(input.SiteDocument.Paragraphs, tpl);
            var siteFills = CollectFills(siteAlignment, input.SiteDocument.Paragraphs, tpl, result.SiteValues);
            ReportValues(input, tpl, fileFills, siteFills, result);
        }

        ReportUnfilled(input, alignment, file, fileFills, result);
        ReportFrozen(input, result);
        return result;
    }

    private sealed record Occurrence(int ParaFile, Dictionary<int, (HoleSeg Hole, string Value)> Holes);

    private static Dictionary<int, List<Occurrence>> CollectFills(AlignResult a, IReadOnlyList<DocPara> doc, IReadOnlyList<TplPara> tpl, List<TagValue> values)
    {
        var byTpl = new Dictionary<int, List<Occurrence>>();

        void Add(int tplIndex, int fileIndex, Dictionary<int, (HoleSeg, string)> holes)
        {
            if (!byTpl.TryGetValue(tplIndex, out var list)) byTpl[tplIndex] = list = new();
            list.Add(new Occurrence(fileIndex, holes));
        }

        var entries = new List<(int File, int Tpl, Dictionary<int, (HoleSeg, string)> Holes)>();
        foreach (var m in a.Matched)
        {
            var holes = new Dictionary<int, (HoleSeg, string)>();
            foreach (var s in m.Segs)
                if (s.Seg is HoleSeg h)
                    holes[h.Ordinal] = (h, doc[m.File].Text.Substring(s.Start, s.Length));
            entries.Add((m.File, m.Tpl, holes));
        }
        foreach (var c in a.Changed)
        {
            var holes = new Dictionary<int, (HoleSeg, string)>();
            foreach (var f in TextDiff.CompareWithTemplate(tpl[c.Tpl], doc[c.File].Text).Fills)
                holes[f.Hole.Ordinal] = (f.Hole, f.Value);
            entries.Add((c.File, c.Tpl, holes));
        }

        foreach (var (fileIndex, tplIndex, holes) in entries.OrderBy(e => e.File))
        {
            Add(tplIndex, fileIndex, holes);
            foreach (var (hole, value) in holes.Values)
                if (hole.Kind == HoleKind.Value)
                    values.Add(new TagValue(hole.Tag, value, doc[fileIndex].Location));
        }
        return byTpl;
    }

    private static string Generalize(string text, List<TagValue> known)
    {
        foreach (var v in known) text = text.Replace(v.Value, "{" + v.Tag + "}", StringComparison.Ordinal);
        return text;
    }

    private static void ReportTextDifferences(CompareInput input, AlignResult a, IReadOnlyList<DocPara> file, IReadOnlyList<TplPara> tpl, List<TagValue> known, DocumentResult r)
    {
        foreach (var c in a.Changed)
        {
            var t = tpl[c.Tpl];
            var diff = TextDiff.CompareWithTemplate(t, file[c.File].Text);
            if (!diff.LiteralChanged) continue;
            var actual = Generalize(diff.Actual, known);
            r.Findings.Add(new Finding
            {
                Kind = FindingKind.TemplateText,
                Template = input.TemplateName,
                File = input.FileName,
                Where = file[c.File].Location,
                Expected = t.DisplayText,
                Actual = actual,
                Summary = Shorten(actual),
                GroupSource = t.DisplayText + "\u0001" + actual,
            });
        }

        var blankAdded = 0;
        foreach (var i in a.Extra)
        {
            if (string.IsNullOrWhiteSpace(file[i].Text)) { blankAdded++; continue; }
            var actual = Generalize(file[i].Text, known);
            r.Findings.Add(new Finding
            {
                Kind = FindingKind.ParagraphAdded,
                Template = input.TemplateName,
                File = input.FileName,
                Where = file[i].Location,
                Actual = actual,
                Summary = Shorten(actual),
                GroupSource = actual,
            });
        }

        var blankMissing = 0;
        foreach (var j in a.Missing)
        {
            if (string.IsNullOrWhiteSpace(tpl[j].DisplayText)) { blankMissing++; continue; }
            r.Findings.Add(new Finding
            {
                Kind = FindingKind.ParagraphMissing,
                Template = input.TemplateName,
                File = input.FileName,
                Where = tpl[j].Source.Location,
                Expected = tpl[j].DisplayText,
                Summary = Shorten(tpl[j].DisplayText),
                GroupSource = tpl[j].DisplayText,
            });
        }

        if (blankAdded + blankMissing > 0)
        {
            var summary = $"Пустых строк добавлено: {blankAdded}, удалено: {blankMissing}";
            r.Findings.Add(new Finding
            {
                Kind = FindingKind.BlankLines,
                Template = input.TemplateName,
                File = input.FileName,
                Summary = summary,
                Actual = summary,
                GroupSource = summary,
            });
        }
    }

    private static string Shorten(string s)
    {
        s = s.Replace('\n', ' ').Replace('\t', ' ').Trim();
        return s.Length <= 120 ? s : s[..117] + "…";
    }

    private static void ReportFormatting(CompareInput input, AlignResult a, IReadOnlyList<DocPara> file, IReadOnlyList<TplPara> tpl, DocumentResult r)
    {
        foreach (var m in a.Matched)
        {
            var f = file[m.File];
            var t = tpl[m.Tpl];
            if (t.Removable) continue;

            CompareRuns(input, f, t, m, r);
            CompareParagraph(input, f, t, r);

            if (f.HasPageBreak != t.Source.HasPageBreak)
            {
                var summary = f.HasPageBreak ? "Добавлен разрыв страницы" : "Убран разрыв страницы";
                r.Findings.Add(new Finding
                {
                    Kind = FindingKind.PageBreak,
                    Template = input.TemplateName,
                    File = input.FileName,
                    Where = f.Location,
                    Expected = t.DisplayText,
                    Actual = f.HasPageBreak ? "есть разрыв" : "нет разрыва",
                    Summary = summary + ": " + Shorten(t.DisplayText),
                    GroupSource = t.DisplayText + "\u0001" + summary,
                });
            }
        }
    }

    private static readonly (string Name, Func<CharFormat, string> Get)[] CharAttributes =
    {
        ("Шрифт", f => f.Font),
        ("Размер", f => (f.HalfPoints / 2.0).ToString("0.#")),
        ("Жирность", f => f.Bold ? "да" : "нет"),
        ("Курсив", f => f.Italic ? "да" : "нет"),
        ("Подчёркивание", f => f.Underline),
    };

    private static void CompareRuns(CompareInput input, DocPara f, TplPara t, MatchedPair m, DocumentResult r)
    {
        var groups = new Dictionary<(string Attr, string From, string To), List<char>>();

        foreach (var s in m.Segs)
        {
            for (var i = 0; i < s.Length; i++)
            {
                var pos = s.Start + i;
                if (char.IsWhiteSpace(f.Text[pos])) continue;

                CharFormat? expected = s.Seg switch
                {
                    LiteralSeg l when l.SourceStart + i < t.Source.Formats.Length => t.Source.Formats[l.SourceStart + i],
                    HoleSeg { Kind: HoleKind.Value } h => h.Format,
                    _ => null,
                };
                if (expected is null) continue;

                var actual = f.Formats[pos];
                if (ReferenceEquals(actual, expected) || actual == expected) continue;
                foreach (var (name, get) in CharAttributes)
                {
                    var from = get(expected);
                    var to = get(actual);
                    if (from == to) continue;
                    var key = (name, from, to);
                    if (!groups.TryGetValue(key, out var chars)) groups[key] = chars = new();
                    chars.Add(f.Text[pos]);
                }
            }
        }

        foreach (var ((attr, from, to), chars) in groups)
        {
            var sample = new string(chars.Take(60).ToArray());
            r.Findings.Add(new Finding
            {
                Kind = FindingKind.CharFormatting,
                Template = input.TemplateName,
                File = input.FileName,
                Where = f.Location,
                Expected = $"{attr}: {from}",
                Actual = $"{attr}: {to}",
                Summary = $"{attr}: {from} → {to}, фрагмент «{Shorten(sample)}»",
                GroupSource = $"{t.DisplayText}\u0001{attr}\u0001{from}\u0001{to}",
            });
        }
    }

    private static void CompareParagraph(CompareInput input, DocPara f, TplPara t, DocumentResult r)
    {
        var a = t.Source.Format;
        var b = f.Format;
        if (a == b) return;

        void Check(string name, string from, string to)
        {
            if (from == to) return;
            r.Findings.Add(new Finding
            {
                Kind = FindingKind.ParaFormatting,
                Template = input.TemplateName,
                File = input.FileName,
                Where = f.Location,
                Expected = $"{name}: {from}",
                Actual = $"{name}: {to}",
                Summary = $"{name}: {from} → {to}, абзац «{Shorten(t.DisplayText)}»",
                GroupSource = $"{t.DisplayText}\u0001{name}\u0001{from}\u0001{to}",
            });
        }

        Check("Выравнивание", a.Align, b.Align);
        Check("Отступ слева", Mm(a.Left), Mm(b.Left));
        Check("Отступ справа", Mm(a.Right), Mm(b.Right));
        Check("Первая строка", Mm(a.FirstLine), Mm(b.FirstLine));
        Check("Интервал до", Mm(a.Before), Mm(b.Before));
        Check("Интервал после", Mm(a.After), Mm(b.After));
        Check("Межстрочный", $"{a.Line} ({a.LineRule})", $"{b.Line} ({b.LineRule})");
    }

    private static string Mm(int twips) => (twips / 56.6929).ToString("0.#") + " мм";

    private static void ReportLayout(CompareInput input, DocumentResult r)
    {
        var reference = (input.SiteDocument ?? input.Template.Doc).Sections;
        var actual = input.File.Sections;

        if (reference.Count != actual.Count)
        {
            var summary = $"Разделов в документе: должно быть {reference.Count}, в файле {actual.Count}";
            r.Findings.Add(new Finding
            {
                Kind = FindingKind.PageLayout,
                Template = input.TemplateName,
                File = input.FileName,
                Expected = reference.Count.ToString(),
                Actual = actual.Count.ToString(),
                Summary = summary,
                GroupSource = summary,
            });
        }

        for (var i = 0; i < Math.Min(reference.Count, actual.Count); i++)
        {
            var a = reference[i];
            var b = actual[i];
            if (a == b) continue;
            var where = reference.Count > 1 ? $"раздел {i + 1}" : "";

            void Check(string name, int from, int to)
            {
                // Редакторы округляют размеры при сохранении, на печати разница до 0,5 мм не видна.
                if (Math.Abs(from - to) < 30) return;
                var f = Mm(from);
                var t = Mm(to);
                r.Findings.Add(new Finding
                {
                    Kind = FindingKind.PageLayout,
                    Template = input.TemplateName,
                    File = input.FileName,
                    Where = where,
                    Expected = $"{name}: {f}",
                    Actual = $"{name}: {t}",
                    Summary = $"{name}: {f} → {t}",
                    GroupSource = $"{i}\u0001{name}\u0001{f}\u0001{t}",
                });
            }

            Check("Ширина страницы", a.Width, b.Width);
            Check("Высота страницы", a.Height, b.Height);
            Check("Поле сверху", a.Top, b.Top);
            Check("Поле справа", a.Right, b.Right);
            Check("Поле снизу", a.Bottom, b.Bottom);
            Check("Поле слева", a.Left, b.Left);
            Check("Колонтитул сверху", a.Header, b.Header);
            Check("Колонтитул снизу", a.Footer, b.Footer);
            Check("Переплёт", a.Gutter, b.Gutter);

            if (a.Orient != b.Orient)
            {
                var summary = $"Ориентация: {a.Orient} → {b.Orient}";
                r.Findings.Add(new Finding
                {
                    Kind = FindingKind.PageLayout,
                    Template = input.TemplateName,
                    File = input.FileName,
                    Where = where,
                    Expected = a.Orient,
                    Actual = b.Orient,
                    Summary = summary,
                    GroupSource = $"{i}\u0001{summary}",
                });
            }
        }
    }

    private static string NormalizeValue(string v) => v.Replace(ExpiredSuffix, "").Trim();

    private static void ReportValues(CompareInput input, IReadOnlyList<TplPara> tpl, Dictionary<int, List<Occurrence>> file, Dictionary<int, List<Occurrence>> site, DocumentResult r)
    {
        var reportedSections = new HashSet<int>();

        foreach (var (tplIndex, siteList) in site.OrderBy(k => k.Key))
        {
            var fileList = file.GetValueOrDefault(tplIndex) ?? new();
            var t = tpl[tplIndex];

            if (fileList.Count != siteList.Count)
            {
                if (t.Optional && reportedSections.Add(t.SectionStart))
                {
                    var summary = $"Строк в повторяющемся блоке: на сайте {siteList.Count}, в файле {fileList.Count}";
                    r.Findings.Add(new Finding
                    {
                        Kind = FindingKind.RowCount,
                        Template = input.TemplateName,
                        File = input.FileName,
                        Where = t.Source.Location,
                        Expected = siteList.Count.ToString(),
                        Actual = fileList.Count.ToString(),
                        Summary = summary + ": " + Shorten(t.DisplayText),
                        GroupSource = t.DisplayText,
                    });
                }
                continue;
            }

            for (var k = 0; k < siteList.Count; k++)
            {
                foreach (var (ordinal, siteHole) in siteList[k].Holes)
                {
                    if (siteHole.Hole.Kind == HoleKind.AutoField) continue;
                    if (!fileList[k].Holes.TryGetValue(ordinal, out var fileHole)) continue;

                    var expected = NormalizeValue(siteHole.Value);
                    var actual = NormalizeValue(fileHole.Value);
                    if (expected == actual) continue;

                    r.Findings.Add(new Finding
                    {
                        Kind = FindingKind.ValueMismatch,
                        Template = input.TemplateName,
                        File = input.FileName,
                        Where = input.File.Paragraphs[fileList[k].ParaFile].Location,
                        Tag = siteHole.Hole.Tag,
                        Expected = expected,
                        Actual = actual,
                        Summary = $"{{{siteHole.Hole.Tag}}}: «{Shorten(expected)}» → «{Shorten(actual)}»",
                        GroupSource = siteHole.Hole.Tag,
                    });
                }
            }
        }
    }

    private static void ReportUnfilled(CompareInput input, AlignResult a, IReadOnlyList<DocPara> file, Dictionary<int, List<Occurrence>> fills, DocumentResult r)
    {
        var seenEmpty = new HashSet<string>();
        foreach (var list in fills.Values)
        {
            foreach (var occurrence in list)
            {
                foreach (var (hole, value) in occurrence.Holes.Values)
                {
                    if (hole.Kind != HoleKind.Value) continue;

                    if (string.IsNullOrWhiteSpace(value))
                    {
                        if (seenEmpty.Add(hole.Tag))
                            r.Findings.Add(new Finding
                            {
                                Kind = FindingKind.EmptyField,
                                Template = input.TemplateName,
                                File = input.FileName,
                                Where = file[occurrence.ParaFile].Location,
                                Tag = hole.Tag,
                                Summary = $"Поле {{{hole.Tag}}} не заполнено",
                                GroupSource = hole.Tag,
                            });
                        continue;
                    }
                    CheckJunk(input, value, file[occurrence.ParaFile].Location, hole.Tag, r);
                }
            }
        }

        foreach (var i in a.Extra.Concat(a.Changed.Select(c => c.File)))
            CheckJunk(input, file[i].Text, file[i].Location, "", r);
    }

    private static void CheckJunk(CompareInput input, string text, string where, string tag, DocumentResult r)
    {
        foreach (var m in LeftoverTag.Matches(text).Concat(JunkWord.Matches(text)).Cast<Match>())
        {
            r.Findings.Add(new Finding
            {
                Kind = FindingKind.Unfilled,
                Template = input.TemplateName,
                File = input.FileName,
                Where = where,
                Tag = tag,
                Actual = m.Value,
                Summary = $"В тексте осталось «{m.Value}»" + (tag.Length > 0 ? $" (поле {{{tag}}})" : ""),
                GroupSource = m.Value + "\u0001" + tag,
            });
        }
    }

    private static void ReportFrozen(CompareInput input, DocumentResult r)
    {
        foreach (var f in input.Template.Frozen)
        {
            // Пустое поле ничего не хранит, а поле с {тегом} сайт заполняет как обычно.
            if (string.IsNullOrWhiteSpace(f.Value) || f.Value.Contains('{')) continue;

            r.Findings.Add(new Finding
            {
                Kind = FindingKind.FrozenField,
                Template = input.TemplateName,
                File = input.FileName,
                Where = f.Location,
                Tag = f.Name,
                Actual = f.Value,
                Summary = $"Поле Word «{f.Name}» хранит постоянный текст «{Shorten(f.Value)}»",
                GroupSource = f.Name + "\u0001" + f.Value,
            });
        }
    }
}
