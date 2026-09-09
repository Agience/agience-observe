using System.Drawing;
using Agience.Core;

namespace Agience.Manager;

/// <summary>
/// The Agience tray application: it starts, stops and configures Origin and Mantle instances.
/// </summary>
/// <remarks>
/// <para>
/// This process owns the services. They are its children, they are in its job object, and they
/// die with it — which is why "Exit" in the menu says so. One process supervising its own
/// children is simpler to install, to stop and to remove than two supervisors that have to
/// agree with each other.
/// </para>
/// <para>
/// The services do not run without a login. There is no Windows Service and no
/// scheduled task; the tray starts at logon and the services start with it. A machine that must
/// serve while nobody is signed in needs the service host this deliberately does not have.
/// </para>
/// </remarks>
internal static class Program
{
    /// <summary>
    /// A second tray would start a second set of services onto the same ports.
    /// </summary>
    /// <remarks>
    /// The second instance's services fail to bind, exit, and are reported by
    /// the second tray as failed — while the first tray reports them healthy, because they are.
    /// Two icons then disagree about one machine.
    /// </remarks>
    private const string SingleInstanceMutex = "Local\\Agience.Manager.SingleInstance";

    /// <summary>
    /// How the uninstaller asks a running tray to shut down.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The uninstaller cannot simply delete the files. This process holds three Python children
    /// in a job object; removing the executable underneath it leaves a tray icon whose exe is gone
    /// and three services nothing will admit to owning, on ports the next install cannot bind.
    /// </para>
    /// <para>
    /// An event, not a kill. Terminating the tray would fire the job object and take the services
    /// with it — the right end state by the wrong route: no clean line in any log, no port released
    /// on request, and a store closed by having its process removed. Asking reaches the same
    /// outcome through <see cref="Supervisor.StopAllAsync"/>.
    /// </para>
    /// </remarks>
    private const string ShutdownEvent = "Local\\Agience.Manager.Shutdown";

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    [STAThread]
    private static int Main(string[] args)
    {
        // `--check` exists because a tray icon cannot be verified by a script. Everything this
        // application does happens behind an icon nobody can assert on. This runs the same
        // discovery and the same evaluation once, prints what it found, and exits non-zero unless
        // everything is up — which is what makes "it works" a measurement rather than a claim.
        //
        // A WinExe has no console. Without attaching to the parent's, every line below would go
        // to a handle that leads nowhere and the command would appear to do nothing at all.
        if (args.Contains("--check", StringComparer.OrdinalIgnoreCase))
        {
            AttachConsole(-1);
            return Check();
        }

        if (args.Contains("--shutdown", StringComparer.OrdinalIgnoreCase))
        {
            AttachConsole(-1);
            return Shutdown();
        }

        // `--icons` exists for the same reason `--check` does: a tray icon cannot be looked at.
        // Every icon this application shows is drawn in code, at 32 pixels, into a tray that may be
        // collapsed behind a chevron — so the only way anyone reviews whether six states are
        // actually distinguishable is to render them side by side and look. Without this, "the
        // badges are legible" is a claim nobody can check.
        var icons = Array.FindIndex(args, a => a.Equals("--icons", StringComparison.OrdinalIgnoreCase));
        if (icons >= 0)
        {
            AttachConsole(-1);
            return WriteIcons(icons + 1 < args.Length ? args[icons + 1] : Path.GetTempPath());
        }

        return RunTray();
    }

    /// <summary>Render every tray state to PNG, plus a strip of all of them, and say where.</summary>
    private static int WriteIcons(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);

            var levels = Enum.GetValues<TrayLevel>();
            const int cell = 64;
            const int pad = 8;

            // The strip is rendered at the size the shell actually uses (16), blown up. Rendering
            // it at 64 would show six icons that are obviously different and prove nothing about
            // the size they are seen at.
            using var strip = new Bitmap((levels.Length * (cell + pad)) + pad, cell + (pad * 2));
            using (var g = Graphics.FromImage(strip))
            {
                g.Clear(Color.White);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;

                for (var i = 0; i < levels.Length; i++)
                {
                    var icon = Icons.For(levels[i]);

                    using (var small = new Icon(icon, 16, 16))
                    using (var bmp = small.ToBitmap())
                    {
                        var path = Path.Combine(directory, $"tray-{levels[i]}.png");
                        using (var large = new Icon(icon, 32, 32))
                        using (var big = large.ToBitmap())
                        {
                            big.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                        }

                        Console.WriteLine($"  {levels[i],-12} {path}");
                        g.DrawImage(bmp, new Rectangle(pad + (i * (cell + pad)), pad, cell, cell));
                    }
                }
            }

