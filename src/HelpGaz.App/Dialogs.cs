using HelpGaz.Core.Scan;
using HelpGaz.Core.Site;

namespace HelpGaz.App;

internal sealed class LoginForm : Form
{
    private readonly SiteClient _site;
    private readonly TextBox _email = new() { Dock = DockStyle.Fill };
    private readonly TextBox _password = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    private readonly Label _error = new() { Dock = DockStyle.Fill, ForeColor = Color.Firebrick, AutoSize = false };
    private readonly Button _ok = new() { Text = "Войти", AutoSize = true };

    public string Email => _email.Text.Trim();
    public string Token { get; private set; } = "";

    public LoginForm(SiteClient site, string? email)
    {
        _site = site;
        Text = "Вход на сайт";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(420, 190);
        _email.Text = email ?? "";

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4, Padding = new Padding(14) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));

        layout.Controls.Add(new Label { Text = "Email:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        layout.Controls.Add(_email, 1, 0);
        layout.Controls.Add(new Label { Text = "Пароль:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        layout.Controls.Add(_password, 1, 1);
        layout.Controls.Add(_error, 0, 2);
        layout.SetColumnSpan(_error, 2);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var cancel = new Button { Text = "Отмена", AutoSize = true, DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(_ok);
        layout.Controls.Add(buttons, 0, 3);
        layout.SetColumnSpan(buttons, 2);

        Controls.Add(layout);
        AcceptButton = _ok;
        CancelButton = cancel;
        _ok.Click += async (_, _) => await SubmitAsync();
    }

    private async Task SubmitAsync()
    {
        _ok.Enabled = false;
        _error.Text = "";
        try
        {
            var result = await _site.LoginAsync(Email, _password.Text);
            Token = result.Token;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception e) when (e is SiteAuthException or SiteException or HttpRequestException or TaskCanceledException)
        {
            _error.Text = e is HttpRequestException or TaskCanceledException ? "Нет связи с сайтом." : e.Message;
            _ok.Enabled = true;
        }
    }
}

internal sealed class ContractPickerForm : Form
{
    private readonly IReadOnlyList<Contract> _contracts;
    private readonly IReadOnlyList<ContractCandidate> _candidates;
    private readonly TextBox _search = new() { Dock = DockStyle.Top, PlaceholderText = "Поиск по номеру, адресу, ФИО" };
    private readonly ListBox _list = new() { Dock = DockStyle.Fill, IntegralHeight = false };

    public Contract? Selected => (_list.SelectedItem as Row)?.Contract;

    private sealed record Row(Contract Contract, string Title)
    {
        public override string ToString() => Title;
    }

    public ContractPickerForm(string folder, IReadOnlyList<Contract> contracts, IReadOnlyList<ContractCandidate> candidates)
    {
        _contracts = contracts;
        _candidates = candidates;
        Text = "Договор для папки " + Path.GetFileName(folder.TrimEnd('\\', '/'));
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(720, 460);
        MinimizeBox = false;

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(4) };
        var ok = new Button { Text = "Выбрать", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Отмена", AutoSize = true, DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        Controls.Add(_list);
        Controls.Add(_search);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;

        _search.TextChanged += (_, _) => Fill();
        _list.DoubleClick += (_, _) => { if (Selected is not null) { DialogResult = DialogResult.OK; Close(); } };
        Fill();
    }

    private void Fill()
    {
        var query = _search.Text.Trim();
        IEnumerable<Contract> rows;
        if (query.Length == 0)
        {
            var first = _candidates.Select(c => c.Contract).ToList();
            rows = first.Concat(_contracts.Where(c => first.All(f => f.Id != c.Id)));
        }
        else
        {
            rows = _contracts.Where(c => Scanner.Title(c).Contains(query, StringComparison.CurrentCultureIgnoreCase));
        }

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var c in rows.Take(500)) _list.Items.Add(new Row(c, Scanner.Title(c)));
        _list.EndUpdate();
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
    }
}
