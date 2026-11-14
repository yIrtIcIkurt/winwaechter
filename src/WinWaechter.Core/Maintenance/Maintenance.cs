using System.Globalization;
using System.Security;
using System.Text;
using System.Text.Json;
using WinWaechter.Core.Improvements;
using WinWaechter.Core.Logging;
using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Maintenance;

/// <summary>The weekly check as a scheduled task of the signed-in user, with highest rights (no UAC prompt for an administrator).</summary>
public static class MaintenanceTask
{
    public const string Path = @"\WinWaechter\Wöchentliche Prüfung";
    public const string Arguments = "--pruefen";

    /// <summary>The first date with that weekday from today on (today included); the trigger repeats it weekly.</summary>
    public static DateTime NextDate(DateTime now, DayOfWeek day)
    {
        var diff = ((int)day - (int)now.DayOfWeek + 7) % 7;
        return now.Date.AddDays(diff);
    }

    public static string Xml(string exePath, string userSid, DayOfWeek day, TimeOnly time, DateTime firstDate)
    {
        static string E(string s) => SecurityElement.Escape(s);
        var start = firstDate.Date.Add(time.ToTimeSpan()).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Author>WinWächter</Author>
                <Description>Prüft einmal pro Woche, ob etwas zu tun ist, und meldet es. Ändert nichts.</Description>
              </RegistrationInfo>
              <Triggers>
                <CalendarTrigger>
                  <StartBoundary>{start}</StartBoundary>
                  <Enabled>true</Enabled>
                  <ScheduleByWeek>
                    <DaysOfWeek>
                      <{day} />
                    </DaysOfWeek>
                    <WeeksInterval>1</WeeksInterval>
                  </ScheduleByWeek>
                </CalendarTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{E(userSid)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>true</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <ExecutionTimeLimit>PT30M</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{E(exePath)}</Command>
                  <Arguments>{Arguments}</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    public static void Register(ITaskStore store, string exePath, string userSid, DayOfWeek day, TimeOnly time, DateTime now) =>
        store.RegisterXml(Path, Xml(exePath, userSid, day, time, NextDate(now, day)));

    public static void Remove(ITaskStore store) => store.Delete(Path);
}

/// <summary>What the last weekly check found. Actions excludes the program-update suggestion (counted in Updates).</summary>
public sealed record CheckResult(DateTimeOffset At, bool Failed, int Improvements, int Actions, int Updates, bool AppUpdate,
    IReadOnlyList<string> Titles)
{
    public int Total => Improvements + Actions + Updates;
}

/// <summary>Reads and writes a result atomically (tmp + move); unreadable = null.</summary>
public static class CheckResultStore
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    static readonly object Sync = new();

    public static CheckResult? Load(string path)
    {
        // a reader can meet the moment the file is replaced (sharing violation): try again briefly
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return null;
                return JsonSerializer.Deserialize<CheckResult>(File.ReadAllText(path), Json);
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(20);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    public static void Save(string path, CheckResult r)
    {
        lock (Sync)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(r, Json));
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                FileLog.Warn("Ergebnis der Prüfung nicht gespeichert", ex);
            }
        }
    }
}

/// <summary>A notification only when something is to do, and again only when the result changed or a week passed.</summary>
public static class NotifyRule
{
    static string Signature(CheckResult r) =>
        $"{r.Improvements}|{r.Actions}|{r.Updates}|{r.AppUpdate}|{string.Join("\u001f", r.Titles.Order(StringComparer.Ordinal))}";

    public static bool ShouldNotify(CheckResult now, CheckResult? lastNotified, DateTimeOffset nowTime)
    {
        if (now.Failed || (now.Total == 0 && !now.AppUpdate)) return false;
        if (lastNotified is null) return true;
        return Signature(now) != Signature(lastNotified) || nowTime - lastNotified.At >= TimeSpan.FromDays(7);
    }
}

