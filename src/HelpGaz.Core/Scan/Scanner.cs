using HelpGaz.Core.Compare;
using HelpGaz.Core.Docx;
using HelpGaz.Core.Log;
using HelpGaz.Core.Site;

namespace HelpGaz.Core.Scan;

public sealed record ScanProgress(int FolderIndex, int FolderCount, string Folder, string File);

public sealed class FolderScanResult
{
    public required string Folder { get; init; }
    public Contract? Contract { get; init; }
    public IReadOnlyList<ContractCandidate> Candidates { get; init; } = Array.Empty<ContractCandidate>();
    public int Documents { get; init; }
    public int DocumentsClean { get; init; }
    public int Findings { get; init; }
}

/// <summary>Проходит папки объектов и записывает находки в журнал.</summary>
public sealed class Scanner(SiteClient site, FindingsStore store)
{
    private readonly Dictionary<int, TemplateModel> _templates = new();

    /// <summary>Все папки, где прямо внутри есть хотя бы один .docx, включая саму указанную.</summary>
    public static List<string> FindObjectFolders(string root)
    {
        var result = new List<string>();
        void Walk(string dir)
        {
            if (DocxFiles(dir).Any()) result.Add(dir);
            string[] children;
            try { children = Directory.GetDirectories(dir); }
            catch (UnauthorizedAccessException) { return; }
            foreach (var child in children.OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase)) Walk(child);
        }
        Walk(root);
        return result;
    }

    private static IEnumerable<string> DocxFiles(string dir)
    {
        try
        {
            return Directory.GetFiles(dir, "*.docx")
                .Where(f => !Path.GetFileName(f).StartsWith("~$", StringComparison.Ordinal))
                .OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    public async Task<List<FolderScanResult>> ScanAsync(string root, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var folders = FindObjectFolders(root);
        var templates = await site.GetTemplatesAsync(ct);
        var custom = await site.GetCustomItdTemplatesAsync(ct);
        var contracts = await site.GetContractsAsync(ct);

        var results = new List<FolderScanResult>();
        for (var i = 0; i < folders.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await ScanFolderAsync(folders[i], i, folders.Count, templates, custom, contracts, progress, ct));
        }
        return results;
    }

    private async Task<FolderScanResult> ScanFolderAsync(string folder, int index, int total,
        IReadOnlyList<SiteTemplate> templates, IReadOnlyList<SiteTemplate> custom, IReadOnlyList<Contract> contracts,
        IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var (contract, candidates) = ResolveContract(folder, contracts);
        var docs = new List<DocumentResult>();
        var findings = new List<Finding>();
        var files = DocxFiles(folder).ToList();

        foreach (var path in files)
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(path);
            progress?.Report(new ScanProgress(index, total, folder, name));

            try
            {
                var resolved = DocumentCatalog.Resolve(name, templates, custom);
                if (resolved is null)
                {
                    findings.Add(new Finding
                    {
                        Kind = FindingKind.UnknownDocument,
                        Template = name,
                        File = name,
                        Summary = "Файла с таким именем нет среди документов ИТД",
                        GroupSource = DocumentCatalog.Normalize(name),
                    });
                    continue;
                }
                if (resolved.Binary) continue;

                var template = await TemplateFor(resolved, ct);
                DocModel? siteDoc = null;
                if (contract is not null)
                {
                    try
                    {
                        siteDoc = DocxReader.Read(await site.GenerateDocumentAsync(resolved.GenerateUrlPrefix, contract.Id, ct));
                    }
                    catch (SiteException e)
                    {
                        findings.Add(ErrorFinding(resolved.TemplateFile, name, "Сайт не сформировал документ: " + e.Message));
                    }
                }

                var result = DocumentComparer.Compare(new CompareInput
                {
                    FileName = name,
                    TemplateName = resolved.TemplateFile,
                    File = DocxReader.Read(await File.ReadAllBytesAsync(path, ct)),
                    Template = template,
                    SiteDocument = siteDoc,
                    CheckValues = resolved.CheckValues,
                });
                docs.Add(result);
                findings.AddRange(result.Findings);
            }
            catch (Exception e) when (e is InvalidDataException or IOException or System.Xml.XmlException or SiteException)
            {
                findings.Add(ErrorFinding(name, name, e.Message));
            }
        }

        findings.AddRange(FolderChecks.CrossCheck(docs));

        var clean = docs.Count(d => !findings.Any(f => f.File == d.File && f.Kind != FindingKind.FrozenField));
        var existing = store.Folder(folder);
        store.ReplaceFolderResults(new FolderRecord
        {
            Path = folder,
            ContractId = contract?.Id,
            ContractTitle = contract is null ? "" : Title(contract),
            ContractPinned = existing?.ContractPinned == true && contract is not null,
            Documents = docs.Count,
            DocumentsClean = clean,
            Findings = findings.Count(f => f.Kind != FindingKind.FrozenField),
            LastScan = DateTime.Now,
        }, findings, DateTime.Now);

        return new FolderScanResult
        {
            Folder = folder,
            Contract = contract,
            Candidates = candidates,
            Documents = docs.Count,
            DocumentsClean = clean,
            Findings = findings.Count(f => f.Kind != FindingKind.FrozenField),
        };
    }

    private (Contract?, IReadOnlyList<ContractCandidate>) ResolveContract(string folder, IReadOnlyList<Contract> contracts)
    {
        var known = store.Folder(folder);
        if (known?.ContractPinned == true && known.ContractId is { } id)
        {
            var pinned = contracts.FirstOrDefault(c => c.Id == id);
            if (pinned is not null) return (pinned, Array.Empty<ContractCandidate>());
        }
        var match = ContractMatcher.Match(Path.GetFileName(folder.TrimEnd('\\', '/')), contracts);
        return (match.Contract, match.Candidates);
    }

    public static string Title(Contract c)
    {
        var parts = new[] { c.Number, c.Customer, c.Address }.Where(s => !string.IsNullOrWhiteSpace(s));
        return $"#{c.Id}: " + string.Join(" · ", parts);
    }

    private static Finding ErrorFinding(string template, string file, string message) => new()
    {
        Kind = FindingKind.Error,
        Template = template,
        File = file,
        Summary = message,
        Actual = message,
        GroupSource = message,
    };

    private async Task<TemplateModel> TemplateFor(ResolvedDocument resolved, CancellationToken ct)
    {
        if (_templates.TryGetValue(resolved.TemplateId, out var cached)) return cached;
        var model = TemplateModel.Parse(DocxReader.Read(await site.DownloadTemplateAsync(resolved.TemplateId, ct)));
        _templates[resolved.TemplateId] = model;
        return model;
    }
}
