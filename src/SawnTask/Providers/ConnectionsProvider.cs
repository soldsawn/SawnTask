using System.Net;
using System.Runtime.InteropServices;
using SawnTask.Core;
using static SawnTask.Native.NativeMethods;

namespace SawnTask.Providers;

// The Connections page. GetExtendedTcpTable and GetExtendedUdpTable
// from IPHLPAPI: one call per protocol and address family returns every socket together
// with the PID that owns it. No admin rights needed.
public sealed class ConnectionsProvider : IMetricProvider
{
    static readonly string[] TcpStates =
        ["?", "Closed", "Listening", "SYN sent", "SYN received", "Established", "FIN wait 1", "FIN wait 2",
         "Close wait", "Closing", "Last ACK", "Time wait", "Delete TCB"];

    static readonly Dictionary<int, string> Services = new()
    {
        [20] = "FTP-data", [21] = "FTP", [22] = "SSH", [25] = "SMTP", [53] = "DNS", [80] = "HTTP", [110] = "POP3",
        [135] = "RPC", [137] = "NetBIOS", [139] = "NetBIOS", [143] = "IMAP", [443] = "HTTPS", [445] = "SMB",
        [465] = "SMTPS", [587] = "SMTP", [631] = "IPP", [993] = "IMAPS", [995] = "POP3S", [1900] = "SSDP",
        [3389] = "RDP", [5353] = "mDNS", [5355] = "LLMNR", [8080] = "HTTP-alt",
    };

    public string Id => "windows.network.connections";
    public string Description => "GetExtendedTcpTable / GetExtendedUdpTable (IPv4 + IPv6) with the owning PID";
    public string Detail { get; private set; } = "";

    public void Collect(Snapshot s)
    {
        // The process provider runs earlier in the same tick, so PIDs can be named now.
        var names = new Dictionary<uint, string>();
        foreach (var p in s.Processes) names.TryAdd(p.Key.Pid, p.Name);

        var list = new List<ConnectionItem>();
        ReadTcp4(list);
        ReadTcp6(list);
        ReadUdp(list, AF_INET, rowSize: 12, addressBytes: 4);
        ReadUdp(list, AF_INET6, rowSize: 28, addressBytes: 16);

        s.Connections = list
            .Select(c => c with { Process = names.GetValueOrDefault(c.Pid, c.Pid == 0 ? "System Idle Process" : "?") })
            .OrderBy(c => c.State == "Established" ? 0 : c.State == "Listening" ? 1 : 2)
            .ThenBy(c => c.Process, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Detail = $"{s.Connections.Count(c => c.State == "Established")} established, {s.Connections.Count(c => c.State == "Listening")} listening, {list.Count} total";
    }

    // Both "get size, allocate, get table" calls follow the usual Windows pattern.
    static IntPtr Query(Func<IntPtr, uint, (uint Status, uint Size)> call, out uint size)
    {
        size = 0;
        (_, size) = call(IntPtr.Zero, 0);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            size += 4096; // the table can grow between the two calls
            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            var (status, needed) = call(buffer, size);
            if (status == 0) return buffer;
            Marshal.FreeHGlobal(buffer);
            size = needed;
        }
        throw new InvalidOperationException("socket table kept growing while being read");
    }

