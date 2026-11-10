using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Programs;

public enum ProgramFlag { None, Protected, Pua, Miner }

/// <summary>One visible uninstall entry. RootKey + KeyName is the registry location inside Hive.</summary>
public sealed record InstalledProgram(Hive Hive, string RootKey, string KeyName, string DisplayName, string? Publisher,
    string? Version, DateTime? InstallDate, long? SizeBytes, string? UninstallString, string? InstallLocation,
    string? DisplayIcon, bool WindowsInstaller, ProgramFlag Flag)
{
    public string RegistryKey => RootKey + "\\" + KeyName;

    public string Id { get; } = $"{(Hive == Hive.CurrentUser ? "HKCU" : "HKLM")}\\{RootKey}\\{KeyName}";

    public bool IsMachineEntry => Hive == Hive.LocalMachine;
}
