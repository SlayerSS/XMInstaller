namespace XMInstaller;

enum UserAction { Cancel, SaveFile, WriteDevice, Delete }

sealed class UserDialog : Form
{
    readonly TextBox _hash = new() { ReadOnly = true, Dock = DockStyle.Top };
    readonly TextBox _newPassword = new() { Dock = DockStyle.Top, UseSystemPasswordChar = false };
    readonly CheckBox _asHash = new() { Text = "Это уже хеш регистратора, не обычный пароль", AutoSize = true, Dock = DockStyle.Top };
    readonly ComboBox _group = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top };
    readonly CheckedListBox _rights = new() { Dock = DockStyle.Fill, CheckOnClick = true };
    public UserAction Action { get; private set; } = UserAction.Cancel;

    public string GroupName => _group.Text;
    public string NewPassword => _newPassword.Text;
    public bool PasswordIsHash => _asHash.Checked;
    public List<string> Rights => _rights.CheckedItems.Cast<string>().ToList();

    public UserDialog(UserRow user, IEnumerable<string> groups, IEnumerable<string> allRights, bool canWriteDevice, bool canSaveFile)
    {
        Text = "Пользователь " + user.Name;
        Width = 560;
        Height = 640;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 10);
        MinimizeBox = false;
        MaximizeBox = false;

        var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(12) };
        var known = Sofia.Hint(user.Password);
        top.Controls.Add(Label(known.Length > 0 ? "Пароль" : "Исходный пароль регистратор не хранит"));
        _hash.Text = known.Length > 0 ? known : "Эти 8 символов — свёртка, из неё пароль не восстанавливается: " + user.Password;
        _hash.Font = new Font("Consolas", 12);
        top.Controls.Add(_hash);
        var copy = new Button { Text = known.Length > 0 ? "Копировать пароль" : "Копировать свёртку", AutoSize = true, Dock = DockStyle.Top };
        copy.Click += (_, _) => Clipboard.SetText(known.Length > 0 ? known : user.Password);
        top.Controls.Add(copy);
        top.Controls.Add(Label("Новый пароль. Пустое поле оставляет текущий."));
        top.Controls.Add(_newPassword);
        top.Controls.Add(_asHash);
        top.Controls.Add(Label("Группа"));
        foreach (var name in groups.Distinct())
            _group.Items.Add(name);
        if (_group.Items.Count == 0)
            _group.Items.Add(user.Group);
        _group.SelectedItem = user.Group;
        if (_group.SelectedIndex < 0 && _group.Items.Count > 0)
            _group.SelectedIndex = 0;
        top.Controls.Add(_group);
        top.Controls.Add(Label("Права"));

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        var cancel = new Button { Text = "Закрыть", AutoSize = true };
        cancel.Click += (_, _) => Close();
        var write = new Button { Text = "Записать на регистратор", AutoSize = true, Enabled = canWriteDevice };
        write.Click += (_, _) => Done(UserAction.WriteDevice);
        var save = new Button { Text = "Оставить в файле для переноса", AutoSize = true, Enabled = canSaveFile };
        save.Click += (_, _) => Done(UserAction.SaveFile);
        var delete = new Button { Text = "Удалить с регистратора", AutoSize = true, Enabled = canWriteDevice };
        delete.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "Удалить пользователя " + user.Name + "?", "Пользователь", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                Done(UserAction.Delete);
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(write);
        buttons.Controls.Add(save);
        buttons.Controls.Add(delete);

        var rights = new HashSet<string>(allRights, StringComparer.Ordinal);
        foreach (var right in user.Authority)
            rights.Add(right);
        foreach (var right in rights.OrderBy(item => item, StringComparer.Ordinal))
            _rights.Items.Add(right, user.Authority.Contains(right));
        Controls.Add(_rights);
        Controls.Add(buttons);
        Controls.Add(top);
    }

    void Done(UserAction action)
    {
        Action = action;
        DialogResult = DialogResult.OK;
        Close();
    }

    static Label Label(string text) => new() { Text = text, AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(0, 8, 0, 2) };
}
