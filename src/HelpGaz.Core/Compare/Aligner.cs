using System.Text.RegularExpressions;
using HelpGaz.Core.Docx;

namespace HelpGaz.Core.Compare;

/// <summary>Где в тексте абзаца оказался очередной кусок шаблона.</summary>
public readonly record struct SegMatch(Seg Seg, int Start, int Length);

public sealed record MatchedPair(int File, int Tpl, List<SegMatch> Segs);
public sealed record ChangedPair(int File, int Tpl);

public sealed class AlignResult
{
    public List<MatchedPair> Matched { get; } = new();
    public List<ChangedPair> Changed { get; } = new();
    public List<int> Extra { get; } = new();
    public List<int> Missing { get; } = new();
}

public static class ParaMatcher
{
    /// <summary>
    /// Проверяет абзац на соответствие шаблону: постоянный текст должен совпасть точно и в том же порядке,
    /// «дырки» принимают что угодно. Поиск идёт слева направо без отката, поэтому работает за линейное время.
    /// </summary>
    public static bool TryMatch(TplPara t, string s, List<SegMatch>? output)
    {
        var segs = t.Segs;
        output?.Clear();

        if (segs.Count == 0) return s.Length == 0;

        var pos = 0;
        var end = s.Length;
        var first = 0;
        var last = segs.Count;
        LiteralSeg? tail = null;

        if (segs[0] is LiteralSeg head)
        {
            if (!s.StartsWith(head.Text, StringComparison.Ordinal)) return false;
            output?.Add(new SegMatch(head, 0, head.Text.Length));
            pos = head.Text.Length;
            first = 1;
            if (segs.Count == 1) return pos == s.Length;
        }

        if (segs[^1] is LiteralSeg l)
        {
            if (s.Length - pos < l.Text.Length || !s.EndsWith(l.Text, StringComparison.Ordinal)) return false;
            end = s.Length - l.Text.Length;
            last = segs.Count - 1;
            tail = l;
        }

        var pending = new List<Seg>();
        for (var k = first; k < last; k++)
        {
            if (segs[k] is HoleSeg)
            {
                pending.Add(segs[k]);
                continue;
            }

            var lit = (LiteralSeg)segs[k];
            var idx = s.IndexOf(lit.Text, pos, end - pos, StringComparison.Ordinal);
            if (idx < 0) return false;
            Flush(pending, pos, idx, output);
            output?.Add(new SegMatch(lit, idx, lit.Text.Length));
            pos = idx + lit.Text.Length;
        }

        Flush(pending, pos, end, output);
        if (tail is not null) output?.Add(new SegMatch(tail, end, tail.Text.Length));
        return true;
    }

    private static void Flush(List<Seg> pending, int start, int end, List<SegMatch>? output)
    {
        for (var i = 0; i < pending.Count; i++)
            output?.Add(i == 0 ? new SegMatch(pending[i], start, end - start) : new SegMatch(pending[i], end, 0));
        pending.Clear();
    }
}

/// <summary>Сопоставляет абзацы документа абзацам шаблона с учётом циклов и условий.</summary>
public static class Aligner
{
    private const double MinSimilarity = 0.5;

