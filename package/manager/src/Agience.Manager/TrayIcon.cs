using System.Runtime.InteropServices;

namespace Agience.Manager;

/// <summary>
/// The notification-area icon, registered with a fixed identity.
/// </summary>
/// <remarks>
/// <para>
/// This exists instead of <c>System.Windows.Forms.NotifyIcon</c> because NotifyIcon cannot declare
/// a GUID. Without <c>NIF_GUID</c>, Windows identifies a tray icon by the executable path plus a
/// window handle, so every build directory, every install location and every rename is a different
/// icon as far as the shell is concerned.
/// </para>
/// <para>
/// Measured: <c>HKCU\Control Panel\NotifyIconSettings</c> had accumulated three separate entries
/// for this one application — the build output, a per-user install and Program Files — and only one
/// of them carried <c>IsPromoted = 1</c>. Windows 11 hides a new tray icon by default, so each of
/// those had to be found in Settings and turned on again by hand. Shipping that means every update
/// silently un-pins the icon, and the person has to go hunting through a list of thirty entries to
/// get it back.
/// </para>
/// <para>
/// With a GUID the shell keys the icon by identity. Promote it once and it stays promoted across
/// reinstalls, upgrades and moves.
/// </para>
/// <para>
/// The window is hidden but not message-only, which is load-bearing: <c>TaskbarCreated</c> is a
/// broadcast, and a message-only window (one parented to <c>HWND_MESSAGE</c>) does not receive
/// broadcasts. A message-only window would mean the icon vanishes for good the first time Explorer
/// restarts.
/// </para>
/// </remarks>
public sealed class TrayIcon : IDisposable
{
    /// <summary>
    /// This icon's permanent identity. Generated once; it must never change again.
    /// </summary>
    /// <remarks>
    /// Changing it is exactly equivalent to shipping a brand-new icon: hidden by default, with
    /// the old one left behind in the shell's list as a ghost nothing will ever clean up.
    /// </remarks>
    private static readonly Guid IconId = new("8B1C4E52-7A93-4D1F-9E60-2C5A7F0B3D84");

    private const int WmApp = 0x8000;
    private const int CallbackMessage = WmApp + 1;

    private readonly NativeWindow _window;
    private readonly uint _taskbarCreated;

    private Icon? _icon;
    private string _text = "";
    private bool _added;
    private bool _disposed;

    /// <summary>Raised on a plain left click or the keyboard equivalent.</summary>
    public event Action? Selected;

    /// <summary>Raised when the menu should be shown, at the point the shell asked for.</summary>
    public event Action<Point>? ContextMenuRequested;

    public TrayIcon()
    {
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _window = new MessageWindow(this);
    }

    /// <summary>The icon the shell draws. Setting it repaints in place.</summary>
    public Icon? Icon
    {
        get => _icon;
        set
        {
            _icon = value;
            if (_added)
            {
                Send(NimModify, NifIcon);
            }
        }
    }

    /// <summary>
    /// The tooltip.
    /// </summary>
    /// <remarks>
    /// Cut to 127 characters. The field is a fixed 128-character buffer including its terminator,
    /// and marshalling a longer string throws rather than truncating — so it is cut here, keeping
    /// the tail, where the caller can see it happen.
    /// </remarks>
    public string Text
    {
        get => _text;
        set
        {
            var text = value ?? "";
            _text = text.Length <= 127 ? text : text[..124] + "...";
            if (_added)
            {
                Send(NimModify, NifTip | NifShowTip);
            }
        }
    }

    /// <summary>Put the icon in the notification area.</summary>
    public void Show()
    {
        if (_added || _disposed)
        {
            return;
        }

        // A failed add is retried after a delete. The shell binds a GUID to the executable path
        // that first registered it, so after the application moves — an upgrade into a different
        // directory, a rename — the add fails outright and the icon simply never appears. Deleting
        // the stale association and adding again is the supported recovery.
        if (!Send(NimAdd, NifMessage | NifIcon | NifTip | NifGuid | NifShowTip))
        {
            Send(NimDelete, NifGuid);
            if (!Send(NimAdd, NifMessage | NifIcon | NifTip | NifGuid | NifShowTip))
            {
                return;
            }
        }

        // Version 4 changes what the callback carries: the shell sends WM_CONTEXTMENU and NIN_SELECT
        // with real screen coordinates in wParam. Without it the callback reports only a mouse
        // message and the menu has to be positioned from the cursor, which is wrong on a
        // keyboard-invoked menu and on multi-monitor setups with mixed scaling.
        SendVersion();
        _added = true;
    }

