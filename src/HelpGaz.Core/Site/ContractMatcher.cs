using System.Globalization;
using System.Text.RegularExpressions;

namespace HelpGaz.Core.Site;

public sealed record ContractCandidate(Contract Contract, int Score);

public sealed record ContractMatch(Contract? Contract, IReadOnlyList<ContractCandidate> Candidates)
{
    /// <summary>Папка названа как архив сайта ITD_Files_&lt;id&gt;, но договора с таким id в списке сайта нет.</summary>
    public int? MissingId { get; init; }

    /// <summary>Имя родительской папки, по которому определён договор; null, если по имени самой папки.</summary>
    public string? FromParent { get; init; }
}

/// <summary>
/// Определяет договор по названию папки. Неверный договор хуже отсутствующего: он засоряет журнал ложными
/// находками, поэтому при любом сомнении договор не выбирается, а предлагается список.
/// </summary>
public static class ContractMatcher
{
    private const int StrongEvidence = 100;
    private const int MinMargin = 20;
    private const int MaxCandidates = 5;

    private static readonly Regex WordRx = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled);

    /// <summary>Кнопка «Скачать все» называет архив ITD_Files_&lt;id договора&gt;, браузер добавляет « (1)» к повторным скачиваниям.</summary>
    private static readonly Regex SiteArchiveName = new(@"^ITD_Files_(\d+)(?:\s*\(\d+\))?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Notes = new(@"\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex LeadingNumber = new(@"^\s*\d+\s+(?=\p{L})", RegexOptions.Compiled);
    private static readonly Regex Person = new(@"([\p{L}-]{2,})\s+(\p{Lu})\.\s*(\p{Lu})\.?", RegexOptions.Compiled);
    private static readonly Regex NumericDate = new(@"(?<!\d)(\d{1,2})\.(\d{1,2})\.(\d{4}|\d{2})(?!\d)", RegexOptions.Compiled);
    private static readonly Regex TextDate = new(
        @"(?<!\d)(\d{1,2})\s+(января|февраля|марта|апреля|мая|июня|июля|августа|сентября|октября|ноября|декабря)\s+(\d{4})(?!\d)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] Months =
    {
        "января", "февраля", "марта", "апреля", "мая", "июня", "июля", "августа", "сентября", "октября", "ноября", "декабря",
    };

    private static readonly HashSet<string> AddressNoise = new()
    {
        "улица", "дом", "дома", "город", "район", "обл", "область", "пос", "поселок", "село", "дер", "деревня", "снт", "квартира", "квартиры",
        "пер", "переулок", "проспект", "мкр", "вблизи", "около", "участок", "участке", "кад", "кадастровый", "номер", "россия", "российская",
        "федерация", "вологодская", "вологодский", "вологда", "адресу", "газификация", "газоснабжение", "газоснабжению", "индивидуального",
        "жилого", "многоквартирного", "нежилого", "здания", "расположенных", "расположенного", "комплекс", "гаражных", "боксов", "замена",
        "газового", "оборудования", "увеличением", "расхода",
    };

    private static readonly HashSet<string> CompanyNoise = new() { "ооо", "оао", "зао", "пао", "ао", "ип", "пко", "гуп", "муп" };

    private sealed record FolderName(
        DateOnly? Date,
        string? Surname,
        string? Initials,
        List<string> AllTokens,
        HashSet<string> Words,
        HashSet<string> Numbers);

    private static string Normalize(string s) => s.ToLowerInvariant().Replace('ё', 'е');

    private static List<string> Tokens(string s) => WordRx.Matches(Normalize(s)).Select(m => m.Value).ToList();

    /// <summary>
    /// Сначала по имени самой папки. Подпапка с общим именем («Новая папка», «Акты») берёт договор
    /// у ближайшей родительской папки, но не выше <paramref name="maxParents"/> уровней.
    /// </summary>
    public static ContractMatch MatchPath(string folderPath, IReadOnlyList<Contract> contracts, int maxParents = 2)
    {
        var dir = folderPath.TrimEnd('\\', '/');
        var own = Match(Path.GetFileName(dir), contracts);
        if (own.Contract is not null || own.MissingId is not null) return own;

        var parent = Path.GetDirectoryName(dir);
        for (var level = 0; level < maxParents && !string.IsNullOrEmpty(parent); level++, parent = Path.GetDirectoryName(parent))
        {
            var name = Path.GetFileName(parent);
            if (string.IsNullOrEmpty(name)) break;
            var up = Match(name, contracts);
            if (up.Contract is not null || up.MissingId is not null) return up with { FromParent = name };
            // Родитель сам неоднозначен: выше подниматься нельзя, там уже чужие папки.
            if (up.Candidates.Count > 0) return own.Candidates.Count > 0 ? own : up;
        }
        return own;
    }

    public static ContractMatch Match(string folderName, IReadOnlyList<Contract> contracts)
    {
        var archive = SiteArchiveName.Match(folderName.Trim());
        if (archive.Success && int.TryParse(archive.Groups[1].Value, out var contractId))
        {
            var exact = contracts.FirstOrDefault(c => c.Id == contractId);
            return exact is null
                ? new ContractMatch(null, Array.Empty<ContractCandidate>()) { MissingId = contractId }
                : new ContractMatch(exact, new[] { new ContractCandidate(exact, int.MaxValue) });
        }

        var folder = Parse(folderName);
        var candidates = contracts
            .Select(c => new ContractCandidate(c, Score(c, folder)))
            .Where(c => c.Score > 0)
            .OrderByDescending(c => c.Score)
            .Take(MaxCandidates)
            .ToList();

        Contract? chosen = null;
        if (candidates.Count > 0 && candidates[0].Score >= StrongEvidence
            && (candidates.Count == 1 || candidates[0].Score - candidates[1].Score >= MinMargin))
            chosen = candidates[0].Contract;

        return new ContractMatch(chosen, candidates);
    }

    /// <summary>Разбирает название вида «494 Фамилия И.О. 16 февраля 2024 (примечание)».</summary>
    private static FolderName Parse(string name)
    {
        var text = Notes.Replace(name, " ");

        DateOnly? date = null;
        var textDate = TextDate.Match(text);
        if (textDate.Success)
        {
            date = MakeDate(textDate.Groups[3].Value, Array.IndexOf(Months, Normalize(textDate.Groups[2].Value)) + 1, textDate.Groups[1].Value);
            text = text.Remove(textDate.Index, textDate.Length);
        }
        else
        {
            var numeric = NumericDate.Match(text);
            if (numeric.Success)
            {
                date = MakeDate(numeric.Groups[3].Value, int.Parse(numeric.Groups[2].Value), numeric.Groups[1].Value);
                text = text.Remove(numeric.Index, numeric.Length);
            }
        }

        string? surname = null, initials = null;
        var person = Person.Match(text);
        if (person.Success)
        {
            surname = Tokens(person.Groups[1].Value).FirstOrDefault();
            initials = Normalize(person.Groups[2].Value + person.Groups[3].Value);
            text = text.Remove(person.Groups[2].Index, person.Index + person.Length - person.Groups[2].Index);
        }

        var all = Tokens(text);
        var tokens = Tokens(LeadingNumber.Replace(text, " "));
        return new FolderName(
            date,
            surname,
            initials,
            all,
            tokens.Where(t => t.Length >= 2 && !t.All(char.IsDigit)).ToHashSet(),
            tokens.Where(t => t.All(char.IsDigit)).ToHashSet());
    }

    private static DateOnly? MakeDate(string year, int month, string day)
    {
        var y = int.Parse(year);
        if (y < 100) y += 2000;
        var d = int.Parse(day);
        return month is >= 1 and <= 12 && d >= 1 && d <= DateTime.DaysInMonth(y, month) ? new DateOnly(y, month, d) : null;
    }

    private static DateOnly? ParseContractDate(string value)
    {
        var s = value.Trim();
        if (s.Length > 10) s = s[..10];
        return DateOnly.TryParseExact(s, new[] { "yyyy-MM-dd", "dd.MM.yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    /// <summary>Номер договора может состоять из нескольких частей («77-А») и должен идти подряд.</summary>
    private static bool ContainsSequence(List<string> folder, List<string> number)
    {
        if (number.Count == 0 || string.Concat(number).Length < 2) return false;
        for (var i = 0; i + number.Count <= folder.Count; i++)
            if (number.Where((t, k) => folder[i + k] == t).Count() == number.Count)
                return true;
        return false;
    }

    private static int Score(Contract c, FolderName folder)
    {
        var customer = Tokens(c.Customer);
        var surname = customer.FirstOrDefault() ?? "";
        var company = Tokens(c.Company).Where(t => t.Length >= 3 && !CompanyNoise.Contains(t)).ToList();
        var score = 0;

        // Фамилия с инициалами названа явно: другой заказчик исключается, какие бы числа ни совпали.
        if (folder.Surname is not null)
        {
            if (folder.Surname == surname)
            {
                if (!InitialsAgree(customer, folder.Initials!)) return 0;
                score += 50;
            }
            else if (!company.Contains(folder.Surname))
            {
                return 0;
            }
        }

        var surnameHit = surname.Length >= 3 && folder.Words.Contains(surname);
        if (surnameHit) score += StrongEvidence;
        if (company.Count > 0 && company.All(folder.Words.Contains)) score += StrongEvidence;

        var number = Tokens(c.Number);
        if (ContainsSequence(folder.AllTokens, number))
        {
            // Простое число легко совпадает с порядковым номером в названии папки, поэтому одного его мало.
            var compound = number.Count >= 2 || number.Any(t => !t.All(char.IsDigit));
            if (compound || surnameHit || folder.AllTokens.Count == number.Count) score += StrongEvidence;
        }

        var addressTokens = Tokens(c.Address);
        var addressWords = addressTokens.Where(t => t.Length >= 3 && !t.Any(char.IsDigit) && !AddressNoise.Contains(t)).ToHashSet();
        var matched = addressWords.Count(folder.Words.Contains);

        if (score > 0)
        {
            score += matched * 30;
        }
        else if (folder.Surname is null && matched > 0 && folder.Words.All(addressWords.Contains))
        {
            // Без фамилии адрес убедителен, только если в названии нет ни одного постороннего слова.
            var house = folder.Numbers.Any(addressTokens.Contains);
            score = matched >= 2 || house ? StrongEvidence + matched * 30 : 30;
        }

        if (score > 0 && folder.Date is { } date && ParseContractDate(c.Date) == date) score += 40;
        return score;
    }

    private static bool InitialsAgree(List<string> customer, string initials)
    {
        if (customer.Count >= 2 && customer[1][0] != initials[0]) return false;
        return customer.Count < 3 || customer[2][0] == initials[1];
    }
}
