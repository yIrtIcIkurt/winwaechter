using WinWaechter.App.Services;
using WinWaechter.App.ViewModels;
using WinWaechter.Core.Actions;
using WinWaechter.Core.Device;
using WinWaechter.Tests.Fakes;

namespace WinWaechter.Tests;

public class DeviceViewModelTests
{
    readonly RecordingDialogs _dialogs = new();
    readonly Dictionary<string, ActivityLock> _locks = ActivityLock.CreateDefaults();
    int _scans;
    int _fixes;

    SystemAction Fix() => new("optimize-C", "Laufwerk C: optimieren", "Optimieren", null, false,
        (_, _) => { _fixes++; return Task.FromResult(new ActionOutcome(true, "Laufwerk C: wurde optimiert.")); }) { SkipGate = true, LockKey = "disk" };

    IReadOnlyList<DeviceRow> Rows(bool battery) =>
    [
        .. battery ? [new DeviceRow("battery", DeviceScanner.GroupBattery, "Akku", "Kapazität …", DeviceLevel.Attention, "gealtert", null,
            new SystemAction("battery-report", "Ausführlichen Akkubericht speichern", "Bericht speichern", null, false,
                (_, _) => Task.FromResult(new ActionOutcome(true, "Gespeichert"))))] : Array.Empty<DeviceRow>(),
        new DeviceRow("disk:Micron", DeviceScanner.GroupDisks, "Micron (SSD)", "Verschleiß 3 %", DeviceLevel.Good, "gesund", null),
        new DeviceRow("volume:C", DeviceScanner.GroupVolumes, "Laufwerk C:", "Noch nie optimiert", DeviceLevel.Attention, "über 30 Tage", Fix()),
    ];

    DeviceViewModel Vm(bool battery = true, bool canChange = true) =>
        new(_ => { _scans++; return Task.FromResult(Rows(battery)); }, new ActionRunner(_dialogs, new ChangeGate(), locks: _locks), canChange);

    [Fact]
    public async Task Groups_rows_and_hides_battery_group_without_battery()
    {
        var vm = Vm();
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal([DeviceScanner.GroupBattery, DeviceScanner.GroupDisks, DeviceScanner.GroupVolumes], vm.Groups.Select(g => g.Title));
        var none = Vm(battery: false);
        await none.RefreshCommand.ExecuteAsync(null);
        Assert.Equal([DeviceScanner.GroupDisks, DeviceScanner.GroupVolumes], none.Groups.Select(g => g.Title));
        Assert.True(vm.IsLoaded);
    }

    [Fact]
    public async Task Fix_runs_through_runner_and_rescans()
    {
        var vm = Vm();
        await vm.RefreshCommand.ExecuteAsync(null);
        var row = vm.Groups.SelectMany(g => g.Rows).Single(r => r.Id == "volume:C");
        Assert.True(row.HasFix);
        Assert.Equal("Optimieren", row.FixText);
        await row.FixCommand.ExecuteAsync(null);
        Assert.Equal(1, _fixes);
        Assert.Equal(2, _scans);
        Assert.Equal("Laufwerk C: wurde optimiert.", vm.ResultText);
    }

    [Fact]
    public async Task Disk_lock_busy_disables_optimize_and_scan()
    {
        var vm = Vm();
        await vm.RefreshCommand.ExecuteAsync(null);
        var row = vm.Groups.SelectMany(g => g.Rows).Single(r => r.Id == "volume:C");
        Assert.True(row.FixCommand.CanExecute(null));
        Assert.True(_locks["disk"].TryBegin());
        Assert.False(row.FixCommand.CanExecute(null));
        _locks["disk"].End();
        Assert.True(row.FixCommand.CanExecute(null));
    }

    [Fact]
    public async Task Locked_without_admin()
    {
        var vm = Vm(canChange: false);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.All(vm.Groups.SelectMany(g => g.Rows).Where(r => r.HasFix), r => Assert.False(r.FixCommand.CanExecute(null)));
    }

    [Fact]
    public void Suggestions_only_for_attention_with_fix()
    {
        var suggestions = DeviceSuggestions.From(Rows(battery: true));
        var s = Assert.Single(suggestions);
        Assert.Equal("device:volume:C", s.Id);
        Assert.Equal("Laufwerk C:", s.Title);
    }
}
