using System.Diagnostics;
using Microsoft.Win32;

namespace Agience.Core;

/// <summary>What "remove Agience" is allowed to delete.</summary>
public enum RemovalScope
{
    /// <summary>The program only. The environment, the keys and the stores stay.</summary>
    KeepEverything,

    /// <summary>The program and the Python environment. The keys and the stores stay.</summary>
    RemoveRuntime,

    /// <summary>All of it, including the keys and the stores.</summary>
    RemoveEverything,
}

/// <summary>
/// Removing this installation.
/// </summary>
/// <remarks>
/// <para>
/// The default is to keep the data. The data directory holds the signing keys of an identity
/// authority and the only copy of whatever this node has stored. An uninstaller that removes it by
/// default is one mis-click from destroying something no backup was ever taken of, and the person
/// doing it believed they were removing a tray icon.
/// </para>
/// <para>
/// The Python environment is a separate choice from the data, because they cost different things
/// to lose. The environment is a rebuild and a download; the keys are gone.
/// </para>
/// </remarks>
public static class Installation
{
    /// <summary>The MSI's product code — the thing <c>msiexec /x</c> takes.</summary>
    /// <remarks>
    /// Held in one place, and the installer reads it from here. A product code typed twice is a
    /// tray whose "Uninstall" uninstalls nothing after the next release bumps it — the failure is
    /// a dialog that flashes and a program that is still installed.
    /// </remarks>
    public const string ProductCode = "{9C1B2F60-3F2E-4C4B-9F1A-1D5E2A6B7C80}";

    /// <summary>The Add/Remove Programs display name, so a person can find it by hand.</summary>
    public const string DisplayName = "Agience";

    /// <summary>Is this build actually installed by the MSI, or being run from a build directory?</summary>
    /// <remarks>
    /// Asked before offering to uninstall. Handing <c>msiexec</c> a product code nothing
    /// registered produces error 1605 and a dialog that names a GUID — which tells a developer
    /// running the exe out of <c>bin\Debug</c> nothing at all.
    /// </remarks>
    public static bool IsMsiInstalled()
    {
        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using var key = root.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + ProductCode);
                if (key is not null)
                {
                    return true;
                }
            }
            catch (Exception)
            {
                // An unreadable hive reads as "not installed", which shows the manual instructions
                // rather than launching something that will fail.
            }
        }

        return false;
    }

    /// <summary>
    /// Delete what the scope allows. Returns what was removed and what could not be.
    /// </summary>
    /// <remarks>
    /// Call this only after the services are stopped. A running service holds its store open;
    /// deleting the directory underneath it removes the entries and leaves the file, which is the
    /// one outcome worse than either "kept" or "removed" — a store that exists, opens, and has lost
    /// what it held.
    /// </remarks>
    public static IReadOnlyList<string> Remove(RemovalScope scope, AgienceConfig config)
    {
        var report = new List<string>();

        if (scope == RemovalScope.KeepEverything)
        {
            report.Add($"Kept the environment at {Paths.Runtime}");
            report.Add($"Kept the data at {config.ResolvedDataRoot}");
            return report;
        }

        Delete(Paths.Runtime, "the Python environment", report);

        if (scope == RemovalScope.RemoveEverything)
        {
            Delete(config.ResolvedDataRoot, "the data directory", report);
            Delete(Paths.Logs, "the logs", report);
            DeleteFile(Paths.ConfigFile, "the settings", report);
        }
        else
        {
            report.Add($"Kept the data at {config.ResolvedDataRoot}");
        }

        return report;
    }

    /// <summary>Hand the removal to Windows Installer and exit. Returns the reason it could not.</summary>
    /// <remarks>
    /// It does not wait. msiexec stops this process as part of the uninstall, so waiting for it
    /// would be waiting for something that is going to kill the waiter.
    /// </remarks>
    public static string? LaunchMsiUninstall()
    {
        try
        {
            Process.Start(new ProcessStartInfo("msiexec.exe")
            {
                Arguments = "/x " + ProductCode,
                UseShellExecute = true,
            });
            return null;
        }
        catch (Exception exc)
        {
            return exc.Message;
        }
    }

    private static void Delete(string directory, string what, List<string> report)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                report.Add($"There was no {what} at {directory}");
                return;
            }

            Directory.Delete(directory, recursive: true);
            report.Add($"Removed {what} ({directory})");
        }
        catch (Exception exc)
        {
            // Named, never swallowed. The usual cause is a file still open — a service that did
            // not stop, or a log tailed in another window — and the person needs to know which
            // directory is still there so they can finish the job by hand.
            report.Add($"Could NOT remove {what} ({directory}): {exc.Message}");
        }
    }

    private static void DeleteFile(string file, string what, List<string> report)
    {
        try
        {
            if (File.Exists(file))
            {
                File.Delete(file);
                report.Add($"Removed {what} ({file})");
            }
        }
        catch (Exception exc)
        {
            report.Add($"Could NOT remove {what} ({file}): {exc.Message}");
        }
    }
}
