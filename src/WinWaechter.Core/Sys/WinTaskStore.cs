using System.Reflection;
using System.Runtime.InteropServices;
using WinWaechter.Core.Logging;

namespace WinWaechter.Core.Sys;

/// <summary>
/// Windows Task Scheduler through its own COM interface ("Schedule.Service", taskschd.dll, signed by Microsoft).
/// No third-party library: an unsigned DLL could be blocked by Smart App Control. Paths look like "\Folder\Name".
/// </summary>
public sealed class WinTaskStore : ITaskStore
{
    const int EnumHidden = 1;          // TASK_ENUM_HIDDEN
    const int CreateOrUpdate = 6;      // TASK_CREATE_OR_UPDATE
    const int ActionExec = 0;          // TASK_ACTION_EXEC

    static dynamic Connect()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        return service;
    }

    public IReadOnlyList<TaskInfo> List()
    {
        var list = new List<TaskInfo>();
        var service = Connect();
        Walk(service.GetFolder(@"\"), list);
        return list;
    }

    public TaskInfo? Get(string path)
    {
        var t = TryFind(Connect(), path);
        return t is null ? null : ToInfo(t);
    }

    public string ExportXml(string path) => (string)Find(Connect(), path).Xml;

    public void SetEnabled(string path, bool enabled)
    {
        var t = Find(Connect(), path);
        t.Enabled = enabled; // IRegisteredTask::put_Enabled is persisted immediately
    }

    // Registers with the principal stored in the XML (same user/logon type as before).
    public void RegisterXml(string path, string xml)
    {
        var (folderPath, name) = Split(path);
        var service = Connect();
        var folder = TryFolder(service, folderPath) ?? service.GetFolder(@"\").CreateFolder(folderPath.TrimStart('\\'), null);
        var def = service.NewTask(0);
        def.XmlText = xml;
        // like the old library: without <Date> Task Scheduler shows no „Erstellt“
        var info = def.RegistrationInfo;
        if (string.IsNullOrEmpty((string?)info.Date))
            info.Date = DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        var principal = def.Principal;
        var user = NullIfEmpty((string?)principal.UserId) ?? NullIfEmpty((string?)principal.GroupId);
        folder.RegisterTaskDefinition(name, def, CreateOrUpdate, user, null, (int)principal.LogonType, null);
    }

    public void Delete(string path)
    {
        var service = Connect();
        if (TryFind(service, path) is null) return;
        var (folderPath, name) = Split(path);
        var folder = TryFolder(service, folderPath);
        if (folder is not null) folder.DeleteTask(name, 0);
    }

    static (string Folder, string Name) Split(string path)
    {
        var cut = path.LastIndexOf('\\');
        return (cut <= 0 ? @"\" : path[..cut], path[(cut + 1)..]);
    }

    static dynamic Find(dynamic service, string path) =>
        TryFind(service, path) ?? throw new InvalidOperationException($"Aufgabe {path} nicht gefunden.");

    static dynamic? TryFind(dynamic service, string path)
    {
        try { return service.GetFolder(@"\").GetTask(path); }
        catch (Exception ex) when (IsNotFound(ex)) { return null; }
    }

    static dynamic? TryFolder(dynamic service, string path)
    {
        try { return service.GetFolder(path); }
        catch (Exception ex) when (IsNotFound(ex)) { return null; }
    }

    // Recursive; folders or tasks we may not read are skipped.
    static void Walk(dynamic folder, List<TaskInfo> list)
    {
        try
        {
            var tasks = folder.GetTasks(EnumHidden);
            int count = tasks.Count;
            for (var i = 1; i <= count; i++)   // COM collections are 1-based
            {
                try { list.Add(ToInfo(tasks.Item(i))); }
                catch (Exception ex) when (IsAccessProblem(ex)) { }
            }
            var subs = folder.GetFolders(0);
            int subCount = subs.Count;
            for (var i = 1; i <= subCount; i++) Walk(subs.Item(i), list);
        }
        catch (Exception ex) when (IsAccessProblem(ex))
        {
            string? at = null;
            try { at = (string)folder.Path; } catch { }
            FileLog.Warn($"Aufgabenordner übersprungen: {at}", Unwrap(ex));
        }
    }

    static TaskInfo ToInfo(dynamic t)
    {
        var def = t.Definition;
        string? exe = null, args = null;
        var actions = def.Actions;
        int count = actions.Count;
        for (var i = 1; i <= count; i++)
        {
            var a = actions.Item(i);
            if ((int)a.Type != ActionExec) continue;
            exe = (string?)a.Path;
            args = NullIfEmpty((string?)a.Arguments);
            break;
        }
        return new TaskInfo((string)t.Path, (string)t.Name, (bool)t.Enabled, (bool)def.Settings.Hidden,
            exe, args, NullIfEmpty((string?)def.RegistrationInfo.Author), NullIfEmpty((string?)def.Principal.UserId));
    }

    static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    static Exception Unwrap(Exception ex) => ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;

    static bool IsNotFound(Exception ex) =>
        Unwrap(ex) is FileNotFoundException or DirectoryNotFoundException
            or COMException { HResult: unchecked((int)0x80070002) or unchecked((int)0x80070003) };

    static bool IsAccessProblem(Exception ex) =>
        Unwrap(ex) is UnauthorizedAccessException or COMException or FileNotFoundException or DirectoryNotFoundException;
}