    public static AlignResult Align(IReadOnlyList<DocPara> doc, IReadOnlyList<TplPara> tpl)
    {
        var n = doc.Count;
        var m = tpl.Count;
        var width = m + 1;
        var best = new int[(n + 1) * width];
        var step = new byte[(n + 1) * width];

        var cache = new Dictionary<(string, int), bool>();
        bool Matches(int i, int j)
        {
            var t = tpl[j];
            var s = doc[i].Text;
            if (s.Length < t.LiteralLength || doc[i].Container != t.Source.Container) return false;

            // Абзац только с маркером цикла исчезает из основного текста, а в ячейке таблицы остаётся пустым.
            if (t.Removable) return s.Length == 0 && (t.KeepsBlank || t.Source.Container.Contains("table:"));
            var key = (s, j);
            if (!cache.TryGetValue(key, out var r))
                cache[key] = r = ParaMatcher.TryMatch(t, s, null);
            return r;
        }

        for (var i = n; i >= 0; i--)
        {
            for (var j = m; j >= 0; j--)
            {
                if (i == n && j == m) continue;

                var v = int.MinValue;
                byte st = 0;
                if (i < n) { v = best[(i + 1) * width + j]; st = 1; }
                if (j < m && best[i * width + j + 1] > v) { v = best[i * width + j + 1]; st = 2; }

                if (i < n && j < m && Matches(i, j))
                {
                    // Пустой абзац напротив абзаца из одних тегов ничего не доказывает.
                    var w = tpl[j].LiteralLength == 0 && doc[i].Text.Length == 0 && tpl[j].Segs.Count > 0 ? 0 : 1 + tpl[j].LiteralLength;
                    var c = w + best[(i + 1) * width + j + 1];
                    if (c >= v) { v = c; st = 3; }
                    for (var x = 0; x < tpl[j].LoopStarts.Count && x < 200; x++)
                    {
                        var jump = w + best[(i + 1) * width + tpl[j].LoopStarts[x]];
                        if (jump > v) { v = jump; st = (byte)(4 + x); }
                    }
                }

                best[i * width + j] = v;
                step[i * width + j] = st;
            }
        }

        var result = new AlignResult();
        var gapDoc = new List<int>();
        var gapTpl = new List<int>();
        int a = 0, b = 0;
        while (a < n || b < m)
        {
            var st = step[a * width + b];
            if (st == 1) { gapDoc.Add(a++); continue; }
            if (st == 2) { gapTpl.Add(b++); continue; }

            FlushGap(doc, tpl, gapDoc, gapTpl, result);
            var segs = new List<SegMatch>();
            ParaMatcher.TryMatch(tpl[b], doc[a].Text, segs);
            result.Matched.Add(new MatchedPair(a, b, segs));
            var next = st == 3 ? b + 1 : tpl[b].LoopStarts[st - 4];
            a++;
            b = next;
        }
        FlushGap(doc, tpl, gapDoc, gapTpl, result);
        return result;
    }

    private static void FlushGap(IReadOnlyList<DocPara> doc, IReadOnlyList<TplPara> tpl, List<int> gapDoc, List<int> gapTpl, AlignResult result)
    {
        if (gapDoc.Count == 0 && gapTpl.Count == 0) return;

        var usedDoc = new bool[gapDoc.Count];
        var usedTpl = new bool[gapTpl.Count];

        if (gapDoc.Count > 0 && gapTpl.Count > 0)
        {
            var sim = new double[gapDoc.Count, gapTpl.Count];
            var words = gapDoc.Select(i => Words(doc[i].Text)).ToArray();
            for (var i = 0; i < gapDoc.Count; i++)
                for (var j = 0; j < gapTpl.Count; j++)
                    sim[i, j] = doc[gapDoc[i]].Container == tpl[gapTpl[j]].Source.Container
                        ? Similarity(words[i], tpl[gapTpl[j]].Words)
                        : 0;

            var dp = new double[gapDoc.Count + 1, gapTpl.Count + 1];
            for (var i = gapDoc.Count - 1; i >= 0; i--)
                for (var j = gapTpl.Count - 1; j >= 0; j--)
                {
                    dp[i, j] = Math.Max(dp[i + 1, j], dp[i, j + 1]);
                    if (sim[i, j] >= MinSimilarity) dp[i, j] = Math.Max(dp[i, j], sim[i, j] + dp[i + 1, j + 1]);
                }

            int x = 0, y = 0;
            while (x < gapDoc.Count && y < gapTpl.Count)
            {
                if (sim[x, y] >= MinSimilarity && Math.Abs(dp[x, y] - (sim[x, y] + dp[x + 1, y + 1])) < 1e-9)
                {
                    result.Changed.Add(new ChangedPair(gapDoc[x], gapTpl[y]));
                    usedDoc[x++] = true;
                    usedTpl[y++] = true;
                }
                else if (dp[x + 1, y] >= dp[x, y + 1]) x++;
                else y++;
            }
        }

        for (var i = 0; i < gapDoc.Count; i++)
            if (!usedDoc[i]) result.Extra.Add(gapDoc[i]);
        for (var j = 0; j < gapTpl.Count; j++)
            if (!usedTpl[j] && !tpl[gapTpl[j]].Optional && !tpl[gapTpl[j]].Removable) result.Missing.Add(gapTpl[j]);

        gapDoc.Clear();
        gapTpl.Clear();
    }

    private static HashSet<string> Words(string s)
    {
        var set = new HashSet<string>();
        foreach (Match m in TemplateModel.WordRx.Matches(s)) set.Add(m.Value.ToLowerInvariant());
        return set;
    }

    private static double Similarity(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var common = a.Count(b.Contains);
        return 2.0 * common / (a.Count + b.Count);
    }
}
