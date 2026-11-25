namespace XMInstaller;

sealed class ChannelMapDialog : Form
{
    readonly List<(int Source, NumericUpDown Target, CheckBox Use)> _rows = [];
    public Dictionary<int, int> Map { get; } = new();

    public ChannelMapDialog(string section, IReadOnlyList<(int Channel, string Title)> rows, int maxChannel)
    {
        Text = "Куда записать";
        Font = new Font("Segoe UI", 10);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        Width = 480;
        var limit = Math.Max(1, maxChannel);
        var intro = new Label
        {
            Dock = DockStyle.Top,
            Height = 52,
            Padding = new Padding(14, 12, 14, 0),
            Text = section + ". Галочка включает строку. Номер справа — канал регистратора, по умолчанию тот же."
        };
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var y = 8;
        foreach (var row in rows.OrderBy(item => item.Channel))
        {
            var use = new CheckBox { Checked = true, Left = 12, Top = y + 2, Width = 22, Text = "" };
            var caption = "Канал " + row.Channel + (row.Title.Length == 0 ? "" : "   " + row.Title);
            scroll.Controls.Add(use);
            scroll.Controls.Add(new Label
            {
                Text = caption,
                AutoEllipsis = true,
                Left = 36,
                Top = y + 4,
                Width = 250,
                Height = 24
            });
            scroll.Controls.Add(new Label { Text = "→", Left = 292, Top = y + 4, Width = 24, Height = 24 });
            var number = new NumericUpDown
            {
                Left = 320,
                Top = y,
                Width = 72,
                Minimum = 1,
                Maximum = limit,
                Value = Math.Min(Math.Max(row.Channel, 1), limit)
            };
            scroll.Controls.Add(number);
            _rows.Add((row.Channel, number, use));
            y += 34;
        }
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 52,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(12, 8, 12, 8)
        };
        var ok = new Button { Text = "Перенести →", AutoSize = true };
        var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, AutoSize = true };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;
        Controls.Add(scroll);
        Controls.Add(buttons);
        Controls.Add(intro);
        Height = Math.Min(560, y + 160);
        ok.Click += (_, _) =>
        {
            var chosen = _rows.Where(row => row.Use.Checked).ToList();
            if (chosen.Count == 0)
            {
                MessageBox.Show(this, "Не отмечено, что переносить.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var used = new HashSet<int>();
            foreach (var row in chosen)
            {
                var target = (int)row.Target.Value;
                if (!used.Add(target))
                {
                    MessageBox.Show(this, "Канал " + target + " указан дважды.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }
            Map.Clear();
            foreach (var row in chosen)
                Map[row.Source] = (int)row.Target.Value;
            DialogResult = DialogResult.OK;
            Close();
        };
    }
}
