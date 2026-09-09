using Agience.Core;

namespace Agience.Manager;

/// <summary>
/// The instances this machine runs, and what each one is.
/// </summary>
/// <remarks>
/// <para>
/// A list, because a machine is not "an Origin and a Mantle". Two Origins on two domains is an
/// ordinary arrangement, and so is a Mantle with no Origin beside it, verifying against an authority
/// somewhere else. A screen with one Origin section and one Mantle section makes both inexpressible,
/// and the second one is what joining a network looks like.
/// </para>
/// <para>
/// Edits a draft, not the live configuration. Half-typed domains and duplicate ports exist
/// while somebody is working; letting those reach the supervisor means the next poll launches
/// against them. Save is what commits, and Save is where the whole draft is checked at once.
/// </para>
/// <para>
/// Saving does not restart anything, and the screen says so: a running instance keeps the settings
/// it started with until it is stopped and started again.
/// </para>
/// </remarks>
public sealed class ConfigPage : PageControl
{
    private readonly Supervisor _supervisor;
    private readonly Action _afterSave;

    private readonly ListBox _list = new();
    private readonly Button _addOrigin = new();
    private readonly Button _addMantle = new();
    private readonly Button _remove = new();

    private readonly GroupBox _editor = new();
    private readonly TextBox _name = new();
    private readonly TextBox _domain = new();
    private readonly NumericUpDown _port = new();
    private readonly ComboBox _authority = new();
    private readonly TextBox _authorityUrl = new();
    private readonly CheckBox _enabled = new();
    private readonly Label _derived = new();
    private readonly Label _dataDir = new();

    private readonly CheckBox _startAtLogin = new();
    private readonly CheckBox _startOnLaunch = new();
    private readonly CheckBox _restartOnExit = new();
    private readonly Label _note = new();
    private readonly Button _save = new();
    private readonly Button _revert = new();
    private readonly Button _apply = new();

    private AgienceConfig _draft;
    private bool _loading;

