namespace WinKeyGuard;

sealed class SettingsForm : Form
{
    readonly Config _cfg;
    readonly Action<Config> _onSave;

    readonly ListBox _list = new()
    {
        Dock = DockStyle.Fill,
        IntegralHeight = false,
        SelectionMode = SelectionMode.MultiExtended
    };

    readonly TextBox _name = new()
    {
        Dock = DockStyle.Fill,
        PlaceholderText = "Program name, e.g. TslGame.exe"
    };

    readonly NumericUpDown _disableDelay = Number(0, 5000);
    readonly NumericUpDown _restoreDelay = Number(0, 10000);

    readonly CheckBox _leftWin = new() { Text = "Left Win key", AutoSize = true };
    readonly CheckBox _rightWin = new() { Text = "Right Win key", AutoSize = true };

    static NumericUpDown Number(int min, int max) => new()
    {
        Minimum = min,
        Maximum = max,
        Increment = 50,
        Width = 90
    };

    // 'working' is a copy of the live config; it is only applied when Save is clicked.
    public SettingsForm(Config working, Action<Config> onSave)
    {
        _cfg = working;
        _onSave = onSave;

        Text = "WinKeyGuard settings";
        Icon = Assets.Load("app.ico", SystemInformation.SmallIconSize);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(480, 580);
        Padding = new Padding(12);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(BuildProgramsGroup(), 0, 0);
        root.Controls.Add(BuildKeysGroup(), 0, 1);
        root.Controls.Add(BuildTimingGroup(), 0, 2);
        root.Controls.Add(BuildButtons(), 0, 3);
        Controls.Add(root);
    }

