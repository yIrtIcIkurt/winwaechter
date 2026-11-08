using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using WinWaechter.Core.Logging;
using static WinWaechter.Core.Sys.Native.ServiceNative;

namespace WinWaechter.Core.Sys;

/// <summary>
/// Win32 services (no drivers). Config comes from HKLM\SYSTEM\CurrentControlSet\Services,
/// display name and run state from the service control manager.
/// </summary>
public sealed class WinServiceControl : IServiceControl
{
    const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";
    const int Win32ServiceTypes = 0x10 | 0x20; // SERVICE_WIN32_OWN_PROCESS | SERVICE_WIN32_SHARE_PROCESS

    public IReadOnlyList<ServiceInfo> List()
    {
        using var scm = OpenScm();
        using var services = OpenServicesKey();
        var list = new List<ServiceInfo>();
        foreach (var name in services.GetSubKeyNames())
        {
            try
            {
                var info = Read(scm, services, name);
                if (info is not null) list.Add(info);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
            {
                FileLog.Warn($"Dienst übersprungen (kein Zugriff): {name}", ex);
            }
        }
        return list;
    }

    public ServiceInfo? Get(string name)
    {
        using var scm = OpenScm();
        using var services = OpenServicesKey();
        return Read(scm, services, name);
    }

    public void SetStartMode(string name, StartMode mode, bool delayed)
    {
        using var scm = OpenScm();
        using var svc = OpenService(scm, name, SERVICE_CHANGE_CONFIG | SERVICE_QUERY_CONFIG);
        if (svc.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), $"Dienst {name} kann nicht geöffnet werden.");

        // Clear the delayed flag while the service is still automatic; ignored otherwise.
        if (mode != StartMode.Automatic) SetDelayed(svc, false);

        if (!ChangeServiceConfig(svc, SERVICE_NO_CHANGE, ToStartType(mode), SERVICE_NO_CHANGE,
                null, null, IntPtr.Zero, null, null, null, null))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Starttyp von {name} konnte nicht geändert werden.");

        if (mode == StartMode.Automatic && !SetDelayed(svc, delayed))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Verzögerter Start von {name} konnte nicht gesetzt werden.");
    }

    static bool SetDelayed(SafeServiceHandle svc, bool delayed)
    {
        var info = new SERVICE_DELAYED_AUTO_START_INFO { fDelayedAutostart = delayed ? 1 : 0 };
        return ChangeServiceConfig2(svc, SERVICE_CONFIG_DELAYED_AUTO_START_INFO, ref info);
    }

    static uint ToStartType(StartMode m) => m switch
    {
        StartMode.Automatic => SERVICE_AUTO_START,
        StartMode.Manual => SERVICE_DEMAND_START,
        StartMode.Disabled => SERVICE_DISABLED,
        StartMode.Boot => SERVICE_BOOT_START,
        StartMode.System => SERVICE_SYSTEM_START,
        _ => throw new ArgumentOutOfRangeException(nameof(m)),
    };

    static StartMode? FromStart(int start) => start switch
    {
        0 => StartMode.Boot,
        1 => StartMode.System,
        2 => StartMode.Automatic,
        3 => StartMode.Manual,
        4 => StartMode.Disabled,
        _ => null,
    };

    static ServiceInfo? Read(SafeServiceHandle scm, RegistryKey services, string name)
    {
        using var k = services.OpenSubKey(name);
        if (k is null) return null;
        if (k.GetValue("Type") is not int type || (type & Win32ServiceTypes) == 0) return null;
        if (k.GetValue("Start") is not int start || FromStart(start) is not { } mode) return null;

        var delayed = mode == StartMode.Automatic && k.GetValue("DelayedAutostart") is int d && d != 0;
        var image = k.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        string? dll;
        using (var p = k.OpenSubKey("Parameters"))
            dll = p?.GetValue("ServiceDll", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;

        var display = DisplayName(scm, name) ?? k.GetValue("DisplayName") as string ?? name;
        return new ServiceInfo(name, display, mode, delayed, IsRunning(scm, name), image, dll);
    }

    // SCM resolves indirect names like "@%SystemRoot%\system32\x.dll,-100".
    static string? DisplayName(SafeServiceHandle scm, string name)
    {
        uint len = 256;
        var sb = new StringBuilder((int)len + 1);
        if (GetServiceDisplayName(scm, name, sb, ref len)) return sb.ToString();
        if (len <= 256 || len > 4096) return null;
        sb = new StringBuilder((int)len + 1);
        len++;
        return GetServiceDisplayName(scm, name, sb, ref len) ? sb.ToString() : null;
    }

    static bool IsRunning(SafeServiceHandle scm, string name)
    {
        using var svc = OpenService(scm, name, SERVICE_QUERY_STATUS);
        if (svc.IsInvalid || !QueryServiceStatus(svc, out var st)) return false;
        return st.dwCurrentState is not (SERVICE_STOPPED or SERVICE_STOP_PENDING);
    }

    static SafeServiceHandle OpenScm()
    {
        var scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
        if (scm.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Dienststeuerung nicht erreichbar.");
        return scm;
    }

    static RegistryKey OpenServicesKey()
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        return hklm.OpenSubKey(ServicesKey) ?? throw new InvalidOperationException("Dienste-Schlüssel fehlt.");
    }
}
