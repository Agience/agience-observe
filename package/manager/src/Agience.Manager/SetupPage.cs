using System.Diagnostics;
using Agience.Core;

namespace Agience.Manager;

/// <summary>
/// Building the private Python environment the services run out of.
/// </summary>
/// <remarks>
/// <para>
/// This page is the first run, and it shows its working. A cold install downloads a few hundred
/// megabytes and takes minutes; a spinner over that is a window a person kills half way, leaving a
/// half-built environment. Every pip line goes in the box, so a failure is a message a person can
/// read and paste rather than "Setup failed".
/// </para>
/// <para>
/// Nothing here installs Python. Silently running somebody else's installer is not a thing a tray
/// application should do; the download page is opened and the person decides.
/// </para>
/// </remarks>
public sealed class SetupPage : PageControl
{
    private readonly Supervisor _supervisor;
    private readonly Label _state = new();
    private readonly Label _interpreters = new();
    private readonly Label _packages = new();
    private readonly ComboBox _source = new();
    private readonly TextBox _sourceRoot = new();
    private readonly Button _browse = new();
    private readonly Button _install = new();
    private readonly Button _getPython = new();
    private readonly Button _rebuild = new();
    private readonly TextBox _output = new();

    private bool _running;

    public SetupPage(Supervisor supervisor)
    {
        _supervisor = supervisor;

        var y = 8;

        _state.AutoSize = false;
        _state.Size = new Size(760, 44);
        _state.Location = new Point(4, y);
        _state.Font = new Font(Font, FontStyle.Bold);
        Controls.Add(_state);
        y += 50;

        _interpreters.AutoSize = false;
        _interpreters.Size = new Size(760, 56);
        _interpreters.Location = new Point(4, y);
        _interpreters.ForeColor = SystemColors.GrayText;
        Controls.Add(_interpreters);
        y += 62;

        _packages.AutoSize = false;
        _packages.Size = new Size(760, 40);
        _packages.Location = new Point(4, y);
        _packages.ForeColor = SystemColors.GrayText;
        Controls.Add(_packages);
        y += 46;

        Controls.Add(new Label { Text = "Install from", AutoSize = true, Location = new Point(4, y + 4) });
        _source.Location = new Point(96, y);
        _source.Size = new Size(200, 24);
        _source.DropDownStyle = ComboBoxStyle.DropDownList;
        _source.Items.AddRange(new object[]
        {
            "Checkout if present, else git",
            "A checkout on this machine",
            "git",
        });
        _source.SelectedIndexChanged += (_, _) => SourceChanged();
        Controls.Add(_source);

        _sourceRoot.Location = new Point(310, y);
        _sourceRoot.Size = new Size(340, 24);
        Controls.Add(_sourceRoot);

        _browse.Text = "Browse…";
        _browse.Location = new Point(660, y - 2);
        _browse.Size = new Size(90, 26);
        _browse.Click += (_, _) => Browse();
        Controls.Add(_browse);
        y += 32;

        Controls.Add(new Label
        {
            Text = "These packages are not on PyPI, so they come from a checkout of the three " +
                   "repositories (agience-origin, agience-mantle, agience-prism) " +
                   "or from git.",
            AutoSize = false,
            Size = new Size(750, 34),
            Location = new Point(96, y),
            ForeColor = SystemColors.GrayText,
        });
        y += 42;

        _install.Text = "Install";
        _install.Location = new Point(4, y);
        _install.Size = new Size(150, 32);
        _install.Click += async (_, _) => await RunSetupAsync().ConfigureAwait(true);

        _getPython.Text = "Get Python…";
        _getPython.Location = new Point(164, y);
        _getPython.Size = new Size(130, 32);
        _getPython.Click += (_, _) => Open(PythonEnvironment.DownloadPage);

        _rebuild.Text = "Delete and rebuild";
        _rebuild.Location = new Point(304, y);
        _rebuild.Size = new Size(160, 32);
        _rebuild.Click += async (_, _) => await RebuildAsync().ConfigureAwait(true);

        Controls.AddRange(new Control[] { _install, _getPython, _rebuild });
        y += 40;

        _output.Location = new Point(4, y);
        _output.Size = new Size(800, 180);
        _output.Multiline = true;
        _output.ReadOnly = true;
        _output.ScrollBars = ScrollBars.Vertical;
        _output.WordWrap = false;
        _output.Font = new Font("Consolas", 8.5f);
        _output.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        Controls.Add(_output);

        LoadState();
    }

    public override void OnShown() => LoadState();

    private void LoadState()
    {
        var config = _supervisor.Config;

        _source.SelectedIndex = config.PackageSource.ToLowerInvariant() switch
        {
            "local" => 1,
            "git" => 2,
            _ => 0,
        };

        _sourceRoot.Text = string.IsNullOrWhiteSpace(config.SourceRoot)
            ? PythonEnvironment.SourceRootFor(config) ?? ""
            : config.SourceRoot;

        SourceChanged();
        Rerender();
    }

