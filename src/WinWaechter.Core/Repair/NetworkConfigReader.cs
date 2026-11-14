using System.Management;
using Microsoft.Win32;
using WinWaechter.Core.Logging;

namespace WinWaechter.Core.Repair;

/// <summary>One active network adapter: a fixed IP (no DHCP) and/or fixed DNS servers are lost by a full reset.</summary>
public sealed record AdapterConfig(string Name, bool StaticIp, IReadOnlyList<string> Ips, bool StaticDns, IReadOnlyList<string> Dns);

public interface INetworkConfig
{
    IReadOnlyList<AdapterConfig> Read();
}

/// <summary>
/// The TCP/IP settings of every interface from the registry – also of adapters that are disconnected right now
/// (dock, Bluetooth), whose fixed settings a full reset deletes as well. Never throws.
/// </summary>
public sealed class RegistryNetworkConfig(Sys.IRegistry reg) : INetworkConfig
{
    const string V4 = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";
    const string V6 = @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters\Interfaces";
    const string Names = @"SYSTEM\CurrentControlSet\Control\Network\{4D36E972-E325-11CE-BFC1-08002BE10318}";

    static IReadOnlyList<string> Split(object? v) => v switch
    {
        string s => s.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        string[] a => a.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToList(),
        _ => [],
    };

    public IReadOnlyList<AdapterConfig> Read()
    {
        var list = new List<AdapterConfig>();
        try
        {
            foreach (var id in reg.SubKeyNames(Sys.Hive.LocalMachine, V4))
            {
                var key = $@"{V4}\{id}";
                var staticIp = reg.Get(Sys.Hive.LocalMachine, key, "EnableDHCP") is int dhcp && dhcp == 0;
                var ips = Split(reg.Get(Sys.Hive.LocalMachine, key, "IPAddress")).Where(a => a != "0.0.0.0").ToList();
                var dns = Split(reg.Get(Sys.Hive.LocalMachine, key, "NameServer"))
                    .Concat(Split(reg.Get(Sys.Hive.LocalMachine, $@"{V6}\{id}", "NameServer"))).ToList();
                var name = reg.Get(Sys.Hive.LocalMachine, $@"{Names}\{id}\Connection", "Name") as string;
                list.Add(new AdapterConfig(string.IsNullOrWhiteSpace(name) ? id : name, staticIp && ips.Count > 0, ips, dns.Count > 0, dns));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            FileLog.Warn("Netzwerkeinstellungen nicht lesbar", ex);
        }
        return list;
    }
}

/// <summary>Win32_NetworkAdapterConfiguration (IPEnabled) plus the interface's NameServer value. Never throws.</summary>
public sealed class WmiNetworkConfig : INetworkConfig
{
    public IReadOnlyList<AdapterConfig> Read()
    {
        var list = new List<AdapterConfig>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Description, DHCPEnabled, IPAddress, DNSServerSearchOrder, SettingID FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE");
            foreach (ManagementBaseObject o in searcher.Get())
            {
                using (o)
                {
                    var id = o["SettingID"] as string;
                    var dnsFixed = id is not null && FixedDns(id);
                    list.Add(new AdapterConfig(
                        o["Description"] as string ?? "Netzwerkadapter",
                        o["DHCPEnabled"] is false,
                        (o["IPAddress"] as string[] ?? []).Where(a => !a.Contains(':')).ToList(),
                        dnsFixed,
                        dnsFixed ? (o["DNSServerSearchOrder"] as string[] ?? []).ToList() : []));
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            FileLog.Warn("Netzwerkeinstellungen nicht lesbar", ex);
        }
        return list;
    }

    static bool FixedDns(string settingId)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{settingId}");
        return key?.GetValue("NameServer") is string s && s.Trim().Length > 0;
    }
}
