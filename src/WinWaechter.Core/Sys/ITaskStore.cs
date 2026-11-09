namespace WinWaechter.Core.Sys;

// UserId: the account the task runs as (principal), as stored by Task Scheduler (SID or name); null if unknown.
public sealed record TaskInfo(string Path, string Name, bool Enabled, bool Hidden, string? ActionExe, string? ActionArgs, string? Author,
    string? UserId = null);

public interface ITaskStore
{
    IReadOnlyList<TaskInfo> List();
    TaskInfo? Get(string path);
    string ExportXml(string path);
    void SetEnabled(string path, bool enabled);
    void RegisterXml(string path, string xml);
}
