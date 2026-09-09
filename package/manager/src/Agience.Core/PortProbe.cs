using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace Agience.Core;

/// <summary>
/// Who holds a port, and whether it is ours.
/// </summary>
/// <remarks>
/// <para>
/// "The port is bound" is not "the service is up", and the difference has cost real time.
/// On node 71 two abandoned processes once held :80 and :443 and the status surface reported
/// caddy up, serving the squatter's 200. A port answering is only evidence about the port. This
/// class answers the second question — which process holds it — so a stranger on our port is
/// reported as a stranger rather than as health.
/// </para>
/// <para>
/// The owning PID is read from the TCP table, not inferred from the process we started. Those
/// disagree exactly when it matters: after a crash the tray's handle is dead and the port is still
/// held, which is the state that makes the next start fail.
/// </para>
/// </remarks>
public static class PortProbe
{
    /// <summary>Is anything listening on this loopback port?</summary>
    public static bool IsBound(int port)
    {
        try
        {
            foreach (var endpoint in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
            {
                if (endpoint.Port == port)
                {
                    return true;
                }
            }
        }
        catch (NetworkInformationException)
        {
            // The table can be momentarily unavailable. Reporting "not bound" would be a specific
            // claim from no evidence; the caller treats false as "no answer yet" and asks again.
        }

        return false;
    }

    /// <summary>
    /// The process id listening on a port, or null if nothing is.
    /// </summary>
    /// <remarks>
    /// Returns null rather than throwing on every failure path. A port whose owner cannot be
    /// determined must not become a service reported as foreign — that is an accusation, and it
    /// sends a person to kill a process.
    /// </remarks>
    public static int? OwnerPid(int port)
    {
        var buffer = IntPtr.Zero;
        try
        {
            var size = 0;
            // First call sizes the buffer; ERROR_INSUFFICIENT_BUFFER (122) is the expected answer.
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidListener, 0);
            if (size <= 0)
            {
                return null;
            }

            buffer = Marshal.AllocHGlobal(size);
            if (GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidListener, 0) != 0)
            {
                return null;
            }

            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<TcpRowOwnerPid>();
            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<TcpRowOwnerPid>(buffer + sizeof(int) + (i * rowSize));

                // The port is big-endian in the low two bytes. Reading it as a plain int gives a
                // number in the tens of millions that matches nothing, so every port reads as free
                // and no squatter is ever found.
                var rowPort = IPAddress.NetworkToHostOrder((short)(row.LocalPort & 0xFFFF)) & 0xFFFF;
                if (rowPort == port)
                {
                    return (int)row.OwningPid;
                }
            }
        }
        catch (Exception)
        {
            // Fall through: unknown, not free.
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return null;
    }

    private const int AfInet = 2;
    private const int TcpTableOwnerPidListener = 3;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order,
                                                   int family, int tableClass, int reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }
}
