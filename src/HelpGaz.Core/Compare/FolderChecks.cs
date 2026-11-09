namespace HelpGaz.Core.Compare;

/// <summary>Сверка документов одной папки между собой.</summary>
public static class FolderChecks
{
    /// <summary>Эти значения обязаны совпадать во всех документах, даже когда договор не привязан.</summary>
    private static readonly HashSet<string> IdentityTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "address", "fio", "fioFull", "fioShort",
    };

    public static List<Finding> CrossCheck(IReadOnlyList<DocumentResult> docs)
    {
        var findings = new List<Finding>();
        var tags = docs.SelectMany(d => d.FileValues.Select(v => v.Tag)).Distinct();

        foreach (var tag in tags)
        {
            var perDoc = docs
                .Select(d => (Doc: d, Values: d.FileValues.Where(v => v.Tag == tag && !string.IsNullOrWhiteSpace(v.Value)).ToList()))
                .Where(x => x.Values.Count > 0)
                .ToList();
            if (perDoc.Count < 2) continue;

            var distinct = perDoc.SelectMany(x => x.Values.Select(v => Norm(v.Value))).Distinct().ToList();
            if (distinct.Count < 2) continue;

            if (!MustMatch(tag, perDoc.Select(x => x.Doc).ToList())) continue;

            var majority = perDoc
                .SelectMany(x => x.Values.Select(v => Norm(v.Value)).Distinct().Select(v => (x.Doc, Value: v)))
                .GroupBy(x => x.Value)
                .OrderByDescending(g => g.Count())
                .First().Key;

            foreach (var (doc, values) in perDoc)
            {
                var odd = values.FirstOrDefault(v => Norm(v.Value) != majority);
                if (odd is null) continue;
                findings.Add(new Finding
                {
                    Kind = FindingKind.FolderConflict,
                    Template = doc.Template,
                    File = doc.File,
                    Where = odd.Where,
                    Tag = tag,
                    Expected = majority,
                    Actual = odd.Value,
                    Summary = $"{{{tag}}}: в этом документе «{odd.Value}», в остальных «{majority}»",
                    GroupSource = tag,
                });
            }
        }
        return findings;
    }

    /// <summary>
    /// Значения считаются обязанными совпадать, если в документах сайта они одинаковы. Иначе расхождение
    /// законно: например, у разных актов разные даты.
    /// </summary>
    private static bool MustMatch(string tag, List<DocumentResult> docs)
    {
        if (docs.All(d => d.HasSiteDocument))
        {
            var site = docs.SelectMany(d => d.SiteValues.Where(v => v.Tag == tag).Select(v => Norm(v.Value))).Distinct().Count();
            return site == 1;
        }
        return IdentityTags.Contains(tag);
    }

    private static string Norm(string v) => string.Join(' ', v.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