    private void Rerender()
    {
        var status = PythonEnvironment.Inspect();
        _supervisor.RuntimeReady = status.Ready;

        _state.Text = status.Ready
            ? "Ready. The services can be started."
            : status.VenvExists
                ? "The environment exists but is incomplete — press Install to finish it."
                : "Setup has not been run. Press Install to build the environment.";

        var found = PythonEnvironment.Discover();
        _interpreters.Text = found.Count == 0
            ? $"No Python {PythonEnvironment.Minimum} or newer was found on this machine. " +
              "Install one — tick \"Add python.exe to PATH\" in its installer — then come back here."
            : "Will build from: " + found[0] + Environment.NewLine +
              (found.Count > 1 ? $"({found.Count - 1} other interpreter(s) also found.)" : "");

        _packages.Text = string.Join("      ",
            status.Packages.Select(p => $"{p.Key} {p.Value ?? "— not installed"}"));

        _install.Text = status.Ready ? "Repair" : "Install";
        _install.Enabled = !_running && found.Count > 0;
        _getPython.Visible = found.Count == 0 || !status.Ready;
        _rebuild.Enabled = !_running && status.VenvExists;
        _browse.Enabled = !_running && _source.SelectedIndex != 2;
        _sourceRoot.Enabled = _browse.Enabled;
        _source.Enabled = !_running;
    }

    private void SourceChanged()
    {
        var mode = _source.SelectedIndex switch { 1 => "local", 2 => "git", _ => "auto" };
        _supervisor.Config.PackageSource = mode;
        _browse.Enabled = !_running && _source.SelectedIndex != 2;
        _sourceRoot.Enabled = _browse.Enabled;
    }

    private async Task RunSetupAsync()
    {
        _running = true;
        _output.Clear();
        Rerender();

        var config = _supervisor.Config;
        config.PackageSource = _source.SelectedIndex switch { 1 => "local", 2 => "git", _ => "auto" };
        config.SourceRoot = _sourceRoot.Text.Trim();

        // Checked here, before pip runs. A partial checkout installs the repositories it finds and
        // resolves the rest from an index that does not carry them — and the error would name a
        // transitive dependency instead of the directory a person could actually fix.
        if (config.PackageSource != "git"
            && !string.IsNullOrWhiteSpace(config.SourceRoot)
            && !PythonEnvironment.IsCompleteCheckout(config.SourceRoot))
        {
            Say($"{config.SourceRoot} does not hold all three repositories " +
                "(agience-origin, agience-mantle, agience-prism/py).");
            Say("Choose a directory that does, or switch to installing from git.");
            _running = false;
            Rerender();
            return;
        }

        config.Save();

        var progress = new Progress<string>(Say);
        try
        {
            var result = await PythonEnvironment.SetupAsync(config, progress).ConfigureAwait(true);
            Say(result.Ok ? "--- setup finished ---" : "--- setup did NOT finish ---");
            if (!result.Ok)
            {
                Say(result.Output);
            }
        }
        catch (Exception exc)
        {
            Say("--- setup failed: " + exc.Message + " ---");
        }
        finally
        {
            _running = false;
            Rerender();
        }

        if (_supervisor.RuntimeReady
            && MessageBox.Show(this, "The environment is ready. Start the services now?",
                               "Agience", MessageBoxButtons.YesNo, MessageBoxIcon.Question)
               == DialogResult.Yes)
        {
            await _supervisor.StartAllAsync().ConfigureAwait(true);
        }
    }

    private async Task RebuildAsync()
    {
        // The services run out of the directory this deletes. Deleting it under them leaves three
        // processes whose interpreter has gone, which fail in ways that name nothing.
        if (MessageBox.Show(this,
                $"This deletes {Paths.Runtime} and builds it again from scratch." +
                Environment.NewLine + Environment.NewLine +
                "The services will be stopped first. Your keys, stores and settings are in a " +
                "different directory and are not touched.",
                "Agience", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
        {
            return;
        }

        _running = true;
        Rerender();
        try
        {
            Say("Stopping the services…");
            await _supervisor.StopAllAsync().ConfigureAwait(true);
            Say($"Deleting {Paths.Runtime} …");
            PythonEnvironment.Remove();
        }
        catch (Exception exc)
        {
            Say("Could not delete it: " + exc.Message);
            _running = false;
            Rerender();
            return;
        }

        _running = false;
        await RunSetupAsync().ConfigureAwait(true);
    }

    private void Browse()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "The directory holding agience-origin, agience-mantle and agience-prism",
            UseDescriptionForTitle = true,
            SelectedPath = _sourceRoot.Text,
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _sourceRoot.Text = dialog.SelectedPath;
            if (!PythonEnvironment.IsCompleteCheckout(dialog.SelectedPath))
            {
                MessageBox.Show(this,
                    "That directory does not hold all three repositories. Setup will refuse it — " +
                    "pick the parent directory the repositories sit in, not one of the repositories.",
                    "Agience", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
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

    private void Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception exc)
        {
            MessageBox.Show(this, exc.Message, "Agience", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
