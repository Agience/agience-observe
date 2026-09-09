using System.Drawing.Drawing2D;
using Microsoft.Win32;

namespace Agience.Manager;

/// <summary>
/// The tray menu, drawn the way Windows 11 draws menus.
/// </summary>
/// <remarks>
/// <para>
/// WinForms' default renderer looks twenty years old, and in a tray menu that is the first thing
/// a person sees. Out of the box a <see cref="ContextMenuStrip"/> is a square-cornered light-grey
/// panel with a gradient gutter down the left and a blue selection bar — the Office 2003 look. Every
/// other tray menu on the machine is rounded, flat, padded, and follows the system theme. The
/// difference does not affect what the application does and completely changes what it looks like it
/// is.
/// </para>
/// <para>
/// The menu follows the system theme rather than picking one. A light menu on a dark taskbar is worse
/// than the old renderer, and the setting is per-user and changes at run time. It is read from the
/// registry each time a menu is built, so a theme switch is picked up without a restart.
/// </para>
/// <para>
/// The rounded corners are a region, not a painted shape. A menu is a top-level window; painting
/// rounded corners inside a square window leaves the square's corners showing behind them in
/// whatever colour the shell put there.
/// </para>
/// </remarks>
public static class MenuStyle
{
    /// <summary>Windows 11's menu corner radius.</summary>
    private const int Radius = 8;

    /// <summary>A menu already styled, with its renderer and padding set.</summary>
    public static ContextMenuStrip NewMenu()
    {
        var menu = new ContextMenuStrip
        {
            RenderMode = ToolStripRenderMode.Professional,

            // The image gutter is switched off. Nothing in this menu has an icon, and the default
            // reserves a 25-pixel column for one — which is where the old renderer's grey gradient
            // stripe lives, and the single strongest "this is an old application" signal in the
            // whole window.
            ShowImageMargin = false,
            ShowCheckMargin = false,
            Padding = new Padding(2, 6, 2, 6),
            DropShadowEnabled = true,
        };

        menu.Opening += (_, _) => Apply(menu);
        menu.Renderer = new ModernRenderer(IsDark());

        // The region has to be rebuilt whenever the menu's size changes, which happens every time
        // its items are replaced — and this menu rebuilds its items on every open.
        menu.Resize += (_, _) => Round(menu);
        return menu;
    }

    /// <summary>Re-read the theme and re-round. Called as the menu opens.</summary>
    private static void Apply(ContextMenuStrip menu)
    {
        var dark = IsDark();
        if (menu.Renderer is not ModernRenderer renderer || renderer.IsDark != dark)
        {
            menu.Renderer = new ModernRenderer(dark);
        }

        menu.BackColor = dark ? DarkTheme.Surface : LightTheme.Surface;
        menu.ForeColor = dark ? DarkTheme.Text : LightTheme.Text;
        Round(menu);
    }

    private static void Round(ContextMenuStrip menu)
    {
        if (menu.Width <= 0 || menu.Height <= 0)
        {
            return;
        }

        using var path = RoundedRect(new Rectangle(0, 0, menu.Width, menu.Height), Radius);
        menu.Region?.Dispose();
        menu.Region = new Region(path);
    }

    private static GraphicsPath RoundedRect(Rectangle box, int radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(box.X, box.Y, d, d, 180, 90);
        path.AddArc(box.Right - d, box.Y, d, d, 270, 90);
        path.AddArc(box.Right - d, box.Bottom - d, d, d, 0, 90);
        path.AddArc(box.X, box.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// Is the shell in dark mode?
    /// </summary>
    /// <remarks>
    /// Reads `AppsUseLightTheme`, not `SystemUsesLightTheme`. They are separate settings and they
    /// routinely disagree: the common Windows 11 arrangement is a dark taskbar with light
    /// applications. A menu belongs to the application, so it follows the application setting —
    /// reading the system one gives a dark menu to somebody whose every other window is light.
    /// </remarks>
    private static bool IsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception)
        {
            // Light is the safer default: a light menu on a light desktop is merely ordinary, while
            // a dark one there is unreadable.
            return false;
        }
    }

