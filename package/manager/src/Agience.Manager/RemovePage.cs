using Agience.Core;

namespace Agience.Manager;

/// <summary>
/// Removing Agience, with the data as a separate decision.
/// </summary>
/// <remarks>
/// <para>
/// "Keep everything" is the default. The data directory holds the signing keys of an identity
/// authority and the only copy of whatever this node has stored. A default that deleted them
/// would be one mis-click from destroying something nobody had a backup of, while the person
/// believed they were removing a tray icon.
/// </para>
/// <para>
/// This page is what "Uninstall" in Add/Remove Programs cannot be. A plain MSI uninstall runs
/// with no useful UI, so it is wired to keep everything — the safe half — and this page is where
/// the other choice is made, before the MSI is handed the job.
/// </para>
/// </remarks>
public sealed class RemovePage : PageControl
{
    private readonly Supervisor _supervisor;
    private readonly RadioButton _keep = new();
    private readonly RadioButton _runtime = new();
    private readonly RadioButton _everything = new();
    private readonly Label _sizes = new();
    private readonly Button _remove = new();
    private readonly TextBox _output = new();

    public RemovePage(Supervisor supervisor)
    {
        _supervisor = supervisor;

        var y = 8;
        Controls.Add(new Label
        {
            Text = "Remove Agience from this machine",
            Font = new Font(Font, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(4, y),
        });
        y += 30;

        Controls.Add(new Label
        {
            Text = "The services are stopped first, whatever you choose. The program itself is " +
                   "removed by Windows Installer at the end.",
            AutoSize = false,
            Size = new Size(780, 34),
            Location = new Point(4, y),
            ForeColor = SystemColors.GrayText,
        });
        y += 42;

        Option(_keep, "Keep my environment and my data",
            "Removes the program only. Reinstalling picks up exactly where this left off — same " +
            "keys, same store, same settings.", ref y, first: true);

        Option(_runtime, "Remove the Python environment, keep my data",
            "Frees the few hundred megabytes under runtime\\. The keys, the identity database and " +
            "the artifact store stay. A reinstall rebuilds the environment.", ref y);

        Option(_everything, "Remove everything, including my data",
            "Deletes the signing keys of this authority and the only copy of what this node has " +
            "stored. There is no undo and no backup is taken.", ref y);

        _sizes.AutoSize = false;
        _sizes.Size = new Size(780, 76);
        _sizes.Location = new Point(4, y);
        _sizes.ForeColor = SystemColors.GrayText;
        Controls.Add(_sizes);
        y += 84;

        _remove.Text = "Remove Agience";
        _remove.Location = new Point(4, y);
        _remove.Size = new Size(180, 32);
        _remove.Click += async (_, _) => await RemoveAsync().ConfigureAwait(true);
        Controls.Add(_remove);
        y += 42;

        _output.Location = new Point(4, y);
        _output.Size = new Size(800, 150);
        _output.Multiline = true;
        _output.ReadOnly = true;
        _output.ScrollBars = ScrollBars.Vertical;
        _output.Font = new Font("Consolas", 8.5f);
        _output.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        Controls.Add(_output);

        _keep.Checked = true;
    }

    public override void OnShown() => Describe();

    private void Option(RadioButton button, string title, string detail, ref int y, bool first = false)
    {
        button.Text = title;
        button.AutoSize = true;
        button.Location = new Point(4, y);
        button.Checked = first;
        button.CheckedChanged += (_, _) => Describe();
        Controls.Add(button);
        y += 24;

        Controls.Add(new Label
        {
            Text = detail,
            AutoSize = false,
            Size = new Size(740, 34),
            Location = new Point(24, y),
            ForeColor = SystemColors.GrayText,
        });
        y += 42;
    }

    private void Describe()
    {
        var data = _supervisor.Config.ResolvedDataRoot;
        _sizes.Text =
            $"Environment   {Paths.Runtime}   {Measure(Paths.Runtime)}{Environment.NewLine}" +
            $"Data          {data}   {Measure(data)}{Environment.NewLine}" +
            $"Settings      {Paths.ConfigFile}{Environment.NewLine}" +
            (Installation.IsMsiInstalled()
                ? "Windows Installer will be asked to remove the program at the end."
                : "This copy was not installed by the MSI, so only the directories above are touched.");
    }

    /// <summary>A directory's size, or why it could not be measured.</summary>
    /// <remarks>
    /// Bounded. A store can hold millions of files, and walking it to put a number on a label
    /// would freeze the window for as long as that takes. Past the cap the answer is a floor,
    /// not an exact total.
    /// </remarks>
    private static string Measure(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return "(not present)";
            }

            long bytes = 0;
            var counted = 0;
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                try
                {
                    bytes += new FileInfo(file).Length;
                }
                catch (Exception)
                {
                    // A file that vanished mid-walk is not worth reporting.
                }

                if (++counted >= 40000)
                {
                    return $"more than {bytes / 1024 / 1024:N0} MB";
                }
            }

