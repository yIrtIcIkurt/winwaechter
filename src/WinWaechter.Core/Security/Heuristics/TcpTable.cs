using System.Net;
using System.Runtime.InteropServices;
using WinWaechter.Core.Logging;

namespace WinWaechter.Core.Security.Heuristics;

public sealed record TcpConn(int Pid, string RemoteAddress, int RemotePort, string State);

public interface IConnectionSource
{
    IReadOnlyList<TcpConn> Established();
}

/// <summary>Established TCP connections (IPv4 and IPv6) with owning process via GetExtendedTcpTable.</summary>
public sealed class TcpTableConnections : IConnectionSource
{
    public IReadOnlyList<TcpConn> Established()
    {
        var list = new List<TcpConn>();
        try
        {
            Read(TcpNative.AfInet, 24, list);
            Read(TcpNative.AfInet6, 56, list);
        }
        catch (Exception ex) { FileLog.Warn("TCP-Tabelle nicht lesbar", ex); }
        return list;
    }

    static void Read(int family, int rowSize, List<TcpConn> into)
    {
        var size = 0;
        var buf = IntPtr.Zero;
        try
        {
            var ok = false;
            for (var attempt = 0; attempt < 5 && !ok; attempt++)
            {
                var rc = TcpNative.GetExtendedTcpTable(buf, ref size, false, family, TcpNative.TcpTableOwnerPidAll, 0);
                if (rc == 0) { ok = buf != IntPtr.Zero; if (!ok) break; continue; }
                if (rc != TcpNative.ErrorInsufficientBuffer) return;
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
                buf = Marshal.AllocHGlobal(size);
            }
            if (!ok) return;
            var count = Marshal.ReadInt32(buf);
            for (var i = 0; i < count; i++)
            {
                var row = buf + 4 + i * rowSize;
                var c = family == TcpNative.AfInet ? ParseV4(row) : ParseV6(row);
                if (c is not null) into.Add(c);
            }
        }
        finally { if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf); }
    }

    // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, pid
    static TcpConn? ParseV4(IntPtr row)
    {
        if (Marshal.ReadInt32(row) != TcpNative.StateEstablished) return null;
        var addr = new IPAddress((uint)Marshal.ReadInt32(row, 12));
        return new TcpConn(Marshal.ReadInt32(row, 20), addr.ToString(),
            PortFromNetwork((uint)Marshal.ReadInt32(row, 16)), "Established");
    }

    // MIB_TCP6ROW_OWNER_PID: localAddr[16], localScope, localPort, remoteAddr[16], remoteScope, remotePort, state, pid
    static TcpConn? ParseV6(IntPtr row)
    {
        if (Marshal.ReadInt32(row, 48) != TcpNative.StateEstablished) return null;
        var bytes = new byte[16];
        Marshal.Copy(row + 24, bytes, 0, 16);
        var addr = new IPAddress(bytes, (uint)Marshal.ReadInt32(row, 40));
        return new TcpConn(Marshal.ReadInt32(row, 52), addr.ToString(),
            PortFromNetwork((uint)Marshal.ReadInt32(row, 44)), "Established");
    }

    /// <summary>The port sits in the low 16 bits in network byte order.</summary>
    internal static int PortFromNetwork(uint raw) => (int)(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF));
}

internal static class TcpNative
{
    public const int AfInet = 2, AfInet6 = 23, TcpTableOwnerPidAll = 5, StateEstablished = 5, ErrorInsufficientBuffer = 122;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    public static extern int GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool sort,
        int family, int tableClass, int reserved);
}
