using System.Text;
using System.Text.RegularExpressions;

namespace HelpGaz.Core.Compare;

public enum DiffKind { Equal, Delete, Insert }
public sealed record DiffOp(DiffKind Kind, string Text);

public sealed record HoleFill(HoleSeg Hole, string Value);
public sealed record ChangedText(string Actual, List<HoleFill> Fills, bool LiteralChanged);

/// <summary>Пословное сравнение строк и строки шаблона с «дырками».</summary>
public static class TextDiff
{
    private static readonly Regex TokenRx = new(@"\p{L}+|\p{N}+|\s+|.", RegexOptions.Compiled | RegexOptions.Singleline);
    private const long MaxCells = 4_000_000;

    private static List<string> Tokenize(string s) => TokenRx.Matches(s).Select(m => m.Value).ToList();

    public static List<DiffOp> Diff(string a, string b)
    {
        var x = Tokenize(a);
        var y = Tokenize(b);
        var pairs = Lcs(x.Count, y.Count, (i, j) => x[i] == y[j]);

        var ops = new List<DiffOp>();
        int i = 0, j = 0;
        foreach (var (pi, pj) in pairs)
        {
            Emit(ops, DiffKind.Delete, x, i, pi);
            Emit(ops, DiffKind.Insert, y, j, pj);
            Emit(ops, DiffKind.Equal, x, pi, pi + 1);
            i = pi + 1;
            j = pj + 1;
        }
        Emit(ops, DiffKind.Delete, x, i, x.Count);
        Emit(ops, DiffKind.Insert, y, j, y.Count);
        return ops;
    }

    private static void Emit(List<DiffOp> ops, DiffKind kind, List<string> tokens, int from, int to)
    {
        if (from >= to) return;
        var text = string.Concat(tokens.Skip(from).Take(to - from));
        if (ops.Count > 0 && ops[^1].Kind == kind) ops[^1] = ops[^1] with { Text = ops[^1].Text + text };
        else ops.Add(new DiffOp(kind, text));
    }

    /// <summary>
    /// Сравнивает абзац шаблона с текстом файла, который не совпал точно. Участки файла напротив «дырок»
    /// считаются значениями и в итоговой строке заменяются на {тег}, чтобы одинаковая правка из разных
    /// папок дала одинаковый текст.
    /// </summary>
    public static ChangedText CompareWithTemplate(TplPara t, string s)
    {
        var tpl = new List<(string? Text, HoleSeg? Hole)>();
        foreach (var seg in t.Segs)
        {
            if (seg is HoleSeg h) tpl.Add((null, h));
            else foreach (var tok in Tokenize(((LiteralSeg)seg).Text)) tpl.Add((tok, null));
        }
        var doc = Tokenize(s);
        var pairs = Lcs(tpl.Count, doc.Count, (i, j) => tpl[i].Text is not null && tpl[i].Text == doc[j]);

        var actual = new StringBuilder();
        var fills = new List<HoleFill>();
        var changed = false;

        void Gap(int ta, int tb, int da, int db)
        {
            if (ta >= tb && da >= db) return;
            var text = string.Concat(doc.Skip(da).Take(db - da));
            var holes = tpl.Skip(ta).Take(tb - ta).Where(x => x.Hole is not null).Select(x => x.Hole!).ToList();
            var literal = tpl.Skip(ta).Take(tb - ta).Where(x => x.Text is not null).Select(x => x.Text!).ToList();

            if (holes.Count > 0 && literal.All(string.IsNullOrWhiteSpace))
            {
                fills.Add(new HoleFill(holes[0], text.Trim()));
                actual.Append('{').Append(holes[0].Tag).Append('}');
                return;
            }
            changed = true;
            actual.Append(text);
        }

        int i = 0, j = 0;
        foreach (var (pi, pj) in pairs)
        {
            Gap(i, pi, j, pj);
            actual.Append(doc[pj]);
            i = pi + 1;
            j = pj + 1;
        }
        Gap(i, tpl.Count, j, doc.Count);

        return new ChangedText(actual.ToString(), fills, changed);
    }

    /// <summary>Наибольшая общая подпоследовательность. Общие начало и конец отсекаются заранее.</summary>
    private static List<(int, int)> Lcs(int n, int m, Func<int, int, bool> eq)
    {
        var result = new List<(int, int)>();
        var start = 0;
        while (start < n && start < m && eq(start, start))
        {
            result.Add((start, start));
            start++;
        }

        var endN = n;
        var endM = m;
        var tail = new List<(int, int)>();
        while (endN > start && endM > start && eq(endN - 1, endM - 1))
        {
            endN--;
            endM--;
            tail.Insert(0, (endN, endM));
        }

        var a = endN - start;
        var b = endM - start;
        if (a > 0 && b > 0 && a * (long)b <= MaxCells)
        {
            var dp = new int[a + 1, b + 1];
            for (var i = a - 1; i >= 0; i--)
                for (var j = b - 1; j >= 0; j--)
                    dp[i, j] = eq(start + i, start + j) ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);

            int x = 0, y = 0;
            while (x < a && y < b)
            {
                if (eq(start + x, start + y) && dp[x, y] == dp[x + 1, y + 1] + 1)
                {
                    result.Add((start + x, start + y));
                    x++;
                    y++;
                }
                else if (dp[x + 1, y] >= dp[x, y + 1]) x++;
                else y++;
            }
        }

        result.AddRange(tail);
        return result;
    }
}
