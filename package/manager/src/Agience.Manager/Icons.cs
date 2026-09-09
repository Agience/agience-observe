using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;
using Agience.Core;

namespace Agience.Manager;

/// <summary>
/// The tray icon: the Agience mark, with a status badge over it.
/// </summary>
/// <remarks>
/// <para>
/// The mark alone cannot be the tray icon. A tray icon's whole job is to answer "is this
/// working?" from across the desk, while a logo answers "this is installed" — which the person
/// already knew. The badge is what makes it a status surface; the mark is what makes it findable
/// among the twenty other icons in the tray.
/// </para>
/// <para>
/// Colour is never the only difference. Roughly one man in twelve cannot separate the red from
/// the green here, and a tray icon is 16 pixels with no label — so every badge differs in shape as
/// well: a tick, a bar-and-dot, a cross, three dots, a down arrow, and a hollow ring. Someone who
/// sees no colour at all still gets six distinguishable icons.
/// </para>
/// <para>
/// The badge is ringed in the background colour, not drawn flat. The mark is a dense tangle of
/// thin strokes; a badge laid straight on top of it merges with whatever stroke it lands on and
/// stops reading as a separate thing at exactly the size where it matters.
/// </para>
/// </remarks>
public static class Icons
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    private static readonly Dictionary<TrayLevel, Icon> Cache = new();
    private static readonly Lazy<Image?> Mark = new(LoadMark);

    /// <summary>
    /// Drawn at 32 and left to the shell to downscale.
    /// </summary>
    /// <remarks>
    /// 16 renders muddy on a HiDPI display, and the mark's thin interlaced strokes are the first
    /// thing to disappear when it does.
    /// </remarks>
    private const int Size = 32;

    /// <summary>
    /// The status dot's share of the icon.
    /// </summary>
    /// <remarks>
    /// Measured: at 0.56 the dot was over a third of the icon's area and read as a second object
    /// stuck onto the mark rather than as a status on it — the mark stopped being recognisable and
    /// the whole thing looked like a mistake — so this uses 0.38. A status indicator is meant to be
    /// glanced at, not read.
    /// </remarks>
    private const float BadgeShare = 0.38f;

    public static Icon For(TrayLevel level)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(level, out var hit))
            {
                return hit;
            }

            var icon = Draw(level);
            Cache[level] = icon;
            return icon;
        }
    }

    /// <summary>The mark on its own, for the window and its title bar.</summary>
    public static Icon Application()
    {
        lock (Cache)
        {
            if (Cache.TryGetValue((TrayLevel)(-1), out var hit))
            {
                return hit;
            }

            using var bmp = new Bitmap(64, 64);
            using (var g = Graphics.FromImage(bmp))
            {
                Prepare(g);
                DrawMark(g, 64, 1.0f);
            }

            var icon = ToIcon(bmp);
            Cache[(TrayLevel)(-1)] = icon;
            return icon;
        }
    }

    /// <summary>The brand gradient, sampled from the mark itself: light at the top, dark at the foot.</summary>
    private static readonly Color BrandLight = Color.FromArgb(0xCA, 0x84, 0xCF);
    private static readonly Color BrandDark = Color.FromArgb(0x5B, 0x25, 0x70);

    private static Icon Draw(TrayLevel level)
    {
        using var bmp = new Bitmap(Size, Size);
        using (var g = Graphics.FromImage(bmp))
        {
            Prepare(g);

            // A solid disc with the mark knocked out in white, not the mark on its own.
            // Measured against the real tray: drawn as it is, the mark is line art, and at 16
            // pixels its thin interlaced strokes antialias to a pale lilac smudge. Beside WhatsApp's
            // green disc, Bluetooth's blue one and Defender's shield — every one of them a solid,
            // saturated shape — it was visibly the faintest thing on the taskbar and the one the eye
            // skips. No amount of choosing a different source file fixes that: the problem is line
            // art at 16 pixels, not which line art.
            //
            // A filled disc gives the icon the same visual weight as its neighbours, and white on
            // saturated purple is the highest contrast available for the strokes that remain.
            //
            // The large app icon is the bare mark (see `Application` and `make-icon.ps1`).
            // At 48 and 256 — Explorer, the Start menu, the shortcut — the strokes have the pixels
            // they need and the logo looks like the logo. The disc is a 16-pixel remedy, not a
            // rebrand.
            var disc = new Rectangle(0, 0, Size - 1, Size - 1);
            using (var fill = new LinearGradientBrush(disc, BrandLight, BrandDark, 65f))
            {
                g.FillEllipse(fill, disc);
            }

            DrawMark(g, Size, 0.98f, thicken: true, white: true);
            DrawBadge(g, level);
        }

        return ToIcon(bmp);
    }

    private static void Prepare(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);
    }

    private static void DrawMark(Graphics g, int size, float scale, bool thicken = false, bool white = false)
    {
        var side = (int)Math.Round(size * scale);
        var offset = (size - side) / 2;

        if (Mark.Value is { } mark)
        {
            if (!thicken)
            {
                g.DrawImage(mark, new Rectangle(offset, offset, side, side));
                return;
            }

            // The mark is drawn twice and its alpha is pushed; without this it is a ghost.
            // Measured against the real tray: the mark is 1407 pixels of thin interlaced strokes,
            // and at 16 those strokes land between pixels. Antialiasing then spreads each one over
            // two pixels at roughly half alpha, so the whole icon renders as a pale smudge sitting
            // beside a row of bold, saturated icons — visibly the faintest thing in the tray, and
            // the one nobody's eye goes to.
            //
            // Multiplying alpha darkens what is already there; drawing it a second time recovers
            // the width of a stroke that fell between two pixels. Neither alone is enough, and at
            // 32 and above the pair is harmless because nothing is being recovered.
            using var attributes = new System.Drawing.Imaging.ImageAttributes();

            // White is produced by a colour matrix, not by a second white artwork file. The matrix
            // maps every channel to 1 while carrying the alpha through untouched, so the mark's
            // exact shape and antialiasing survive and there is still only one brand asset in the
            // repository to keep current.
            attributes.SetColorMatrix(white
                ? new System.Drawing.Imaging.ColorMatrix(new[]
                {
                    new[] { 0f, 0f, 0f, 0f, 0f },
                    new[] { 0f, 0f, 0f, 0f, 0f },
                    new[] { 0f, 0f, 0f, 0f, 0f },
                    new[] { 0f, 0f, 0f, 2.1f, 0f },
                    new[] { 1f, 1f, 1f, 0f, 1f },
                })
                : new System.Drawing.Imaging.ColorMatrix { Matrix33 = 1.9f });

            var box = new Rectangle(offset, offset, side, side);
            for (var pass = 0; pass < 2; pass++)
            {
                g.DrawImage(mark, box, 0, 0, mark.Width, mark.Height, GraphicsUnit.Pixel, attributes);
            }

            return;
        }

        // A fallback that is a usable icon on its own. If the embedded mark is ever missing, a tray
        // with no icon at all is indistinguishable from a tray application that failed to start —
        // so a plain ring stands in and every status badge still works over it.
        using var pen = new Pen(white ? Color.White : Color.FromArgb(0x6B, 0x35, 0x8C),
                                Math.Max(2f, side / 12f));
        var inset = pen.Width;
        g.DrawEllipse(pen, offset + inset, offset + inset, side - (inset * 2), side - (inset * 2));
    }

    /// <summary>
    /// The status dot: a small solid circle, bottom-right, ringed so it lifts off the mark.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Solid, and no glyph inside it. At the size a dot has to be in order to leave the mark
    /// legible, a glyph inside it is three or four pixels — it does not read as a symbol, it just
    /// muddies the one thing the dot has to communicate, which is its colour. A clean disc at 6
    /// pixels says more than a smeared tick at 6 pixels.
    /// </para>
    /// <para>
    /// The tray icon is the one surface here that leans on colour: the colours below are separated
    /// by luminance as well as hue, so they stay distinguishable to the roughly one man in twelve
    /// who cannot separate red from green — the amber is markedly lighter than the red and the
    /// green is darker than both. Everywhere a person can actually read something — the tooltip,
    /// the menu headline, every row in the window — the state is a word and a sentence, never a
    /// colour.
    /// </para>
    /// </remarks>
    private static void DrawBadge(Graphics g, TrayLevel level)
    {
        var d = (int)Math.Round(Size * BadgeShare);
        var box = new Rectangle(Size - d - 1, Size - d - 1, d, d);

        // The halo is punched out, not painted. The mark underneath is a dense tangle of white
        // strokes on purple; a dot laid straight on top merges with whatever it lands on and stops
        // reading as a separate thing at exactly the size where that matters.
        g.CompositingMode = CompositingMode.SourceCopy;
        using (var clear = new SolidBrush(Color.Transparent))
        {
            g.FillEllipse(clear, Rectangle.Inflate(box, 2, 2));
        }

        g.CompositingMode = CompositingMode.SourceOver;

        using (var fill = new SolidBrush(Colour(level)))
        {
            g.FillEllipse(fill, box);
        }
    }

    /// <summary>Separated by luminance as well as hue — see the remark on <see cref="DrawBadge"/>.</summary>
    private static Color Colour(TrayLevel level) => level switch
    {
        TrayLevel.Healthy => Color.FromArgb(0x1F, 0xA3, 0x55),    // green, mid
        TrayLevel.Degraded => Color.FromArgb(0xF0, 0xA8, 0x00),   // amber, clearly the lightest
        TrayLevel.Foreign => Color.FromArgb(0xC4, 0x1E, 0x14),    // red, clearly the darkest
        TrayLevel.Starting => Color.FromArgb(0x2E, 0x7D, 0xD1),   // blue: working, not broken
        TrayLevel.NotReady => Color.FromArgb(0x5A, 0x5A, 0x5A),   // dark grey: nothing to run yet
        _ => Color.FromArgb(0xB4, 0xB4, 0xB4),                    // light grey: stopped
    };

    /// <summary>
    /// The brand mark, embedded in this assembly.
    /// </summary>
    /// <remarks>
    /// Embedded, not loaded from disk. The published application is a single self-contained
    /// executable; a mark read from a file beside it would be a second file the MSI has to carry and
    /// a second thing that can go missing — and the failure would be a tray icon that silently
    /// stopped looking like Agience.
    /// </remarks>
    private static Image? LoadMark()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var name = assembly.GetManifestResourceNames()
                               .FirstOrDefault(n => n.EndsWith("agience-logo.png", StringComparison.OrdinalIgnoreCase));
            if (name is null)
            {
                return null;
            }

            using var stream = assembly.GetManifestResourceStream(name);
            return stream is null ? null : Image.FromStream(stream);
        }
        catch (Exception)
        {
            // The fallback ring in DrawMark covers this. A missing logo must never be a tray that
            // will not start.
            return null;
        }
    }

    private static Icon ToIcon(Bitmap bmp)
    {
        var handle = bmp.GetHicon();
        try
        {
            // Clone, because the Icon returned by FromHandle does not own the handle and the
            // original must be released explicitly or every redraw leaks a GDI object.
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }
}