    /// <summary>Remove the icon.</summary>
    public void Hide()
    {
        if (!_added)
        {
            return;
        }

        Send(NimDelete, NifGuid);
        _added = false;
    }

    /// <summary>A balloon notification.</summary>
    public void Notify(string title, string message, bool error = false)
    {
        if (!_added)
        {
            return;
        }

        var data = Build(NifInfo | NifGuid);
        data.szInfoTitle = Trim(title, 63);
        data.szInfo = Trim(message, 255);
        data.dwInfoFlags = error ? NiifError : NiifInfo;
        Shell_NotifyIcon(NimModify, ref data);
    }

    private static string Trim(string value, int max) =>
        value.Length <= max ? value : value[..max];

    /// <summary>The shell's callback, unpacked.</summary>
    private void OnCallback(IntPtr wParam, IntPtr lParam)
    {
        // The layout puts the notification in the low word of lParam and the screen coordinates in
        // wParam.
        var notification = (int)(lParam.ToInt64() & 0xFFFF);
        var x = (short)(wParam.ToInt64() & 0xFFFF);
        var y = (short)((wParam.ToInt64() >> 16) & 0xFFFF);

        switch (notification)
        {
            case NinSelect:
            case NinKeySelect:
                Selected?.Invoke();
                break;

            case WmContextMenu:
                ContextMenuRequested?.Invoke(new Point(x, y));
                break;
        }
    }

    /// <summary>Explorer restarted; every tray icon on the machine has to re-register itself.</summary>
    private void OnTaskbarCreated()
    {
        _added = false;
        Show();
    }

    private bool Send(int message, int flags)
    {
        var data = Build(flags);
        return Shell_NotifyIcon(message, ref data);
    }

    private void SendVersion()
    {
        var data = Build(NifGuid);
        data.uVersion = NotifyIconVersion4;
        Shell_NotifyIcon(NimSetVersion, ref data);
    }

    private NotifyIconData Build(int flags) => new()
    {
        cbSize = Marshal.SizeOf<NotifyIconData>(),
        hWnd = _window.Handle,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon?.Handle ?? IntPtr.Zero,
        szTip = _text,
        szInfo = "",
        szInfoTitle = "",
        guidItem = IconId,
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Hide();
        _window.DestroyHandle();
    }

    /// <summary>
    /// The hidden top-level window the shell posts to.
    /// </summary>
    /// <remarks>
    /// Not parented to HWND_MESSAGE — see the remark on <see cref="TrayIcon"/>. It must be able to
    /// receive the <c>TaskbarCreated</c> broadcast.
    /// </remarks>
    private sealed class MessageWindow : NativeWindow
    {
        private readonly TrayIcon _owner;

        public MessageWindow(TrayIcon owner)
        {
            _owner = owner;
            CreateHandle(new CreateParams
            {
                Caption = "Agience",
                Style = unchecked((int)0x80000000),   // WS_POPUP: never shown, never in the taskbar
                ExStyle = 0x00000080,                 // WS_EX_TOOLWINDOW: not in Alt-Tab
                X = 0,
                Y = 0,
                Width = 0,
                Height = 0,
            });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == CallbackMessage)
            {
                _owner.OnCallback(m.WParam, m.LParam);
                return;
            }

            if (_owner._taskbarCreated != 0 && m.Msg == (int)_owner._taskbarCreated)
            {
                _owner.OnTaskbarCreated();
                return;
            }

            base.WndProc(ref m);
        }
    }

    // ── the shell API ───────────────────────────────────────────────────────────────────────────

    private const int NimAdd = 0x00;
    private const int NimModify = 0x01;
    private const int NimDelete = 0x02;
    private const int NimSetVersion = 0x04;

    private const int NifMessage = 0x01;
    private const int NifIcon = 0x02;
    private const int NifTip = 0x04;
    private const int NifInfo = 0x10;
    private const int NifGuid = 0x20;
    private const int NifShowTip = 0x80;

    private const int NiifInfo = 0x01;
    private const int NiifError = 0x03;

    private const int NotifyIconVersion4 = 4;

    private const int WmContextMenu = 0x007B;
    private const int NinSelect = 0x0400;        // WM_USER
    private const int NinKeySelect = 0x0401;     // WM_USER + 1

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public int dwState;
        public int dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        /// <summary>A union of uTimeout and uVersion. Only the version is used here.</summary>
        public int uVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(int message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string message);
}
