using WinWaechter.Core.Journal;
using WinWaechter.Core.Model;
using WinWaechter.Core.Updates;

namespace WinWaechter.Tests;

public sealed class WingetTelemetryTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("ww-wingetset").FullName;
    string PathIn => Path.Combine(_dir, "LocalState", "settings.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    // the file winget writes on first use (with its comment), plus one own setting
    const string Default = """
        {
            "$schema": "https://aka.ms/winget-settings.schema.json",

            // For documentation on these settings, see: https://aka.ms/winget-settings
            // "source": {
            //    "autoUpdateIntervalInMinutes": 5
            // },
            "visual": {"progressBar":"rainbow"}
        }
        """;

    [Fact]
    public void SetDisable_keeps_comments_and_other_members_byte_identical()
    {
        var result = WingetSettingsFile.SetDisable(Default, true);
        Assert.NotNull(result);
        var inserted = result!.Replace("\n    \"telemetry\": { \"disable\": true },", "");
        Assert.Equal(Default, inserted);
        Assert.Equal(TelemetryState.Off, WingetSettingsFile.ReadText(result));
    }

    [Fact]
    public void SetDisable_replaces_existing_false()
    {
        const string text = """{ "telemetry": { "disable": false }, "x": 1 }""";
        Assert.Equal("""{ "telemetry": { "disable": true }, "x": 1 }""", WingetSettingsFile.SetDisable(text, true));
        Assert.Equal(TelemetryState.On, WingetSettingsFile.ReadText(text));
    }

    [Fact]
    public void SetDisable_inserts_into_existing_telemetry()
    {
        const string text = """{ "telemetry": {}, "x": 1 }""";
        var r = WingetSettingsFile.SetDisable(text, true);
        Assert.Equal("""{ "telemetry": { "disable": true }, "x": 1 }""", r);
        const string withOther = """{ "telemetry": { "y": 2 } }""";
        Assert.Equal("""{ "telemetry": { "disable": true, "y": 2 } }""", WingetSettingsFile.SetDisable(withOther, true));
    }

    [Fact]
    public void SetDisable_ignores_telemetry_inside_strings_comments_and_nested_objects()
    {
        const string text = """
            {
                // "telemetry": { "disable": false }
                "note": "\"telemetry\": {}",
                "nested": { "telemetry": { "disable": false } }
            }
            """;
        var r = WingetSettingsFile.SetDisable(text, true)!;
        Assert.Contains("// \"telemetry\": { \"disable\": false }", r);
        Assert.Contains("\"nested\": { \"telemetry\": { \"disable\": false } }", r);
        Assert.Equal(TelemetryState.Off, WingetSettingsFile.ReadText(r));
    }

    [Theory]
    [InlineData("""{ "telemetry": { "disable": false }, "telemetry": {} }""")]
    [InlineData("""{ "telemetry": { "disable": false, "disable": true } }""")]
    [InlineData("""{ "telemetry": true }""")]
    [InlineData("""{ "telemetry": { "disable": "nein" } }""")]
    [InlineData("""{ "visual": """)]
    [InlineData("""[1, 2]""")]
    public void SetDisable_returns_null_for_ambiguous_or_broken_text(string text)
    {
        Assert.Null(WingetSettingsFile.SetDisable(text, true));
    }

    [Fact]
    public void Empty_text_gets_minimal_file()
    {
        Assert.Equal("{\n    \"telemetry\": { \"disable\": true }\n}", WingetSettingsFile.SetDisable("  ", true));
    }

    [Fact]
    public void Missing_file_counts_as_on()
    {
        Assert.Equal(TelemetryState.On, WingetSettingsFile.Read(PathIn));
        File.WriteAllText(Path.Combine(_dir, "x.json"), "{ kaputt");
        Assert.Equal(TelemetryState.Unreadable, WingetSettingsFile.Read(Path.Combine(_dir, "x.json")));
    }

    SessionContext Ctx() => new(_dir);

    [Fact]
    public void Missing_file_apply_creates_and_undo_deletes()
    {
        var change = new WingetTelemetryChange(PathIn);
        var rec = change.Apply(Ctx());
        Assert.Equal(ChangeStatus.Applied, rec.Status);
        Assert.Equal(TelemetryState.Off, WingetSettingsFile.Read(PathIn));
        Assert.Equal(DriftState.Unchanged, change.CheckDrift(rec));
        change.Undo(rec, Ctx());
        Assert.False(File.Exists(PathIn));
    }

    [Fact]
    public void Apply_undo_restores_exact_prior_text()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathIn)!);
        File.WriteAllText(PathIn, Default);
        var change = new WingetTelemetryChange(PathIn);
        var rec = change.Apply(Ctx());
        Assert.NotEqual(Default, File.ReadAllText(PathIn));
        var again = WingetTelemetryChange.FromRecord(rec, PathIn);
        again.Undo(rec, Ctx());
        Assert.Equal(Default, File.ReadAllText(PathIn));
        Assert.Equal(DriftState.Changed, change.CheckDrift(rec));
    }

    [Fact]
    public void Apply_refuses_ambiguous_file_and_leaves_it()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathIn)!);
        const string bad = """{ "telemetry": {}, "telemetry": {} }""";
        File.WriteAllText(PathIn, bad);
        Assert.Throws<InvalidOperationException>(() => new WingetTelemetryChange(PathIn).Apply(Ctx()));
        Assert.Equal(bad, File.ReadAllText(PathIn));
    }

    [Fact]
    public void FromRecord_rejects_other_paths()
    {
        var rec = new ChangeRecord("1", WingetTelemetryChange.KindName, @"C:\Windows\win.ini", "x", new(), new(), ChangeStatus.Applied, DateTimeOffset.Now);
        Assert.Throws<InvalidDataException>(() => WingetTelemetryChange.FromRecord(rec, PathIn));
    }

    // Datenschutz page: the winget row turns the usage data off through the engine (undo in the Verlauf)
    [Fact]
    public async Task Privacy_row_turns_winget_usage_data_off_and_shows_off()
    {
        var dialogs = new Fakes.RecordingDialogs();
        var engine = new Core.Optimizer.OptimizerEngine([], new SessionStore(Path.Combine(_dir, "data")), new Core.Changes.ChangeFactory(),
            _ => new Core.Optimizer.RestorePointResult(true, "ok"));
        var vm = new App.ViewModels.PrivacyViewModel(_ => Task.FromResult<IReadOnlyList<Core.Privacy.PrivacyRow>>([]), engine, dialogs, true,
            new App.Services.ChangeGate())
        {
            WingetTelemetry = () => new App.ViewModels.WingetTelemetryOffer(WingetSettingsFile.Read(PathIn), new WingetTelemetryChange(PathIn)),
        };
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(vm.ShowWinget);
        Assert.Equal("An", vm.WingetStateText);
        Assert.True(vm.TurnOffWingetCommand.CanExecute(null));

        await vm.TurnOffWingetCommand.ExecuteAsync(null);

        Assert.Single(dialogs.Confirms);
        Assert.Equal(TelemetryState.Off, WingetSettingsFile.Read(PathIn));
        Assert.Equal("Aus", vm.WingetStateText);
        Assert.False(vm.TurnOffWingetCommand.CanExecute(null));
    }

    // device test 2026-11-21: „Abschalten“ stayed grey – the offer arrives while the page is still busy reading,
    // and the button was never told when reading had finished
    [Fact]
    public async Task Winget_button_is_told_when_reading_has_finished()
    {
        var engine = new Core.Optimizer.OptimizerEngine([], new SessionStore(Path.Combine(_dir, "data")), new Core.Changes.ChangeFactory(),
            _ => new Core.Optimizer.RestorePointResult(true, "ok"));
        var gate = new App.Services.ChangeGate();
        var vm = new App.ViewModels.PrivacyViewModel(_ => Task.FromResult<IReadOnlyList<Core.Privacy.PrivacyRow>>([]), engine,
            new Fakes.RecordingDialogs(), true, gate)
        {
            WingetTelemetry = () => new App.ViewModels.WingetTelemetryOffer(WingetSettingsFile.Read(PathIn), new WingetTelemetryChange(PathIn)),
        };
        bool? lastSeen = null;
        vm.TurnOffWingetCommand.CanExecuteChanged += (_, _) => lastSeen = vm.TurnOffWingetCommand.CanExecute(null);

        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(lastSeen);   // what the button shows

        // another page's change holds the gate: the button greys out and comes back
        var held = new TaskCompletionSource();
        bool? whileHeld = null;
        var other = gate.RunAsync(new object(), async () => { whileHeld = lastSeen; await held.Task; });
        Assert.False(whileHeld);
        held.SetResult();
        await other;
        Assert.True(lastSeen);
    }

    [Fact]
    public async Task Privacy_row_is_hidden_without_winget_and_locked_without_change()
    {
        var engine = new Core.Optimizer.OptimizerEngine([], new SessionStore(Path.Combine(_dir, "data")), new Core.Changes.ChangeFactory(),
            _ => new Core.Optimizer.RestorePointResult(true, "ok"));
        var none = new App.ViewModels.PrivacyViewModel(_ => Task.FromResult<IReadOnlyList<Core.Privacy.PrivacyRow>>([]), engine,
            new Fakes.RecordingDialogs(), true, new App.Services.ChangeGate()) { WingetTelemetry = () => null };
        await none.RefreshCommand.ExecuteAsync(null);
        Assert.False(none.ShowWinget);

        var locked = new App.ViewModels.PrivacyViewModel(_ => Task.FromResult<IReadOnlyList<Core.Privacy.PrivacyRow>>([]), engine,
            new Fakes.RecordingDialogs(), true, new App.Services.ChangeGate())
        {
            WingetTelemetry = () => new App.ViewModels.WingetTelemetryOffer(TelemetryState.Unreadable, null),
        };
        await locked.RefreshCommand.ExecuteAsync(null);
        Assert.True(locked.ShowWinget);
        Assert.False(locked.TurnOffWingetCommand.CanExecute(null));
        Assert.Equal(WingetTelemetryChange.AmbiguousText, locked.WingetNote);
    }

    [Fact]
    public void Path_for_local_app_data()
    {
        Assert.Equal(@"C:\Users\u\AppData\Local\Packages\Microsoft.DesktopAppInstaller_8wekyb3d8bbwe\LocalState\settings.json",
            WingetSettingsFile.PathFor(@"C:\Users\u\AppData\Local"));
    }
}