            return bytes < 1024L * 1024 ? $"{bytes / 1024:N0} KB" : $"{bytes / 1024 / 1024:N0} MB";
        }
        catch (Exception)
        {
            return "(could not be measured)";
        }
    }

    private RemovalScope Scope() =>
        _everything.Checked ? RemovalScope.RemoveEverything
        : _runtime.Checked ? RemovalScope.RemoveRuntime
        : RemovalScope.KeepEverything;

    private async Task RemoveAsync()
    {
        var scope = Scope();
        var data = _supervisor.Config.ResolvedDataRoot;

        // The destructive choice is confirmed by typing, not by pressing OK. An OK button on a
        // warning is pressed reflexively; typing the word is the smallest thing that requires a
        // person to have read what they are about to lose.
        if (scope == RemovalScope.RemoveEverything)
        {
            using var confirm = new ConfirmDeleteForm(data);
            if (confirm.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }
        }
        else if (MessageBox.Show(this,
                     scope == RemovalScope.RemoveRuntime
                         ? $"Remove the program and the Python environment?{Environment.NewLine}{Environment.NewLine}" +
                           $"Your data at {data} will be kept."
                         : $"Remove the program?{Environment.NewLine}{Environment.NewLine}" +
                           $"Your environment and your data at {data} will be kept.",
                     "Agience", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
        {
            return;
        }

        _remove.Enabled = false;
        try
        {
            Say("Stopping the services…");
            await _supervisor.StopAllAsync().ConfigureAwait(true);

            // Only after the stop. A running service holds its store open; deleting the directory
            // underneath it removes the entries and leaves the file — a store that exists, opens,
            // and has lost what it held, which is worse than either outcome a person chose between.
            foreach (var line in Installation.Remove(scope, _supervisor.Config))
            {
                Say(line);
            }

            if (!Installation.IsMsiInstalled())
            {
                Say("This copy was not installed by the MSI, so there is nothing for Windows " +
                    "Installer to remove. You can close Agience now.");
                return;
            }

            Say("Handing over to Windows Installer…");
            if (Installation.LaunchMsiUninstall() is { } failure)
            {
                Say("Could not start the uninstaller: " + failure);
                Say("Remove \"" + Installation.DisplayName + "\" from Settings > Apps by hand.");
            }
        }
        finally
        {
            _remove.Enabled = true;
            Describe();
        }
    }

    private void Say(string line)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => Say(line));
            return;
        }

        _output.AppendText(line + Environment.NewLine);
    }
}

/// <summary>The one dialog that asks a person to type rather than to click.</summary>
internal sealed class ConfirmDeleteForm : Form
{
    private const string Word = "delete";

    public ConfirmDeleteForm(string dataDirectory)
    {
        Text = "Delete the data as well?";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(520, 240);
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;

        Controls.Add(new Label
        {
            Text = "This deletes " + dataDirectory + Environment.NewLine + Environment.NewLine +
                   "It holds the signing keys of this Agience authority and the only copy of what " +
                   "this node has stored. Nothing is backed up first, and there is no undo." +
                   Environment.NewLine + Environment.NewLine +
                   "Type " + Word + " to confirm:",
            AutoSize = false,
            Size = new Size(480, 140),
            Location = new Point(20, 16),
        });

        var box = new TextBox { Location = new Point(20, 160), Size = new Size(200, 24) };
        var ok = new Button
        {
            Text = "Delete it",
            Location = new Point(300, 195),
            Size = new Size(100, 30),
            DialogResult = DialogResult.OK,
            Enabled = false,
        };
        var cancel = new Button
        {
            Text = "Cancel",
            Location = new Point(408, 195),
            Size = new Size(90, 30),
            DialogResult = DialogResult.Cancel,
        };

        box.TextChanged += (_, _) =>
            ok.Enabled = string.Equals(box.Text.Trim(), Word, StringComparison.OrdinalIgnoreCase);

        Controls.AddRange(new Control[] { box, ok, cancel });
        AcceptButton = ok;
        CancelButton = cancel;
    }
}
