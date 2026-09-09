using System.Diagnostics;
using Agience.Core;

namespace Agience.Manager;

/// <summary>
/// The tray icon, its menu, and the poll loop behind them.
/// </summary>
/// <remarks>
/// <para>
/// One icon represents the whole installation. Three icons — one per service — would put three
/// near-identical dots in a tray that already has twelve, and the question a person actually has is
/// "is Agience working", which is one question with one answer.
/// </para>
/// <para>
/// The verdict is rendered here, not decided. <see cref="TrayState.Evaluate"/> maps the service
/// states onto one level and a sentence; this class puts that sentence in a tooltip and paints the
/// matching icon. Nothing in this file looks at a port or an HTTP code.
/// </para>
/// </remarks>
public sealed class TrayApp : IDisposable
{
    private readonly TrayIcon _icon = new();
    private readonly ContextMenuStrip _menu = MenuStyle.NewMenu();
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly Supervisor _supervisor;
    private readonly Action _onExit;

    private MainForm? _window;
    private bool _polling;
    private bool _busy;

    public TrayApp(Supervisor supervisor, Action onExit)
    {
        _supervisor = supervisor;
        _onExit = onExit;

        _icon.Icon = Icons.For(TrayLevel.NotRunning);
        _icon.Text = "Agience — starting";

        // A single left click opens the window. Every tray application a person already uses opens
        // on one click; wiring only a double click would mean the obvious gesture does nothing at
        // all, which reads as an application that has hung rather than one with a different
        // convention.
        _icon.Selected += () => ShowWindow();
        _icon.ContextMenuRequested += ShowMenu;
        _icon.Show();

        // Startup runs on the first tick, not from the caller and not via
        // SynchronizationContext.Current. Posting it from Program.RunTray failed silently:
        // SynchronizationContext.Current is null there, because WinForms only installs its context
        // once a window handle exists, which has not happened before Application.Run. The `?.`
        // swallowed the null, so startup never ran — no setup page on a first launch, no services
        // started, and nothing anywhere saying why.
        //
        // A timer works because it cannot fire until the message loop is pumping, which is exactly
        // the condition being waited for — and it is the same loop the poll already uses, so there
        // is no second mechanism to keep working.
        _timer.Interval = 50;
        _timer.Tick += async (_, _) => await TickAsync().ConfigureAwait(true);
        _timer.Start();
    }

    private bool _startedUp;

    /// <summary>
    /// Ask the tray to shut down, from any thread.
    /// </summary>
    /// <remarks>
    /// It marshals onto the UI thread first. The caller is the shutdown listener, which is a
    /// background thread; touching a WinForms control from there is undefined and in practice hangs
    /// or throws somewhere unrelated. <see cref="Control.BeginInvoke(Delegate)"/> on a control whose
    /// handle exists is the one mechanism that is valid before, during and after startup.
    /// </remarks>
    public void RequestExit()
    {
        try
        {
            if (_menu.IsHandleCreated)
            {
                _menu.BeginInvoke(() => _onExit());
                return;
            }
        }
        catch (Exception)
        {
            // Falls through: an exit that cannot be marshalled is still an exit.
        }

        _onExit();
    }

    private async Task TickAsync()
    {
        if (!_startedUp)
        {
            _startedUp = true;
            _timer.Interval = (int)Supervisor.PollInterval.TotalMilliseconds;
            await StartupAsync().ConfigureAwait(true);
            return;
        }

        await PollAsync().ConfigureAwait(true);
    }

    /// <summary>Everything that happens after the tray is on screen.</summary>
    /// <remarks>
    /// Runs after the icon is shown, deliberately: setup and a first start both take minutes, and
    /// doing either before the icon exists means a person double-clicks the shortcut and watches
    /// nothing happen.
    /// </remarks>
    public async Task StartupAsync()
    {
        Refresh();
        await PollAsync().ConfigureAwait(true);

        if (_supervisor.Config.Instances.Count == 0)
        {
            // A fresh install has nothing to show and nothing to offer. The configuration page
            // is the only screen with a useful next action on it.
            ShowWindow(MainForm.Page.Configuration);
            return;
        }

        if (!_supervisor.RuntimeReady)
        {
            // The window opens itself exactly once, on the one state a person cannot guess their
            // way out of. A first run with no environment shows a grey icon whose menu can only
            // offer things that will not work; the setup page is the only useful screen there is.
            ShowWindow(MainForm.Page.Setup);
            return;
        }

        if (_supervisor.Config.StartServicesOnLaunch)
        {
            await WithBusyAsync("Starting", () => _supervisor.StartAllAsync()).ConfigureAwait(true);
        }
    }

