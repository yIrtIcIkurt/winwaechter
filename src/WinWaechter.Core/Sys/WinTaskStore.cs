using System.Runtime.InteropServices;
using Microsoft.Win32.TaskScheduler;
using WinWaechter.Core.Logging;

namespace WinWaechter.Core.Sys;

/// <summary>Windows Task Scheduler via the TaskScheduler library. Paths look like "\Folder\Name".</summary>
public sealed class WinTaskStore : ITaskStore
{
    static TaskService Service => TaskService.Instance;

    public IReadOnlyList<TaskInfo> List()
    {
        var list = new List<TaskInfo>();
        Walk(Service.RootFolder, list);
        return list;
    }

    public TaskInfo? Get(string path)
    {
        using var t = Service.GetTask(path);
        return t is null ? null : ToInfo(t);
    }

    public string ExportXml(string path)
    {
        using var t = Find(path);
        return t.Xml;
    }

    public void SetEnabled(string path, bool enabled)
    {
        using var t = Find(path);
        t.Enabled = enabled; // persisted immediately by the library
    }

    // Registers with the principal stored in the XML (same user/logon type as before).
    public void RegisterXml(string path, string xml)
    {
        var cut = path.LastIndexOf('\\');
        var folderPath = cut <= 0 ? @"\" : path[..cut];
        var name = path[(cut + 1)..];

        using var folder = Service.GetFolder(folderPath) ?? Service.RootFolder.CreateFolder(folderPath.TrimStart('\\'));
        using var td = Service.NewTask();
        td.XmlText = xml;
        using var _ = folder.RegisterTaskDefinition(name, td);
    }

    static Microsoft.Win32.TaskScheduler.Task Find(string path) =>
        Service.GetTask(path) ?? throw new InvalidOperationException($"Aufgabe {path} nicht gefunden.");

    // Recursive; folders or tasks we may not read are skipped.
    static void Walk(TaskFolder folder, List<TaskInfo> list)
    {
        try
        {
            foreach (var t in folder.Tasks)
            {
                using (t)
                {
                    try { list.Add(ToInfo(t)); }
                    catch (Exception ex) when (IsAccessProblem(ex)) { }
                }
            }
            foreach (var sub in folder.SubFolders)
            {
                using (sub) Walk(sub, list);
            }
        }
        catch (Exception ex) when (IsAccessProblem(ex))
        {
            FileLog.Warn($"Aufgabenordner übersprungen: {folder.Path}", ex);
        }
    }

    static bool IsAccessProblem(Exception ex) =>
        ex is UnauthorizedAccessException or COMException or FileNotFoundException;

    static TaskInfo ToInfo(Microsoft.Win32.TaskScheduler.Task t)
    {
        var def = t.Definition;
        var exec = def.Actions.OfType<ExecAction>().FirstOrDefault();
        return new TaskInfo(t.Path, t.Name, t.Enabled, def.Settings.Hidden,
            exec?.Path, string.IsNullOrEmpty(exec?.Arguments) ? null : exec.Arguments,
            def.RegistrationInfo.Author);
    }
}
