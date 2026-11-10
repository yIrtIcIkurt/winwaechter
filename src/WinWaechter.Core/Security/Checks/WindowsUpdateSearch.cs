using System.Reflection;
using System.Runtime.InteropServices;
using WinWaechter.Core.Logging;

namespace WinWaechter.Core.Security.Checks;

/// <summary>Pending = null when unknown; LastSearchUtc = last successful Windows search.</summary>
public sealed record WindowsUpdateState(int? Pending, DateTime? LastSearchUtc, bool TimedOut);

public interface IWindowsUpdateSearch
{
    WindowsUpdateState Search(TimeSpan timeout);
}

/// <summary>
/// Counts pending updates from the Windows Update Agent cache (Online=false: no network, no download, no install).
/// Runs on its own MTA thread and aborts the search after the timeout. Never throws.
/// </summary>
public sealed class WuaUpdateSearch : IWindowsUpdateSearch
{
    public const string Criteria = "IsInstalled=0 and IsHidden=0 and Type='Software' and BrowseOnly=0";
    static readonly WindowsUpdateState Unknown = new(null, null, false);

    public WindowsUpdateState Search(TimeSpan timeout)
    {
        var result = Unknown;
        var worker = new Thread(() => result = Run(timeout)) { IsBackground = true, Name = "WinWaechter WU-Suche" };
        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
        // COM creation itself could hang; give it a little extra before giving up.
        return worker.Join(timeout + TimeSpan.FromSeconds(5)) ? result : new(null, null, true);
    }

    static WindowsUpdateState Run(TimeSpan timeout)
    {
        var lastSearch = LastSearchUtc();
        object? session = null, searcher = null, job = null;
        try
        {
            session = Create("Microsoft.Update.Session");
            searcher = Invoke(session, "CreateUpdateSearcher");
            searcher!.GetType().InvokeMember("Online", BindingFlags.SetProperty, null, searcher, [false]);
            job = BeginSearch(searcher);

            var deadline = DateTime.UtcNow + timeout;
            while (!Convert.ToBoolean(Get(job, "IsCompleted")))
            {
                if (DateTime.UtcNow >= deadline)
                {
                    try { Invoke(job, "RequestAbort"); } catch (Exception) { /* already finished or agent gone: still a timeout */ }
                    return new(null, lastSearch, true);
                }
                Thread.Sleep(250);
            }

            var res = Invoke(searcher, "EndSearch", job);
            try
            {
                var updates = Get(res!, "Updates");
                try { return new(Convert.ToInt32(Get(updates!, "Count")), lastSearch, false); }
                finally { Release(updates); }
            }
            finally { Release(res); }
        }
        catch (Exception ex)
        {
            FileLog.Warn("Windows-Update-Suche fehlgeschlagen", ex);
            return Unknown;
        }
        finally
        {
            Release(job);
            Release(searcher);
            Release(session);
        }
    }

    static object BeginSearch(object searcher)
    {
        try { return Invoke(searcher, "BeginSearch", Criteria, null, null)!; }
        catch (Exception ex) when (ex is COMException or ArgumentException or TargetInvocationException)
        {
            // Some agents reject a null callback; hand over a no-op one and keep polling.
            return Invoke(searcher, "BeginSearch", Criteria, new NoOpCallback(), null)!;
        }
    }

    static DateTime? LastSearchUtc()
    {
        object? au = null, results = null;
        try
        {
            au = Create("Microsoft.Update.AutoUpdate");
            results = Get(au, "Results");
            return Get(results!, "LastSearchSuccessDate") is DateTime d && d.Year > 1900
                ? DateTime.SpecifyKind(d, DateTimeKind.Utc)
                : null;
        }
        catch (Exception ex)
        {
            FileLog.Warn("Zeitpunkt der letzten Windows-Suche nicht lesbar", ex);
            return null;
        }
        finally
        {
            Release(results);
            Release(au);
        }
    }

    static object Create(string progId) =>
        Activator.CreateInstance(Type.GetTypeFromProgID(progId, throwOnError: true)!)
        ?? throw new InvalidOperationException($"{progId} nicht verfügbar.");

    static object? Invoke(object o, string method, params object?[] args) =>
        o.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, o, args);

    static object? Get(object o, string prop) =>
        o.GetType().InvokeMember(prop, BindingFlags.GetProperty, null, o, null);

    static void Release(object? o)
    {
        if (o is not null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o);
    }

    // ISearchCompletedCallback; the search result is fetched by polling instead.
    [ComVisible(true), Guid("88AEE058-D4B0-4725-A2F1-814A67AE964C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ISearchCompletedCallback
    {
        void Invoke([MarshalAs(UnmanagedType.IUnknown)] object searchJob, [MarshalAs(UnmanagedType.IUnknown)] object callbackArgs);
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class NoOpCallback : ISearchCompletedCallback
    {
        public void Invoke(object searchJob, object callbackArgs) { }
    }
}
