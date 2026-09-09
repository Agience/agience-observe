using System.Diagnostics;
using Agience.Core;

namespace Agience.Manager;

/// <summary>
/// What the instances are doing, and the buttons that change it.
/// </summary>
/// <remarks>
/// <para>
/// The headline and the rows are rendered from one <see cref="Verdict"/> and one pass over the same
/// objects, so the banner and the rows it summarizes never disagree.
/// </para>
/// <para>
/// Each row shows its detail sentence, not just a coloured word: "Failed" tells nobody anything,
/// while "Exited on its own with code 1. The last lines of mantle-home.log say why." is the
/// difference between a status screen and a support call.
/// </para>
/// </remarks>
public sealed class StatusPage : PageControl
{
    private readonly Supervisor _supervisor;
    private readonly Label _headline = new();
    private readonly Label _detail = new();
    private readonly ListView _rows = new();
    private readonly Button _startAll = new();
    private readonly Button _stopAll = new();
    private readonly Button _restartOne = new();
    private readonly Button _open = new();
    private readonly Button _log = new();

    private bool _busy;

    public StatusPage(Supervisor supervisor)
    {
        _supervisor = supervisor;

        _headline.Font = new Font(Font.FontFamily, 13f, FontStyle.Bold);
        _headline.AutoSize = true;
        _headline.Location = new Point(4, 4);

        _detail.AutoSize = false;
        _detail.Location = new Point(4, 32);
        _detail.Size = new Size(820, 54);
        _detail.ForeColor = SystemColors.GrayText;

        _rows.Location = new Point(4, 94);
        _rows.Size = new Size(820, 300);
        _rows.View = View.Details;
        _rows.FullRowSelect = true;
        _rows.MultiSelect = false;
        _rows.HideSelection = false;
        _rows.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        _rows.Columns.Add("Instance", 150);
        _rows.Columns.Add("Answers at", 190);
        _rows.Columns.Add("State", 100);
        _rows.Columns.Add("Port", 55);
        _rows.Columns.Add("Uptime", 70);
        _rows.Columns.Add("What is happening", 340);
        _rows.SelectedIndexChanged += (_, _) => UpdateButtons();
        _rows.DoubleClick += (_, _) => OpenSelectedLog();

        var y = 404;
        Button(_startAll, "Start all", 4, y, 110, () => _ = RunAsync(() => _supervisor.StartAllAsync()));
        Button(_stopAll, "Stop all", 122, y, 110, () => _ = RunAsync(() => _supervisor.StopAllAsync()));
        Button(_restartOne, "Restart selected", 240, y, 140, () =>
        {
            if (Selected() is { } service)
            {
                _ = RunAsync(() => _supervisor.RestartAsync(service.Instance.Id));
            }
        });
        Button(_open, "Open in browser", 388, y, 140, OpenSelected);
        Button(_log, "Open log", 536, y, 110, OpenSelectedLog);

        Controls.AddRange(new Control[]
        {
            _headline, _detail, _rows, _startAll, _stopAll, _restartOne, _open, _log,
        });

        Reload();
    }

    /// <summary>
    /// Place a button and wire it up.
    /// </summary>
    /// <remarks>
    /// One overload, taking <see cref="Action"/>, and there must never be a second taking
    /// <c>Func&lt;Task&gt;</c>. There was, and it crashed the application before the tray icon
    /// existed: the Task-returning overload forwarded to the Action one as
    /// <c>Button(…, () =&gt; _ = onClick())</c>, but that lambda's body is an expression of type
    /// Task — so overload resolution picked the Task overload again, and it called itself until the
    /// stack ran out. A StackOverflowException cannot be caught, so the process vanished with no
    /// dialog, no log line and no tray icon, and the only evidence was an APPCRASH in the Event Log.
    /// An async handler is written <c>() =&gt; _ = SomethingAsync()</c> at the call site, where the
    /// discard is visible.
    /// </remarks>
    private void Button(Button button, string text, int x, int y, int width, Action onClick)
    {
        button.Text = text;
        button.Location = new Point(x, y);
        button.Size = new Size(width, 30);
        button.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        button.Click += (_, _) => onClick();
        Controls.Add(button);
    }

    /// <summary>Redraw everything from the supervisor's current state.</summary>
    public void Reload() => Apply(_supervisor.Verdict);