    GroupBox BuildProgramsGroup()
    {
        var group = new GroupBox
        {
            Text = "Programs that disable the Windows keys",
            Dock = DockStyle.Fill,
            Padding = new Padding(8)
        };

        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var hint = new Label
        {
            Text = "The Windows keys are disabled while any of these programs has focus.",
            AutoSize = true,
            Padding = new Padding(0, 0, 0, 6)
        };

        foreach (var target in _cfg.Targets) _list.Items.Add(target);
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete) RemoveSelected();
        };

        // Row: [ program name ........ ] [Add]
        var addRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
        addRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        addRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var add = new Button { Text = "Add", AutoSize = true };
        add.Click += (_, _) => AddFromTextBox();
        _name.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            AddFromTextBox();
        };
        addRow.Controls.Add(_name, 0, 0);
        addRow.Controls.Add(add, 1, 0);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        var pick = new Button { Text = "Pick running program...", AutoSize = true };
        pick.Click += (_, _) => PickRunning();
        var browse = new Button { Text = "Browse for .exe...", AutoSize = true };
        browse.Click += (_, _) => Browse();
        var remove = new Button { Text = "Remove selected", AutoSize = true };
        remove.Click += (_, _) => RemoveSelected();
        buttons.Controls.AddRange(new Control[] { pick, browse, remove });

        table.Controls.Add(hint, 0, 0);
        table.Controls.Add(_list, 0, 1);
        table.Controls.Add(addRow, 0, 2);
        table.Controls.Add(buttons, 0, 3);
        group.Controls.Add(table);
        return group;
    }

    GroupBox BuildTimingGroup()
    {
        var group = new GroupBox
        {
            Text = "Focus timing",
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(8)
        };

        var table = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, RowCount = 3 };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _disableDelay.Value = Math.Clamp(_cfg.DisableDelayMs, 0, 5000);
        _restoreDelay.Value = Math.Clamp(_cfg.RestoreDelayMs, 0, 10000);

        AddTimingRow(table, 0, "Wait before disabling after a program gains focus", _disableDelay);
        AddTimingRow(table, 1, "Wait before restoring after a program loses focus", _restoreDelay);

        var note = new Label
        {
            Text = "The restore wait absorbs the brief focus flicker when you alt-tab, so a quick " +
                   "alt-tab back into the game never re-enables the keys. A lower value brings the " +
                   "Windows key back sooner after you leave the game. 0 means immediately.",
            AutoSize = true,
            MaximumSize = new Size(420, 0),
            ForeColor = SystemColors.GrayText,
            Padding = new Padding(0, 6, 0, 0)
        };
        table.Controls.Add(note, 0, 2);
        table.SetColumnSpan(note, 3);

        group.Controls.Add(table);
        return group;
    }

    GroupBox BuildKeysGroup()
    {
        var group = new GroupBox
        {
            Text = "Keys to disable",
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(8)
        };

        _leftWin.Checked = _cfg.DisableLeftWin;
        _rightWin.Checked = _cfg.DisableRightWin;
        _rightWin.Margin = new Padding(24, 3, 3, 3);

        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        flow.Controls.AddRange(new Control[] { _leftWin, _rightWin });
        group.Controls.Add(flow);
        return group;
    }

    static void AddTimingRow(TableLayoutPanel table, int row, string label, NumericUpDown number)
    {
        table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 6) }, 0, row);
        table.Controls.Add(number, 1, row);
        table.Controls.Add(new Label { Text = "ms", AutoSize = true, Anchor = AnchorStyles.Left }, 2, row);
    }

    Control BuildButtons()
    {
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 8, 0, 0)
        };

        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var save = new Button { Text = "Save", AutoSize = true };
        save.Click += (_, _) => SaveAndClose();

        flow.Controls.Add(cancel);   // right-to-left: first added sits on the far right
        flow.Controls.Add(save);
        AcceptButton = save;
        CancelButton = cancel;
        return flow;
    }

    // ---- Program list actions --------------------------------------------------------------

    bool AddName(string raw)
    {
        string name = Path.GetFileName(raw.Trim().Trim('"'));
        if (name.Length == 0) return false;
        if (!Path.HasExtension(name)) name += ".exe";

        bool exists = _list.Items.Cast<string>()
            .Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
        if (exists) return false;

        _list.Items.Add(name);
        return true;
    }

    void AddFromTextBox()
    {
        AddName(_name.Text);
        _name.Clear();
        _name.Focus();
    }

    void Browse()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Choose the game's .exe",
            Filter = "Programs (*.exe)|*.exe",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) AddName(dialog.FileName);
    }

    void PickRunning()
    {
        using var dialog = new RunningProgramsDialog();
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        foreach (var exe in dialog.SelectedExes) AddName(exe);
    }

    void RemoveSelected()
    {
        foreach (var item in _list.SelectedItems.Cast<object>().ToList())
            _list.Items.Remove(item);
    }

    void SaveAndClose()
    {
        if (_list.Items.Count == 0)
        {
            MessageBox.Show(this, "Add at least one program, or the Windows keys will never be disabled.",
                "WinKeyGuard", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!_leftWin.Checked && !_rightWin.Checked)
        {
            MessageBox.Show(this, "Select at least one Win key, or nothing will be disabled.",
                "WinKeyGuard", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _cfg.Targets = _list.Items.Cast<string>().ToList();
        _cfg.DisableLeftWin = _leftWin.Checked;
        _cfg.DisableRightWin = _rightWin.Checked;
        _cfg.DisableDelayMs = (int)_disableDelay.Value;
        _cfg.RestoreDelayMs = (int)_restoreDelay.Value;

        try
        {
            _onSave(_cfg);
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not save settings: " + ex.Message,
                "WinKeyGuard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}

// Lets you add a game by picking it from the programs that currently have a window.
sealed class RunningProgramsDialog : Form
{
    readonly ListView _view = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = true,
        HideSelection = false
    };

    public List<string> SelectedExes { get; } = new();

    public RunningProgramsDialog()
    {
        Text = "Pick running programs";
        Icon = Assets.Load("app.ico", SystemInformation.SmallIconSize);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(560, 420);
        Padding = new Padding(12);

        _view.Columns.Add("Program", 190);
        _view.Columns.Add("Window title", 340);
        foreach (var (exe, title) in RunningWindows.List())
            _view.Items.Add(new ListViewItem(new[] { exe, title }));
        _view.DoubleClick += (_, _) => Accept();

        var hint = new Label
        {
            Text = "Start the game first, then select it here (Ctrl-click for several).",
            AutoSize = true,
            Padding = new Padding(0, 0, 0, 6)
        };

        var add = new Button { Text = "Add selected", AutoSize = true };
        add.Click += (_, _) => Accept();
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 8, 0, 0)
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(add);
        AcceptButton = add;
        CancelButton = cancel;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(hint, 0, 0);
        root.Controls.Add(_view, 0, 1);
        root.Controls.Add(buttons, 0, 2);
        Controls.Add(root);
    }

    void Accept()
    {
        SelectedExes.Clear();
        foreach (ListViewItem item in _view.SelectedItems) SelectedExes.Add(item.Text);
        if (SelectedExes.Count > 0) DialogResult = DialogResult.OK;
    }
}
