using System.Text.Json;
using WinWaechter.Core.Logging;

namespace WinWaechter.Core.Actions;

/// <summary>When an action last ran and how it ended (shown as „Zuletzt: …“). Link: the way out the outcome offered.</summary>
public sealed record ActionStamp(string Id, DateTimeOffset At, bool Success, string Summary, string? Link = null);

/// <summary>
/// Keeps the last outcome per action id in a small JSON file (the log is purged after 7 days).
/// An unreadable file counts as empty.
/// </summary>
public sealed class ActionStampStore(string path)
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    readonly object _sync = new();

    public ActionStamp? Get(string id)
    {
        lock (_sync) return Load().GetValueOrDefault(id);
    }

    public void Set(ActionStamp s)
    {
        lock (_sync)
        {
            var all = Load();
            all[s.Id] = s;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(all, Json));
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                FileLog.Warn("Aktionsstand konnte nicht gespeichert werden", ex);
            }
        }
    }

    Dictionary<string, ActionStamp> Load()
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Dictionary<string, ActionStamp>>(File.ReadAllText(path), Json)
                       ?? new(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            FileLog.Warn("Aktionsstand nicht lesbar, beginne leer", ex);
        }
        return new(StringComparer.Ordinal);
    }
}
