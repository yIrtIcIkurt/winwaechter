using WinWaechter.Core.Sys;

namespace WinWaechter.Tests.Fakes;

// In-memory task store. Remove() simulates a task deleted by someone else; RegisterXml brings it back.
public sealed class FakeTaskStore : ITaskStore
{
    readonly Dictionary<string, TaskInfo> _tasks = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, TaskInfo> _removed = new(StringComparer.OrdinalIgnoreCase);

    public List<(string Path, bool Enabled)> SetEnabledCalls { get; } = [];
    public List<(string Path, string Xml)> RegisterXmlCalls { get; } = [];
    public List<string> ExportXmlCalls { get; } = [];
    public Exception? RegisterXmlThrows { get; set; }

    public FakeTaskStore Add(TaskInfo t)
    {
        _tasks[t.Path] = t;
        return this;
    }

    public FakeTaskStore Add(string path, bool enabled = true, bool hidden = false,
        string? actionExe = null, string? actionArgs = null, string? author = null, string? userId = null) =>
        Add(new TaskInfo(path, path[(path.LastIndexOf('\\') + 1)..], enabled, hidden, actionExe, actionArgs, author, userId));

    public bool Remove(string path)
    {
        if (!_tasks.Remove(path, out var t)) return false;
        _removed[path] = t;
        return true;
    }

    /// <summary>When set, List throws it.</summary>
    public Exception? ListError { get; set; }

    public IReadOnlyList<TaskInfo> List() => ListError is { } e ? throw e : _tasks.Values.ToList();

    public TaskInfo? Get(string path) => _tasks.GetValueOrDefault(path);

    public string ExportXml(string path)
    {
        ExportXmlCalls.Add(path);
        var t = Get(path) ?? throw new InvalidOperationException($"Aufgabe {path} nicht gefunden.");
        return $"<Task><Settings><Enabled>{(t.Enabled ? "true" : "false")}</Enabled></Settings><Path>{t.Path}</Path></Task>";
    }

    public void SetEnabled(string path, bool enabled)
    {
        SetEnabledCalls.Add((path, enabled));
        var t = Get(path) ?? throw new InvalidOperationException($"Aufgabe {path} nicht gefunden.");
        _tasks[path] = t with { Enabled = enabled };
    }

    // Enabled state comes from the XML, the rest from the removed task (if known).
    public void RegisterXml(string path, string xml)
    {
        RegisterXmlCalls.Add((path, xml));
        if (RegisterXmlThrows is not null) throw RegisterXmlThrows;
        var enabled = !xml.Contains("<Enabled>false</Enabled>", StringComparison.OrdinalIgnoreCase);
        var t = _removed.Remove(path, out var old) ? old : Get(path);
        _tasks[path] = (t ?? new TaskInfo(path, path[(path.LastIndexOf('\\') + 1)..], enabled, false, null, null, null))
            with { Enabled = enabled };
    }
}
