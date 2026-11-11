using WinWaechter.Core.Performance;
using WinWaechter.Tests.Fakes;
using Xunit;

namespace WinWaechter.Tests;

public class SystemSamplerTests
{
    [Fact]
    public void Cpu_busy_from_deltas()
    {
        // kernel time includes idle: (100 + 50 - 50) / (100 + 50)
        Assert.Equal(100.0 / 150 * 100, CpuMath.Busy(new CpuTimes(100, 200, 100), new CpuTimes(150, 300, 150))!.Value, 6);
        Assert.Equal(50, CpuMath.Busy(new CpuTimes(100, 200, 100), new CpuTimes(150, 300, 100)));
        Assert.Null(CpuMath.Busy(new CpuTimes(100, 200, 100), new CpuTimes(100, 200, 100)));
    }

    [Fact]
    public void Top_groups_by_name_and_sorts()
    {
        TimeSpan s(double v) => TimeSpan.FromSeconds(v);
        var prev = new List<ProcTime>
        {
            new(0, "Idle", s(100), 0),
            new(1, "chrome", s(1), 100),
            new(2, "Chrome", s(0.5), 200),
            new(3, "code", s(1), 5000),
        };
        var now = new List<ProcTime>
        {
            new(0, "Idle", s(120), 0),
            new(1, "chrome", s(1.2), 100),
            new(2, "Chrome", s(0.7), 200),
            new(3, "code", s(1.1), 5000),
            new(99, "neu", null, 50),
        };

        var top = TopConsumers.Compute(prev, now, s(2), 1);

        Assert.DoesNotContain(top, c => c.Name == "Idle");
        Assert.Equal(["chrome", "code", "neu"], top.Select(c => c.Name.ToLowerInvariant()).ToArray());
        Assert.Equal(2, top[0].Count);
        Assert.Equal(300, top[0].MemoryBytes);
        Assert.Equal(20, top[0].CpuPercent!.Value, 6);
        Assert.Equal(5, top[1].CpuPercent!.Value, 6);
        Assert.Null(top[2].CpuPercent);
    }

    [Fact]
    public void Reused_pid_gives_no_delta()
    {
        var prev = new List<ProcTime> { new(7, "alt", TimeSpan.FromSeconds(1), 10) };
        var now = new List<ProcTime> { new(7, "neu", TimeSpan.FromSeconds(50), 20) };
        var c = Assert.Single(TopConsumers.Compute(prev, now, TimeSpan.FromSeconds(2), 1));
        Assert.Equal("neu", c.Name);
        Assert.Null(c.CpuPercent);
    }

    [Fact]
    public void Reset_drops_the_baseline()
    {
        var src = new FakeSystemSources
        {
            CpuValue = new(100, 200, 100),
            Procs = [new(1, "a", TimeSpan.FromSeconds(1), 1)],
        };
        var sampler = new SystemSampler(src, (_, _) => { });
        sampler.Next(TimeSpan.FromSeconds(2));
        src.CpuValue = new(150, 300, 100);
        src.Procs = [new(1, "a", TimeSpan.FromSeconds(2), 1)];

        sampler.Reset();
        var first = sampler.Next(TimeSpan.FromMinutes(5));
        Assert.Null(first.CpuPercent);
        Assert.Null(Assert.Single(first.Top).CpuPercent);

        src.CpuValue = new(200, 400, 100);
        Assert.Equal(50, sampler.Next(TimeSpan.FromSeconds(2)).CpuPercent);
    }

    [Fact]
    public void Failing_source_gives_null_reading_once_logged()
    {
        var src = new FakeSystemSources { ThrowCpu = true, ThrowDisk = true, Procs = [new(1, "a", null, 1)] };
        var logs = new List<string>();
        var sampler = new SystemSampler(src, (m, _) => logs.Add(m));

        var a = sampler.Next(TimeSpan.FromSeconds(2));
        var b = sampler.Next(TimeSpan.FromSeconds(2));
        var c = sampler.Next(TimeSpan.FromSeconds(2));

        Assert.Null(a.CpuPercent);
        Assert.Null(b.CpuPercent);
        Assert.Null(c.DiskPercent);
        Assert.Equal(8L << 30, c.MemoryUsedBytes);
        Assert.Equal(2, logs.Count);
    }

    [Fact]
    public void First_call_has_no_cpu_then_value()
    {
        var src = new FakeSystemSources { CpuValue = new(100, 200, 100) };
        var sampler = new SystemSampler(src, (_, _) => { });
        Assert.Null(sampler.Next(TimeSpan.FromSeconds(2)).CpuPercent);
        src.CpuValue = new(150, 300, 100);
        Assert.Equal(50, sampler.Next(TimeSpan.FromSeconds(2)).CpuPercent);
    }

    [Fact]
    public void Disk_busy_from_idle_and_query_time()
    {
        Assert.Equal(50, DiskMath.Busy(1000, 2000, 1050, 2100));
        Assert.Equal(0, DiskMath.Busy(0, 0, 100, 100));
        Assert.Equal(100, DiskMath.Busy(10, 0, 10, 100));
        Assert.Equal(0, DiskMath.Busy(0, 0, 150, 100));   // idle beyond the interval: clamped
        Assert.Equal(100, DiskMath.Busy(50, 0, 10, 100)); // idle counter went back: clamped
        Assert.Null(DiskMath.Busy(0, 100, 50, 100));      // zero query delta
        Assert.Null(DiskMath.Busy(0, 200, 50, 100));      // negative query delta
    }

    [Fact]
    public void Disk_busy_reports_the_busiest_drive()
    {
        Assert.Null(DiskMath.Max([]));
        Assert.Null(DiskMath.Max([null, null]));
        Assert.Equal(70, DiskMath.Max([10, null, 70, 30]));
    }

    [Fact]
    public void Real_sources_smoke()
    {
        using var src = new WinSystemSources();
        Assert.True(src.Cores >= 1);
        _ = src.Cpu();
        _ = src.Memory();
        _ = src.DiskBusy();
        _ = src.Processes();
        var sampler = new SystemSampler(src, (_, _) => { });
        _ = sampler.Next(TimeSpan.FromSeconds(2));
        _ = sampler.Next(TimeSpan.FromSeconds(2));
    }
}
