namespace WinWaechter.Core.Sys;

public enum StartMode { Automatic, Manual, Disabled, Boot, System }

public sealed record ServiceInfo(string Name, string DisplayName, StartMode Mode, bool DelayedStart,
    bool Running, string? ImagePath, string? ServiceDll);

public interface IServiceControl
{
    IReadOnlyList<ServiceInfo> List();
    ServiceInfo? Get(string name);
    void SetStartMode(string name, StartMode mode, bool delayed);
}
