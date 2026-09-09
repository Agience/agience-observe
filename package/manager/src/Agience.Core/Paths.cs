namespace Agience.Core;

/// <summary>
/// Every directory this application owns, named once.
/// </summary>
/// <remarks>
/// <para>
/// The install and the data are separate trees, and the uninstaller is the reason. "Remove the
/// program but keep my data" is only expressible if the two never overlap: the MSI owns
/// <c>%ProgramFiles%\Agience</c> and removes it unconditionally, and everything a person would
/// grieve over lives under <see cref="Root"/>, which is removed only when they ask.
/// </para>
/// <para>
/// The Python environment is data, not program. It is built on this machine by pip, it is
/// several hundred megabytes, and rebuilding it needs the network — so it sits beside the data and
/// survives a reinstall. An MSI that shipped it would be shipping a tree it did not author and
/// cannot repair.
/// </para>
/// </remarks>
public static class Paths
{
    /// <summary>%LOCALAPPDATA%\Agience — configuration, the runtime, logs, and the default data directory.</summary>
    /// <remarks>
    /// LOCALAPPDATA rather than APPDATA: none of this roams, and a roaming profile carrying a
    /// several-hundred-megabyte virtualenv and a SQLite store is a logon that times out.
    /// </remarks>
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Agience");

    /// <summary>The settings file the tray reads and the configuration screen writes.</summary>
    public static string ConfigFile => Path.Combine(Root, "manager.json");

    /// <summary>The private virtualenv the services run out of.</summary>
    public static string Runtime => Path.Combine(Root, "runtime");

    /// <summary><c>runtime\Scripts\python.exe</c> — the interpreter every service is launched with.</summary>
    public static string VenvPython => Path.Combine(Runtime, "Scripts", "python.exe");

    /// <summary><c>runtime\Scripts\pip.exe</c>.</summary>
    public static string VenvPip => Path.Combine(Runtime, "Scripts", "pip.exe");

    /// <summary>One log file per service, plus the manager's own.</summary>
    public static string Logs => Path.Combine(Root, "logs");

    /// <summary>The default for <see cref="AgienceConfig.DataDirectory"/>; the keys, the stores, the indexes.</summary>
    public static string DefaultData => Path.Combine(Root, "data");

    /// <summary>Create the directories this application writes to. Idempotent.</summary>
    public static void EnsureRoot()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Logs);
    }
}