            var stripPath = Path.Combine(directory, "tray-all.png");
            strip.Save(stripPath, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine();
            Console.WriteLine("  all six, drawn at 16px and magnified: " + stripPath);
            Console.WriteLine("  order: " + string.Join(", ", levels));
            return 0;
        }
        catch (Exception exc)
        {
            Console.WriteLine("could not write the icons: " + exc.Message);
            return 1;
        }
    }

    /// <summary>
    /// Ask a running tray to stop its services and exit, and wait until it has.
    /// </summary>
    /// <remarks>
    /// It waits, and the wait is the point. The uninstaller runs this and then deletes the
    /// directory; returning the moment the event is set would race the shutdown, and the delete
    /// would land on an executable still running. The single-instance mutex coming free is the
    /// signal that the tray has actually gone — nothing else observable is.
    /// </remarks>
    private static int Shutdown()
    {
        if (!EventWaitHandle.TryOpenExisting(ShutdownEvent, out var signal))
        {
            // Success, not failure. A tray that is not running is exactly the state the caller
            // wanted; a non-zero exit here would stop an uninstall over nothing being wrong.
            Console.WriteLine("Agience is not running.");
            return 0;
        }

        using (signal)
        {
            signal.Set();
        }

        // Up to 45s: three services, each given time to close its store rather than be killed.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using (var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutex, out var isFree))
            {
                if (isFree)
                {
                    mutex.ReleaseMutex();
                    Console.WriteLine("Agience has stopped.");
                    return 0;
                }
            }

            Thread.Sleep(500);
        }

        Console.WriteLine("Agience did not stop within 45s.");
        return 1;
    }

    private static int Check()
    {
        var config = AgienceConfig.Load();
        using var supervisor = new Supervisor(config);

        var runtime = PythonEnvironment.Inspect();
        supervisor.RuntimeReady = runtime.Ready;
        supervisor.PollAsync().GetAwaiter().GetResult();

        Console.WriteLine();
        Console.WriteLine("settings   " + Paths.ConfigFile);
        Console.WriteLine("runtime    " + Paths.Runtime);
        Console.WriteLine("           " + runtime.Summary);
        Console.WriteLine("data root  " + config.ResolvedDataRoot);
        Console.WriteLine();

        if (config.Instances.Count == 0)
        {
            Console.WriteLine("  no Origin and no Mantle have been configured on this machine");
        }

        foreach (var service in supervisor.Services.Concat(supervisor.Orphans))
        {
            var instance = service.Instance;
            var removed = config.ById(instance.Id) is null ? "   REMOVED FROM CONFIGURATION" : "";
            Console.WriteLine($"  {instance.Id,-16} :{service.Port,-6} {service.State,-14} " +
                              $"pid={service.Pid?.ToString() ?? "-"}{removed}");
            Console.WriteLine($"    answers at   {instance.PublicUri}");

            // The issuer is printed for every instance, because a machine can hold several and
            // "which authority is this one on" is the question a mismatch actually raises.
            var issuer = config.AuthorityIssuerFor(instance);
            var address = config.AuthorityAddressFor(instance);
            Console.WriteLine($"    authority    {issuer ?? "NOT DECIDED — every token it is handed is rejected"}" +
                              (address is not null && address != issuer ? $"   (reached at {address})" : ""));
            Console.WriteLine($"    data         {config.DataDirectoryOf(instance)}");
            Console.WriteLine($"    {service.Detail}");
        }

        var verdict = supervisor.Verdict;
        Console.WriteLine();
        Console.WriteLine($"verdict    {verdict.Level}: {verdict.Headline}");
        Console.WriteLine($"           {verdict.Detail}");
        Console.WriteLine();

        // Non-zero on anything but green, so a script can gate on it.
        return verdict.Level == TrayLevel.Healthy ? 0 : 2;
    }

    private static int RunTray()
    {
        using var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutex, out var isFirst);
        if (!isFirst)
        {
            // Silent on purpose: the usual cause is launching it twice from the Start menu, and a
            // dialog for that is noise. The already-running instance owns the tray and the services.
            return 0;
        }

        Paths.EnsureRoot();
        ApplicationConfiguration.Initialize();

        var config = AgienceConfig.Load();
        using var supervisor = new Supervisor(config);
        TrayApp? tray = null;

        void ExitAll()
        {
            tray?.Dispose();
            Application.Exit();
        }

        tray = new TrayApp(supervisor, ExitAll);

        // The uninstaller's way in. A thread rather than a timer: it has to answer while the UI
        // thread is inside a start or a stop, which is exactly when an uninstall tends to arrive.
        //
        // It marshals through the tray's own control, not through SynchronizationContext.Current.
        // That property is null here — WinForms installs its context only once a window handle
        // exists, which has not happened before Application.Run — so a `?.Post` on it silently does
        // nothing, and the uninstaller's request to shut down would be accepted and then ignored.
        using var shutdown = new EventWaitHandle(false, EventResetMode.ManualReset, ShutdownEvent);
        var listener = tray;
        new Thread(() =>
        {
            shutdown.WaitOne();
            listener.RequestExit();
        })
        {
            IsBackground = true,
            Name = "Agience shutdown listener",
        }.Start();

        // The services are stopped on the way out even when the exit did not come through the
        // menu — a logoff, a shutdown, an Application.Exit from anywhere. The job object catches
        // what this misses, but an orderly stop writes a clean line in each log and releases the
        // ports without a kill.
        Application.ApplicationExit += (_, _) =>
        {
            try
            {
                supervisor.StopAllAsync().GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                // Shutting down anyway; the job object is the backstop.
            }
        };

        Application.Run();
        return 0;
    }
}
