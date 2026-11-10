using System.Diagnostics;
using WinWaechter.Core.Sys.Native;

namespace WinWaechter.Core.Sys;

/// <summary>A running process; Name includes ".exe", e.g. "msedge.exe". Pid is 0 when unknown.</summary>
public sealed record RunningProcess(string Name, string? ExePath, int Pid = 0);

public interface IProcessList { IReadOnlyList<RunningProcess> Snapshot(); }

/// <summary>Real process list. Never throws; ExePath is null when it cannot be read.</summary>
public sealed class WinProcessList : IProcessList
{
    public IReadOnlyList<RunningProcess> Snapshot()
    {
        var result = new List<RunningProcess>();
        Process[] all;
        try { all = Process.GetProcesses(); }
        catch (Exception) { return result; }

        foreach (var p in all)
        {
            try
            {
                result.Add(new RunningProcess(p.ProcessName + ".exe", PathOf(p.Id), p.Id));
            }
            catch (Exception)
            {
                // process ended meanwhile: skip it
            }
            finally
            {
                p.Dispose();
            }
        }
        return result;
    }

    internal static string? PathOf(int pid)
    {
        try
        {
            using var h = UserNative.OpenProcess(UserNative.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h.IsInvalid) return null;
            var buf = new char[32768];
            var size = (uint)buf.Length;
            return UserNative.QueryFullProcessImageNameW(h, 0, buf, ref size) ? new string(buf, 0, (int)size) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
