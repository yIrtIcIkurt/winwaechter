using WinWaechter.Core.Performance;
using WinWaechter.Tests.Fakes;
using Xunit;

namespace WinWaechter.Tests;

public class BootReportTests
{
    static readonly DateTime Now = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);

    static BootEvent Boot(int daysAgo, long ms) => new(100, Now.AddDays(-daysAgo), null, ms, null);
    static BootEvent Delay(int id, int daysAgo, string name, long ms) => new(id, Now.AddDays(-daysAgo), name, null, ms);

    [Fact]
    public void Report_from_events()
    {
        var events = new List<BootEvent>
        {
            Boot(1, 40_000), Boot(2, 50_000), Boot(3, 60_000),
            Delay(101, 1, "Alpha", 3_000),
            Delay(103, 2, "Alpha", 9_000),
            Delay(102, 3, "Treiber", 5_000),
            Delay(101, 45, "Uralt", 99_000),
        };

        var r = BootReport.From(events, Now);

        Assert.Equal([TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(50), TimeSpan.FromSeconds(60)], r.LastBoots);
        Assert.Equal(TimeSpan.FromSeconds(50), r.Average);
        Assert.Equal(2, r.Slowest.Count);
        Assert.Equal(new BootDelay("Alpha", TimeSpan.FromSeconds(9)), r.Slowest[0]);
        Assert.Equal(new BootDelay("Treiber", TimeSpan.FromSeconds(5)), r.Slowest[1]);
        Assert.Null(r.Message);
    }

    [Fact]
    public void Limits_to_five_each()
    {
        var events = new List<BootEvent>();
        for (var i = 0; i < 7; i++) events.Add(Boot(i, 10_000 + i));
        for (var i = 0; i < 7; i++) events.Add(Delay(101, 1, "P" + i, 1_000 * (i + 1)));

        var r = BootReport.From(events, Now);

        Assert.Equal(5, r.LastBoots.Count);
        Assert.Equal(5, r.Slowest.Count);
        Assert.Equal("P6", r.Slowest[0].Name);
    }

    [Fact]
    public void No_measurement_message()
    {
        var r = BootReport.From([], Now);

        Assert.Empty(r.LastBoots);
        Assert.Null(r.Average);
        Assert.Equal("Noch keine Messung. Windows misst die Startzeit nach einem Neustart.", r.Message);
    }

    [Fact]
    public void Access_denied_gives_message()
    {
        var src = new FakeBootEventSource { Throws = new UnauthorizedAccessException() };

        var r = BootReport.Load(src, Now);

        Assert.Equal("Nur mit Adminrechten lesbar.", r.Message);
        Assert.Empty(r.LastBoots);
        Assert.Null(r.Average);
    }

    [Fact]
    public void Other_failure_gives_message()
    {
        var src = new FakeBootEventSource { Throws = new InvalidOperationException("kaputt") };

        var r = BootReport.Load(src, Now);

        Assert.Equal("Die Startzeit ließ sich nicht lesen.", r.Message);
    }

    [Fact]
    public void Load_reads_the_last_30_days()
    {
        var src = new FakeBootEventSource();
        src.Events.Add(Boot(1, 30_000));

        var r = BootReport.Load(src, Now);

        Assert.Equal(Now.AddDays(-30), src.LastSince);
        Assert.Single(r.LastBoots);
    }

    [Fact]
    public void Real_source_smoke()
    {
        try { _ = new WinBootEventSource().Read(DateTime.UtcNow.AddDays(-30)); }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void File_name_is_carried()
    {
        var events = new List<BootEvent>
        {
            Boot(1, 40_000),
            new(101, Now.AddDays(-1), "Foo Updater", null, 4_000, "foo.exe"),
            new(103, Now.AddDays(-2), "Foo Updater", null, 9_000, "foo.exe"),
            Delay(102, 3, "Treiber", 5_000),
        };

        var r = BootReport.From(events, Now);

        Assert.Equal(new BootDelay("Foo Updater", TimeSpan.FromSeconds(9), "foo.exe"), r.Slowest[0]);
        Assert.Null(r.Slowest[1].FileName);
    }
}