    /// <summary>Redraw from a verdict the caller already has.</summary>
    public void Apply(Verdict verdict)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => Apply(verdict));
            return;
        }

        _headline.Text = verdict.Headline;
        _headline.ForeColor = Colour(verdict.Level);
        _detail.Text = verdict.Detail;

        // Orphans are listed too: deleting an instance from the configuration does not stop its
        // process, and a running service that no surface lists would otherwise go unaccounted for.
        var services = _supervisor.Services.Concat(_supervisor.Orphans).ToList();

        // Rows are updated in place rather than rebuilt: clearing and re-adding every two seconds
        // would drop the selection, making "Restart selected" unusable since the button stays
        // enabled right up until the moment it is pressed.
        while (_rows.Items.Count > services.Count)
        {
            _rows.Items.RemoveAt(_rows.Items.Count - 1);
        }

        for (var i = 0; i < services.Count; i++)
        {
            var service = services[i];
            if (i >= _rows.Items.Count)
            {
                var fresh = new ListViewItem("");
                for (var c = 1; c < _rows.Columns.Count; c++)
                {
                    fresh.SubItems.Add("");
                }

                _rows.Items.Add(fresh);
            }

            var orphan = _supervisor.Config.ById(service.Instance.Id) is null;
            var row = _rows.Items[i];
            row.Tag = service.Instance.Id;
            row.Text = service.Instance.Display + (orphan ? "  (removed)" : "");
            Set(row, 1, service.Instance.PublicUri);
            Set(row, 2, Word(service.State));
            Set(row, 3, service.Port.ToString());
            Set(row, 4, Uptime(service));
            Set(row, 5, orphan
                ? "No longer configured, and still running. Stop it here."
                : service.Detail);
            row.ForeColor = Colour(service.State);
        }

        UpdateButtons();
    }

    private static void Set(ListViewItem row, int index, string value)
    {
        if (row.SubItems[index].Text != value)
        {
            row.SubItems[index].Text = value;
        }
    }

    private void UpdateButtons()
    {
        var enabled = _supervisor.Enabled;
        var selected = Selected();
        _startAll.Enabled = !_busy && _supervisor.RuntimeReady
                            && enabled.Any(s => s.State != ServiceState.Serving);
        _stopAll.Enabled = !_busy && (enabled.Any(s => s.Pid is not null) || _supervisor.Orphans.Count > 0);
        _restartOne.Enabled = !_busy && selected is { State: not ServiceState.Disabled };
        _open.Enabled = selected is { State: ServiceState.Serving };
        _log.Enabled = selected is not null;
    }

    private ServiceProcess? Selected()
    {
        if (_rows.SelectedItems.Count == 0 || _rows.SelectedItems[0].Tag is not string id)
        {
            return null;
        }

        return _supervisor[id];
    }

    private void OpenSelected()
    {
        if (Selected() is not { } service)
        {
            return;
        }

        // Opens the loopback address, not the public one: the public name may not resolve from this
        // machine, or may resolve somewhere else entirely, and a browser tab that lands on the wrong
        // node while reporting success is worse than one that does not open.
        Open(service.Instance.LoopbackUri);
    }

    private void OpenSelectedLog()
    {
        if (Selected() is not { } service)
        {
            return;
        }

        if (!File.Exists(service.LogPath))
        {
            // Said plainly: an "open" that silently does nothing reads as a broken button, and the
            // true cause — this instance has never run here — is useful to know.
            MessageBox.Show(this,
                $"There is no log yet at {service.LogPath}.{Environment.NewLine}{Environment.NewLine}" +
                $"{service.Instance.Display} has not been started on this machine.",
                "Agience", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Open(service.LogPath);
    }

    private void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception exc)
        {
            MessageBox.Show(this, exc.Message, "Agience", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task RunAsync(Func<Task> action)
    {
        _busy = true;
        UpdateButtons();
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception exc)
        {
            MessageBox.Show(this, exc.Message, "Agience", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _busy = false;
            Reload();
        }
    }

    private static string Uptime(ServiceProcess service)
    {
        if (service.StartedAt is not { } at)
        {
            return "—";
        }

        var span = DateTimeOffset.UtcNow - at;
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}h {span.Minutes}m"
            : span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes}m {span.Seconds}s" : $"{span.Seconds}s";
    }

    private static string Word(ServiceState state) => state switch
    {
        ServiceState.Serving => "Serving",
        ServiceState.Starting => "Starting",
        ServiceState.Stopping => "Stopping",
        ServiceState.Unhealthy => "Not answering",
        ServiceState.Failed => "Failed",
        ServiceState.Foreign => "Port taken",
        ServiceState.Disabled => "Off",
        _ => "Stopped",
    };

    /// <summary>
    /// The colour for a state.
    /// </summary>
    /// <remarks>
    /// Colour is decoration here, not information: every row already carries the state as a word
    /// and a sentence, so a person who cannot separate these colours loses nothing — the same rule
    /// the tray icon follows with shape.
    /// </remarks>
    private static Color Colour(ServiceState state) => state switch
    {
        ServiceState.Serving => Color.FromArgb(0x1E, 0x5C, 0x38),
        ServiceState.Failed or ServiceState.Unhealthy => Color.FromArgb(0x8A, 0x5A, 0x00),
        ServiceState.Foreign => Color.FromArgb(0xB3, 0x26, 0x1E),
        ServiceState.Disabled => SystemColors.GrayText,
        _ => SystemColors.ControlText,
    };

    private static Color Colour(TrayLevel level) => level switch
    {
        TrayLevel.Healthy => Color.FromArgb(0x1E, 0x5C, 0x38),
        TrayLevel.Degraded => Color.FromArgb(0x8A, 0x5A, 0x00),
        TrayLevel.Foreign => Color.FromArgb(0xB3, 0x26, 0x1E),
        _ => SystemColors.ControlText,
    };
}
