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