/// <summary>Text and XML of the Windows notification (shared with the notification helper, which links this file).</summary>
public static class ToastContent
{
    public const string Launch = "winwaechter:verbessern";

    public static string Text(CheckResult r)
    {
        var parts = new List<string>();
        var rec = r.Improvements + r.Actions;
        if (rec > 0) parts.Add(rec == 1 ? "1 Empfehlung" : $"{rec} Empfehlungen");
        if (r.Updates > 0) parts.Add(r.Updates == 1 ? "1 Programm-Update" : $"{r.Updates} Programm-Updates");
        if (r.AppUpdate) parts.Add("WinWächter-Update verfügbar");
        return string.Join(", ", parts);
    }

    public static string Xml(string title, string text)
    {
        static string E(string s) => SecurityElement.Escape(s) ?? "";
        return $"<toast launch=\"{Launch}\" activationType=\"protocol\"><visual><binding template=\"ToastGeneric\">"
               + $"<text>{E(title)}</text><text>{E(text)}</text></binding></visual>"
               + $"<actions><action content=\"Ansehen\" arguments=\"{Launch}\" activationType=\"protocol\" /></actions></toast>";
    }
}

/// <summary>Windows command-line quoting for one argument (CommandLineToArgvW rules).</summary>
public static class CommandLineArgs
{
    public static string Quote(string arg)
    {
        var sb = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') sb.Append('\\', backslashes * 2 + 1);
            else sb.Append('\\', backslashes);
            backslashes = 0;
            sb.Append(c);
        }
        sb.Append('\\', backslashes * 2);
        return sb.Append('"').ToString();
    }
}

/// <summary>
/// The headless weekly check: collects what „Rechner in Ordnung bringen“ would offer plus the counts of the other
/// sources, saves the result and asks for a notification when the rule says so. It never applies anything.
/// </summary>
public sealed class MaintenanceCheck(ImprovementCollector collector, Func<CancellationToken, Task<int>> updateCount,
    Func<CancellationToken, Task<bool>> appUpdate, string dataRoot, Action<string, string> launchNotifier, Func<DateTimeOffset> now)
{
    public const string UpdatesSuggestionId = "updates";

    public string ResultPath => System.IO.Path.Combine(dataRoot, "wartung", "letzte-pruefung.json");
    public string NotifiedPath => System.IO.Path.Combine(dataRoot, "wartung", "zuletzt-gemeldet.json");

    public async Task<CheckResult> RunAsync(CancellationToken ct)
    {
        CheckResult result;
        try
        {
            var scan = await collector.CollectAsync(null, ct).ConfigureAwait(false);
            var actions = scan.Actions.Where(a => a.Id != UpdatesSuggestionId).ToList();
            var updates = await updateCount(ct).ConfigureAwait(false);
            var app = await appUpdate(ct).ConfigureAwait(false);
            result = new CheckResult(now(), false, scan.Items.Count, actions.Count, updates, app,
                [.. scan.Items.Select(i => i.Title), .. actions.Select(a => a.Title)]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            FileLog.Error("Wöchentliche Prüfung fehlgeschlagen", ex);
            result = new CheckResult(now(), true, 0, 0, 0, false, []);
        }
        CheckResultStore.Save(ResultPath, result);
        FileLog.Info($"Wöchentliche Prüfung: {(result.Failed ? "fehlgeschlagen" : ToastContent.Text(result) is { Length: > 0 } t ? t : "alles in Ordnung")}");

        if (NotifyRule.ShouldNotify(result, CheckResultStore.Load(NotifiedPath), result.At))
        {
            try
            {
                launchNotifier("WinWächter", ToastContent.Text(result));
                CheckResultStore.Save(NotifiedPath, result);
            }
            catch (Exception ex)
            {
                FileLog.Warn("Benachrichtigung ließ sich nicht zeigen", ex);
            }
        }
        return result;
    }
}
