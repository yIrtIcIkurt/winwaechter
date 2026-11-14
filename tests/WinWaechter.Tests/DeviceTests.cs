using WinWaechter.Core.Device;
using WinWaechter.Core.Repair;
using WinWaechter.Tests.Fakes;

namespace WinWaechter.Tests;

public class DeviceTests
{
    static readonly DateTime Now = new(2026, 11, 14, 12, 0, 0);
    readonly FakeProcessRunner _run = new();

    sealed class Battery(BatteryState? s) : IBatteryInfo
    {
        public BatteryState? Read() => s;
    }

    sealed class Disks(params DiskState[] d) : IDiskHealth
    {
        public IReadOnlyList<DiskState> Read() => d;
    }

    sealed class Volumes(params VolumeState[] v) : IVolumeInfo
    {
        public IReadOnlyList<VolumeState> Read() => v;
    }

    RepairActions Repair() => new(_run, new FakeServiceControl(), new FakeRepairFiles(), new FakeNetworkConfig(), () => Task.FromResult(true),
        new FakeProcessEnder(), @"C:\Windows", @"C:\Users\u\AppData\Local", Now);

    static DiskState Ssd(string health = "Healthy", ulong? wear = 3, int? temp = 38, int? tempMax = 70, ulong? read = 0, ulong? write = 0) =>
        new("2650 Micron 1024GB", "SSD", health, wear, temp, tempMax, read, write, 1234);

    static VolumeState Vol(DateTime? optimized, bool? dirty = false, int errors = 0, ulong free = 200UL << 30) =>
        new('C', "NTFS", 1000UL << 30, free, optimized, dirty, errors);

    IReadOnlyList<DeviceRow> Scan(BatteryState? b = null, DiskState[]? disks = null, VolumeState[]? vols = null) =>
        new DeviceScanner(new Battery(b), new Disks(disks ?? [Ssd()]), new Volumes(vols ?? [Vol(Now.AddDays(-3))]), Repair(), _run,
            @"C:\Users\u\Downloads", Now).Scan();

    [Theory]
    [InlineData(81000, DeviceLevel.Good)]     // 19 %
    [InlineData(80000, DeviceLevel.Attention)] // 20 %
    [InlineData(60000, DeviceLevel.Attention)] // 40 %
    [InlineData(59000, DeviceLevel.Bad)]       // 41 %
    public void Battery_levels_at_19_20_40_41_percent_wear(long full, DeviceLevel level)
    {
        var row = Scan(new BatteryState(100000, full, 278)).Single(r => r.Id == "battery");
        Assert.Equal(level, row.Level);
        Assert.Null(row.Fix);
        Assert.NotNull(row.Extra);
        if (level == DeviceLevel.Bad) Assert.Equal("Akku stark gealtert – ein Austausch lohnt sich, wenn die Laufzeit nicht mehr reicht.", row.Why);
    }

    [Fact]
    public void Battery_detail_names_capacity_wear_and_cycles()
    {
        var row = Scan(new BatteryState(54000, 42818, 278)).Single(r => r.Id == "battery");
        Assert.Equal("Kapazität heute 42.818 mWh von ursprünglich 54.000 mWh (21 % Verschleiß), 278 Ladezyklen", row.Detail);
    }

    [Fact]
    public void Battery_without_design_capacity_is_unknown_without_warning()
    {
        var row = Scan(new BatteryState(null, 42818, null)).Single(r => r.Id == "battery");
        Assert.Equal(DeviceLevel.Unknown, row.Level);
        Assert.Equal("Ursprüngliche Kapazität nicht lesbar", row.Detail);
    }

    [Fact]
    public void No_battery_gives_no_battery_row()
    {
        Assert.DoesNotContain(Scan(null), r => r.Id == "battery");
    }

    [Fact]
    public void Disk_levels_for_health_wear_errors_and_temperature()
    {
        DeviceLevel L(DiskState d) => Scan(disks: [d]).Single(r => r.Id.StartsWith("disk:")).Level;
        Assert.Equal(DeviceLevel.Good, L(Ssd()));
        Assert.Equal(DeviceLevel.Bad, L(Ssd(health: "Warning")));
        Assert.Equal(DeviceLevel.Bad, L(Ssd(wear: 90)));
        Assert.Equal(DeviceLevel.Attention, L(Ssd(wear: 70)));
        Assert.Equal(DeviceLevel.Attention, L(Ssd(read: 2)));
        Assert.Equal(DeviceLevel.Attention, L(Ssd(temp: 75)));
        var bad = Scan(disks: [Ssd(health: "Unhealthy")]).Single(r => r.Id.StartsWith("disk:"));
        Assert.Equal("Sichern Sie Ihre Daten bald.", bad.Why);
        Assert.Equal("Verschleiß 3 %, 38 °C, 1234 Betriebsstunden", Scan().Single(r => r.Id.StartsWith("disk:")).Detail);
    }