    static void ReadTcp4(List<ConnectionItem> list)
    {
        IntPtr table = Query((buf, len) => { uint s = len; return (GetExtendedTcpTable(buf, ref s, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0), s); }, out _);
        try
        {
            // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, pid (6 x uint32).
            int count = Marshal.ReadInt32(table);
            for (int i = 0; i < count; i++)
            {
                IntPtr row = table + 4 + i * 24;
                uint state = (uint)Marshal.ReadInt32(row, 0);
                var local = new IPAddress((uint)Marshal.ReadInt32(row, 4));
                int localPort = Port(Marshal.ReadInt32(row, 8));
                var remote = new IPAddress((uint)Marshal.ReadInt32(row, 12));
                int remotePort = Port(Marshal.ReadInt32(row, 16));
                uint pid = (uint)Marshal.ReadInt32(row, 20);
                list.Add(Tcp("TCP4", state, local, localPort, remote, remotePort, pid));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(table);
        }
    }

    static void ReadTcp6(List<ConnectionItem> list)
    {
        IntPtr table = Query((buf, len) => { uint s = len; return (GetExtendedTcpTable(buf, ref s, false, AF_INET6, TCP_TABLE_OWNER_PID_ALL, 0), s); }, out _);
        try
        {
            // MIB_TCP6ROW_OWNER_PID: localAddr[16], scope, port, remoteAddr[16], scope, port, state, pid = 56 bytes.
            int count = Marshal.ReadInt32(table);
            for (int i = 0; i < count; i++)
            {
                IntPtr row = table + 4 + i * 56;
                var local = new IPAddress(Bytes(row, 0, 16));
                int localPort = Port(Marshal.ReadInt32(row, 20));
                var remote = new IPAddress(Bytes(row, 24, 16));
                int remotePort = Port(Marshal.ReadInt32(row, 44));
                uint state = (uint)Marshal.ReadInt32(row, 48);
                uint pid = (uint)Marshal.ReadInt32(row, 52);
                list.Add(Tcp("TCP6", state, local, localPort, remote, remotePort, pid));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(table);
        }
    }

    static void ReadUdp(List<ConnectionItem> list, int family, int rowSize, int addressBytes)
    {
        IntPtr table = Query((buf, len) => { uint s = len; return (GetExtendedUdpTable(buf, ref s, false, family, UDP_TABLE_OWNER_PID, 0), s); }, out _);
        try
        {
            // MIB_UDPROW_OWNER_PID: addr, port, pid. MIB_UDP6ROW_OWNER_PID: addr[16], scope, port, pid.
            int count = Marshal.ReadInt32(table);
            for (int i = 0; i < count; i++)
            {
                IntPtr row = table + 4 + i * rowSize;
                var address = new IPAddress(Bytes(row, 0, addressBytes));
                int portOffset = family == AF_INET ? 4 : 20;
                int port = Port(Marshal.ReadInt32(row, portOffset));
                uint pid = (uint)Marshal.ReadInt32(row, portOffset + 4);
                list.Add(new ConnectionItem(family == AF_INET ? "UDP4" : "UDP6", "Datagram", Endpoint(address, port), Fmt.Missing,
                    Services.GetValueOrDefault(port, ""), ListenExposure(address), pid, ""));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(table);
        }
    }

    static ConnectionItem Tcp(string protocol, uint state, IPAddress local, int localPort, IPAddress remote, int remotePort, uint pid)
    {
        string stateText = state < TcpStates.Length ? TcpStates[state] : state.ToString();
        bool listening = state == 2;
        int servicePort = listening ? localPort : remotePort;
        return new ConnectionItem(protocol, stateText, Endpoint(local, localPort),
            listening ? Fmt.Missing : Endpoint(remote, remotePort),
            Services.GetValueOrDefault(servicePort, ""),
            listening ? ListenExposure(local) : RemoteExposure(remote), pid, "");
    }

    // Ports are stored in network byte order in the low 16 bits.
    static int Port(int raw) => ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);

    static byte[] Bytes(IntPtr row, int offset, int length)
    {
        var bytes = new byte[length];
        Marshal.Copy(row + offset, bytes, 0, length);
        return bytes;
    }

    static string Endpoint(IPAddress address, int port) =>
        address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{address}]:{port}" : $"{address}:{port}";

    // The "Exposure" column: who can reach this socket.
    static string ListenExposure(IPAddress address) =>
        IPAddress.IsLoopback(address) ? "Loopback"
        : address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) ? "All interfaces"
        : "One interface";

    static string RemoteExposure(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return "Loopback";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        byte[] b = address.GetAddressBytes();
        bool local = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254)
            : address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || (b[0] & 0xFE) == 0xFC;
        return local ? "Local network" : "Internet";
    }
}
