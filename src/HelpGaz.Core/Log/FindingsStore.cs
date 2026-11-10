using System.Text.Json;
using System.Text.Json.Serialization;
using HelpGaz.Core.Compare;

namespace HelpGaz.Core.Log;

public enum FindingStatus { New, FixTemplate, Normal, Fixed }

public static class FindingStatuses
{
    public static string Title(FindingStatus s) => s switch
    {
        FindingStatus.New => "Новая",
        FindingStatus.FixTemplate => "Править шаблон",
        FindingStatus.Normal => "Это норма",
        FindingStatus.Fixed => "Исправлено",
        _ => s.ToString(),
    };
}

public sealed class Occurrence
{
    public string Folder { get; set; } = "";
    public string File { get; set; } = "";
    public string Where { get; set; } = "";
    public string Expected { get; set; } = "";
    public string Actual { get; set; } = "";
    public DateTime SeenAt { get; set; }
}

public sealed class FindingGroup
{
    public string Key { get; set; } = "";
    public FindingKind Kind { get; set; }
    public string Template { get; set; } = "";
    public string Tag { get; set; } = "";
    public string Expected { get; set; } = "";
    public string Actual { get; set; } = "";
    public string Summary { get; set; } = "";
    public FindingStatus Status { get; set; } = FindingStatus.New;
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
    public List<Occurrence> Occurrences { get; set; } = new();

    [JsonIgnore]
    public int FolderCount => Occurrences.Select(o => o.Folder).Distinct(StringComparer.OrdinalIgnoreCase).Count();
}

public sealed class FolderRecord
{
    public string Path { get; set; } = "";
    public int? ContractId { get; set; }
    public string ContractTitle { get; set; } = "";

    /// <summary>Откуда взят договор («По папке …», «Выбран вручную») или почему его нет («Договора #940 нет на сайте»).</summary>
    public string ContractNote { get; set; } = "";

    /// <summary>Договор выбран вручную: при повторных проверках определять его заново нельзя.</summary>
    public bool ContractPinned { get; set; }
    public int Documents { get; set; }
    public int DocumentsClean { get; set; }
    public int Findings { get; set; }
    public DateTime? LastScan { get; set; }

    /// <summary>Версия программы, которой проверена папка. Пусто — проверено версией до 1.0.4.</summary>
    public string AppVersion { get; set; } = "";
}

/// <summary>Журнал находок. Хранится в одном JSON-файле, чтобы его можно было открыть и передать для правки шаблонов.</summary>
public sealed class FindingsStore
{
    private sealed class Data
    {
        public int Version { get; set; } = 1;
        public List<FindingGroup> Groups { get; set; } = new();
        public List<FolderRecord> Folders { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _path;
    private readonly object _lock = new();
    private Data _data = new();

    public FindingsStore(string path)
    {
        _path = Path.GetFullPath(path);
        Load();
    }

    public string FilePath => _path;

    private void Load()
    {
        if (!System.IO.File.Exists(_path)) return;
        try
        {
            _data = JsonSerializer.Deserialize<Data>(System.IO.File.ReadAllText(_path), Json) ?? new Data();
        }
        catch (JsonException)
        {
            // Повреждённый журнал не должен молча затереться следующей проверкой.
            System.IO.File.Move(_path, _path + $".broken-{DateTime.Now:yyyyMMdd-HHmmss}");
            _data = new Data();
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        System.IO.File.WriteAllText(temp, JsonSerializer.Serialize(_data, Json));
        System.IO.File.Move(temp, _path, overwrite: true);
    }

    public IReadOnlyList<FindingGroup> Groups()
    {
        lock (_lock) return _data.Groups.ToList();
    }

    public IReadOnlyList<FolderRecord> Folders()
    {
        lock (_lock) return _data.Folders.ToList();
    }

    public FolderRecord? Folder(string path)
    {
        lock (_lock) return _data.Folders.FirstOrDefault(f => Same(f.Path, path));
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Заменяет результаты папки новыми: повторная проверка не создаёт дублей.</summary>
    public void ReplaceFolderResults(FolderRecord record, IReadOnlyList<Finding> findings, DateTime now)
    {
        lock (_lock)
        {
            foreach (var g in _data.Groups)
                g.Occurrences.RemoveAll(o => Same(o.Folder, record.Path));

            foreach (var f in findings)
            {
                var group = _data.Groups.FirstOrDefault(g => g.Key == f.GroupKey);
                if (group is null)
                {
                    group = new FindingGroup
                    {
                        Key = f.GroupKey,
                        Kind = f.Kind,
                        Template = f.Template,
                        Tag = f.Tag,
                        Expected = f.Expected,
                        Actual = f.Actual,
                        Summary = f.Summary,
                        FirstSeen = now,
                    };
                    _data.Groups.Add(group);
                }
                else if (group.Status == FindingStatus.Fixed)
                {
                    // Отличие снова встретилось: правка не помогла.
                    group.Status = FindingStatus.New;
                }

                group.LastSeen = now;
                group.Occurrences.Add(new Occurrence
                {
                    Folder = record.Path,
                    File = f.File,
                    Where = f.Where,
                    Expected = f.Expected,
                    Actual = f.Actual,
                    SeenAt = now,
                });
            }

            // «Норму» держим и без вхождений, иначе решение пользователя потеряется при следующей проверке.
            _data.Groups.RemoveAll(g => g.Occurrences.Count == 0 && g.Status != FindingStatus.Normal);

            var existing = _data.Folders.FindIndex(f => Same(f.Path, record.Path));
            if (existing >= 0) _data.Folders[existing] = record;
            else _data.Folders.Add(record);

            Save();
        }
    }

    public void SetStatus(string key, FindingStatus status)
    {
        lock (_lock)
        {
            var group = _data.Groups.FirstOrDefault(g => g.Key == key);
            if (group is null) return;
            group.Status = status;
            Save();
        }
    }

    public void SetContract(string path, int? contractId, string title)
    {
        lock (_lock)
        {
            var folder = _data.Folders.FirstOrDefault(f => Same(f.Path, path));
            if (folder is null) _data.Folders.Add(folder = new FolderRecord { Path = path });
            folder.ContractId = contractId;
            folder.ContractTitle = title;
            folder.ContractPinned = contractId is not null;
            Save();
        }
    }
}