    public ConfigPage(Supervisor supervisor, Action afterSave)
    {
        _supervisor = supervisor;
        _afterSave = afterSave;
        _draft = supervisor.Config.Clone();

        AutoScroll = true;

        // ── the list ────────────────────────────────────────────────────────────────────────────
        Controls.Add(new Label
        {
            Text = "Instances on this machine",
            Font = new Font(Font, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(4, 8),
        });

        _list.Location = new Point(4, 32);
        _list.Size = new Size(250, 210);
        _list.SelectedIndexChanged += (_, _) => ShowSelected();
        Controls.Add(_list);

        Button(_addOrigin, "Add Origin", 4, 250, 118, () => Add(ServiceCatalog.Origin));
        Button(_addMantle, "Add Mantle", 136, 250, 118, () => Add(ServiceCatalog.Mantle));
        Button(_remove, "Remove selected", 4, 284, 250, RemoveSelected);

        Controls.Add(new Label
        {
            Text = "An Origin is an identity authority. A Mantle is a store, and it verifies " +
                   "against an Origin — one here, or one somewhere else.",
            AutoSize = false,
            Size = new Size(250, 60),
            Location = new Point(4, 320),
            ForeColor = SystemColors.GrayText,
        });

        // ── the editor ──────────────────────────────────────────────────────────────────────────
        _editor.Text = "Selected instance";
        _editor.Location = new Point(268, 24);
        _editor.Size = new Size(540, 300);
        Controls.Add(_editor);

        var y = 28;
        Field(_editor, "Name", _name, ref y, 200);
        _name.TextChanged += (_, _) => CaptureEditor();

        Field(_editor, "Domain", _domain, ref y, 260);
        _domain.TextChanged += (_, _) => CaptureEditor();

        Field(_editor, "Port", _port, ref y);
        _port.Minimum = 1;
        _port.Maximum = 65535;
        _port.ValueChanged += (_, _) => CaptureEditor();

        _editor.Controls.Add(new Label { Text = "Authority", AutoSize = true, Location = new Point(12, y + 4) });
        _authority.Location = new Point(120, y);
        _authority.Size = new Size(260, 24);
        _authority.DropDownStyle = ComboBoxStyle.DropDownList;
        _authority.SelectedIndexChanged += (_, _) => { CaptureEditor(); ShowDerived(); };
        _editor.Controls.Add(_authority);
        y += 30;

        _authorityUrl.Location = new Point(120, y);
        _authorityUrl.Size = new Size(400, 24);
        _authorityUrl.PlaceholderText = "https://origin.example.com";
        _authorityUrl.TextChanged += (_, _) => { CaptureEditor(); ShowDerived(); };
        _editor.Controls.Add(_authorityUrl);
        y += 30;

        _enabled.Text = "Run this instance";
        _enabled.AutoSize = true;
        _enabled.Location = new Point(120, y);
        _enabled.CheckedChanged += (_, _) => CaptureEditor();
        _editor.Controls.Add(_enabled);
        y += 28;

        _derived.AutoSize = false;
        _derived.Size = new Size(510, 56);
        _derived.Location = new Point(12, y);
        _derived.ForeColor = SystemColors.GrayText;
        _editor.Controls.Add(_derived);
        y += 60;

        _dataDir.AutoSize = false;
        _dataDir.Size = new Size(510, 34);
        _dataDir.Location = new Point(12, y);
        _dataDir.ForeColor = SystemColors.GrayText;
        _editor.Controls.Add(_dataDir);

        // ── everything that is not per-instance ─────────────────────────────────────────────────
        var b = 340;
        Controls.Add(new Label
        {
            Text = "Behaviour",
            Font = new Font(Font, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(268, b),
        });
        b += 26;

        Check(_startAtLogin, "Start Agience when I log in", 268, ref b);
        Check(_startOnLaunch, "Start the instances when Agience starts", 268, ref b);
        Check(_restartOnExit, "Restart an instance that stops on its own", 268, ref b);

        _note.AutoSize = false;
        _note.Size = new Size(540, 34);
        _note.Location = new Point(268, b + 6);
        _note.ForeColor = SystemColors.GrayText;
        Controls.Add(_note);
        b += 48;

        Button(_save, "Save", 268, b, 110, () => Save());
        Button(_revert, "Revert", 386, b, 110, LoadSettings);
        Button(_apply, "Save and restart", 504, b, 160, () => _ = SaveAndRestartAsync());

        LoadSettings();
    }

    public override void OnShown() => LoadSettings();

    // ── layout helpers ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Place a button and wire it up.
    /// </summary>
    /// <remarks>
    /// One overload only — see the identical remark in <see cref="StatusPage"/>. A second overload
    /// taking <c>Func&lt;Task&gt;</c> that forwards to this one resolves back to itself, recursing
    /// without terminating.
    /// </remarks>
    private void Button(Button button, string text, int x, int y, int width, Action onClick)
    {
        button.Text = text;
        button.Location = new Point(x, y);
        button.Size = new Size(width, 30);
        button.Click += (_, _) => onClick();
        Controls.Add(button);
    }


    private static void Field(Control parent, string caption, Control box, ref int y, int width = 90)
    {
        parent.Controls.Add(new Label { Text = caption, AutoSize = true, Location = new Point(12, y + 4) });
        box.Location = new Point(120, y);
        box.Size = new Size(width, 24);
        parent.Controls.Add(box);
        y += 30;
    }

    private void Check(CheckBox box, string text, int x, ref int y)
    {
        box.Text = text;
        box.AutoSize = true;
        box.Location = new Point(x, y);
        Controls.Add(box);
        y += 26;
    }

    // ── the list ────────────────────────────────────────────────────────────────────────────────

    private void LoadSettings()
    {
        _draft = _supervisor.Config.Clone();
        _loading = true;
        try
        {
            _startAtLogin.Checked = Autostart.IsEnabled();
            _startOnLaunch.Checked = _draft.StartServicesOnLaunch;
            _restartOnExit.Checked = _draft.RestartOnExit;
        }
        finally
        {
            _loading = false;
        }

        RefillList(_draft.Instances.FirstOrDefault()?.Id);
    }

    private void RefillList(string? select)
    {
        _loading = true;
        try
        {
            _list.Items.Clear();
            foreach (var instance in _draft.InStartOrder)
            {
                _list.Items.Add(new Row(instance));
            }

            var index = _list.Items.Cast<Row>()
                .Select((row, i) => (row, i))
                .Where(t => string.Equals(t.row.Instance.Id, select, StringComparison.OrdinalIgnoreCase))
                .Select(t => t.i)
                .DefaultIfEmpty(_list.Items.Count > 0 ? 0 : -1)
                .First();
            _list.SelectedIndex = index;
        }
        finally
        {
            _loading = false;
        }

        ShowSelected();
    }

    private ServiceInstance? Selected() => (_list.SelectedItem as Row)?.Instance;

    private void Add(ServiceKind kind)
    {
        // A name is asked for up front because it becomes the instance's id, and the id becomes a
        // directory holding keys — so it is the one field that cannot be changed afterwards.
        using var dialog = new NameInstanceForm(kind, _draft.Instances.Select(i => i.Name));
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var instance = _draft.Add(kind, dialog.InstanceName, dialog.Domain);
        RefillList(instance.Id);
        Recompute();
    }

    private void RemoveSelected()
    {
        if (Selected() is not { } instance)
        {
            return;
        }

        var running = _supervisor[instance.Id]?.Pid is not null;

        // Removing an instance never deletes its data. That directory holds signing keys and a
        // store; a configuration screen is not where something irreversible happens, and the Remove
        // page is where deleting data is a deliberate, typed decision.
        var message =
            $"Remove {instance.Display} from the configuration?" + Environment.NewLine + Environment.NewLine +
            $"Its data is left alone at {_draft.DataDirectoryOf(instance)} — adding it back with " +
            "the same name picks it up again." +
            (running ? Environment.NewLine + Environment.NewLine +
                       "It is running right now. It keeps running until you stop it, and it stays " +
                       "in the list until you do." : "");

        if (MessageBox.Show(this, message, "Agience", MessageBoxButtons.OKCancel, MessageBoxIcon.Question)
            != DialogResult.OK)
        {
            return;
        }

        _draft.Instances.Remove(instance);
        RefillList(null);
        Recompute();
    }

    // ── the editor ──────────────────────────────────────────────────────────────────────────────

    private void ShowSelected()
    {
        var instance = Selected();
        _editor.Enabled = instance is not null;
        _remove.Enabled = instance is not null;

        if (instance is null)
        {
            _editor.Text = "No instance selected";
            _derived.Text = "";
            _dataDir.Text = "";
            return;
        }

        _loading = true;
        try
        {
            _editor.Text = instance.Display;
            _name.Text = instance.Name;
            _domain.Text = instance.Domain;
            _port.Value = Math.Clamp(instance.Port, _port.Minimum, _port.Maximum);
            _enabled.Checked = instance.Enabled;
            FillAuthorities(instance);
        }
        finally
        {
            _loading = false;
        }

        ShowDerived();
    }

    /// <summary>
    /// The authorities this instance could verify against.
    /// </summary>
    /// <remarks>
    /// The Origins on this machine are offered by name, because that is what a person means
    /// almost every time — and typing a URL for an Origin two lines above in the same list is how
    /// the two come to disagree after one of them changes domain. "Somewhere else" stays available,
    /// because that is what joining another plane is.
    /// </remarks>
    private void FillAuthorities(ServiceInstance instance)
    {
        _authority.Items.Clear();

        var isOrigin = string.Equals(instance.Kind, ServiceCatalog.Origin.Name,
                                     StringComparison.OrdinalIgnoreCase);

        _authority.Items.Add(isOrigin
            ? new AuthorityChoice("", "Itself — this is its own authority")
            : new AuthorityChoice("", "Work it out from the domain"));

        foreach (var origin in _draft.Instances.Where(i =>
                     string.Equals(i.Kind, ServiceCatalog.Origin.Name, StringComparison.OrdinalIgnoreCase)
                     && !ReferenceEquals(i, instance)))
        {
            _authority.Items.Add(new AuthorityChoice(origin.Id, origin.Display + "  " + origin.PublicUri));
        }

        _authority.Items.Add(new AuthorityChoice(" url", "An authority somewhere else…"));

        var isUrl = instance.Authority.Contains("://", StringComparison.Ordinal);
        var wanted = isUrl ? " url" : instance.Authority;
        var match = _authority.Items.Cast<AuthorityChoice>()
            .Select((choice, i) => (choice, i))
            .Where(t => string.Equals(t.choice.Key, wanted, StringComparison.OrdinalIgnoreCase))
            .Select(t => t.i)
            .DefaultIfEmpty(0)
            .First();
        _authority.SelectedIndex = match;

        _authorityUrl.Text = isUrl ? instance.Authority : "";
        _authorityUrl.Visible = isUrl;
    }

    /// <summary>Read the editor back into the selected instance.</summary>
    private void CaptureEditor()
    {
        if (_loading || Selected() is not { } instance)
        {
            return;
        }

        instance.Name = _name.Text.Trim();
        instance.Domain = _domain.Text.Trim();
        instance.Port = (int)_port.Value;
        instance.Enabled = _enabled.Checked;

        var choice = _authority.SelectedItem as AuthorityChoice;
        var isUrl = choice?.Key == " url";
        _authorityUrl.Visible = isUrl;
        instance.Authority = isUrl ? _authorityUrl.Text.Trim() : choice?.Key ?? "";

        // The list row is refreshed in place. Rebuilding the list on every keystroke drops the
        // selection, and the field being typed in loses focus mid-word.
        if (_list.SelectedIndex >= 0)
        {
            _list.Items[_list.SelectedIndex] = new Row(instance);
        }

        ShowDerived();
        Recompute();
    }

    private void ShowDerived()
    {
        if (Selected() is not { } instance)
        {
            return;
        }

        var issuer = _draft.AuthorityIssuerFor(instance);
        var address = _draft.AuthorityAddressFor(instance);

        _derived.Text =
            $"Answers at        {instance.PublicUri}{Environment.NewLine}" +
            $"On this machine   {instance.LoopbackUri}{Environment.NewLine}" +
            (issuer is null
                ? "Authority         not decided — choose one, or every token it is handed is rejected"
                : $"Authority         {issuer}" +
                  (address is not null && address != issuer ? $"   (reached at {address})" : ""));

        _dataDir.Text = "Data              " + _draft.DataDirectoryOf(instance) +
                        Environment.NewLine +
                        "                  keys, the store and its indexes — this instance's alone";
    }

    // ── saving ──────────────────────────────────────────────────────────────────────────────────

    private void Recompute()
    {
        if (_loading)
        {
            return;
        }

        var problems = _draft.Problems();
        var running = _supervisor.Enabled.Count(s => s.Pid is not null);

        _note.Text = problems.Count > 0
            ? "Cannot be saved yet: " + problems[0]
            : running == 0
                ? "Nothing is running, so saving takes effect the next time you start."
                : $"{running} instance(s) are running with the settings they were started with. " +
                  "Saving does not change them — use \"Save and restart\" to apply this now.";

        _note.ForeColor = problems.Count > 0 ? Color.FromArgb(0xB3, 0x26, 0x1E) : SystemColors.GrayText;
        _save.Enabled = problems.Count == 0;
        _apply.Enabled = problems.Count == 0;
    }

    private bool Save()
    {
        var problems = _draft.Problems();
        if (problems.Count > 0)
        {
            MessageBox.Show(this, string.Join(Environment.NewLine + Environment.NewLine, problems),
                            "Agience", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        _draft.StartAtLogin = _startAtLogin.Checked;
        _draft.StartServicesOnLaunch = _startOnLaunch.Checked;
        _draft.RestartOnExit = _restartOnExit.Checked;

        try
        {
            foreach (var instance in _draft.Instances)
            {
                // Tested by creating it, not by inspecting the string. A path that parses and
                // cannot be written is the case that matters, and it only shows up on the attempt.
                Directory.CreateDirectory(_draft.DataDirectoryOf(instance));
            }
        }
        catch (Exception exc)
        {
            MessageBox.Show(this, "A data directory could not be created: " + exc.Message,
                            "Agience", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        _draft.Save();
        _supervisor.Config = _draft;
        _draft = _supervisor.Config.Clone();

        if (Autostart.Set(_draft.StartAtLogin) is { } failure)
        {
            MessageBox.Show(this,
                "The settings were saved, but starting at login could not be changed: " + failure,
                "Agience", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        _afterSave();
        RefillList(Selected()?.Id);
        return true;
    }

    private async Task SaveAndRestartAsync()
    {
        if (!Save())
        {
            return;
        }

        _apply.Enabled = false;
        _save.Enabled = false;
        try
        {
            await _supervisor.StopAllAsync().ConfigureAwait(true);
            await _supervisor.StartAllAsync().ConfigureAwait(true);
        }
        finally
        {
            _apply.Enabled = true;
            _save.Enabled = true;
            _afterSave();
            Recompute();
        }
    }

    /// <summary>A row in the list. Its text is what a person scans for.</summary>
    private sealed record Row(ServiceInstance Instance)
    {
        public override string ToString()
        {
            var where = Instance.HasDomain ? Instance.Domain : "this machine only";
            var off = Instance.Enabled ? "" : "  (off)";
            return $"{Instance.Display}   :{Instance.Port}   {where}{off}";
        }
    }

    /// <summary>An entry in the authority list: a stable key, and what a person reads.</summary>
    private sealed record AuthorityChoice(string Key, string Text)
    {
        public override string ToString() => Text;
    }
}

/// <summary>
/// Naming a new instance.
/// </summary>
/// <remarks>
/// The name is asked for before the instance exists, because it becomes the id, and the id
/// becomes the directory holding this instance's keys and store. It cannot be changed afterwards
/// without orphaning both — so it is a decision, not a field to fill in later.
/// </remarks>
internal sealed class NameInstanceForm : Form
{
    private readonly TextBox _name = new();
    private readonly TextBox _domain = new();

    public string InstanceName => _name.Text.Trim();

    public string Domain => _domain.Text.Trim();

    public NameInstanceForm(ServiceKind kind, IEnumerable<string> existing)
    {
        Text = "Add " + kind.Display;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(460, 250);
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;

        Controls.Add(new Label
        {
            Text = kind.Summary,
            AutoSize = false,
            Size = new Size(420, 34),
            Location = new Point(20, 14),
            ForeColor = SystemColors.GrayText,
        });

        Controls.Add(new Label { Text = "Name", AutoSize = true, Location = new Point(20, 60) });
        _name.Location = new Point(110, 56);
        _name.Size = new Size(180, 24);
        Controls.Add(_name);

        Controls.Add(new Label
        {
            Text = "Short, and it never changes — it names the directory holding this instance's " +
                   "keys and store. \"home\", \"lab\", \"work\".",
            AutoSize = false,
            Size = new Size(420, 34),
            Location = new Point(110, 82),
            ForeColor = SystemColors.GrayText,
        });

        Controls.Add(new Label { Text = "Domain", AutoSize = true, Location = new Point(20, 126) });
        _domain.Location = new Point(110, 122);
        _domain.Size = new Size(260, 24);
        _domain.PlaceholderText = "home.agience.ai";
        Controls.Add(_domain);

        Controls.Add(new Label
        {
            Text = "Optional. Leave it empty to run on this machine only — that is a complete " +
                   "configuration, not a missing one.",
            AutoSize = false,
            Size = new Size(420, 34),
            Location = new Point(110, 148),
            ForeColor = SystemColors.GrayText,
        });

        var ok = new Button
        {
            Text = "Add",
            Location = new Point(250, 200),
            Size = new Size(90, 30),
            DialogResult = DialogResult.OK,
            Enabled = false,
        };
        var cancel = new Button
        {
            Text = "Cancel",
            Location = new Point(348, 200),
            Size = new Size(90, 30),
            DialogResult = DialogResult.Cancel,
        };

        var taken = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        _name.TextChanged += (_, _) => ok.Enabled = InstanceName.Length > 0 && !taken.Contains(InstanceName);

        Controls.AddRange(new Control[] { ok, cancel });
        AcceptButton = ok;
        CancelButton = cancel;
    }
}
