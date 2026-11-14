using System.Text.Json;
using WinWaechter.App.Services;
using WinWaechter.App.ViewModels;
using WinWaechter.Core.Maintenance;
using WinWaechter.Tests.Fakes;

namespace WinWaechter.Tests;

public sealed class MaintenanceAppTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("ww-maintapp").FullName;
    readonly FakeTaskStore _tasks = new();
    readonly RecordingDialogs _dialogs = new();
    readonly AppSettings _settings = new();
    int _saves;
    CheckResult? _last;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    MaintenanceSettingsViewModel Vm(bool canChange = true, bool ownAccount = true) =>
        new(_settings, () => _saves++, _tasks, @"C:\Program Files\WinWaechter\WinWaechter.App.exe", "S-1-5-21-7", canChange, ownAccount,
            () => _last, _dialogs, () => new DateTime(2026, 11, 14, 10, 0, 0));

    [Fact]
    public void Settings_clamp_invalid_day_and_time()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, """{ "MaintenanceEnabled": true, "MaintenanceDay": 42, "MaintenanceTime": "25:99" }""");
        var s = AppSettings.Load(path);
        Assert.True(s.MaintenanceEnabled);
        Assert.Equal(DayOfWeek.Sunday, s.MaintenanceDay);
        Assert.Equal("11:00", s.MaintenanceTime);
        var fresh = new AppSettings();
        Assert.False(fresh.MaintenanceEnabled);
        Assert.False(fresh.MaintenanceHintDismissed);
    }

    [Fact]
    public void Toggle_registers_and_removes_task_with_user_sid()
    {
        var vm = Vm();
        vm.Enabled = true;
        var (path, xml) = _tasks.RegisterXmlCalls.Single();
        Assert.Equal(MaintenanceTask.Path, path);
        Assert.Contains("<UserId>S-1-5-21-7</UserId>", xml);
        Assert.Contains("<Sunday />", xml);
        Assert.Contains("2026-11-15T11:00:00", xml);
        Assert.True(_settings.MaintenanceEnabled);
        vm.Enabled = false;
        Assert.Equal([MaintenanceTask.Path], _tasks.DeleteCalls);
        Assert.False(_settings.MaintenanceEnabled);
        Assert.Equal(2, _saves);
    }

    [Fact]
    public void Changing_day_reregisters()
    {
        var vm = Vm();
        vm.Enabled = true;
        vm.SelectedDay = vm.Days.Single(d => d.Day == DayOfWeek.Wednesday);
        vm.SelectedTime = "18:00";
        Assert.Equal(3, _tasks.RegisterXmlCalls.Count);
        Assert.Contains("<Wednesday />", _tasks.RegisterXmlCalls[^1].Xml);
        Assert.Contains("T18:00:00", _tasks.RegisterXmlCalls[^1].Xml);
        Assert.Equal((DayOfWeek.Wednesday, "18:00"), (_settings.MaintenanceDay, _settings.MaintenanceTime));
        Assert.Equal("Mittwoch", vm.SelectedDay.Name);
        Assert.Equal(17, vm.Times.Count);
    }

    [Fact]
    public void Failed_registration_flips_back_and_tells()
    {
        _tasks.RegisterXmlThrows = new UnauthorizedAccessException("verweigert");
        var vm = Vm();
        vm.Enabled = true;
        Assert.False(vm.Enabled);
        Assert.False(_settings.MaintenanceEnabled);
        Assert.Single(_dialogs.Informs);
    }

    [Fact]
    public void Toggle_locked_without_own_account()
    {
        var vm = Vm(ownAccount: false);
        Assert.False(vm.CanEdit);
        Assert.Equal("Die automatische Prüfung lässt sich nur für das eigene Administratorkonto einrichten.", vm.LockText);
        vm.Enabled = true;
        Assert.Empty(_tasks.RegisterXmlCalls);
        Assert.False(vm.ShowHint);
    }

    [Fact]
    public void Overview_shows_last_check_line_states()
    {
        var at = new DateTimeOffset(2026, 11, 15, 11, 0, 0, TimeSpan.FromHours(1));
        Assert.Null(Vm().LastCheckText);
        _last = new CheckResult(at, false, 5, 1, 12, false, []);
        Assert.Equal($"Letzte automatische Prüfung: {at.ToLocalTime():dd.MM.yyyy HH:mm} – 6 Empfehlungen, 12 Programm-Updates", Vm().LastCheckText);
        _last = new CheckResult(at, false, 0, 0, 0, false, []);
        Assert.EndsWith("– alles in Ordnung", Vm().LastCheckText);
        _last = new CheckResult(at, true, 0, 0, 0, false, []);
        Assert.EndsWith("– fehlgeschlagen", Vm().LastCheckText);
    }

    [Fact]
    public void Hint_dismissed_is_remembered()
    {
        var vm = Vm();
        Assert.True(vm.ShowHint);
        vm.DismissHintCommand.Execute(null);
        Assert.False(vm.ShowHint);
        Assert.True(_settings.MaintenanceHintDismissed);
        Assert.False(Vm().ShowHint);
        var other = new AppSettings();
        _settings.MaintenanceHintDismissed = false;
        var enable = Vm();
        enable.EnableCommand.Execute(null);
        Assert.True(enable.Enabled);
        Assert.False(enable.ShowHint);
        Assert.NotNull(other);
    }

    [Theory]
    [InlineData(new string[0], StartupMode.Normal)]
    [InlineData(new[] { "--pruefen" }, StartupMode.Check)]
    [InlineData(new[] { "winwaechter:verbessern" }, StartupMode.Improve)]
    [InlineData(new[] { "WINWAECHTER:verbessern/" }, StartupMode.Improve)]
    [InlineData(new[] { "winwaechter:" }, StartupMode.Improve)]
    [InlineData(new[] { "winwaechter:loeschen" }, StartupMode.Normal)]
    [InlineData(new[] { "--irgendwas" }, StartupMode.Normal)]
    public void Startup_args_parser_recognises_pruefen_and_protocol(string[] args, StartupMode mode)
    {
        Assert.Equal(mode, StartupArgs.Parse(args));
    }

    [Fact]
    public async Task Activation_verb_other_than_verbessern_is_dropped()
    {
        Assert.True(ActivationPipe.IsValidVerb("verbessern"));
        Assert.False(ActivationPipe.IsValidVerb("verbessern;del"));
        Assert.False(ActivationPipe.IsValidVerb(""));
        var name = "WinWaechter.Test." + Guid.NewGuid().ToString("N");
        var got = new List<string>();
        var received = new TaskCompletionSource();
        using (ActivationPipe.Listen(name, v => { lock (got) got.Add(v); received.TrySetResult(); }))
        {
            Assert.True(ActivationPipe.Send(name, "boese"));
            Assert.True(ActivationPipe.Send(name, "verbessern"));
            await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(["verbessern"], got);
        Assert.False(ActivationPipe.Send("WinWaechter.Test.fehlt." + Guid.NewGuid().ToString("N"), "verbessern"));
    }
}
