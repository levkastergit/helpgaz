using System.Text.RegularExpressions;

namespace HelpGaz.Core.Site;

public sealed record ContractCandidate(Contract Contract, int Score);

public sealed record ContractMatch(Contract? Contract, IReadOnlyList<ContractCandidate> Candidates);

/// <summary>Определяет договор по названию папки. При сомнениях договор не выбирается, а предлагается список.</summary>
public static class ContractMatcher
{
    private const int MinScore = 60;
    private const int MinMargin = 20;
    private const int MaxCandidates = 5;

    private static readonly Regex WordRx = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled);

    private static readonly HashSet<string> StopWords = new()
    {
        "ул", "улица", "д", "дом", "г", "город", "р", "н", "р-н", "район", "обл", "область", "пос", "поселок", "п", "с", "село",
        "дер", "деревня", "снт", "кв", "квартира", "пер", "переулок", "пр", "проспект", "мкр", "вблизи", "около", "уч", "участок",
        "кад", "кн", "кадастровый", "номер", "россия", "российская", "федерация", "вологодская", "вологда",
    };

    private static List<string> Tokens(string s) =>
        WordRx.Matches(s.ToLowerInvariant().Replace('ё', 'е')).Select(m => m.Value).ToList();

    public static ContractMatch Match(string folderName, IReadOnlyList<Contract> contracts)
    {
        var folderTokens = Tokens(folderName);
        var folder = folderTokens.ToHashSet();
        var candidates = contracts
            .Select(c => new ContractCandidate(c, Score(c, folderTokens, folder)))
            .Where(c => c.Score > 0)
            .OrderByDescending(c => c.Score)
            .Take(MaxCandidates)
            .ToList();

        Contract? chosen = null;
        if (candidates.Count > 0 && candidates[0].Score >= MinScore
            && (candidates.Count == 1 || candidates[0].Score - candidates[1].Score >= MinMargin))
            chosen = candidates[0].Contract;

        return new ContractMatch(chosen, candidates);
    }

    /// <summary>Номер договора может состоять из нескольких частей («77-А») и должен идти подряд.</summary>
    private static bool ContainsNumber(List<string> folder, List<string> number)
    {
        if (number.Count == 0 || string.Concat(number).Length < 2) return false;
        for (var i = 0; i + number.Count <= folder.Count; i++)
            if (number.Where((t, k) => folder[i + k] == t).Count() == number.Count)
                return true;
        return false;
    }

    private static int Score(Contract c, List<string> ordered, HashSet<string> folder)
    {
        var score = 0;

        if (ContainsNumber(ordered, Tokens(c.Number))) score += 100;

        // Номер записи слишком короткий и легко совпадает с номером дома, поэтому даёт лишь слабый сигнал.
        if (ordered.Count > 0 && ordered[0] == c.Id.ToString()) score += 30;

        var address = Tokens(c.Address).Where(t => !StopWords.Contains(t)).Distinct().ToList();
        var matched = address.Where(folder.Contains).ToList();
        score += matched.Count * 20;
        if (matched.Count >= 2 && matched.Any(t => t.Any(char.IsDigit))) score += 40;

        var customer = Tokens(c.Customer);
        if (customer.Count > 0 && customer[0].Length >= 3 && folder.Contains(customer[0]))
        {
            score += 40;
            score += customer.Skip(1).Count(folder.Contains) * 10;
        }

        score += Tokens(c.Company).Where(t => t.Length >= 4 && !StopWords.Contains(t)).Count(folder.Contains) * 15;
        return score;
    }
}
