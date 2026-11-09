using System.Diagnostics;
using HelpGaz.Core.Compare;
using HelpGaz.Core.Log;
using HelpGaz.Core.Scan;
using HelpGaz.Core.Site;

namespace HelpGaz.App;

internal sealed class MainForm : Form
{
    private readonly SiteClient _site = new();
    private readonly FindingsStore _store = new(AppPaths.Findings);
    private readonly Dictionary<string, IReadOnlyList<ContractCandidate>> _candidates = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _scan;
    private string? _email;
    private bool _busy;

    private readonly TextBox _path = new() { Dock = DockStyle.Fill, AllowDrop = true, PlaceholderText = "Перетащите сюда папку или нажмите «Обзор»" };
    private readonly Button _browse = new() { Text = "Обзор…", AutoSize = true };
    private readonly Button _start = new() { Text = "Проверить", AutoSize = true, Font = new Font(DefaultFont, FontStyle.Bold) };
    private readonly Button _stop = new() { Text = "Стоп", AutoSize = true, Enabled = false };
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
    private readonly RichTextBox _details = new() { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.FixedSingle, BackColor = SystemColors.Window };

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
        var top = new TableLayoutPanel { Dock = DockStyle.Top, Height = 46, ColumnCount = 6, Padding = new Padding(8, 8, 8, 4) };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 4; i++) top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.Controls.Add(new Label { Text = "Папка ИТД:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        top.Controls.Add(_path, 1, 0);
        top.Controls.Add(_browse, 2, 0);
        top.Controls.Add(_start, 3, 0);
        top.Controls.Add(_stop, 4, 0);
        top.Controls.Add(_account, 5, 0);

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
        AddColumn(_folders, "Папка", 34);
        AddColumn(_folders, "Договор", 34);
        AddColumn(_folders, "Документов", 9, DataGridViewContentAlignment.MiddleCenter);
        AddColumn(_folders, "Совпало", 8, DataGridViewContentAlignment.MiddleCenter);
        AddColumn(_folders, "Находок", 8, DataGridViewContentAlignment.MiddleCenter);
        AddColumn(_folders, "Итог", 15);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(2) };
        buttons.Controls.Add(_pick);
        buttons.Controls.Add(_rescan);

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
        var log = new Button { Text = "Папка журнала", AutoSize = true };
        log.Click += (_, _) => { Directory.CreateDirectory(AppPaths.Root); Process.Start(new ProcessStartInfo(AppPaths.Root) { UseShellExecute = true }); };
        statusButtons.Controls.Add(log);

        var right = new Panel { Dock = DockStyle.Fill };
        right.Controls.Add(_occurrences);
        right.Controls.Add(statusButtons);

        var bottom = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = 6 };
        bottom.Panel1.Controls.Add(_details);
        bottom.Panel2.Controls.Add(right);
        bottom.HandleCreated += (_, _) => bottom.SplitterDistance = bottom.Width / 2;

        var main = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 6 };
        main.Panel1.Controls.Add(_groups);
        main.Panel2.Controls.Add(bottom);
        main.HandleCreated += (_, _) => main.SplitterDistance = main.Height * 55 / 100;

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
        _occurrences.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) OpenSelectedFile(); };
    }

    private async Task OnShownAsync()
    {
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
        await AutoUpdateAsync();
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
        if (exe is null || !Updater.Enabled) return;
        Updater.Cleanup(exe);

        try
        {
            using var updater = new Updater();
            var release = await updater.FindNewerAsync(CancellationToken.None);
            if (release is null) return;

            _status.Text = $"Скачиваю обновление {release.Tag}…";
            var downloaded = await updater.DownloadAsync(release, exe, CancellationToken.None);

            while (_busy) await Task.Delay(1000);

            _status.Text = $"Устанавливаю обновление {release.Tag}…";
            Updater.InstallAndRestart(downloaded, exe);
            Application.Exit();
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or TaskCanceledException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception)
        {
            // Сбой обновления не должен мешать работе: программа попробует при следующем запуске.
            if (!_busy) _status.Text = "Обновление не установлено: " + e.Message;
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

        try
        {
            var scanner = new Scanner(_site, _store);
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
            _status.Text = $"Готово: папок {results.Count}, с находками {withFindings}"
                + (noContract > 0 ? $", без договора {noContract} (значения в них не сверялись)" : "");
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Проверка остановлена. Уже проверенные папки сохранены.";
        }
        catch (SiteAuthException)
        {
            needRelogin = true;
        }
        catch (Exception e) when (e is SiteException or HttpRequestException)
        {
            _status.Text = "Ошибка связи с сайтом";
            MessageBox.Show(this, "Не удалось получить данные с сайта:\n" + e.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            SetBusy(false);
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

    private void FillFolders()
    {
        var selected = SelectedFolder();
        _folders.Rows.Clear();
        foreach (var f in _store.Folders().Where(f => f.LastScan is not null).OrderBy(f => f.Path, StringComparer.CurrentCultureIgnoreCase))
        {
            var verdict = f.Findings == 0 ? "Всё совпало" : "Есть находки";
            var contract = f.ContractTitle.Length > 0 ? f.ContractTitle : "Договор не определён";
            var row = _folders.Rows[_folders.Rows.Add(Path.GetFileName(f.Path.TrimEnd('\\', '/')), contract, f.Documents, f.DocumentsClean, f.Findings, verdict)];
            row.Tag = f.Path;
            row.Cells[0].ToolTipText = f.Path;
            row.DefaultCellStyle.ForeColor = f.Findings == 0 ? Color.ForestGreen : SystemColors.ControlText;
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

        var groups = _store.Groups()
            .Where(StatusOk)
            .Where(g => kindIndex == 0 || (int)g.Kind == kindIndex - 1)
            .Where(g => query.Length == 0
                || g.Template.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || g.Summary.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(g => g.Kind == FindingKind.FrozenField ? 1 : 0)
            .ThenByDescending(g => g.FolderCount)
            .ThenBy(g => g.Template, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        _groups.Rows.Clear();
        foreach (var g in groups)
        {
            var row = _groups.Rows[_groups.Rows.Add(FindingStatuses.Title(g.Status), FindingKinds.Title(g.Kind), g.Template, g.Summary, g.FolderCount)];
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
        if (group is null) { _details.Clear(); return; }

        foreach (var o in group.Occurrences.OrderBy(o => o.Folder, StringComparer.CurrentCultureIgnoreCase))
        {
            var row = _occurrences.Rows[_occurrences.Rows.Add(Path.GetFileName(o.Folder.TrimEnd('\\', '/')), o.File, o.Where)];
            row.Tag = o;
        }
        ShowDetails();
    }

    private void ShowDetails()
    {
        var group = SelectedGroup();
        _details.Clear();
        if (group is null) return;

        var occurrence = _occurrences.CurrentRow?.Tag as Occurrence ?? group.Occurrences.FirstOrDefault();
        var expected = occurrence?.Expected ?? group.Expected;
        var actual = occurrence?.Actual ?? group.Actual;

        Append(FindingKinds.Title(group.Kind) + "\n", bold: true);
        Append(group.Template + "\n", color: SystemColors.GrayText);
        if (group.Tag.Length > 0) Append("Поле: {" + group.Tag + "}\n", color: SystemColors.GrayText);
        Append("\n");

        if (expected.Length > 0 && actual.Length > 0)
        {
            var ops = TextDiff.Diff(expected, actual);
            Append("Должно быть:\n", bold: true);
            foreach (var op in ops.Where(o => o.Kind != DiffKind.Insert))
                Append(op.Text, background: op.Kind == DiffKind.Delete ? Color.MistyRose : null);
            Append("\n\nВ файле:\n", bold: true);
            foreach (var op in ops.Where(o => o.Kind != DiffKind.Delete))
                Append(op.Text, background: op.Kind == DiffKind.Insert ? Color.PaleGreen : null);
        }
        else if (expected.Length > 0)
        {
            Append("Должно быть:\n", bold: true);
            Append(expected);
        }
        else if (actual.Length > 0)
        {
            Append("В файле:\n", bold: true);
            Append(actual);
        }
        else
        {
            Append(group.Summary);
        }
        _details.SelectionStart = 0;
        _details.ScrollToCaret();
    }

    private void Append(string text, bool bold = false, Color? color = null, Color? background = null)
    {
        _details.SelectionStart = _details.TextLength;
        _details.SelectionLength = 0;
        _details.SelectionFont = new Font(_details.Font, bold ? FontStyle.Bold : FontStyle.Regular);
        _details.SelectionColor = color ?? SystemColors.WindowText;
        _details.SelectionBackColor = background ?? _details.BackColor;
        _details.AppendText(text);
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
        }
        base.Dispose(disposing);
    }
}
