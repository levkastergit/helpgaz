using System.Diagnostics;
using HelpGaz.Core.Compare;
using HelpGaz.Core.Diagnostics;
using HelpGaz.Core.Docx;
using HelpGaz.Core.Log;
using HelpGaz.Core.Scan;
using HelpGaz.Core.Site;

namespace HelpGaz.App;

internal sealed class MainForm : Form
{
    private readonly SiteClient _site = new();
    private readonly FindingsStore _store = new(AppPaths.Findings);
    private readonly TemplateCache _templateCache = new(AppPaths.Templates);
    private readonly Dictionary<string, (DateTime Stamp, TemplateModel Model)> _templateModels = new();
    private int _detailsVersion;
    private readonly SemaphoreSlim _templateDownload = new(1, 1);
    private IReadOnlyList<SiteTemplate>? _siteTemplates;
    private readonly System.Windows.Forms.Timer _detailsDelay = new() { Interval = 150 };
    private string _detailsHtml = "";
    private readonly Dictionary<string, IReadOnlyList<ContractCandidate>> _candidates = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _scan;
    private string? _email;
    private bool _busy;

    private readonly TextBox _path = new() { Dock = DockStyle.Fill, AllowDrop = true, PlaceholderText = "Перетащите сюда папку или нажмите «Обзор»" };
    private readonly Button _browse = new() { Text = "Обзор…", AutoSize = true };
    private readonly Button _start = new() { Text = "Проверить", AutoSize = true, Font = new Font(DefaultFont, FontStyle.Bold) };
    private readonly Button _stop = new() { Text = "Стоп", AutoSize = true, Enabled = false };
    private readonly CheckBox _onlyCurrent = new() { Text = "Показывать только эту папку", Checked = true, AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly LinkLabel _account = new() { AutoSize = true, Anchor = AnchorStyles.Right };

    private readonly DataGridView _folders = Grid();
    private readonly Button _pick = new() { Text = "Выбрать договор…", AutoSize = true };
    private readonly Button _rescan = new() { Text = "Проверить папку заново", AutoSize = true };

    private readonly ComboBox _statusFilter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
    private readonly ComboBox _kindFilter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 210 };
    private readonly TextBox _search = new() { Width = 260, PlaceholderText = "Поиск по шаблону и тексту" };
    private readonly Label _count = new() { AutoSize = true, Margin = new Padding(12, 7, 3, 3) };
    private readonly DataGridView _groups = Grid();
    private readonly DataGridView _occurrences = Grid();
    private readonly WebBrowser _view = new()
    {
        Dock = DockStyle.Fill,
        AllowWebBrowserDrop = false,
        ScriptErrorsSuppressed = true,
        WebBrowserShortcutsEnabled = true,
    };

