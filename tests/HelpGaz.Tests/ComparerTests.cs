using System.IO.Compression;
using System.Text;
using HelpGaz.Core.Compare;
using HelpGaz.Core.Docx;
using HelpGaz.Core.Log;
using HelpGaz.Core.Site;
using Xunit;

namespace HelpGaz.Tests;

/// <summary>Все документы собираются здесь же из вымышленного текста: реальных шаблонов в репозитории нет.</summary>
public class ComparerTests
{
    private const string Ns = "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"";
    private const string Sect = "<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1134\" w:right=\"850\" w:bottom=\"1134\" w:left=\"1701\" w:header=\"708\" w:footer=\"708\" w:gutter=\"0\"/></w:sectPr>";

    private static string P(string text, string rPr = "", string pPr = "") =>
        $"<w:p>{(pPr.Length > 0 ? $"<w:pPr>{pPr}</w:pPr>" : "")}<w:r>{(rPr.Length > 0 ? $"<w:rPr>{rPr}</w:rPr>" : "")}<w:t xml:space=\"preserve\">{text}</w:t></w:r></w:p>";

    private static string Row(params string[] cells) =>
        "<w:tr>" + string.Concat(cells.Select(c => $"<w:tc>{P(c)}</w:tc>")) + "</w:tr>";

    private static string Table(params string[] rows) => "<w:tbl>" + string.Concat(rows) + "</w:tbl>";

