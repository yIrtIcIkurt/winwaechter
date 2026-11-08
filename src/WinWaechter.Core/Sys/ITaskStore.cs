namespace WinWaechter.Core.Sys;

public sealed record TaskInfo(string Path, string Name, bool Enabled, bool Hidden, string? ActionExe, string? ActionArgs, string? Author);

public interface ITaskStore
{
    IReadOnlyList<TaskInfo> List();
    TaskInfo? Get(string path);
    string ExportXml(string path);
    void SetEnabled(string path, bool enabled);
    void RegisterXml(string path, string xml);
}
