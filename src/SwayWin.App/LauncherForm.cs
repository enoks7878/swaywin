namespace SwayWin;

internal sealed class LauncherForm : Form
{
    private readonly Func<string, bool> _launch;
    private string[] _commands;
    private readonly TextBox _command;
    private readonly ListBox _suggestions;
    private readonly Label _status;
    private readonly Font _font = new("Segoe UI", 10f);

    internal LauncherForm(Rectangle monitor, Func<string, bool> launch, IReadOnlyList<string> history)
    {
        _launch = launch;
        _commands = history.ToArray();
        Text = "SwayWin Run";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(420, 360);
        Location = new Point(monitor.Left + (monitor.Width - Width) / 2, monitor.Top + (monitor.Height - Height) / 2);
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        DoubleBuffered = true;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = _font;

        Controls.Add(new Label
        {
            Text = "RUN COMMAND",
            Bounds = new Rectangle(20, 18, 380, 24),
            ForeColor = Theme.Text
        });
        var inputSurface = new Panel
        {
            Bounds = new Rectangle(20, 52, 380, 36),
            BackColor = Theme.Surface
        };
        _command = new TextBox
        {
            Bounds = new Rectangle(9, 8, 362, 24),
            BorderStyle = BorderStyle.None,
            BackColor = Theme.Surface,
            ForeColor = Theme.Text,
            Text = string.Empty,
            TabIndex = 0
        };
        inputSurface.Controls.Add(_command);
        Controls.Add(inputSurface);
        Controls.Add(new Label
        {
            Text = "Commands",
            Bounds = new Rectangle(20, 100, 380, 22),
            ForeColor = Theme.Muted
        });
        _suggestions = new ListBox
        {
            Bounds = new Rectangle(20, 124, 380, 150),
            BackColor = Theme.Surface,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.None,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 28,
            IntegralHeight = false,
            TabIndex = 1
        };
        _suggestions.DrawItem += (_, e) =>
        {
            if (e.Index < 0)
                return;
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using var background = new SolidBrush(selected ? Theme.Accent : Theme.Surface);
            e.Graphics.FillRectangle(background, e.Bounds);
            TextRenderer.DrawText(e.Graphics, (string)_suggestions.Items[e.Index], _font,
                Rectangle.Inflate(e.Bounds, -6, 0), Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        };
        _suggestions.DoubleClick += (_, _) =>
        {
            if (_suggestions.SelectedItem is string command)
            {
                _command.Text = command;
                RunCommand();
            }
        };
        Controls.Add(_suggestions);
        _status = new Label
        {
            Text = "Enter runs what you type, including arguments.",
            Bounds = new Rectangle(20, 282, 380, 30),
            ForeColor = Theme.Muted
        };
        Controls.Add(_status);
        var run = new Button
        {
            Text = "Run",
            Bounds = new Rectangle(230, 318, 78, 26),
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Accent,
            ForeColor = Theme.Text,
            TabIndex = 2
        };
        run.FlatAppearance.BorderSize = 0;
        run.Click += (_, _) => RunCommand();
        Controls.Add(run);
        var cancel = new Button
        {
            Text = "Cancel",
            Bounds = new Rectangle(318, 318, 82, 26),
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Surface,
            ForeColor = Theme.Text,
            TabIndex = 3,
            DialogResult = DialogResult.Cancel
        };
        cancel.FlatAppearance.BorderSize = 0;
        cancel.Click += (_, _) => Cancel();
        Controls.Add(cancel);
        AcceptButton = run;
        CancelButton = cancel;
        _command.TextChanged += (_, _) => FilterSuggestions();
        FilterSuggestions();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= 0x80; // A tool-window overlay, not an ordinary tiled app.
            return parameters;
        }
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        _command.Focus();
        string[] commands = await Task.Run(() =>
        {
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IEnumerable<string> directories = new[] { Environment.SystemDirectory }
                .Concat((Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator));
            foreach (string entry in directories.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    string directory = Environment.ExpandEnvironmentVariables(entry.Trim().Trim('"'));
                    if (!Path.IsPathFullyQualified(directory))
                        continue;
                    foreach (string path in Directory.EnumerateFiles(directory))
                    {
                        if (Path.GetExtension(path).ToLowerInvariant() is not (".exe" or ".com"))
                            continue;
                        string name = Path.GetFileName(path);
                        if (Path.GetFileNameWithoutExtension(name).ToLowerInvariant() is "sh" or "bash" or "dash" or "zsh" or "fish")
                            continue;
                        found.Add(name.Any(char.IsWhiteSpace) ? $"\"{name}\"" : name);
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
                {
                    // An inaccessible PATH directory must not prevent typed commands from running.
                }
            }
            return found.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        });
        if (IsDisposed || Disposing)
            return;
        _commands = _commands.Concat(commands).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        FilterSuggestions();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var border = new Pen(Theme.Accent, 2f);
        e.Graphics.DrawRectangle(border, 1, 1, ClientSize.Width - 2, ClientSize.Height - 2);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            Cancel();
            return true;
        }
        if (keyData == Keys.Enter)
        {
            if (_suggestions.Focused && _suggestions.SelectedItem is string suggestion)
                _command.Text = suggestion;
            RunCommand();
            return true;
        }
        if (keyData == Keys.Down && _command.Focused && _suggestions.Items.Count > 0)
        {
            _suggestions.SelectedIndex = 0;
            _suggestions.Focus();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void FilterSuggestions()
    {
        string filter = _command.Text.Trim();
        _suggestions.BeginUpdate();
        _suggestions.Items.Clear();
        _suggestions.Items.AddRange(_commands.Where(item => item.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray());
        _suggestions.EndUpdate();
    }

    private void RunCommand()
    {
        string command = _command.Text.TrimStart();
        if (string.IsNullOrWhiteSpace(command))
            return;
        if (!_launch(command))
        {
            _status.Text = "Use a Windows executable, shortcut path, or supported URI. Linux shell commands are not run.";
            _command.Focus();
            return;
        }
        DialogResult = DialogResult.OK;
        Close();
    }

    private void Cancel()
    {
        DialogResult = DialogResult.Cancel;
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _font.Dispose();
        base.Dispose(disposing);
    }
}