    private static DocModel Doc(string body, string sect = Sect)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("word/document.xml");
            using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            w.Write($"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document {Ns}><w:body>{body}{sect}</w:body></w:document>");
        }
        return DocxReader.Read(ms.ToArray());
    }

    private static DocumentResult Compare(string template, string file, string? site = null, string sect = Sect) =>
        DocumentComparer.Compare(new CompareInput
        {
            FileName = "файл.docx",
            TemplateName = "шаблон.docx",
            File = Doc(file, sect),
            Template = TemplateModel.Parse(Doc(template)),
            SiteDocument = site is null ? null : Doc(site),
        });

    private static List<Finding> Real(DocumentResult r) => r.Findings.Where(f => f.Kind != FindingKind.FrozenField).ToList();

    [Fact]
    public void FilledCopyHasNoFindings()
    {
        var r = Compare(P("Адрес: {address}, заказчик {fio}"), P("Адрес: г. Энск, ул. Тестовая, 1, заказчик Петров П.П."));
        Assert.Empty(Real(r));
    }

    [Fact]
    public void ChangedTemplateTextIsReportedWithTags()
    {
        var r = Compare(P("Акт составлен по адресу {address} комиссией"), P("Акт оформлен по адресу Энск комиссией"));
        var f = Assert.Single(Real(r));
        Assert.Equal(FindingKind.TemplateText, f.Kind);
        Assert.Equal("Акт составлен по адресу {address} комиссией", f.Expected);
        Assert.Equal("Акт оформлен по адресу {address} комиссией", f.Actual);
    }

    [Fact]
    public void SameEditInDifferentFoldersGroupsTogether()
    {
        var a = Real(Compare(P("Акт составлен по адресу {address} комиссией"), P("Акт оформлен по адресу Энск комиссией")))[0];
        var b = Real(Compare(P("Акт составлен по адресу {address} комиссией"), P("Акт оформлен по адресу Заречный комиссией")))[0];
        Assert.Equal(a.GroupKey, b.GroupKey);
    }

    [Fact]
    public void ValueDifferentFromSiteIsReported()
    {
        var r = Compare(P("Заказчик: {fio}"), P("Заказчик: Сидоров"), P("Заказчик: Петров"));
        var f = Assert.Single(Real(r));
        Assert.Equal(FindingKind.ValueMismatch, f.Kind);
        Assert.Equal("fio", f.Tag);
        Assert.Equal("Петров", f.Expected);
        Assert.Equal("Сидоров", f.Actual);
    }

    [Fact]
    public void ExpiredCertificateMarkIsIgnored()
    {
        var r = Compare(P("Сертификат: {cert}"), P("Сертификат: С-1"), P("Сертификат: С-1 (истёк)"));
        Assert.Empty(Real(r));
    }

    [Fact]
    public void ParagraphLoopRepeatsWholeParagraph()
    {
        var tpl = P("Заголовок") + P("{#items}") + P("Пункт {name}") + P("{/items}") + P("Конец");
        var file = P("Заголовок") + P("Пункт один") + P("Пункт два") + P("Конец");
        Assert.Empty(Real(Compare(tpl, file)));
    }

    [Fact]
    public void LoopOnSingleParagraphRepeats()
    {
        var tpl = P("{#items}{name} — готово{/items}") + P("Конец");
        var file = P("Один — готово") + P("Два — готово") + P("Конец");
        Assert.Empty(Real(Compare(tpl, file)));
    }

    [Fact]
    public void RowLoopRepeatsCellsOutsideTags()
    {
        var tpl = Table(Row("№", "Имя", "Итог"), Row("", "{#rows}{name}", "{value}{/rows}"), Row("Всего", "", ""));
        var file = Table(Row("№", "Имя", "Итог"), Row("", "А", "1"), Row("", "Б", "2"), Row("Всего", "", ""));
        Assert.Empty(Real(Compare(tpl, file)));
    }

    [Fact]
    public void MarkerNextToContentLeavesBlankLine()
    {
        var tpl = P("{#rows}{name}") + P("{/rows}") + P("Конец");
        var file = P("А") + P("Б") + P("") + P("Конец");
        Assert.Empty(Real(Compare(tpl, file)));
    }

    [Fact]
    public void RowCountDifferenceIsReported()
    {
        var tpl = P("{#items}") + P("Пункт {name}") + P("{/items}");
        var file = P("Пункт один");
        var site = P("Пункт один") + P("Пункт два");
        var f = Assert.Single(Real(Compare(tpl, file, site)));
        Assert.Equal(FindingKind.RowCount, f.Kind);
    }

    [Fact]
    public void BoldOnLiteralIsReported()
    {
        var f = Assert.Single(Real(Compare(P("Постоянный текст"), P("Постоянный текст", "<w:b/>"))));
        Assert.Equal(FindingKind.CharFormatting, f.Kind);
        Assert.Contains("Жирность", f.Summary);
    }

    [Fact]
    public void AlignmentChangeIsReported()
    {
        var f = Assert.Single(Real(Compare(P("Текст"), P("Текст", "", "<w:jc w:val=\"center\"/>"))));
        Assert.Equal(FindingKind.ParaFormatting, f.Kind);
    }

    [Fact]
    public void MarginChangeIsReported()
    {
        var changed = Sect.Replace("w:left=\"1701\"", "w:left=\"567\"");
        var f = Assert.Single(Real(Compare(P("Текст"), P("Текст"), sect: changed)));
        Assert.Equal(FindingKind.PageLayout, f.Kind);
        Assert.Contains("Поле слева", f.Summary);
    }

    [Fact]
    public void AddedAndRemovedParagraphsAreReported()
    {
        var r = Real(Compare(P("Первый абзац текста") + P("Второй абзац текста"), P("Первый абзац текста") + P("Совсем новое дополнение")));
        Assert.Contains(r, f => f.Kind == FindingKind.ParagraphAdded);
        Assert.Contains(r, f => f.Kind == FindingKind.ParagraphMissing);
    }

    [Fact]
    public void LeftoverTagIsReported()
    {
        var r = Real(Compare(P("Адрес: {address}"), P("Адрес: {address}")));
        Assert.Contains(r, f => f.Kind == FindingKind.Unfilled);
    }

    [Fact]
    public void EmptyValueIsReported()
    {
        var f = Assert.Single(Real(Compare(P("Адрес: {address}"), P("Адрес: "))));
        Assert.Equal(FindingKind.EmptyField, f.Kind);
    }

    [Fact]
    public void WrappedLiteralInSplitRunsStillMatches()
    {
        var split = "<w:p><w:r><w:t>Постоян</w:t></w:r><w:r><w:t xml:space=\"preserve\">ный </w:t></w:r><w:r><w:t>текст</w:t></w:r></w:p>";
        Assert.Empty(Real(Compare(P("Постоянный текст"), split)));
    }

    [Fact]
    public void FolderConflictIsFoundWhenSiteValuesAgree()
    {
        var d1 = Compare(P("Адрес: {address}"), P("Адрес: Энск"), P("Адрес: Энск"));
        var d2 = Compare(P("Объект: {address}"), P("Объект: Энск"), P("Объект: Энск"));
        var d3 = Compare(P("Место: {address}"), P("Место: Заречный"), P("Место: Энск"));
        var f = Assert.Single(FolderChecks.CrossCheck(new[] { d1, d2, d3 }));
        Assert.Equal(FindingKind.FolderConflict, f.Kind);
        Assert.Equal("Заречный", f.Actual);
    }

    [Fact]
    public void LegitimatelyDifferentDatesAreNotConflict()
    {
        var d1 = Compare(P("Дата: {date}"), P("Дата: 01.02.2026"), P("Дата: 01.02.2026"));
        var d2 = Compare(P("Дата: {date}"), P("Дата: 05.03.2026"), P("Дата: 05.03.2026"));
        Assert.Empty(FolderChecks.CrossCheck(new[] { d1, d2 }));
    }

    [Fact]
    public void BrowserCopySuffixDoesNotChangeHash()
    {
        Assert.Equal(DocumentCatalog.Hash("13 Акт.docx"), DocumentCatalog.Hash("13  акт (1).docx"));
    }

    [Theory]
    [InlineData("ITD_Files_1235")]
    [InlineData("ITD_Files_1235 (2)")]
    [InlineData("itd_files_1235")]
    public void SiteArchiveNameGivesContractById(string folder)
    {
        var contracts = new[] { new Contract(1235, "", "", "", ""), new Contract(1236, "", "", "", "") };
        Assert.Equal(1235, ContractMatcher.Match(folder, contracts).Contract?.Id);
    }

    private static readonly Contract[] RealisticContracts =
    {
        new(1, "", "Газификация жилого дома на участке с к.н. 00:00:0000000:16 в д. Тестово", "Участкова Анна Николаевна", ""),
        new(2, "", "Газоснабжение квартиры №7 многоквартирного жилого дома по адресу: г. Энск, ул. Речная, д. 13", "Квартирова Юлия Сергеевна", ""),
        new(3, "", "Газификация индивидуального жилого дома по адресу: д. Заречье, д. 5", "Образцов Игорь Викторович", ""),
        new(4, "14-2023", "Газоснабжение жилого дома по адресу: д. Луговое", "Примеров Сергей Анатольевич", ""),
        new(5, "", "Газификация жилого дома по адресу: д. Старое", "Двойников Пётр Иванович", ""),
        new(6, "", "Газификация жилого дома по адресу: д. Новое", "Двойников Олег Павлович", ""),
        new(7, "494", "Газификация жилого дома по адресу: д. Лесное", "Номеров Павел Павлович", ""),
    };

    [Theory]
    [InlineData("494 Незнакомов В.А. 16 февраля 2024 (папка в газпроме, остаток оплатить)")]
    [InlineData("268 Незнакомова Т.В. 16 июня 2023")]
    [InlineData("927 Незнакомов С.А. 13 марта 2025 (Папка в газпроме,Незнаю по оплате)")]
    [InlineData("322 Незнакомова И.В. 13 июля 2023")]
    [InlineData("Журнал сварочных")]
    public void DigitsDatesAndInitialsDoNotMatchAddress(string folder)
    {
        Assert.Null(ContractMatcher.Match(folder, RealisticContracts).Contract);
    }

    [Theory]
    [InlineData("148 Образцов И.В. 03 ноября 2022", 3)]
    [InlineData("Примеров Луговое", 4)]
    [InlineData("14-2023 Примеров", 4)]
    [InlineData("Двойников д.Старое", 5)]
    [InlineData("1045 Двойников О.П. 16 сентября 2025", 6)]
    public void SurnameFindsContract(string folder, int id)
    {
        Assert.Equal(id, ContractMatcher.Match(folder, RealisticContracts).Contract?.Id);
    }

    [Fact]
    public void WrongInitialsRejectNamesake()
    {
        Assert.Null(ContractMatcher.Match("200 Образцов К.Н. 05 августа 2025", RealisticContracts).Contract);
    }

    [Fact]
    public void LeadingNumberDoesNotBeatDifferentSurname()
    {
        Assert.Null(ContractMatcher.Match("494 Незнакомов В.А. 05 февраля 2024", RealisticContracts).Contract);
    }

    [Fact]
    public void NamesakesWithoutDetailsAreNotGuessed()
    {
        var match = ContractMatcher.Match("Двойников", RealisticContracts);
        Assert.Null(match.Contract);
        Assert.Equal(2, match.Candidates.Count);
    }

    [Fact]
    public void ContractDateSeparatesNamesakesWithSameInitials()
    {
        var contracts = new[]
        {
            new Contract(1, "", "", "Близнецов Пётр Иванович", "", "2023-06-16"),
            new Contract(2, "", "", "Близнецов Павел Ильич", "", "2024-02-16"),
        };
        Assert.Equal(2, ContractMatcher.Match("10 Близнецов П.И. 16 февраля 2024", contracts).Contract?.Id);
    }

    [Fact]
    public void CompanyNameFindsContract()
    {
        var contracts = new[] { new Contract(1, "", "г. Энск", "", "ООО Ромашка"), new Contract(2, "", "г. Энск", "", "ООО Василёк") };
        Assert.Equal(1, ContractMatcher.Match("ООО Ромашка 2024", contracts).Contract?.Id);
    }

    [Fact]
    public void WholeFolderNameEqualToContractNumberMatches()
    {
        var contracts = new[] { new Contract(1, "123", "", "Кто-то", "") };
        Assert.Equal(1, ContractMatcher.Match("123", contracts).Contract?.Id);
    }

    [Fact]
    public void AddressAloneWorksWhenEveryWordIsExplained()
    {
        Assert.Equal(2, ContractMatcher.Match("Речная 13", RealisticContracts).Contract?.Id);
    }

    [Fact]
    public void SiteArchiveNameWithUnknownIdChoosesNothing()
    {
        var contracts = new[] { new Contract(1, "", "", "", "") };
        var match = ContractMatcher.Match("ITD_Files_1235 (1)", contracts);
        Assert.Null(match.Contract);
        Assert.Equal(1235, match.MissingId);
    }

    private static readonly Contract[] WithArchive = RealisticContracts.Append(new Contract(1235, "", "", "Архивов Ян Янович", "")).ToArray();

    [Theory]
    [InlineData(@"X:\ИТД\ITD_Files_1235\Новая папка", 1235, "ITD_Files_1235")]
    [InlineData(@"X:\ИТД\148 Образцов И.В. 03 ноября 2022\Акты\Новая папка", 3, "148 Образцов И.В. 03 ноября 2022")]
    [InlineData(@"X:\ИТД\ITD_Files_1235", 1235, null)]
    public void SubfolderTakesContractFromParent(string path, int id, string? parent)
    {
        var match = ContractMatcher.MatchPath(path, WithArchive);
        Assert.Equal(id, match.Contract?.Id);
        Assert.Equal(parent, match.FromParent);
    }

    [Fact]
    public void AppLogAppendsReadableLines()
    {
        var dir = Path.Combine(Path.GetTempPath(), "helpgaz-log-" + Guid.NewGuid());
        HelpGaz.Core.Diagnostics.AppLog.Init(dir);
        HelpGaz.Core.Diagnostics.AppLog.Info("первая строка");
        HelpGaz.Core.Diagnostics.AppLog.Error("вторая строка", new InvalidOperationException("сбой"));

        var file = HelpGaz.Core.Diagnostics.AppLog.CurrentFile!;
        using var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        var text = reader.ReadToEnd();
        Assert.Contains("[INFO ] первая строка", text);
        Assert.Contains("[ERROR] вторая строка", text);
        Assert.Contains("InvalidOperationException: сбой", text);
    }

    [Fact]
    public void SubfolderOfMissingArchiveReportsMissingId()
    {
        var match = ContractMatcher.MatchPath(@"X:\ИТД\ITD_Files_940\Новая папка", WithArchive);
        Assert.Null(match.Contract);
        Assert.Equal(940, match.MissingId);
    }

    [Theory]
    [InlineData(@"X:\ITD_Files_1235\Двойников\Новая папка", 2)]
    [InlineData(@"X:\ITD_Files_1235\Акты\Сварка\Новая папка", 0)]
    [InlineData(@"X:\Документы\Новая папка", 0)]
    public void ParentSearchStopsAtAmbiguousOrDistantFolders(string path, int candidates)
    {
        var match = ContractMatcher.MatchPath(path, WithArchive);
        Assert.Null(match.Contract);
        Assert.Null(match.MissingId);
        Assert.Equal(candidates, match.Candidates.Count);
    }

    [Theory]
    [InlineData("№ 77-А Петров", true)]
    [InlineData("Сидоров", false)]
    public void ContractIsChosenOnlyWithStrongEvidence(string folder, bool chosen)
    {
        var contracts = new[]
        {
            new Contract(1, "77-А", "г. Энск, ул. Тестовая, д. 5", "Петров Пётр Петрович", ""),
            new Contract(2, "78", "г. Энск, ул. Лесная, д. 9", "Сидоров Иван", ""),
            new Contract(3, "79", "г. Энск, ул. Речная, д. 3", "Сидоров Олег", ""),
        };
        var match = ContractMatcher.Match(folder, contracts);
        Assert.Equal(chosen, match.Contract is not null);
    }

    [Fact]
    public void RescanDoesNotDuplicateAndKeepsNormalStatus()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var store = new FindingsStore(path);
            var finding = Real(Compare(P("Текст шаблона"), P("Текст файла")))[0];
            var record = new FolderRecord { Path = "C:\\A" };

            store.ReplaceFolderResults(record, new[] { finding }, DateTime.Now);
            store.ReplaceFolderResults(record, new[] { finding }, DateTime.Now);
            var group = Assert.Single(store.Groups());
            Assert.Single(group.Occurrences);

            store.SetStatus(group.Key, FindingStatus.Normal);
            store.ReplaceFolderResults(record, Array.Empty<Finding>(), DateTime.Now);
            Assert.Equal(FindingStatus.Normal, Assert.Single(new FindingsStore(path).Groups()).Status);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FixedFindingReopensWhenSeenAgain()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var store = new FindingsStore(path);
            var finding = Real(Compare(P("Текст шаблона"), P("Текст файла")))[0];
            var record = new FolderRecord { Path = "C:\\A" };
            store.ReplaceFolderResults(record, new[] { finding }, DateTime.Now);
            store.SetStatus(finding.GroupKey, FindingStatus.Fixed);
            store.ReplaceFolderResults(record, new[] { finding }, DateTime.Now);
            Assert.Equal(FindingStatus.New, Assert.Single(store.Groups()).Status);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
