using Agience.Core;

namespace Agience.Manager;

/// <summary>
/// The one window: what the services are doing, how they are configured, and how to remove them.
/// </summary>
/// <remarks>
/// <para>
/// One window, four pages, and no modal dialogs for anything that takes time. Setup runs for
/// minutes; a modal progress box over it means a person cannot look at the configuration that
/// setup is about to use, and cannot read the status page to see what is already running.
/// </para>
/// <para>
/// Closing it does not stop anything. The window is a view; the tray owns the services. Closing
/// hides it rather than disposing it, so the state a person had — a page, a scroll position, a
/// half-typed domain — survives the accidental click on the X.
/// </para>
/// </remarks>
public sealed class MainForm : Form
{
    /// <summary>Which page to land on. Setup is where a first run goes.</summary>
    public enum Page
    {
        Status,
        Configuration,
        Setup,
        Remove,
    }

    private readonly TabControl _tabs = new();
    private readonly StatusPage _status;
    private readonly ConfigPage _configuration;
    private readonly SetupPage _setup;
    private readonly RemovePage _remove;

    public MainForm(Supervisor supervisor)
    {
        Text = "Agience";
        Icon = Icons.Application();
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(760, 560);
        Size = new Size(880, 640);
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;

        _status = new StatusPage(supervisor);
        _configuration = new ConfigPage(supervisor, () => _status.Reload());
        _setup = new SetupPage(supervisor);
        _remove = new RemovePage(supervisor);

        _tabs.Dock = DockStyle.Fill;
        _tabs.Padding = new Point(14, 6);
        _tabs.TabPages.Add(Wrap("Status", _status));
        _tabs.TabPages.Add(Wrap("Configuration", _configuration));
        _tabs.TabPages.Add(Wrap("Setup", _setup));
        _tabs.TabPages.Add(Wrap("Remove", _remove));
        _tabs.SelectedIndexChanged += (_, _) => Current()?.OnShown();

        Controls.Add(_tabs);

        // Hide rather than dispose: see the remark on the class.
        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        };
    }

    private static TabPage Wrap(string title, Control page)
    {
        var tab = new TabPage(title) { Padding = new Padding(12), BackColor = SystemColors.Window };
        page.Dock = DockStyle.Fill;
        tab.Controls.Add(page);
        return tab;
    }

    private PageControl? Current() =>
        _tabs.SelectedTab?.Controls.Count > 0 ? _tabs.SelectedTab.Controls[0] as PageControl : null;

    /// <summary>Bring the window up on a page.</summary>
    public void Show(Page page)
    {
        _tabs.SelectedIndex = (int)page;
        base.Show();
        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }

        Activate();
        Current()?.OnShown();
    }

    /// <summary>Called after every poll. Cheap: only the visible page is asked to redraw.</summary>
    public void Refresh(Verdict verdict)
    {
        if (!Visible)
        {
            return;
        }

        _status.Apply(verdict);
    }
}

/// <summary>A page in the window. Only the visible one is refreshed.</summary>
public abstract class PageControl : UserControl
{
    protected PageControl() => BackColor = SystemColors.Window;

    /// <summary>Called whenever this page becomes the visible one.</summary>
    public virtual void OnShown()
    {
    }
}
