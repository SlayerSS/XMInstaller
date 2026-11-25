using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XMInstaller;

sealed class MainForm : Form
{
    static readonly string[] Pages =
    [
        "Сведения",
        "Цифровые каналы",
        "Учётные записи",
        "Сеть",
        "Сетевые службы",
        "Запись и диск",
        "Тревога",
        "Журнал",
        "Все параметры",
        "Файл cfg"
    ];

    readonly AppSettings _settings = AppSettings.Load();
    readonly ComboBox _found = new() { MinimumSize = new Size(72, 0), Margin = new Padding(3, 0, 8, 0), DropDownStyle = ComboBoxStyle.DropDownList };
    readonly TextBox _host = new() { Width = 112, Margin = new Padding(3, 0, 8, 0) };
    readonly TextBox _port = new() { Width = 54, Margin = new Padding(3, 0, 8, 0) };
    readonly TextBox _user = new() { Width = 90, Margin = new Padding(3, 0, 8, 0) };
    readonly TextBox _pass = new() { Width = 110, Margin = new Padding(3, 0, 8, 0), UseSystemPasswordChar = true };
    readonly Label _fileName = new() { AutoEllipsis = true, Dock = DockStyle.Top, Height = 22, Padding = new Padding(12, 4, 8, 0), TextAlign = ContentAlignment.MiddleLeft };
    readonly CheckBox _cameras = new() { Text = "Камеры", Checked = true, AutoSize = true };
    readonly CheckBox _titles = new() { Text = "Названия", Checked = true, AutoSize = true };
    readonly CheckBox _record = new() { Text = "Запись", Checked = true, AutoSize = true };
    readonly CheckBox _detect = new() { Text = "Тревога", Checked = true, AutoSize = true };
    readonly CheckBox _accounts = new() { Text = "Учётные записи", Checked = true, AutoSize = true };
    readonly CheckBox _network = new() { Text = "Сеть (IP)", Checked = false, AutoSize = true };
    readonly ListBox _pages = new() { Dock = DockStyle.Left, Width = 210, IntegralHeight = false, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 36, BorderStyle = BorderStyle.None };
    readonly DataGridView _grid = new();
    readonly DataGridView _liveGrid = new();
    readonly TextBox _text = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill, Font = new Font("Consolas", 10), HideSelection = false, BorderStyle = BorderStyle.None };
    readonly TextBox _liveText = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill, Font = new Font("Consolas", 10), HideSelection = false, BorderStyle = BorderStyle.None };
    readonly TableLayoutPanel _compare = new() { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
    readonly SplitContainer _param = new() { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, Visible = false };
    readonly Button _move = new() { Text = "→", Font = new Font("Segoe UI", 18, FontStyle.Bold), Size = new Size(52, 64) };
    readonly Button _pick = new() { Text = "Отличающиеся", AutoSize = true };
    readonly Button _clear = new() { Text = "Снять", AutoSize = true };
    static readonly Color Ink = Color.FromArgb(23, 37, 54);
    static readonly Color Accent = Color.FromArgb(31, 90, 160);
    static readonly Color Diff = Color.FromArgb(255, 236, 179);
    readonly TreeView _tree = new() { Dock = DockStyle.Fill, HideSelection = false };
    readonly TextBox _detail = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill, Font = new Font("Consolas", 10) };
    readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly Button _connect = new() { Text = "Подключиться", AutoSize = true };
    readonly Button _write = new() { Text = "Записать на регистратор", AutoSize = true };

    NvrClient? _nvr;
    DeviceSnapshot? _live;
    CfgStore? _file;
    CancellationTokenSource? _search;
    bool _busy;

    public MainForm()
    {
        Text = "XM установщик  ·  Slayer NSK";
        Width = 1360;
        Height = 820;
        MinimumSize = new Size(1100, 680);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10);
        AllowDrop = true;

        BackColor = Color.FromArgb(244, 247, 251);
        _host.Text = _settings.Host;
        _port.Text = _settings.Port.ToString();
        _user.Text = string.IsNullOrWhiteSpace(_settings.User) ? "admin" : _settings.User;
        _fileName.Text = "Файл не открыт";
        _fileName.ForeColor = Color.FromArgb(70, 84, 102);
        foreach (var page in Pages)
            _pages.Items.Add(page);
        _pages.BackColor = Ink;
        _pages.ForeColor = Color.White;
        _pages.DrawItem += DrawPage;

        StyleGrid(_grid, selectMany: true);
        StyleGrid(_liveGrid, selectMany: false);
        ArmSelection(_grid);
        _grid.CellDoubleClick += (_, _) => EditCurrentUser(fromFile: true);
        _liveGrid.CellDoubleClick += (_, _) => EditCurrentUser(fromFile: false);

        _param.Panel1.Controls.Add(_tree);
        _param.Panel2.Controls.Add(_detail);
        _param.SplitterDistance = 320;
        _tree.AfterSelect += (_, e) =>
        {
            if (e.Node?.Tag is JsonNode node)
                _detail.Text = DeviceSnapshot.Outline(node);
        };

        var menu = new MenuStrip();
        var file = new ToolStripMenuItem("Файл");
        file.DropDownItems.Add("Открыть cfg", null, (_, _) => ChooseCfg());
        file.DropDownItems.Add("Экспорт для переноса", null, (_, _) => ExportTransfer());
        file.DropDownItems.Add("Собрать cfg для импорта", null, (_, _) => BuildCfg());
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add("Выход", null, (_, _) => Close());
        var device = new ToolStripMenuItem("Регистратор");
        device.DropDownItems.Add("Найти в сети", null, async (_, _) => await SearchAsync());
        device.DropDownItems.Add("Подключиться", null, async (_, _) => await ConnectAsync());
        device.DropDownItems.Add("Обновить данные", null, async (_, _) => await (_nvr == null ? ConnectAsync() : ReloadAsync()));
        device.DropDownItems.Add("Отключиться", null, (_, _) => Disconnect());
        var help = new ToolStripMenuItem("Справка");
        help.DropDownItems.Add("О программе", null, (_, _) => About());
        menu.Items.AddRange([file, device, help]);
        MainMenuStrip = menu;

        var find = new Button { Text = "Найти", AutoSize = true };
        find.Click += async (_, _) => await SearchAsync();
        _connect.Click += async (_, _) => await ConnectAsync();
        var disconnect = new Button { Text = "Отключиться", AutoSize = true };
        disconnect.Click += (_, _) => Disconnect();
        var open = new Button { Text = "Открыть cfg", AutoSize = true };
        open.Click += (_, _) => ChooseCfg();
        _write.Click += async (_, _) => await WriteToDeviceAsync();
        _move.Click += async (_, _) => await MoveSelectedAsync();
        _pick.Click += (_, _) => PickDifferent();
        _clear.Click += (_, _) => ClearPick();
        AcceptButton = _connect;
        foreach (var button in new[] { find, _connect, disconnect, open, _write, _pick, _clear })
            PaintButton(button, button == _connect || button == _write);
        PaintButton(_move, true);
        _move.AutoSize = false;
        _move.Font = new Font("Segoe UI", 18, FontStyle.Bold);
        _move.Size = new Size(52, 64);
        _move.Text = "→";

        var row1 = new TableLayoutPanel { Dock = DockStyle.Top, Height = 46, RowCount = 1, BackColor = Color.White, Padding = new Padding(8, 6, 8, 2) };
        row1.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var col1 = 0;
        void Put1(Control control, SizeType type, float size, AnchorStyles anchor)
        {
            row1.ColumnStyles.Add(new ColumnStyle(type, size));
            control.Anchor = anchor;
            row1.Controls.Add(control, col1++, 0);
        }
        Put1(Mk("Найден"), SizeType.AutoSize, 0, AnchorStyles.Left);
        Put1(_found, SizeType.Percent, 100, AnchorStyles.Left | AnchorStyles.Right);
        Put1(Mk("IP"), SizeType.AutoSize, 0, AnchorStyles.Left);
        Put1(_host, SizeType.AutoSize, 0, AnchorStyles.Left);
        Put1(Mk("Порт"), SizeType.AutoSize, 0, AnchorStyles.Left);
        Put1(_port, SizeType.AutoSize, 0, AnchorStyles.Left);
        Put1(Mk("Логин"), SizeType.AutoSize, 0, AnchorStyles.Left);
        Put1(_user, SizeType.AutoSize, 0, AnchorStyles.Left);
        Put1(Mk("Пароль"), SizeType.AutoSize, 0, AnchorStyles.Left);
        Put1(_pass, SizeType.AutoSize, 0, AnchorStyles.Left);
        Put1(find, SizeType.AutoSize, 0, AnchorStyles.None);
        Put1(_connect, SizeType.AutoSize, 0, AnchorStyles.None);
        Put1(disconnect, SizeType.AutoSize, 0, AnchorStyles.None);
        row1.ColumnCount = col1;

        var options = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 4, 0, 0), BackColor = Color.White };
        options.Controls.AddRange([open, _cameras, _titles, _record, _detect, _accounts, _network]);
        var row2 = new TableLayoutPanel { Dock = DockStyle.Top, Height = 46, RowCount = 1, BackColor = Color.White, Padding = new Padding(8, 2, 8, 2) };
        row2.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var col2 = 0;
        void Put2(Control control, SizeType type, float size, AnchorStyles anchor)
        {
            row2.ColumnStyles.Add(new ColumnStyle(type, size));
            control.Anchor = anchor;
            row2.Controls.Add(control, col2++, 0);
        }
        Put2(options, SizeType.Percent, 100, AnchorStyles.Left | AnchorStyles.Right);
        Put2(_write, SizeType.AutoSize, 0, AnchorStyles.None);
        Put2(_pick, SizeType.AutoSize, 0, AnchorStyles.None);
        Put2(_clear, SizeType.AutoSize, 0, AnchorStyles.None);
        row2.ColumnCount = col2;

        var hint = new Label
        {
            Dock = DockStyle.Top,
            Height = 22,
            Padding = new Padding(12, 0, 8, 0),
            ForeColor = Color.FromArgb(90, 104, 122),
            Text = "Стрелка переносит только открытый раздел. Строка камеры идёт в тот же канал, номер можно сменить. Пароль входа не сохраняется."
        };
        var bar = new Panel { Dock = DockStyle.Top, Height = 138, BackColor = Color.White };
        bar.Controls.Add(hint);
        bar.Controls.Add(_fileName);
        bar.Controls.Add(row2);
        bar.Controls.Add(row1);

        var fileHead = Head("Файл cfg");
        var liveHead = Head("Регистратор");
        var filePane = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Margin = new Padding(0) };
        var livePane = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Margin = new Padding(0) };
        filePane.Controls.Add(_grid);
        filePane.Controls.Add(_text);
        filePane.Controls.Add(fileHead);
        livePane.Controls.Add(_liveGrid);
        livePane.Controls.Add(_liveText);
        livePane.Controls.Add(liveHead);
        var railText = new Label
        {
            Text = "только\nэтот\nраздел",
            TextAlign = ContentAlignment.TopCenter,
            ForeColor = Color.FromArgb(90, 104, 122),
            Font = new Font("Segoe UI", 8),
            Height = 48
        };
        var rail = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(244, 247, 251), Margin = new Padding(0) };
        void PlaceRail(object? sender, EventArgs e)
        {
            _move.Left = Math.Max(0, (rail.ClientSize.Width - _move.Width) / 2);
            _move.Top = Math.Max(8, (rail.ClientSize.Height - _move.Height) / 2 - 20);
            railText.Left = 0;
            railText.Width = rail.ClientSize.Width;
            railText.Top = _move.Bottom + 6;
        }
        rail.Resize += PlaceRail;
        rail.Controls.Add(railText);
        rail.Controls.Add(_move);
        _compare.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _compare.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
        _compare.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _compare.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _compare.Controls.Add(filePane, 0, 0);
        _compare.Controls.Add(rail, 1, 0);
        _compare.Controls.Add(livePane, 2, 0);

        var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 8, 8, 4) };
        right.Controls.Add(_compare);
        right.Controls.Add(_param);

        var body = new Panel { Dock = DockStyle.Fill };
        body.Controls.Add(right);
        body.Controls.Add(_pages);

        var status = new StatusStrip();
        status.Items.Add(_status);
        status.Items.Add(new ToolStripStatusLabel("Slayer NSK"));

        Controls.Add(body);
        Controls.Add(bar);
        Controls.Add(status);
        Controls.Add(menu);

        _found.SelectedIndexChanged += (_, _) =>
        {
            if (_found.SelectedItem is FoundDevice deviceFound)
                ApplyFound(deviceFound);
        };
        _pages.SelectedIndexChanged += (_, _) => ShowPage();
        DragEnter += (_, e) =>
        {
            if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
                e.Effect = DragDropEffects.Copy;
        };
        DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                OpenFile(files[0]);
        };
        _pages.SelectedIndex = 0;
        ShowView("text");
        Status("Автор: Slayer NSK. Пароль не сохраняется.");
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try
        {
            if (_param.Width > 500)
                _param.SplitterDistance = 340;
        }
        catch
        {
            /* ширина ещё не готова */
        }
        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && File.Exists(args[1]))
            OpenFile(args[1]);
        else if (!string.IsNullOrWhiteSpace(_settings.LastCfg) && File.Exists(_settings.LastCfg))
            OpenFile(_settings.LastCfg);
        await ProbeSavedAsync();
        _pass.Focus();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _search?.Cancel();
        _nvr?.Dispose();
        base.OnFormClosed(e);
    }

    async Task ProbeSavedAsync()
    {
        var host = _host.Text.Trim();
        if (host.Length == 0)
        {
            Status("Откройте cfg или нажмите «Найти». Пароль не сохраняется.");
            return;
        }
        Status("Проверяю прошлый регистратор " + host + "…");
        try
        {
            var list = await Task.Run(() => Finder.Probe(host));
            if (IsDisposed || list.Count == 0)
            {
                Status(host + " не ответил. Можно нажать «Найти» или ввести другой адрес.");
                return;
            }
            _found.Items.Clear();
            foreach (var item in list)
                _found.Items.Add(item);
            _found.SelectedItem = list[0];
            ApplyFound(list[0]);
            Status("Регистратор " + list[0] + " на связи. Введите пароль. Он не сохраняется.");
        }
        catch (Exception ex)
        {
            Status(ex.Message);
        }
    }

    async Task SearchAsync()
    {
        if (_busy)
            return;
        _search?.Cancel();
        _search = new CancellationTokenSource();
        var token = _search.Token;
        Status("Ищу регистраторы в локальной сети…");
        try
        {
            var list = await Task.Run(() => Finder.Scan(token), token);
            if (IsDisposed || token.IsCancellationRequested)
                return;
            _found.Items.Clear();
            foreach (var item in list)
                _found.Items.Add(item);
            if (list.Count == 0)
            {
                Status("Регистраторы не ответили. Адрес и порт можно ввести вручную. Порт по умолчанию у новых аппаратов часто 34567.");
                return;
            }
            var pick = list.FirstOrDefault(item => item.Ip == _host.Text.Trim()) ?? list[0];
            _found.SelectedItem = pick;
            ApplyFound(pick);
            _pass.Focus();
            Status("Найдено " + list.Count + ". Введите пароль и нажмите «Подключиться». Пароль не сохраняется.");
        }
        catch (OperationCanceledException)
        {
            /* поиск заменён новым */
        }
        catch (Exception ex)
        {
            Status(ex.Message);
        }
    }

    async Task ConnectAsync()
    {
        if (_busy)
            return;
        var host = _host.Text.Trim();
        var user = _user.Text.Trim();
        var password = _pass.Text;
        if (!int.TryParse(_port.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            Warn("Укажите порт, обычно 34567 или 34568.");
            return;
        }
        if (host.Length == 0 || user.Length == 0)
        {
            Warn("Укажите адрес и логин.");
            return;
        }
        if (password.Length == 0)
        {
            Warn("Введите пароль. Он нужен только для этого входа и на диск не пишется.");
            _pass.Focus();
            return;
        }
        _settings.Host = host;
        _settings.Port = port;
        _settings.User = user;
        _settings.Save();
        SetBusy(true);
        Status("Подключение к " + host + ":" + port + "…");
        var progress = new Progress<string>(text => Status("Читаю: " + text));
        try
        {
            var loaded = await Task.Run(() =>
            {
                NvrClient? client = null;
                try
                {
                    client = NvrClient.Connect(host, port, user, password);
                    var shot = DeviceSnapshot.Load(client, progress);
                    return (client, shot);
                }
                catch
                {
                    client?.Dispose();
                    throw;
                }
            });
            _nvr?.Dispose();
            _nvr = loaded.client;
            _live = loaded.shot;
            _pass.Clear();
            _pages.SelectedIndex = _file == null ? 0 : 1;
            FillTree();
            Status("Подключён " + host + ":" + port + ". Данные прочитаны с регистратора.");
            ShowPage();
        }
        catch (Exception ex)
        {
            Status(ex.Message);
            Warn(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    void Disconnect()
    {
        _nvr?.Dispose();
        _nvr = null;
        _live = null;
        _tree.Nodes.Clear();
        _detail.Clear();
        Status("Отключён.");
        ShowPage();
    }

    void ChooseCfg()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "Конфиг регистратора|*.cfg|Все файлы|*.*",
            Title = "Открыть cfg"
        };
        if (!string.IsNullOrWhiteSpace(_settings.LastCfg))
            dialog.InitialDirectory = Path.GetDirectoryName(_settings.LastCfg);
        if (dialog.ShowDialog(this) == DialogResult.OK)
            OpenFile(dialog.FileName);
    }

    void OpenFile(string path)
    {
        try
        {
            _file = CfgStore.Open(path);
            _settings.LastCfg = path;
            _settings.Save();
            var summary = _file.Describe();
            _fileName.Text = summary.File + "  ·  " + summary.Generation + "  ·  " + summary.Version;
            if (_pages.SelectedIndex is 0 or 8 or 9)
                _pages.SelectedIndex = 1;
            Status("Открыт " + summary.File + ", формат " + summary.Generation + ".");
            ShowPage();
        }
        catch (Exception ex)
        {
            Warn("Не удалось открыть cfg: " + ex.Message);
        }
    }

    async Task WriteToDeviceAsync()
    {
        if (_nvr == null)
        {
            Warn("Сначала подключитесь к регистратору.");
            return;
        }
        if (_file == null)
        {
            Warn("Сначала откройте cfg, из которого переносить.");
            return;
        }
        var parts = new List<string>();
        if (_cameras.Checked) parts.Add("камеры: IP, порт, протокол, логин и пароль камеры");
        if (_titles.Checked) parts.Add("названия каналов");
        if (_record.Checked) parts.Add("расписание записи");
        if (_detect.Checked) parts.Add("движение, потеря, засвет");
        if (_accounts.Checked) parts.Add("учётные записи и права");
        if (_network.Checked) parts.Add("IP, DNS и порты — связь с аппаратом может пропасть");
        if (parts.Count == 0)
        {
            Warn("Отметьте, что переносить.");
            return;
        }
        var ask = "Записать на " + _nvr.Host + ":" + _nvr.Port + " из файла " + _file.Name + ":\n\n"
            + string.Join("\n", parts.Select(part => "• " + part))
            + "\n\nТекущий вход " + _nvr.Username + " не изменяется.";
        if (MessageBox.Show(this, ask, "Запись на регистратор", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        SetBusy(true);
        var progress = new Progress<string>(text => Status("Записываю: " + text));
        var file = _file;
        var cameras = _cameras.Checked;
        var titles = _titles.Checked;
        var record = _record.Checked;
        var detect = _detect.Checked;
        var network = _network.Checked;
        var accounts = _accounts.Checked;
        var nvr = _nvr;
        try
        {
            var lines = await Task.Run(() => DevicePush.Run(nvr, file, cameras, titles, record, detect, network, accounts, progress));
            Status("Запись закончена.");
            MessageBox.Show(this, string.Join("\n", lines), "Результат", MessageBoxButtons.OK, MessageBoxIcon.Information);
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            Status(ex.Message);
            Warn(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    async Task ReloadAsync()
    {
        if (_nvr == null)
            return;
        var progress = new Progress<string>(text => Status("Обновляю: " + text));
        var nvr = _nvr;
        _live = await Task.Run(() => DeviceSnapshot.Load(nvr, progress));
        FillTree();
        ShowPage();
        Status("Данные регистратора обновлены.");
    }

    void ExportTransfer()
    {
        if (_file == null)
        {
            Warn("Сначала откройте cfg.");
            return;
        }
        using var dialog = new SaveFileDialog
        {
            Filter = "Перенос|*.xmt.json",
            FileName = Path.GetFileNameWithoutExtension(_file.Name) + ".xmt.json",
            Title = "Экспорт для переноса"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        var json = _file.Transfer().ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(dialog.FileName, json);
        Status("Экспорт записан: " + dialog.FileName);
    }

    void BuildCfg()
    {
        if (_file == null)
        {
            Warn("Сначала откройте cfg-источник.");
            return;
        }
        using var open = new OpenFileDialog
        {
            Filter = "Экспорт приёмника|*.cfg",
            Title = "Свежий экспорт того регистратора, на который переносим"
        };
        if (open.ShowDialog(this) != DialogResult.OK)
            return;
        using var save = new SaveFileDialog
        {
            Filter = "Конфиг|*.cfg",
            FileName = "import.cfg",
            Title = "Куда сохранить cfg для импорта"
        };
        if (save.ShowDialog(this) != DialogResult.OK)
            return;
        try
        {
            var template = CfgStore.Open(open.FileName);
            var bytes = CfgStore.Apply(template, _file.Transfer(), _cameras.Checked, _titles.Checked, _record.Checked, _detect.Checked, _network.Checked);
            File.WriteAllBytes(save.FileName, bytes);
            Status("Cfg собран. Учётные записи в него не входят, их нужно записать по сети.");
            MessageBox.Show(this, "Файл готов:\n" + save.FileName + "\n\nИмпорт на регистраторе переносит камеры, названия, запись и тревогу. Пользователей он не принимает — для них кнопка «Записать на регистратор».", "Cfg", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Warn(ex.Message);
        }
    }

    void EditCurrentUser(bool fromFile)
    {
        if (_pages.SelectedIndex != 2)
            return;
        var grid = fromFile ? _grid : _liveGrid;
        if (grid.CurrentRow?.Tag is not string name)
            return;
        var list = fromFile ? _file?.Users : _live?.Users;
        var user = list?.FirstOrDefault(item => item.Name == name);
        if (user != null)
            EditUser(user, fromFile);
    }

    void EditUser(UserRow user, bool fromFile)
    {
        var fromDevice = fromFile == false && _live != null;
        IEnumerable<string> groups = [user.Group];
        if (fromDevice && _live != null && _live.Groups.Count > 0)
            groups = _live.Groups.Keys;
        else if (_file != null && _file.Groups.Count > 0)
            groups = _file.Groups.Keys;
        var rights = fromDevice && _live != null && _live.Authority.Count > 0
            ? _live.Authority
            : (_file?.Groups.SelectMany(pair => pair.Value) ?? []).Concat(user.Authority);
        using var dialog = new UserDialog(user, groups, rights, _nvr != null, _file?.Users.Any(item => item.Name == user.Name) == true);
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        var hash = string.IsNullOrEmpty(dialog.NewPassword)
            ? user.Password
            : dialog.PasswordIsHash ? dialog.NewPassword.Trim() : Sofia.Hash(dialog.NewPassword);
        if (dialog.Action == UserAction.SaveFile && _file != null)
        {
            var stored = _file.Users.FirstOrDefault(item => item.Name == user.Name);
            if (stored == null)
                return;
            stored.Group = dialog.GroupName;
            stored.Password = hash;
            stored.Authority = dialog.Rights;
            Status("В файле обновлён " + user.Name + ". На регистратор это попадёт кнопкой «Записать на регистратор».");
            ShowPage();
            return;
        }
        if (_nvr == null)
            return;
        if (dialog.Action == UserAction.Delete && user.Name == _nvr.Username)
        {
            Warn("Нельзя удалить пользователя, под которым вы вошли.");
            return;
        }
        var edited = new UserRow
        {
            Name = user.Name,
            Group = dialog.GroupName,
            Memo = user.Memo,
            Password = hash,
            Reserved = user.Reserved,
            Sharable = user.Sharable,
            Authority = dialog.Rights
        };
        SetBusy(true);
        var nvr = _nvr;
        try
        {
            var line = dialog.Action == UserAction.Delete
                ? Task.Run(() => NvrClient.Text(NvrClient.CodeOf(nvr.DeleteUser(user.Name)), "отказ")).GetAwaiter().GetResult()
                : Task.Run(() => DevicePush.WriteUser(nvr, edited, hash, edited.Authority)).GetAwaiter().GetResult();
            Status(user.Name + ": " + line);
            MessageBox.Show(this, user.Name + ": " + line, "Учётная запись", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Warn(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
        _ = ReloadAsync();
    }

    void ShowPage()
    {
        var page = _pages.SelectedIndex;
        if (page == 8)
        {
            ShowView("param");
            if (_tree.Nodes.Count == 0)
                FillTree();
            Status("Параметры подключённого регистратора.");
            return;
        }
        if (page is 0 or 4 or 9)
        {
            ShowView("text");
            _text.Text = page switch
            {
                0 => FileText(shortForm: true),
                4 => FileServicesText(),
                _ => FileText(shortForm: false)
            };
            _liveText.Text = page switch
            {
                0 => DeviceText(),
                4 => ServicesText(_live),
                _ => _live == null ? "Регистратор не подключён." : DeviceText()
            };
            return;
        }
        ShowView("grids");
        switch (page)
        {
            case 1:
                BindCameras(_grid, _file?.Cameras());
                BindCameras(_liveGrid, _live?.Cameras);
                Note("Камеры. Жёлтым отмечено отличие файла от регистратора.");
                break;
            case 2:
                BindUsers(_grid, _file?.Users);
                BindUsers(_liveGrid, _live?.Users);
                Note("Учётные записи. Двойной щелчок открывает карточку. Системные admin и default аппарат в живой список не отдаёт.");
                break;
            case 3:
                BindNetwork(_grid, _file?.Network(), file: true);
                BindNetwork(_liveGrid, _live?.Network, file: false);
                Note("Сеть. MAC при переносе не меняется. IP переносится только выбранной строкой.");
                break;
            case 5:
                BindRecords(_grid, _file?.Records());
                BindRecords(_liveGrid, _live?.Records);
                Note("Запись по каналам.");
                break;
            case 6:
                BindMotion(_grid, _file?.Motion());
                BindMotion(_liveGrid, _live?.Motion);
                Note("Тревога по каналам.");
                break;
            case 7:
                BindLogs(_grid, _file?.Logs());
                BindLogs(_liveGrid, _live?.Logs);
                Note("Журнал. Живой запрос отдаёт не больше 128 последних строк. Перенос журнала не делается.");
                break;
        }
        MarkDifferent();
        _grid.ClearSelection();
        _liveGrid.ClearSelection();
    }

    int ChannelLimit(int page)
    {
        var live = page switch
        {
            1 => _live?.Cameras.Count ?? 0,
            5 => _live?.Records.Count ?? 0,
            _ => _live?.Motion.Count ?? 0
        };
        var file = page switch
        {
            1 => _file?.Cameras().Count ?? 0,
            5 => _file?.Records().Count ?? 0,
            _ => _file?.Motion().Count ?? 0
        };
        return Math.Max(1, Math.Max(live, file));
    }

    async Task MoveSelectedAsync()
    {
        if (_nvr == null || _file == null)
        {
            Warn("Нужны открытый cfg и подключение к регистратору.");
            return;
        }
        var page = _pages.SelectedIndex;
        if (page is not (1 or 2 or 3 or 5 or 6))
        {
            Warn("В этом разделе строки не переносятся.");
            return;
        }
        var channels = new HashSet<int>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (DataGridViewRow row in _grid.SelectedRows)
        {
            if (row.Tag is int channel)
                channels.Add(channel);
            else if (row.Tag is string key && page == 2)
                names.Add(key);
            else if (row.Tag is string field)
                fields.Add(field);
        }
        if (channels.Count == 0 && names.Count == 0 && fields.Count == 0 && page is 2 or 3)
        {
            if (page == 3)
            {
                Warn("Выберите строки сети слева. IP переносится только отмеченной строкой.");
                return;
            }
            if (MessageBox.Show(this, "Перенести весь раздел «Учётные записи»?", "Перенос", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return;
        }
        else if (channels.Count == 0 && names.Count == 0 && fields.Count == 0)
        {
            var section = page switch { 1 => "Цифровые каналы", 5 => "Запись", _ => "Тревога" };
            if (MessageBox.Show(this, "Перенести весь раздел «" + section + "» в те же каналы регистратора?", "Перенос", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return;
        }
        Dictionary<int, int>? map = null;
        if (page is 1 or 5 or 6 && channels.Count > 0)
        {
            var picked = new List<(int Channel, string Title)>();
            foreach (DataGridViewRow row in _grid.SelectedRows)
            {
                if (row.Tag is not int channel)
                    continue;
                var cell = page == 1 ? 2 : 1;
                var title = cell < row.Cells.Count ? row.Cells[cell].Value?.ToString() ?? "" : "";
                picked.Add((channel, title));
            }
            var section = page switch { 1 => "Цифровые каналы", 5 => "Запись", _ => "Тревога" };
            using var dialog = new ChannelMapDialog(section, picked, ChannelLimit(page));
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;
            map = dialog.Map;
        }
        else if (page == 2 && names.Count > 0)
        {
            if (MessageBox.Show(this, "Перенести учётные записи " + string.Join(", ", names.Order()) + "?", "Перенос", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return;
        }
        else if (page == 3)
        {
            if (fields.Contains("mac"))
                fields.Remove("mac");
            if (fields.Count == 0)
            {
                Warn("MAC регистратора не переносится.");
                return;
            }
            if (MessageBox.Show(this, "Перенести параметры сети: " + string.Join(", ", fields.Order()) + "?", "Перенос", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return;
        }
        var nvr = _nvr;
        var file = _file;
        SetBusy(true);
        try
        {
            var progress = new Progress<string>(Status);
            var lines = await Task.Run(() => page switch
            {
                1 => DevicePush.Run(nvr, file, true, true, false, false, false, false, progress, channelMap: map),
                2 => DevicePush.Run(nvr, file, false, false, false, false, false, true, progress, onlyUsers: names.Count == 0 ? null : names),
                3 => DevicePush.Run(nvr, file, false, false, false, false, true, false, progress, netFields: fields),
                5 => DevicePush.Run(nvr, file, false, false, true, false, false, false, progress, channelMap: map),
                _ => DevicePush.Run(nvr, file, false, false, false, true, false, false, progress, channelMap: map)
            });
            Status(string.Join(" | ", lines));
            MessageBox.Show(this, string.Join("\n", lines), "Перенос строк", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Warn(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
        await ReloadAsync();
    }

    void PickDifferent()
    {
        if (!_grid.Visible)
            return;
        _grid.ClearSelection();
        var count = 0;
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.DefaultCellStyle.BackColor == Diff)
            {
                row.Selected = true;
                count++;
            }
        }
        Status(count == 0 ? "Отличий нет." : "Выбрано отличающихся строк: " + count);
    }

    void FillTree()
    {
        _tree.BeginUpdate();
        _tree.Nodes.Clear();
        if (_live != null)
        {
            TreeNode Folder(string name)
            {
                foreach (TreeNode node in _tree.Nodes)
                    if (node.Text == name)
                        return node;
                return _tree.Nodes.Add(name);
            }
            var info = Folder("Сведения");
            info.Nodes.Add(new TreeNode("SystemInfo") { Tag = _live.Info });
            if (_live.Function != null)
                info.Nodes.Add(new TreeNode("Возможности аппарата") { Tag = _live.Function });
            foreach (var item in Catalog.Items)
            {
                if (_live.Configs.TryGetValue(item.Name, out var node))
                    Folder(item.Group).Nodes.Add(new TreeNode(item.Title) { Tag = node, ToolTipText = item.Name });
            }
            _tree.ExpandAll();
        }
        _tree.EndUpdate();
    }

    string DeviceText()
    {
        if (_live == null || _nvr == null)
            return "Регистратор не подключён.\nНажмите «Найти», введите пароль и «Подключиться».\nПароль на диск не пишется.";
        return _live.Overview(_nvr.Host + ":" + _nvr.Port + "   логин " + _nvr.Username);
    }

    string FileText(bool shortForm)
    {
        if (_file == null)
            return "Cfg не открыт. Его можно перетащить в окно или нажать «Открыть cfg».";
        var summary = _file.Describe();
        var builder = new StringBuilder();
        builder.AppendLine(summary.File);
        builder.AppendLine("Формат: " + summary.Generation);
        builder.AppendLine("Прошивка в файле: " + summary.Version);
        builder.AppendLine("Модель: " + summary.Product);
        builder.AppendLine("Серийный: " + summary.Serial);
        builder.AppendLine("IP в файле: " + summary.Ip + "   MAC " + summary.Mac);
        builder.AppendLine("Каналов: " + summary.Channels + ", с камерой: " + summary.Cameras);
        builder.AppendLine();
        builder.AppendLine("Учётные записи. Показан пароль, если он заводской и известен. Остальные регистратор хранит только свёрткой, её нельзя превратить обратно в пароль.");
        foreach (var user in _file.Users)
            builder.AppendLine("  " + user.Name + "   группа " + user.Group + "   пароль " + user.Shown + "   прав " + user.Authority.Count);
        if (!shortForm)
        {
            builder.AppendLine();
            builder.AppendLine("Камеры");
            foreach (var camera in _file.Cameras())
            {
                if (camera.Ip.Length == 0 && camera.Title.Length == 0)
                    continue;
                var password = camera.Password.Length == 0 ? "(пустой)" : camera.Password;
                builder.AppendLine("  " + camera.Channel + "  " + camera.Title + "  " + camera.Ip + ":" + camera.Port + "  " + camera.Protocol + "  " + camera.User + "  пароль " + password);
            }
        }
        builder.AppendLine();
        builder.AppendLine("Чтобы перенести это на подключённый регистратор, отметьте пункты сверху и нажмите «Записать на регистратор».");
        return builder.ToString();
    }

    string ServicesText(DeviceSnapshot? shot)
    {
        if (shot == null)
            return "Сетевые службы читаются после подключения.";
        var builder = new StringBuilder();
        foreach (var item in Catalog.Items.Where(item => item.Group == "Сервисы"))
        {
            builder.AppendLine(item.Title);
            if (shot.Configs.TryGetValue(item.Name, out var node))
                builder.AppendLine(DeviceSnapshot.Outline(node));
            else
                builder.AppendLine("нет на этом аппарате");
            builder.AppendLine();
        }
        return builder.ToString();
    }

    string FileServicesText()
    {
        if (_file?.Json.GetValueOrDefault("Json/NetWork") is not JsonObject net)
            return "В файле нет раздела сети.";
        var builder = new StringBuilder();
        foreach (var key in new[] { "NetEmail", "NetFTP", "NetDDNS", "NetPPPoE", "Upnp", "RTSP", "Nat", "Wifi", "NetARSP" })
        {
            builder.AppendLine(key);
            builder.AppendLine(net[key] == null ? "нет в файле" : DeviceSnapshot.Outline(net[key]));
            builder.AppendLine();
        }
        return builder.ToString();
    }

    void BindCameras(DataGridView grid, List<CameraRow>? rows)
    {
        Setup(grid, "Канал", "Вкл", "Название", "IP", "Протокол", "Порт", "Логин", "Пароль камеры");
        foreach (var row in rows ?? [])
            Add(grid, row.Channel, row.Channel, row.Enable ? "да" : "", row.Title, row.Ip, row.Protocol, row.Port == 0 ? "" : row.Port, row.User, row.Password.Length == 0 ? "(пустой)" : row.Password);
    }

    void BindUsers(DataGridView grid, List<UserRow>? rows)
    {
        Setup(grid, "Имя", "Группа", "Пароль", "Прав", "Заметка");
        foreach (var row in rows ?? [])
            Add(grid, row.Name, row.Name, row.Group, row.Shown, row.Authority.Count, row.Memo);
    }

    void BindNetwork(DataGridView grid, NetInfo? net, bool file)
    {
        Setup(grid, "Параметр", "Значение");
        if (net == null)
            return;
        Add(grid, "ip", "IP", net.Ip);
        Add(grid, "mask", "Маска", net.Mask);
        Add(grid, "gateway", "Шлюз", net.Gateway);
        Add(grid, "dns", "DNS", net.Dns);
        Add(grid, "dns2", "DNS запасной", net.Dns2);
        Add(grid, "mac", "MAC", net.Mac);
        Add(grid, "tcp", "TCP", net.Tcp.ToString());
        Add(grid, "udp", "UDP", net.Udp.ToString());
        Add(grid, "http", "HTTP", net.Http.ToString());
        Add(grid, "ntp", "NTP", net.Ntp ? "включён, пояс " + net.TimeZone : "выключен");
        if (!file && _live?.Configs.GetValueOrDefault("NetWork.NetDHCP") is JsonArray dhcp)
        {
            foreach (var item in dhcp)
            {
                if (item is JsonObject row)
                    Add(grid, "dhcp", "DHCP " + NvrClient.Str(row, "Interface"), NvrClient.Bool(row, "Enable") ? "включён" : "выключен");
            }
        }
    }

    void BindRecords(DataGridView grid, List<RecordRow>? rows)
    {
        Setup(grid, "Канал", "Режим", "Маска", "Интервал");
        foreach (var row in rows ?? [])
            Add(grid, row.Channel, row.Channel, row.Mode, row.Mask, row.Period);
    }

    void BindMotion(DataGridView grid, List<MotionRow>? rows)
    {
        Setup(grid, "Канал", "Движение", "Чувствительность", "Потеря", "Засвет");
        foreach (var row in rows ?? [])
            Add(grid, row.Channel, row.Channel, row.Motion ? "да" : "", row.Level, row.Loss ? "да" : "", row.Blind ? "да" : "");
    }

    void BindLogs(DataGridView grid, List<LogRow>? rows)
    {
        Setup(grid, "Время", "Кто", "Событие", "Данные", "Откуда");
        foreach (var row in (rows ?? []).TakeLast(500))
            Add(grid, null, row.Time, row.Who, row.Event, row.Data, row.Source);
    }

    void Add(DataGridView grid, object? tag, params object[] values)
    {
        var index = grid.Rows.Add(values);
        grid.Rows[index].Tag = tag;
    }

    void Setup(DataGridView grid, params string[] columns)
    {
        grid.DataSource = null;
        grid.Columns.Clear();
        grid.Rows.Clear();
        foreach (var column in columns)
            grid.Columns.Add(column, column);
    }

    void MarkDifferent()
    {
        var live = new Dictionary<string, DataGridViewRow>(StringComparer.Ordinal);
        foreach (DataGridViewRow row in _liveGrid.Rows)
        {
            if (row.Tag == null)
                continue;
            live[row.Tag.ToString() ?? ""] = row;
        }
        foreach (DataGridViewRow row in _grid.Rows)
        {
            row.DefaultCellStyle.BackColor = Color.White;
            if (row.Tag == null)
                continue;
            if (!live.TryGetValue(row.Tag.ToString() ?? "", out var other))
            {
                row.DefaultCellStyle.BackColor = Diff;
                continue;
            }
            for (var column = 0; column < row.Cells.Count && column < other.Cells.Count; column++)
            {
                var left = row.Cells[column].Value?.ToString() ?? "";
                var right = other.Cells[column].Value?.ToString() ?? "";
                if (left != right)
                {
                    row.DefaultCellStyle.BackColor = Diff;
                    break;
                }
            }
        }
    }

    void Note(string text) => Status(text);

    void ShowView(string mode)
    {
        var grids = mode == "grids";
        var text = mode == "text";
        _compare.Visible = mode != "param";
        _param.Visible = mode == "param";
        _grid.Visible = grids;
        _liveGrid.Visible = grids;
        _text.Visible = text;
        _liveText.Visible = text;
        _move.Enabled = grids && !_busy;
        _pick.Enabled = grids && !_busy;
    }

    void ApplyFound(FoundDevice device)
    {
        _host.Text = device.Ip;
        _port.Text = device.Port.ToString();
    }

    void About()
    {
        MessageBox.Show(this,
            "XM установщик\nАвтор: Slayer NSK\n\nСлева файл, справа регистратор. Стрелка между ними переносит только открытый раздел. Строку камеры можно отправить в другой канал.\nПароль входа в программу не записывается.",
            "О программе",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    void SetBusy(bool busy)
    {
        _busy = busy;
        _connect.Enabled = !busy;
        _write.Enabled = !busy;
        _move.Enabled = !busy && _grid.Visible;
        _pick.Enabled = !busy && _grid.Visible;
        UseWaitCursor = busy;
    }

    void Status(string text) => _status.Text = text;

    void Warn(string text) => MessageBox.Show(this, text, "XM установщик", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    static Label Mk(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(6, 8, 2, 0), ForeColor = Ink };

    void ArmSelection(DataGridView grid)
    {
        var pressRow = -1;
        var onlyThis = false;
        grid.CellMouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || e.RowIndex < 0 || ModifierKeys != Keys.None)
            {
                pressRow = -1;
                return;
            }
            pressRow = e.RowIndex;
            onlyThis = grid.SelectedRows.Count == 1 && grid.Rows[e.RowIndex].Selected;
        };
        grid.CellMouseUp += (_, e) =>
        {
            if (pressRow < 0 || e.RowIndex != pressRow || ModifierKeys != Keys.None)
                return;
            var row = pressRow;
            var clear = onlyThis;
            pressRow = -1;
            BeginInvoke(() =>
            {
                if (!grid.IsHandleCreated || row < 0 || row >= grid.Rows.Count)
                    return;
                grid.ClearSelection();
                if (clear)
                {
                    try { grid.CurrentCell = null; } catch { /* сетка оставляет текущую ячейку */ }
                    Status("Выделение снято.");
                    return;
                }
                grid.Rows[row].Selected = true;
            });
        };
        grid.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Escape)
                return;
            ClearPick();
            e.Handled = true;
            e.SuppressKeyPress = true;
        };
    }

    void ClearPick()
    {
        _grid.ClearSelection();
        try { _grid.CurrentCell = null; } catch { /* сетка оставляет текущую ячейку */ }
        Status("Выделение снято. Ctrl добавляет строку, повторный щелчок снимает одну.");
    }

    static Label Head(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Top,
        Height = 32,
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(12, 0, 0, 0),
        BackColor = Ink,
        ForeColor = Color.White,
        Font = new Font("Segoe UI Semibold", 11)
    };

    static void PaintButton(Button button, bool primary)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = primary ? 0 : 1;
        button.FlatAppearance.BorderColor = Accent;
        button.BackColor = primary ? Accent : Color.White;
        button.ForeColor = primary ? Color.White : Accent;
        button.Cursor = Cursors.Hand;
        button.Margin = new Padding(6, 2, 0, 0);
    }

    static void StyleGrid(DataGridView grid, bool selectMany)
    {
        grid.Dock = DockStyle.Fill;
        grid.ReadOnly = true;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.RowHeadersVisible = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.MultiSelect = selectMany;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        grid.ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText;
        grid.BackgroundColor = Color.White;
        grid.BorderStyle = BorderStyle.None;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.GridColor = Color.FromArgb(226, 232, 240);
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersHeight = 34;
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(236, 242, 248);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Ink;
        grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI Semibold", 9);
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(236, 242, 248);
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(214, 230, 245);
        grid.DefaultCellStyle.SelectionForeColor = Ink;
        grid.DefaultCellStyle.Padding = new Padding(4, 0, 4, 0);
        grid.RowTemplate.Height = 28;
        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(248, 250, 252);
    }

    void DrawPage(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0)
            return;
        var selected = (e.State & DrawItemState.Selected) != 0;
        using var brush = new SolidBrush(selected ? Accent : Ink);
        e.Graphics.FillRectangle(brush, e.Bounds);
        var text = _pages.Items[e.Index]?.ToString() ?? "";
        TextRenderer.DrawText(e.Graphics, text, Font, new Rectangle(e.Bounds.X + 16, e.Bounds.Y, e.Bounds.Width - 16, e.Bounds.Height), Color.White, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
    }
}
