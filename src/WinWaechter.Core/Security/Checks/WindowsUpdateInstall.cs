using System.Reflection;
using System.Runtime.InteropServices;
using WinWaechter.Core.Logging;

namespace WinWaechter.Core.Security.Checks;

/// <summary>What the install step needs to know about one found update.</summary>
public sealed record UpdateFacts(string Id, string Title, bool AutoSelectOnWebSites, bool BrowseOnly, bool EulaAccepted,
    IReadOnlyList<string> Categories, int Type);

/// <param name="NeedsWindowsUpdate">Updates that want a licence acceptance; WinWächter never accepts one for you.</param>
public sealed record UpdateInstallResult(int Installed, int Failed, int NeedsWindowsUpdate, bool RebootRequired);

public interface IWindowsUpdateInstaller
{
    Task<UpdateInstallResult> InstallAsync(IProgress<string>? p, CancellationToken ct);
}

/// <summary>
/// Installs the automatic software updates of the Windows Update Agent (no optional updates, drivers or feature upgrades).
/// Runs on its own MTA thread. Never runs in tests.
/// </summary>
public sealed class WuaUpdateInstaller : IWindowsUpdateInstaller
{
    public const string Criteria = "IsInstalled=0 and IsHidden=0 and Type='Software' and BrowseOnly=0 and AutoSelectOnWebSites=1";
    const int SoftwareType = 1;
    const string FeatureUpgrades = "Upgrades";

    // Everything but the licence: what WinWächter would install if the licence were accepted.
    static bool Automatic(UpdateFacts u) =>
        u.Type == SoftwareType && u.AutoSelectOnWebSites && !u.BrowseOnly
        && !u.Categories.Contains(FeatureUpgrades, StringComparer.OrdinalIgnoreCase);

    /// <summary>The updates WinWächter installs: automatic ones whose licence is already accepted.</summary>
    public static IReadOnlyList<UpdateFacts> Select(IReadOnlyList<UpdateFacts> all) =>
        all.Where(u => Automatic(u) && u.EulaAccepted).ToList();

    /// <summary>Automatic updates that need a licence acceptance in Windows Update.</summary>
    public static int NeedingLicence(IReadOnlyList<UpdateFacts> all) => all.Count(u => Automatic(u) && !u.EulaAccepted);

    public Task<UpdateInstallResult> InstallAsync(IProgress<string>? p, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<UpdateInstallResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try { tcs.SetResult(Run(p, ct)); }
            catch (OperationCanceledException) { tcs.SetCanceled(ct); }
            catch (Exception ex) { tcs.SetException(ex); }
        }) { IsBackground = true, Name = "WinWaechter WU-Installation" };
        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
        return tcs.Task;
    }

    static UpdateInstallResult Run(IProgress<string>? p, CancellationToken ct)
    {
        object? session = null, searcher = null, res = null, found = null, wanted = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            p?.Report("Updates werden gesucht …");
            session = Create("Microsoft.Update.Session");
            searcher = Invoke(session, "CreateUpdateSearcher");
            searcher!.GetType().InvokeMember("Online", BindingFlags.SetProperty, null, searcher, [true]);
            res = Invoke(searcher, "Search", Criteria);
            found = Get(res!, "Updates");
            var count = Convert.ToInt32(Get(found!, "Count"));
            var facts = new List<UpdateFacts>();
            var objs = new List<object>();
            for (var i = 0; i < count; i++)
            {
                var u = Invoke(found!, "Item", i)!;
                objs.Add(u);
                facts.Add(ReadFacts(u));
            }
            var chosen = Select(facts);
            var needsWu = NeedingLicence(facts);
            if (chosen.Count == 0) return new(0, 0, needsWu, false);

            wanted = Create("Microsoft.Update.UpdateColl");
            foreach (var f in chosen) Invoke(wanted, "Add", objs[facts.IndexOf(f)]);

            ct.ThrowIfCancellationRequested();
            p?.Report("Updates werden geladen …");
            var downloader = Invoke(session, "CreateUpdateDownloader")!;
            try
            {
                downloader.GetType().InvokeMember("Updates", BindingFlags.SetProperty, null, downloader, [wanted]);
                Release(Invoke(downloader, "Download"));
            }
            finally { Release(downloader); }

            ct.ThrowIfCancellationRequested();
            p?.Report("Updates werden installiert …");
            var installer = Invoke(session, "CreateUpdateInstaller")!;
            object? result = null;
            try
            {
                installer.GetType().InvokeMember("Updates", BindingFlags.SetProperty, null, installer, [wanted]);
                result = Invoke(installer, "Install")!;
                var installed = 0;
                var failed = 0;
                for (var i = 0; i < chosen.Count; i++)
                {
                    var ur = Invoke(result, "GetUpdateResult", i)!;
                    try
                    {
                        // 2 = succeeded, 3 = succeeded with errors
                        if (Convert.ToInt32(Get(ur, "ResultCode")) is 2 or 3) installed++; else failed++;
                    }
                    finally { Release(ur); }
                }
                return new(installed, failed, needsWu, Convert.ToBoolean(Get(result, "RebootRequired")));
            }
            finally
            {
                Release(result);
                Release(installer);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FileLog.Warn("Windows-Update-Installation fehlgeschlagen", ex);
            throw;
        }
        finally
        {
            Release(wanted);
            Release(found);
            Release(res);
            Release(searcher);
            Release(session);
        }
    }

    static UpdateFacts ReadFacts(object u)
    {
        var cats = new List<string>();
        var coll = Get(u, "Categories");
        try
        {
            var n = Convert.ToInt32(Get(coll!, "Count"));
            for (var i = 0; i < n; i++)
            {
                var c = Invoke(coll!, "Item", i);
                try { cats.Add(Convert.ToString(Get(c!, "Name")) ?? ""); }
                finally { Release(c); }
            }
        }
        finally { Release(coll); }
        var identity = Get(u, "Identity");
        string id;
        try { id = identity is null ? "" : Convert.ToString(Get(identity, "UpdateID")) ?? ""; }
        finally { Release(identity); }
        return new(id, Convert.ToString(Get(u, "Title")) ?? "",
            Convert.ToBoolean(Get(u, "AutoSelectOnWebSites")), Convert.ToBoolean(Get(u, "BrowseOnly")),
            Convert.ToBoolean(Get(u, "EulaAccepted")), cats, Convert.ToInt32(Get(u, "Type")));
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
}
