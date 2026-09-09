using Microsoft.Win32;

namespace Agience.Core;

/// <summary>
/// Whether the tray starts when this user logs in.
/// </summary>
/// <remarks>
/// <para>
/// Written to HKCU\Run by the application, not the installer. A per-machine MSI installs for
/// everyone and runs elevated; a Run entry it wrote would either be per-machine — starting a tray
/// icon in every account on the box, including ones that never asked — or a per-user component
/// whose repair behaviour reinstalls itself on every launch of an unrelated program. Written here,
/// it belongs to the person who ticked the box.
/// </para>
/// <para>
/// It also means the setting survives an upgrade without the MSI needing to know it existed.
/// </para>
/// </remarks>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>The value name. Stable, so setting it twice does not make two entries.</summary>
    private const string ValueName = "Agience";

    /// <summary>Is the tray registered to start at logon for this user?</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Register or unregister. Returns the reason it could not be done, or null.</summary>
    public static string? Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
                ?? throw new InvalidOperationException(@"HKCU\" + RunKey + " could not be opened");

            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return null;
            }

            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe))
            {
                return "the path of this executable could not be determined";
            }

            // Quoted: the default install directory is under Program Files, whose space would
            // otherwise split the command at "C:\Program" — an entry that does nothing every
            // logon and looks correct in the registry editor.
            key.SetValue(ValueName, "\"" + exe + "\"", RegistryValueKind.String);
            return null;
        }
        catch (Exception exc)
        {
            return exc.Message;
        }
    }
}