    private static class LightTheme
    {
        public static readonly Color Surface = Color.FromArgb(0xF9, 0xF9, 0xF9);
        public static readonly Color Border = Color.FromArgb(0xE1, 0xE1, 0xE1);
        public static readonly Color Text = Color.FromArgb(0x1A, 0x1A, 0x1A);
        public static readonly Color Muted = Color.FromArgb(0x75, 0x75, 0x75);
        public static readonly Color Hover = Color.FromArgb(0xEA, 0xEA, 0xEA);
        public static readonly Color Separator = Color.FromArgb(0xE0, 0xE0, 0xE0);
    }

    private static class DarkTheme
    {
        public static readonly Color Surface = Color.FromArgb(0x2C, 0x2C, 0x2C);
        public static readonly Color Border = Color.FromArgb(0x40, 0x40, 0x40);
        public static readonly Color Text = Color.FromArgb(0xF2, 0xF2, 0xF2);
        public static readonly Color Muted = Color.FromArgb(0xA0, 0xA0, 0xA0);
        public static readonly Color Hover = Color.FromArgb(0x3D, 0x3D, 0x3D);
        public static readonly Color Separator = Color.FromArgb(0x45, 0x45, 0x45);
    }

    /// <summary>Flat surfaces, a rounded hover pill, and no gutter.</summary>
    private sealed class ModernRenderer : ToolStripProfessionalRenderer
    {
        public bool IsDark { get; }

        public ModernRenderer(bool dark)
            : base(new ModernColours(dark))
        {
            IsDark = dark;

            // Arrows, images and the gutter are all drawn by the base renderer's own colour
            // table; RoundedEdges off stops it clipping its own square border over our region.
            RoundedEdges = false;
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            e.Graphics.Clear(IsDark ? DarkTheme.Surface : LightTheme.Surface);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            // A one-pixel border inside the rounded region, so the menu reads as a surface rather
            // than as paint floating on the desktop.
            using var pen = new Pen(IsDark ? DarkTheme.Border : LightTheme.Border);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = RoundedRect(
                new Rectangle(0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1), Radius);
            e.Graphics.DrawPath(pen, path);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled)
            {
                return;
            }

            // A rounded pill inset from the edges, which is what Windows 11 draws. A full-width
            // rectangle that touches the menu's border is the single clearest tell of the old
            // renderer.
            var box = new Rectangle(4, 1, e.Item.Width - 8, e.Item.Height - 2);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(IsDark ? DarkTheme.Hover : LightTheme.Hover);
            using var path = RoundedRect(box, 4);
            e.Graphics.FillPath(brush, path);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new Pen(IsDark ? DarkTheme.Separator : LightTheme.Separator);
            var y = e.Item.Height / 2;
            e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            // A disabled item is muted, not greyed into illegibility. Most of this menu is
            // disabled on purpose — the headline, the detail lines, the service rows — because they
            // are information rather than commands. The default disabled colour makes the one part
            // of the menu that carries the answer the hardest part to read.
            e.TextColor = e.Item.Enabled
                ? IsDark ? DarkTheme.Text : LightTheme.Text
                : IsDark ? DarkTheme.Muted : LightTheme.Muted;
            base.OnRenderItemText(e);
        }


    }

    /// <summary>The base renderer's colour table, so nothing it draws falls back to Office 2003.</summary>
    private sealed class ModernColours : ProfessionalColorTable
    {
        private readonly bool _dark;

        public ModernColours(bool dark)
        {
            _dark = dark;
            UseSystemColors = false;
        }

        private Color Surface => _dark ? DarkTheme.Surface : LightTheme.Surface;

        private Color Hover => _dark ? DarkTheme.Hover : LightTheme.Hover;

        public override Color ToolStripDropDownBackground => Surface;

        public override Color MenuBorder => _dark ? DarkTheme.Border : LightTheme.Border;

        public override Color MenuItemBorder => Hover;

        public override Color MenuItemSelected => Hover;

        public override Color MenuItemSelectedGradientBegin => Hover;

        public override Color MenuItemSelectedGradientEnd => Hover;

        public override Color MenuItemPressedGradientBegin => Hover;

        public override Color MenuItemPressedGradientEnd => Hover;

        public override Color ImageMarginGradientBegin => Surface;

        public override Color ImageMarginGradientMiddle => Surface;

        public override Color ImageMarginGradientEnd => Surface;

        public override Color SeparatorDark => _dark ? DarkTheme.Separator : LightTheme.Separator;

        public override Color SeparatorLight => _dark ? DarkTheme.Separator : LightTheme.Separator;
    }
}
