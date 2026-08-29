using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace ClaudeWatch;

/// <summary>
/// Startup guard: if something is already listening on the app's ports (an orphaned instance
/// from a previous session, a stray `dotnet run`), kill it so the round pipeline owns the port
/// and the build isn't blocked by locked output DLLs.
/// </summary>
public static class OrphanKiller
{
    public static void KillListenersOn(IReadOnlyList<int> ports)
    {
        if (ports.Count == 0 || !OperatingSystem.IsWindows()) return;
        foreach (var (port, pid) in GetListeners().Where(l => ports.Contains(l.Port)).DistinctBy(l => l.Pid))
        {
            if (pid == Environment.ProcessId) continue;
            try
            {
                using var process = Process.GetProcessById(pid);
                Log.Warn($"killing orphaned listener on port {port}: {process.ProcessName} (pid {pid})");
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            catch (ArgumentException) { } // already gone
            catch (Exception ex)
            {
                Log.Warn($"could not kill pid {pid} on port {port}: {ex.Message}");
            }
        }
    }

    private static List<(int Port, int Pid)> GetListeners()
    {
        var results = new List<(int, int)>();
        foreach (var ipv6 in new[] { false, true })
        {
            var af = ipv6 ? AF_INET6 : AF_INET;
            var size = 0;
            GetExtendedTcpTable(0, ref size, false, af, TCP_TABLE_OWNER_PID_LISTENER, 0);
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buffer, ref size, false, af, TCP_TABLE_OWNER_PID_LISTENER, 0) != 0)
                    continue;
                var rowCount = Marshal.ReadInt32(buffer);
                var rowPtr = buffer + sizeof(int);
                var rowSize = ipv6 ? 56 : Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
                for (var i = 0; i < rowCount; i++, rowPtr += rowSize)
                {
                    // dwLocalPort offset: 8 (v4: after state+addr), 20 (v6: after addr[16]+scope)
                    var portOffset = ipv6 ? 20 : 8;
                    var pidOffset = ipv6 ? 52 : 20;
                    var rawPort = (uint)Marshal.ReadInt32(rowPtr + portOffset);
                    var port = (ushort)IPAddress.NetworkToHostOrder((short)rawPort);
                    var pid = Marshal.ReadInt32(rowPtr + pidOffset);
                    results.Add((port, pid));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return results;
    }

    private const int AF_INET = 2;
    private const int AF_INET6 = 23;
    private const int TCP_TABLE_OWNER_PID_LISTENER = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetExtendedTcpTable(
        nint pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, uint reserved);
}