    [Fact]
    public void Disk_missing_counters_says_not_available()
    {
        var row = Scan(disks: [new DiskState("Alt", "HDD", "Healthy", null, null, null, null, null, null)]).Single(r => r.Id.StartsWith("disk:"));
        Assert.Equal(DeviceLevel.Good, row.Level);
        Assert.Equal("Weitere Werte nicht verfügbar", row.Detail);
    }

    [Fact]
    public async Task Volume_not_optimized_for_31_days_offers_optimize_with_defrag_O_U()
    {
        var row = Scan(vols: [Vol(Now.AddDays(-31))]).Single(r => r.Id == "volume:C");
        Assert.Equal(DeviceLevel.Attention, row.Level);
        Assert.NotNull(row.Fix);
        Assert.Null(row.Fix!.ConfirmText);
        Assert.True(row.Fix.SkipGate);
        Assert.Equal("disk", row.Fix.LockKey);
        await row.Fix.RunAsync(null, CancellationToken.None);
        Assert.Equal((@"C:\Windows\System32\defrag.exe", "C: /O /U"), _run.Calls.Single());
        Assert.Equal(DeviceLevel.Good, Scan(vols: [Vol(Now.AddDays(-29))]).Single(r => r.Id == "volume:C").Level);
        Assert.Equal(DeviceLevel.Attention, Scan(vols: [Vol(null)]).Single(r => r.Id == "volume:C").Level);
    }

    [Fact]
    public void Dirty_volume_or_disk_errors_offer_scan_disk()
    {
        foreach (var v in new[] { Vol(Now, dirty: true), Vol(Now, errors: 3) })
        {
            var row = Scan(vols: [v]).Single(r => r.Id == "volume-errors:C");
            Assert.Equal(DeviceLevel.Attention, row.Level);
            Assert.Equal("repair-chkdsk-scan", row.Fix!.Id);
        }
        Assert.DoesNotContain(Scan(vols: [Vol(Now)]), r => r.Id == "volume-errors:C");
    }

    [Fact]
    public void Low_free_space_is_attention_without_fix()
    {
        var row = Scan(vols: [Vol(Now, free: 50UL << 30)]).Single(r => r.Id == "volume-space:C");
        Assert.Equal(DeviceLevel.Attention, row.Level);
        Assert.Null(row.Fix);
    }

    [Fact]
    public async Task Battery_report_is_saved_to_downloads_and_never_opened_elevated()
    {
        var row = Scan(new BatteryState(54000, 42818, 278)).Single(r => r.Id == "battery");
        var outcome = await row.Extra!.RunAsync(null, CancellationToken.None);
        Assert.Equal((@"C:\Windows\System32\powercfg.exe", "/batteryreport /output \"C:\\Users\\u\\Downloads\\Akkubericht-20261114.html\""),
            _run.Calls.Single());
        Assert.Null(outcome.Link);
        Assert.Equal("Gespeichert in Downloads: Akkubericht-20261114.html", outcome.Message);
    }

    [Fact]
    public void Powercfg_xml_fallback_reads_capacities()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <BatteryReport xmlns="http://schemas.microsoft.com/battery/2012">
              <Batteries>
                <Battery>
                  <DesignCapacity>54000</DesignCapacity>
                  <FullChargeCapacity>42818</FullChargeCapacity>
                  <CycleCount>278</CycleCount>
                </Battery>
              </Batteries>
            </BatteryReport>
            """;
        Assert.Equal(new BatteryState(54000, 42818, 278), WmiBatteryInfo.ParseReport(xml));
        Assert.Null(WmiBatteryInfo.ParseReport("<kaputt"));
    }

    // Read-only check on the owner's machine: WW_DEVICE=1. Never runs by default.
    [Fact]
    public void Device_real_disks_and_battery_are_readable()
    {
        if (Environment.GetEnvironmentVariable("WW_DEVICE") != "1") return;
        var disks = new CimDiskHealth().Read();
        Assert.Contains(disks, d => d.Name.Contains("Micron") && d.Health == "Healthy" && d.Media == "SSD");
        var b = new WmiBatteryInfo(() => true, new Core.Sys.ProcessRunner(), @"C:\Windows", Path.GetTempPath()).Read();
        Assert.NotNull(b);
        Assert.InRange(b!.FullMWh ?? 0, 30000, 60000);
        var vols = new WinVolumeInfo(new RepairChecks(new Core.Sys.ProcessRunner(), @"C:\Windows"), new WinDiskErrorEvents(), 'C').Read();
        Assert.Contains(vols, v => v.Letter == 'C' && v.Size > 0);
    }

    [Fact]
    public void Systemtime_bytes_from_defrag_statistics_are_read()
    {
        // 2026-11-07 (Saturday) 09:15:30
        byte[] raw = [0xEA, 0x07, 11, 0, 6, 0, 7, 0, 9, 0, 15, 0, 30, 0, 0, 0];
        Assert.Equal(new DateTime(2026, 11, 7, 9, 15, 30), WinVolumeInfo.FromSystemTime(raw));
        Assert.Null(WinVolumeInfo.FromSystemTime(new byte[16]));
        Assert.Null(WinVolumeInfo.FromSystemTime([1, 2]));
    }
}