    private readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft, Text = "Готово" };
    private readonly ToolStripProgressBar _progress = new() { Width = 220, Visible = false };

    public MainForm()
    {
        Text = $"HelpGaz — сверка ИТД с шаблонами (v{Updater.CurrentVersion.ToString(3)})";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1240, 760);
        MinimumSize = new Size(960, 560);
        AllowDrop = true;

        BuildLayout();
        WireEvents();
        RefreshAll();
    }

    private static DataGridView Grid() => new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false,
        RowHeadersVisible = false,
        BackgroundColor = SystemColors.Window,
        BorderStyle = BorderStyle.FixedSingle,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
    };

    private static void AddColumn(DataGridView grid, string header, int weight, DataGridViewContentAlignment align = DataGridViewContentAlignment.MiddleLeft)
    {
        var column = new DataGridViewTextBoxColumn { HeaderText = header, FillWeight = weight, SortMode = DataGridViewColumnSortMode.Automatic };
        column.DefaultCellStyle.Alignment = align;
        grid.Columns.Add(column);
    }

    private void BuildLayout()
    {
        var top = new TableLayoutPanel { Dock = DockStyle.Top, Height = 46, ColumnCount = 7, Padding = new Padding(8, 8, 8, 4) };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 5; i++) top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.Controls.Add(new Label { Text = "Папка ИТД:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        top.Controls.Add(_path, 1, 0);
        top.Controls.Add(_browse, 2, 0);
        top.Controls.Add(_start, 3, 0);
        top.Controls.Add(_stop, 4, 0);
        top.Controls.Add(_onlyCurrent, 5, 0);
        top.Controls.Add(_account, 6, 0);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildFoldersTab());
        tabs.TabPages.Add(BuildFindingsTab());

        var strip = new StatusStrip();
        strip.Items.Add(_status);
        strip.Items.Add(_progress);

        Controls.Add(tabs);
        Controls.Add(top);
        Controls.Add(strip);
    }

    private TabPage BuildFoldersTab()
    {
        AddColumn(_folders, "Папка", 28);
        AddColumn(_folders, "Договор", 34);
        AddColumn(_folders, "Документов", 9, DataGridViewContentAlignment.MiddleCenter);
        AddColumn(_folders, "Совпало", 8, DataGridViewContentAlignment.MiddleCenter);
        AddColumn(_folders, "Находок", 8, DataGridViewContentAlignment.MiddleCenter);
        AddColumn(_folders, "Итог", 12);
        AddColumn(_folders, "Проверено", 20);

        var log = new Button { Text = "Лог программы", AutoSize = true, Margin = new Padding(24, 3, 3, 3) };
        log.Click += (_, _) => OpenLog();

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(2) };
        buttons.Controls.Add(_pick);
        buttons.Controls.Add(_rescan);
        buttons.Controls.Add(log);

        var page = new TabPage("Папки") { Padding = new Padding(6) };
        page.Controls.Add(_folders);
        page.Controls.Add(buttons);
        return page;
    }

    private TabPage BuildFindingsTab()
    {
        _statusFilter.Items.AddRange(new object[] { "Требуют решения", "Все статусы" });
        foreach (var s in Enum.GetValues<FindingStatus>()) _statusFilter.Items.Add(FindingStatuses.Title(s));
        _statusFilter.SelectedIndex = 0;

        _kindFilter.Items.Add("Все типы");
        foreach (var k in Enum.GetValues<FindingKind>()) _kindFilter.Items.Add(FindingKinds.Title(k));
        _kindFilter.SelectedIndex = 0;

        var filters = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(2) };
        filters.Controls.Add(_statusFilter);
        filters.Controls.Add(_kindFilter);
        filters.Controls.Add(_search);
        filters.Controls.Add(_count);

        AddColumn(_groups, "Статус", 10);
        AddColumn(_groups, "Тип", 15);
        AddColumn(_groups, "Шаблон", 25);
        AddColumn(_groups, "Суть", 44);
        AddColumn(_groups, "Папок", 6, DataGridViewContentAlignment.MiddleCenter);

        AddColumn(_occurrences, "Папка", 35);
        AddColumn(_occurrences, "Файл", 35);
        AddColumn(_occurrences, "Где", 30);

        var statusButtons = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 38, Padding = new Padding(0, 2, 0, 0) };
        foreach (var s in Enum.GetValues<FindingStatus>())
        {
            var status = s;
            var b = new Button { Text = FindingStatuses.Title(status), AutoSize = true };
            b.Click += (_, _) => ApplyStatus(status);
            statusButtons.Controls.Add(b);
        }
        var open = new Button { Text = "Открыть файл", AutoSize = true, Margin = new Padding(24, 3, 3, 3) };
        open.Click += (_, _) => OpenSelectedFile();
        statusButtons.Controls.Add(open);
        var journal = new Button { Text = "Папка журнала", AutoSize = true };
        journal.Click += (_, _) => { Directory.CreateDirectory(AppPaths.Root); Process.Start(new ProcessStartInfo(AppPaths.Root) { UseShellExecute = true }); };
        statusButtons.Controls.Add(journal);

        var right = new Panel { Dock = DockStyle.Fill };
        right.Controls.Add(_occurrences);
        right.Controls.Add(statusButtons);

        var wide = new Button { Text = "Открыть сравнение в отдельном окне", AutoSize = true, Dock = DockStyle.Bottom };
        wide.Click += (_, _) => ShowComparisonWindow();
        var viewFrame = new Panel { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle };
        viewFrame.Controls.Add(_view);
        var left = new Panel { Dock = DockStyle.Fill };
        left.Controls.Add(viewFrame);
        left.Controls.Add(wide);

        var bottom = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = 6 };
        bottom.Panel1.Controls.Add(left);
        bottom.Panel2.Controls.Add(right);
        bottom.HandleCreated += (_, _) => bottom.SplitterDistance = bottom.Width * 65 / 100;

        var main = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 6 };
        main.Panel1.Controls.Add(_groups);
        main.Panel2.Controls.Add(bottom);
        main.HandleCreated += (_, _) => main.SplitterDistance = main.Height * 40 / 100;

        var page = new TabPage("Находки") { Padding = new Padding(6) };
        page.Controls.Add(main);
        page.Controls.Add(filters);
        return page;
    }

    private void WireEvents()
    {
        Shown += async (_, _) => await OnShownAsync();
        FormClosing += (_, _) => _scan?.Cancel();

        _browse.Click += (_, _) => Browse();
        _start.Click += async (_, _) => await ScanAsync(_path.Text.Trim());
        _onlyCurrent.CheckedChanged += (_, _) => RefreshAll();
        _path.TextChanged += (_, _) => { if (_onlyCurrent.Checked) RefreshAll(); };
        _stop.Click += (_, _) => { _scan?.Cancel(); _status.Text = "Останавливаю проверку…"; };
        _account.LinkClicked += (_, _) => AccountClicked();

        foreach (var c in new Control[] { this, _path })
        {
            c.DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
            c.DragDrop += (_, e) =>
            {
                if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } items)
                    _path.Text = Directory.Exists(items[0]) ? items[0] : Path.GetDirectoryName(items[0]);
            };
        }

        _pick.Click += async (_, _) => await PickContractAsync();
        _rescan.Click += async (_, _) => { if (SelectedFolder() is { } f) await ScanAsync(f); };
        _folders.CellDoubleClick += async (_, e) => { if (e.RowIndex >= 0) await PickContractAsync(); };

        _statusFilter.SelectedIndexChanged += (_, _) => FillGroups();
        _kindFilter.SelectedIndexChanged += (_, _) => FillGroups();
        _search.TextChanged += (_, _) => FillGroups();
        _groups.SelectionChanged += (_, _) => FillOccurrences();
        _occurrences.SelectionChanged += (_, _) => ShowDetails();
        _detailsDelay.Tick += (_, _) => { _detailsDelay.Stop(); ShowDetailsNow(); };
        // Сравнение — готовая страница: переходы по ссылкам в ней не нужны.
        _view.Navigating += (_, e) => { if (e.Url?.ToString() != "about:blank") e.Cancel = true; };
        _occurrences.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) OpenSelectedFile(); };
    }

    /// <summary>Открывает папку с логами и выделяет сегодняшний файл.</summary>
    private static void OpenLog()
    {
        Directory.CreateDirectory(AppPaths.Logs);
        if (AppLog.CurrentFile is { } file && File.Exists(file))
            Process.Start("explorer.exe", $"/select,\"{file}\"");
        else
            Process.Start(new ProcessStartInfo(AppPaths.Logs) { UseShellExecute = true });
    }

    private async Task OnShownAsync()
    {
        // Сначала обновление: иначе проверку, начатую сразу после запуска, сделала бы старая версия.
        await AutoUpdateAsync();

        var session = SessionStore.Load();
        if (session is { } s)
        {
            _site.Token = s.Token;
            _email = s.Email;
            UpdateAccount();
        }
        else
        {
            Login();
        }
    }

    private void UpdateAccount() => _account.Text = _email is null ? "Войти" : _email + " (выйти)";

    private void AccountClicked()
    {
        if (_email is null) { Login(); return; }
        SessionStore.Clear();
        _site.Token = null;
        _email = null;
        UpdateAccount();
    }

    private bool Login()
    {
        using var form = new LoginForm(_site, _email);
        if (form.ShowDialog(this) != DialogResult.OK) { UpdateAccount(); return false; }
        _email = form.Email;
        SessionStore.Save(form.Token, form.Email);
        UpdateAccount();
        return true;
    }

    private async Task AutoUpdateAsync()
    {
        var exe = Environment.ProcessPath;
        if (exe is null || !Updater.Enabled)
        {
            AppLog.Info("Обновление отключено: сборка без версии или задан HELPGAZ_NO_UPDATE");
            return;
        }
        Updater.Cleanup(exe);

        // Пока идёт проверка обновления, новую проверку папок не начинаем.
        _start.Enabled = _pick.Enabled = _rescan.Enabled = false;
        _status.Text = "Проверяю обновления…";
        try
        {
            using var updater = new Updater();
            using var lookup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var release = await updater.FindNewerAsync(lookup.Token);
            if (release is null)
            {
                _status.Text = "Готово";
                return;
            }

            _status.Text = $"Скачиваю обновление {release.Tag}…";
            var downloaded = await updater.DownloadAsync(release, exe, CancellationToken.None);

            while (_busy) await Task.Delay(1000);

            _status.Text = $"Устанавливаю обновление {release.Tag}…";
            Updater.InstallAndRestart(downloaded, exe);
            Application.Exit();
        }
        catch (Exception e)
        {
            // Сбой обновления не должен мешать работе: программа попробует при следующем запуске.
            AppLog.Error("Обновление не установлено", e);
            if (!_busy) _status.Text = "Обновление не установлено: " + e.Message;
        }
        finally
        {
            if (!_busy) _start.Enabled = _pick.Enabled = _rescan.Enabled = true;
        }
    }

    private void Browse()
    {
        using var dialog = new FolderBrowserDialog { Description = "Папка ИТД или общая папка с папками объектов", UseDescriptionForTitle = true };
        if (Directory.Exists(_path.Text)) dialog.SelectedPath = _path.Text;
        if (dialog.ShowDialog(this) == DialogResult.OK) _path.Text = dialog.SelectedPath;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _start.Enabled = _browse.Enabled = _pick.Enabled = _rescan.Enabled = !busy;
        _stop.Enabled = busy;
        _progress.Visible = busy;
    }

    private async Task ScanAsync(string root, bool retried = false)
    {
        if (!Directory.Exists(root))
        {
            MessageBox.Show(this, "Укажите существующую папку.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (_email is null && !Login()) return;

        SetBusy(true);
        _scan = new CancellationTokenSource();
        var token = _scan.Token;
        var needRelogin = false;
        var scanner = new Scanner(_site, _store) { Cache = _templateCache };
        string StoppedAt() => scanner.CurrentFolder is { } f ? $" на папке «{Path.GetFileName(f)}»" : "";
        const string Stale = " Папки после неё показывают результаты прошлой проверки (см. столбец «Проверено»).";

        try
        {
            var progress = new Progress<ScanProgress>(p =>
            {
                _progress.Maximum = p.FolderCount;
                _progress.Value = Math.Min(p.FolderIndex + 1, p.FolderCount);
                _status.Text = $"Папка {p.FolderIndex + 1} из {p.FolderCount}: {Path.GetFileName(p.Folder)} — {p.File}";
            });

            var results = await Task.Run(() => scanner.ScanAsync(root, progress, token), token);
            foreach (var r in results) _candidates[r.Folder] = r.Candidates;

            var withFindings = results.Count(r => r.Findings > 0);
            var noContract = results.Count(r => r.Contract is null);
            var missing = results.Count(r => r.MissingContractId is not null);
            _status.Text = $"Готово: папок {results.Count}, с находками {withFindings}"
                + (noContract > 0 ? $", без договора {noContract} (значения в них не сверялись)" : "")
                + (missing > 0 ? $"; из них {missing} — архивы ITD_Files с номером договора, которого нет на сайте" : "");
            AppLog.Info(_status.Text);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            AppLog.Info("Проверка остановлена пользователем" + StoppedAt());
            _status.Text = "Проверка остановлена" + StoppedAt() + "." + Stale;
        }
        catch (OperationCanceledException e)
        {
            // HttpClient сообщает о таймауте тем же исключением, что и об отмене.
            AppLog.Error("Проверка прервана: сайт не ответил вовремя" + StoppedAt(), e);
            _status.Text = "Сайт не ответил вовремя, проверка прервана" + StoppedAt() + "." + Stale;
            MessageBox.Show(this, _status.Text + "\n\nПодробности в логе программы.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (SiteAuthException)
        {
            AppLog.Warn("Сессия истекла" + StoppedAt());
            needRelogin = true;
        }
        catch (Exception e) when (e is SiteException or HttpRequestException)
        {
            AppLog.Error("Проверка прервана: ошибка связи с сайтом" + StoppedAt(), e);
            _status.Text = "Ошибка связи с сайтом, проверка прервана" + StoppedAt() + "." + Stale;
            MessageBox.Show(this, "Не удалось получить данные с сайта:\n" + e.Message + "\n\n" + _status.Text, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception e)
        {
            AppLog.Error("Проверка прервана из-за ошибки" + StoppedAt(), e);
            _status.Text = "Проверка прервана из-за ошибки" + StoppedAt() + "." + Stale;
            MessageBox.Show(this, e.Message + "\n\n" + _status.Text + "\nПодробности в логе программы.", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
            _siteTemplates = null;
            RefreshAll();
        }

        if (needRelogin)
        {
            SessionStore.Clear();
            _email = null;
            UpdateAccount();
            if (!retried && Login()) await ScanAsync(root, retried: true);
        }
    }

    private void RefreshAll()
    {
        FillFolders();
        FillGroups();
    }

    private string? SelectedFolder() => _folders.CurrentRow?.Tag as string;

    /// <summary>
    /// Папка из поля «Папка ИТД» вместе с подпапками. null, если показывать надо всё:
    /// флажок снят, поле пустое или путь в нём неверный.
    /// </summary>
    private string? Scope()
    {
        var text = _path.Text.Trim();
        if (!_onlyCurrent.Checked || text.Length == 0) return null;
        try { return Path.GetFullPath(text).TrimEnd('\\', '/'); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private static bool InScope(string path, string? scope)
    {
        if (scope is null) return true;
        string full;
        try { full = Path.GetFullPath(path).TrimEnd('\\', '/'); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        return full.Equals(scope, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(scope + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private void FillFolders()
    {
        var selected = SelectedFolder();
        _folders.Rows.Clear();
        var current = Updater.CurrentVersion.ToString(3);
        var scope = Scope();
        foreach (var f in _store.Folders().Where(f => f.LastScan is not null && InScope(f.Path, scope)).OrderBy(f => f.Path, StringComparer.CurrentCultureIgnoreCase))
        {
            var verdict = f.Findings == 0 ? "Всё совпало" : "Есть находки";
            var contract = f.ContractTitle.Length > 0 ? f.ContractTitle
                : f.ContractNote.Length > 0 ? f.ContractNote
                : "Договор не определён";
            // Результат старой версии мог устареть: например, до 1.0.1 архивы ITD_Files не связывались с договором.
            var stale = f.AppVersion != current;
            var scanned = $"{f.LastScan:dd.MM.yyyy HH:mm}" + (stale ? $", {(f.AppVersion.Length > 0 ? "v" + f.AppVersion : "до v1.0.4")}" : "");
            var row = _folders.Rows[_folders.Rows.Add(Path.GetFileName(f.Path.TrimEnd('\\', '/')), contract, f.Documents, f.DocumentsClean, f.Findings, verdict, scanned)];
            row.Tag = f.Path;
            row.Cells[0].ToolTipText = f.Path;
            row.Cells[1].ToolTipText = f.ContractNote;
            row.DefaultCellStyle.ForeColor = f.Findings == 0 ? Color.ForestGreen : SystemColors.ControlText;
            if (stale)
            {
                row.Cells[6].Style.ForeColor = Color.Firebrick;
                row.Cells[6].ToolTipText = "Папка проверена другой версией программы. Проверьте её заново.";
            }
            if (f.ContractTitle.Length == 0) row.Cells[1].Style.ForeColor = Color.Firebrick;
            if (f.Path == selected) row.Selected = true;
        }
    }

    private async Task PickContractAsync()
    {
        var folder = SelectedFolder();
        if (folder is null || _busy) return;

        IReadOnlyList<Contract> contracts;
        try
        {
            contracts = await _site.GetContractsAsync();
        }
        catch (Exception e) when (e is SiteException or SiteAuthException or HttpRequestException)
        {
            MessageBox.Show(this, "Не удалось загрузить договоры:\n" + e.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        using var picker = new ContractPickerForm(folder, contracts, _candidates.GetValueOrDefault(folder) ?? Array.Empty<ContractCandidate>());
        if (picker.ShowDialog(this) != DialogResult.OK || picker.Selected is null) return;

        AppLog.Info($"Договор выбран вручную: «{folder}» → #{picker.Selected.Id}");
        _store.SetContract(folder, picker.Selected.Id, Scanner.Title(picker.Selected));
        await ScanAsync(folder);
    }

    private FindingGroup? SelectedGroup() => _groups.CurrentRow?.Tag as FindingGroup;

    private void FillGroups()
    {
        var key = SelectedGroup()?.Key;
        var query = _search.Text.Trim();
        var statusIndex = _statusFilter.SelectedIndex;
        var kindIndex = _kindFilter.SelectedIndex;

        bool StatusOk(FindingGroup g) => statusIndex switch
        {
            0 => g.Status is FindingStatus.New or FindingStatus.FixTemplate,
            1 => true,
            _ => (int)g.Status == statusIndex - 2,
        };

        var scope = Scope();
        int Folders(FindingGroup g) => g.Occurrences.Where(o => InScope(o.Folder, scope))
            .Select(o => o.Folder).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        var groups = _store.Groups()
            .Where(g => scope is null || Folders(g) > 0)
            .Where(StatusOk)
            .Where(g => kindIndex == 0 || (int)g.Kind == kindIndex - 1)
            .Where(g => query.Length == 0
                || g.Template.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || g.Summary.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(g => g.Kind == FindingKind.FrozenField ? 1 : 0)
            .ThenByDescending(Folders)
            .ThenBy(g => g.Template, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        _groups.Rows.Clear();
        foreach (var g in groups)
        {
            var row = _groups.Rows[_groups.Rows.Add(FindingStatuses.Title(g.Status), FindingKinds.Title(g.Kind), g.Template, g.Summary, Folders(g))];
            row.Tag = g;
            if (g.Key == key) { row.Selected = true; _groups.CurrentCell = row.Cells[0]; }
        }
        _count.Text = $"Находок: {groups.Count}";
        FillOccurrences();
    }

    private void FillOccurrences()
    {
        _occurrences.Rows.Clear();
        var group = SelectedGroup();
        if (group is null) { ShowDetails(); return; }

        var scope = Scope();
        foreach (var o in group.Occurrences.Where(o => InScope(o.Folder, scope)).OrderBy(o => o.Folder, StringComparer.CurrentCultureIgnoreCase))
        {
            var row = _occurrences.Rows[_occurrences.Rows.Add(Path.GetFileName(o.Folder.TrimEnd('\\', '/')), o.File, o.Where)];
            row.Tag = o;
        }
        ShowDetails();
    }

    private static readonly HashSet<FindingKind> NoVisual = new() { FindingKind.UnknownDocument, FindingKind.Error };

    /// <summary>Показывает шаблон и файл рядом для выбранной находки. Файл и шаблон читаются в фоне.</summary>
    /// <summary>
    /// При смене фильтра выбор строки меняется несколько раз подряд. Страница строится один раз, когда выбор
    /// успокоился: частая перезагрузка встроенного браузера портит отрисовку шрифтов.
    /// </summary>
    private void ShowDetails()
    {
        ++_detailsVersion;
        _detailsDelay.Stop();
        _detailsDelay.Start();
    }

    private async void ShowDetailsNow()
    {
        var version = ++_detailsVersion;
        var group = SelectedGroup();
        if (group is null)
        {
            SetHtml(ComparisonHtml.Message("Выберите находку", "Выберите строку в списке сверху: здесь появятся шаблон и ваш файл рядом, отличия выделены цветом."));
            return;
        }

        var scope = Scope();
        var occurrence = _occurrences.CurrentRow?.Tag as Occurrence ?? group.Occurrences.FirstOrDefault(o => InScope(o.Folder, scope));
        var expected = occurrence?.Expected ?? group.Expected;
        var actual = occurrence?.Actual ?? group.Actual;
        var title = FindingKinds.Title(group.Kind);
        var subtitle = group.Template + (group.Tag.Length > 0 ? " · поле {" + group.Tag + "}" : "")
            + (occurrence is null ? "" : " · папка «" + Path.GetFileName(occurrence.Folder.TrimEnd('\\', '/')) + "»");
        var brief = Brief(group, expected, actual);

        if (occurrence is null || NoVisual.Contains(group.Kind))
        {
            SetHtml(ComparisonHtml.Render(title, subtitle, brief, "", null, null));
            return;
        }

        var kind = group.Kind;
        var templateName = group.Template;
        // Во время проверки сайт не нагружаем вторым потоком запросов: запросы к нему идут строго по одному.
        var canDownload = _email is not null && !_busy;
        var build = Task.Run(() => BuildComparisonAsync(title, subtitle, brief, kind, templateName, occurrence, canDownload));
        // Заглушку показываем, только если сравнение строится заметно долго.
        if (await Task.WhenAny(build, Task.Delay(400)) != build && version == _detailsVersion)
            SetHtml(ComparisonHtml.Render(title, subtitle, brief, occurrence.File, null, "Строю сравнение…"));
        var html = await build;
        if (version == _detailsVersion) SetHtml(html);
    }

    private static List<(string Label, string Text)> Brief(FindingGroup g, string expected, string actual)
    {
        var lines = new List<(string, string)> { ("Суть:", g.Summary) };
        switch (g.Kind)
        {
            case FindingKind.ValueMismatch:
                lines.Add(("Сайт по договору подставляет:", expected));
                lines.Add(("В файле:", actual));
                break;
            case FindingKind.FolderConflict:
                lines.Add(("В остальных документах папки:", expected));
                lines.Add(("В этом документе:", actual));
                break;
            case FindingKind.CharFormatting or FindingKind.ParaFormatting or FindingKind.PageLayout:
                lines.Add(("В шаблоне:", expected));
                lines.Add(("В файле:", actual));
                break;
            case FindingKind.Error:
                lines.Add(("Ошибка:", actual));
                break;
        }
        return lines;
    }

    private async Task<string> BuildComparisonAsync(string title, string subtitle, List<(string Label, string Text)> brief, FindingKind kind, string templateName, Occurrence o, bool canDownload)
    {
        var path = Path.Combine(o.Folder, o.File);
        try
        {
            if (!File.Exists(path))
                return ComparisonHtml.Render(title, subtitle, brief, o.File, null, "Файл не найден: " + path);

            var template = LoadTemplate(templateName);
            if (template is null)
            {
                if (!canDownload)
                    return ComparisonHtml.Render(title, subtitle, brief, o.File, null, _email is null
                        ? "Копии шаблона на этом компьютере нет. Войдите на сайт (ссылка справа вверху), и программа скачает шаблон для сравнения."
                        : "Копии шаблона на этом компьютере нет. Скачаю её с сайта, когда закончится проверка: выберите находку ещё раз.");
                var problem = await DownloadTemplateAsync(templateName);
                if (problem is not null) return ComparisonHtml.Render(title, subtitle, brief, o.File, null, problem);
                template = LoadTemplate(templateName);
                if (template is null) return ComparisonHtml.Render(title, subtitle, brief, o.File, null, "Шаблон скачан, но прочитать его копию не удалось. Подробности в логе программы.");
            }

            byte[] bytes;
            // Открытый в Word файл читать можно, если не требовать монопольного доступа.
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var ms = new MemoryStream())
            {
                stream.CopyTo(ms);
                bytes = ms.ToArray();
            }
            var cmp = SideBySide.Build(template, DocxReader.Read(bytes), kind, o.Where, o.Expected, o.Actual);
            return ComparisonHtml.Render(title, subtitle, brief, o.File, cmp, null);
        }
        catch (Exception e)
        {
            AppLog.Error($"Не удалось построить сравнение для «{path}»", e);
            return ComparisonHtml.Render(title, subtitle, brief, o.File, null, "Не удалось построить сравнение: " + e.Message);
        }
    }

    /// <summary>Скачивает шаблон, которого нет в копиях: например, находка осталась от проверки версией до 1.0.6.</summary>
    /// <returns>Null при успехе, иначе понятное объяснение, почему сравнения не будет.</returns>
    private async Task<string?> DownloadTemplateAsync(string templateName)
    {
        await _templateDownload.WaitAsync();
        try
        {
            if (File.Exists(_templateCache.PathFor(templateName))) return null;
            _siteTemplates ??= (await _site.GetTemplatesAsync()).Concat(await _site.GetCustomItdTemplatesAsync()).ToList();
            var template = _siteTemplates.FirstOrDefault(t => t.FileName == templateName);
            if (template is null)
            {
                AppLog.Warn($"Шаблона «{templateName}» нет среди шаблонов сайта: сравнение не построено");
                return $"Шаблона «{templateName}» сейчас нет на сайте: возможно, его переименовали или удалили. Проверьте папку заново.";
            }
            _templateCache.Save(templateName, await _site.DownloadTemplateAsync(template.Id));
            AppLog.Info($"Для сравнения скачан шаблон #{template.Id} «{templateName}»");
            return null;
        }
        catch (SiteAuthException)
        {
            return "Сессия сайта истекла. Нажмите «выйти» справа вверху, войдите снова и выберите находку ещё раз.";
        }
        catch (Exception e) when (e is SiteException or HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            AppLog.Error($"Не удалось скачать шаблон «{templateName}» для сравнения", e);
            return "Не удалось скачать шаблон с сайта: " + e.Message;
        }
        finally
        {
            _templateDownload.Release();
        }
    }

    private TemplateModel? LoadTemplate(string templateName)
    {
        var path = _templateCache.PathFor(templateName);
        if (!File.Exists(path)) return null;
        var stamp = File.GetLastWriteTimeUtc(path);
        lock (_templateModels)
        {
            if (_templateModels.TryGetValue(templateName, out var cached) && cached.Stamp == stamp) return cached.Model;
        }
        var model = TemplateModel.Parse(DocxReader.Read(File.ReadAllBytes(path)));
        lock (_templateModels) _templateModels[templateName] = (stamp, model);
        return model;
    }

    private void SetHtml(string html)
    {
        if (html == _detailsHtml) return;
        _detailsHtml = html;
        _view.DocumentText = html;
    }

    private void ShowComparisonWindow()
    {
        if (_detailsHtml.Length == 0) return;
        var browser = new WebBrowser { Dock = DockStyle.Fill, AllowWebBrowserDrop = false, ScriptErrorsSuppressed = true, DocumentText = _detailsHtml };
        browser.Navigating += (_, e) => { if (e.Url?.ToString() != "about:blank") e.Cancel = true; };
        var form = new Form
        {
            Text = "Сравнение с шаблоном — HelpGaz",
            StartPosition = FormStartPosition.CenterParent,
            Size = new Size(1300, 850),
            WindowState = FormWindowState.Maximized,
            Icon = Icon,
        };
        form.Controls.Add(browser);
        form.Show(this);
    }

    private void ApplyStatus(FindingStatus status)
    {
        if (SelectedGroup() is not { } g) return;
        _store.SetStatus(g.Key, status);
        FillGroups();
    }

    private void OpenSelectedFile()
    {
        var occurrence = _occurrences.CurrentRow?.Tag as Occurrence ?? SelectedGroup()?.Occurrences.FirstOrDefault();
        if (occurrence is null) return;
        var path = Path.Combine(occurrence.Folder, occurrence.File);
        if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        else MessageBox.Show(this, "Файл не найден:\n" + path, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _site.Dispose();
            _scan?.Dispose();
            _detailsDelay.Dispose();
            _templateDownload.Dispose();
        }
        base.Dispose(disposing);
    }
}