    private async Task PollAsync()
    {
        // Ticks are dropped rather than queued: a poll that outruns the interval would otherwise
        // stack until the slow thing finished, and then run all of them at once.
        if (_polling || _busy)
        {
            return;
        }

        _polling = true;
        try
        {
            _supervisor.RuntimeReady = PythonEnvironment.Inspect().Ready;
            await _supervisor.PollAsync().ConfigureAwait(true);
            Refresh();
        }
        finally
        {
            _polling = false;
        }
    }

    private void Refresh()
    {
        var verdict = _supervisor.Verdict;
        _icon.Icon = Icons.For(verdict.Level);
        _icon.Text = Truncate($"Agience — {verdict.Headline}");
        _window?.Refresh(verdict);
    }

    /// <summary>
    /// Show the menu at the point the shell asked for.
    /// </summary>
    /// <remarks>
    /// Calls SetForegroundWindow first; it is not optional. A tray menu belongs to a window that is
    /// not in the foreground, and Windows only dismisses a menu when its owner loses focus, which
    /// it never does otherwise. Without this the menu stays on screen after a click elsewhere, sits
    /// above every other window, and has to be dismissed by picking something from it — the oldest
    /// known symptom in tray applications, and it looks like the application has hung.
    /// </remarks>
    private void ShowMenu(Point at)
    {
        BuildMenu();
        SetForegroundWindow(_menu.Handle);
        _menu.Show(at);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    private void BuildMenu()
    {
        var menu = _menu;
        menu.Items.Clear();

        var verdict = _supervisor.Verdict;
        menu.Items.Add(new ToolStripMenuItem($"Agience — {verdict.Headline}")
        {
            Enabled = false,
            Font = new Font(menu.Font, FontStyle.Bold),
        });

        if (_busy)
        {
            // Every verb is withdrawn while one is running. A second "Stop all" issued during the
            // first one races the supervisor's own lifecycle lock and, from the person's side,
            // looks like the menu did nothing.
            menu.Items.Add(new ToolStripMenuItem("Working…") { Enabled = false });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Open Agience", null, (_, _) => ShowWindow()));
            return;
        }

        if (_supervisor.Config.Instances.Count > 0 && !_supervisor.RuntimeReady)
        {
            menu.Items.Add(new ToolStripMenuItem("Finish setup…", null,
                (_, _) => ShowWindow(MainForm.Page.Setup))
            {
                Font = new Font(menu.Font, FontStyle.Bold),
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => _onExit()));
            return;
        }

        // The row is the control. A separate "restart a service…" submenu would be a second list
        // of service names to keep in step with the catalogue's.
        var listed = _supervisor.Services.Concat(_supervisor.Orphans).ToList();
        if (listed.Count == 0)
        {
            menu.Items.Add(new ToolStripMenuItem("Add an Origin or a Mantle…", null,
                (_, _) => ShowWindow(MainForm.Page.Configuration))
            {
                Font = new Font(menu.Font, FontStyle.Bold),
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => _onExit()));
            return;
        }

        foreach (var service in listed)
        {
            var row = new ToolStripMenuItem(Describe(service));
            if (service.State == ServiceState.Disabled)
            {
                row.Enabled = false;
            }
            else if (service.State is ServiceState.Serving or ServiceState.Unhealthy
                                   or ServiceState.Starting)
            {
                var name = service.Instance.Id;
                row.DropDownItems.Add(new ToolStripMenuItem("Restart", null,
                    async (_, _) => await WithBusyAsync("Restarting", () => _supervisor.RestartAsync(name))
                        .ConfigureAwait(true)));
                row.DropDownItems.Add(new ToolStripMenuItem("Stop", null,
                    async (_, _) => await WithBusyAsync("Stopping", () => _supervisor.StopAsync(name))
                        .ConfigureAwait(true)));
                row.DropDownItems.Add(new ToolStripSeparator());
                row.DropDownItems.Add(new ToolStripMenuItem("Open log", null,
                    (_, _) => Open(service.LogPath)));
            }
            else
            {
                var name = service.Instance.Id;
                row.DropDownItems.Add(new ToolStripMenuItem("Start", null,
                    async (_, _) => await WithBusyAsync("Starting", () => _supervisor.StartAsync(name))
                        .ConfigureAwait(true)));
                row.DropDownItems.Add(new ToolStripMenuItem("Open log", null,
                    (_, _) => Open(service.LogPath)));
            }

            menu.Items.Add(row);
        }

        menu.Items.Add(new ToolStripSeparator());

        var anyRunning = _supervisor.Enabled.Any(s => s.Pid is not null);
        menu.Items.Add(new ToolStripMenuItem("Start all", null,
            async (_, _) => await WithBusyAsync("Starting", () => _supervisor.StartAllAsync())
                .ConfigureAwait(true))
        {
            Enabled = _supervisor.Enabled.Any(s => s.State != ServiceState.Serving),
        });

        menu.Items.Add(new ToolStripMenuItem("Stop all", null,
            async (_, _) => await WithBusyAsync("Stopping", () => _supervisor.StopAllAsync())
                .ConfigureAwait(true))
        {
            Enabled = anyRunning,
        });

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Open Agience", null, (_, _) => ShowWindow()));

        menu.Items.Add(new ToolStripMenuItem("Logs folder", null, (_, _) => Open(Paths.Logs)));

        menu.Items.Add(new ToolStripSeparator());

        // The wording says what exiting does, because it is not what a tray icon usually does.
        // This process owns the services — they are in its job object and they die with it — so
        // "Exit" here is a shutdown of the whole node, not the dismissal of a status light.
        menu.Items.Add(new ToolStripMenuItem(
            anyRunning ? "Exit (stops the services)" : "Exit", null,
            async (_, _) => await ExitAsync().ConfigureAwait(true)));
    }

    private static string Describe(ServiceProcess service)
    {
        var state = service.State switch
        {
            ServiceState.Serving => "serving",
            ServiceState.Starting => "starting…",
            ServiceState.Stopping => "stopping…",
            ServiceState.Unhealthy => "not answering",
            ServiceState.Failed => "failed",
            ServiceState.Foreign => "port taken",
            ServiceState.Disabled => "off",
            _ => "stopped",
        };

        var port = service.State == ServiceState.Disabled ? "" : $":{service.Port}";
        return $"   {service.Instance.Display}{port}   {state}";
    }

    private async Task ExitAsync()
    {
        await WithBusyAsync("Stopping", () => _supervisor.StopAllAsync()).ConfigureAwait(true);
        _onExit();
    }

    /// <summary>Run a lifecycle verb with the menu withdrawn and the outcome announced.</summary>
    private async Task WithBusyAsync(string what, Func<Task> action)
    {
        _busy = true;
        _icon.Text = Truncate($"Agience — {what}…");
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception exc)
        {
            _icon.Notify(what + " failed", exc.Message, error: true);
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }

    private void ShowWindow(MainForm.Page page = MainForm.Page.Status)
    {
        if (_window is null || _window.IsDisposed)
        {
            _window = new MainForm(_supervisor);
            _window.FormClosed += (_, _) => _window = null;
        }

        _window.Show(page);
    }

    private void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception exc)
        {
            _icon.Notify("Could not open it", $"{target}: {exc.Message}", error: true);
        }
    }

    /// <summary>The shell truncates a tooltip past 127 characters; doing it here keeps the tail.</summary>
    private static string Truncate(string s) => s.Length <= 127 ? s : s[..124] + "...";

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
        _icon.Dispose();            // removes it, or the icon lingers until the shell is poked
        _menu.Dispose();
        _window?.Dispose();
    }
}
