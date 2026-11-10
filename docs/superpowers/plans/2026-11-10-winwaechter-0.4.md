# WinWächter 0.4.0 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the page „Leistung“ (live monitor, boot-time report, reversible performance settings) and release it as 0.4.0.

**Architecture:** Same pattern as 0.1–0.3. Core readers return plain records; every system change is an `IChange` journalled by `OptimizerEngine` and undoable through `ChangeFactory`. New Core namespace `WinWaechter.Core.Performance` holds four change kinds (`perfvalue`, `powermode`, `spi`, `hibernate`), the settings scanner, the live sampler and the boot report, all behind interfaces with fakes. App gets `PerformanceViewModel` / `PerformanceView`.

**Tech Stack:** C# 13 / .NET 10 (`net10.0-windows`), WPF, CommunityToolkit.Mvvm, P/Invoke (kernel32, powrprof, pdh, user32, shlwapi), xUnit. One new package: `System.Diagnostics.EventLog` (Microsoft).

**Spec:** `docs/superpowers/specs/2026-11-10-winwaechter-0.4-design.md`. Design notes: `docs/design-notes.md`. Style reference: `docs/superpowers/plans/2026-11-10-winwaechter-0.3.md`.

## Global Constraints

- Branch `feature/v0.4` (checked out). Do not switch branches, do not push.
- `$DOTNET` = `C:\Users\tahsi\AppData\Local\Microsoft\dotnet\dotnet.exe`, with `DOTNET_CLI_TELEMETRY_OPTOUT=1` and `DOTNET_NOLOGO=1`. Tests: `$DOTNET test tests\WinWaechter.Tests\WinWaechter.Tests.csproj` (`--filter "FullyQualifiedName~<Class>"` for one class). The full suite (682 tests at start) stays green after every task. Close any running `WinWaechter.App.exe` from `bin\Debug` before building (it locks the DLLs).
- Only new package: `System.Diagnostics.EventLog` (Task 6). No telemetry, no third-party code.
- UI copy German, sachlich, „Sie“-Anrede, no promises of percentages. Anti-goals from spec §2 (RAM boosters, prefetch deletion, mass service changes, priorities, core parking) are never added.
- Every change is reversible and journalled. Targets come only from fixed lists in code, never from a journal record or from the network.
- Nothing preselected. Only differences between the chosen and the current state become changes.
- No `reg.exe`. The only external program is `%SystemRoot%\System32\powercfg.exe` with exactly `/hibernate off` or `/hibernate on`.
- URIs only via `SystemLinks.Allowed`; add exactly `ms-settings:easeofaccess-visualeffects`, `ms-settings:display-advancedgraphics`, `ms-settings:storagepolicies`, `ms-settings:powersleep`.
- Tests use fakes and temp dirs; no test writes the real registry, power settings, SPI or runs powercfg. Read-only smoke reads of real sources are allowed and must tolerate failure.
- Design per `docs/design-notes.md`: existing tokens only, `DynamicResource` colours, readouts in Bahnschrift with tabular figures, no cards/shadows/gradients.
- Version `0.4.0` in `Directory.Build.props` and `## 0.4.0` in `CHANGELOG.md` (Task 9).
- Every commit message ends with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **Registry value has an unexpected type or the key is missing** (e.g. `EnableTransparency` as REG_SZ, no `GameBar` key) → `perfvalue` refuses odd types with a clear message, treats a missing value as the Windows default, creates the key on apply and deletes the value on undo. Test `Missing_or_odd_values_are_handled` in Task 1.
2. **Undo after the user changed the setting in Windows meanwhile** → drift detected, undo skipped and reported (no overwrite). Tests `Drift_is_detected` in Tasks 1, 2 and 3.
3. **Journal record tampered** (key outside the whitelist, `..\` or `\` in a background-app name, target 5 for `EnableTransparency`, GUID not one of the three) → `FromRecord` throws, undo refused. Tests `FromRecord_rejects_unlisted` in Tasks 1 and 2.
4. **A monitor or boot source fails** (PDH counter missing, event log access denied, process exits between snapshots) → only that readout shows „–“ / the boot block says why; no exception, no log spam every 2 s. Tests `Failing_source_gives_null_reading_once_logged` (Task 5) and `Access_denied_gives_message` (Task 6).
5. **Monitor keeps running while hidden** (page left, window minimized) → sampling stops; no timer ticks while not visible. Test `Monitor_runs_only_while_visible` in Task 7.

---

## File Structure

Core (`src/WinWaechter.Core/Performance/`):
- `PerfValues.cs` – whitelist (`PerfValue` records, background-app key rule).
- `PerfValueChange.cs` – kind `perfvalue`.
- `RegDword.cs` (`src/WinWaechter.Core/Sys/`) – shared DWORD set/restore/drift logic used by `RegValueChange` and `PerfValueChange`.
- `PowerModes.cs` – `IPowerModes`, `PowerSource`, GUID constants, `WinPowerModes`, `PowerModeChange` (kind `powermode`).
- `AnimationChange.cs` – `IAnimationSetting`, `WinAnimationSetting`, `AnimationChange` (kind `spi`).
- `Hibernation.cs` – `IHibernation`, `WinHibernation`, `HibernateChange` (kind `hibernate`).
- `SettingBroadcast.cs` – `ISettingBroadcast`, `WinSettingBroadcast`.
- `PerformanceScanner.cs` – `PerfSetting`, `PerfOption`, scanner.
- `AppDisplayNames.cs` – `IAppDisplayNames`, `WinAppDisplayNames`.
- `SystemSampler.cs` – `SystemSample`, `Consumer`, pure `CpuMath`/`TopConsumers`, `ISystemSources`, `WinSystemSources`, `SystemSampler`.
- `BootReport.cs` – `BootEvent`, `BootReport`, `IBootEventSource`, `WinBootEventSource`.
- `PerformanceRegistration.cs` – `Register(ChangeFactory, …)` for all four kinds.

App:
- `ViewModels/PerformanceViewModel.cs` (+ `PerfSettingViewModel`, `ConsumerViewModel`).
- `Views/PerformanceView.xaml(.cs)`.
- Modify `ViewModels/ShellViewModel.cs`, `Views/ShellWindow.xaml`, `Services/AppServices.cs`, `src/WinWaechter.Core/Sys/SystemLinks.cs`, `docs/design-notes.md`.

Tests (`tests/WinWaechter.Tests/`): `PerfValueTests.cs`, `PowerModeTests.cs`, `AnimationHibernateTests.cs`, `PerformanceScannerTests.cs`, `SystemSamplerTests.cs`, `BootReportTests.cs`, `PerformanceViewModelTests.cs`; fakes `Fakes/FakePerformance.cs` (FakePowerModes, FakeAnimationSetting, FakeHibernation, FakeBroadcast, FakeAppDisplayNames, FakeSystemSources, FakeBootEventSource); extend `ViewSmokeTests.cs`.

---

### Task 1: `perfvalue` change kind and whitelist

**Files:**
- Create: `src/WinWaechter.Core/Sys/RegDword.cs`, `src/WinWaechter.Core/Performance/PerfValues.cs`, `src/WinWaechter.Core/Performance/PerfValueChange.cs`, `src/WinWaechter.Core/Performance/SettingBroadcast.cs`, `tests/WinWaechter.Tests/PerfValueTests.cs`, `tests/WinWaechter.Tests/Fakes/FakePerformance.cs` (FakeBroadcast only for now)
- Modify: `src/WinWaechter.Core/Privacy/RegValueChange.cs` (delegate to `RegDword`, behaviour unchanged)

**Interfaces:**
- Produces:
  - `static class RegDword { Dictionary<string,string?> Snapshot(IRegistry, Hive, string key, string name); void Write(IRegistry, Hive, string key, string name, int value); void Restore(IRegistry, Hive, string key, string name, IReadOnlyDictionary<string,string?> before); DriftState Drift(IRegistry, Hive, string key, string name, int target); }` – exactly today's `RegValueChange` logic (type check with message „Der Wert „{name}“ hat einen unerwarteten Typ ({kind}) und wird nicht verändert.“, Before keys `hive,key,name,existed,value,kind`). `Write` creates the key if missing.
  - `sealed record PerfValue(string Id, Hive Hive, string Key, string Name, IReadOnlyList<int> Targets, int DefaultWhenMissing, bool Broadcast)`.
  - `static class PerfValues { PerfValue Transparency; PerfValue GameMode; PerfValue GpuScheduling; PerfValue StorageSenseOn; PerfValue StorageSenseFrequency; const string BackgroundRoot = @"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications"; PerfValue BackgroundDisabled(string app); PerfValue BackgroundDisabledByUser(string app); PerfValue? Find(Hive, string key, string name); bool IsBackgroundApp(string app); }`
    - Transparency: HKCU `Software\Microsoft\Windows\CurrentVersion\Themes\Personalize` `EnableTransparency` targets [0,1], default 1, Broadcast true.
    - GameMode: HKCU `Software\Microsoft\GameBar` `AutoGameModeEnabled` [0,1], default 1.
    - GpuScheduling: HKLM `SYSTEM\CurrentControlSet\Control\GraphicsDrivers` `HwSchMode` [1,2], default 0 (means „not offered“).
    - StorageSenseOn: HKCU `Software\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy` `01` [0,1], default 0.
    - StorageSenseFrequency: same key, `2048` [0,1,7,30], default 0.
    - Background: HKCU `BackgroundRoot\<app>` `Disabled` and `DisabledByUser` [0,1], default 0. `IsBackgroundApp`: non-empty, ≤ 200 chars, no `\`, `/`, no `..`, no control chars.
  - `interface ISettingBroadcast { void ImmersiveColorSetChanged(); }`; `WinSettingBroadcast` = `SendMessageTimeoutW(HWND_BROADCAST, WM_SETTINGCHANGE, 0, "ImmersiveColorSet", SMTO_ABORTIFHUNG, 2000, out _)`, never throws.
  - `sealed class PerfValueChange : IChange` – `PerfValueChange(IRegistry, PerfValue, int target, string description, ISettingBroadcast?)`; ctor throws `ArgumentException` if `target ∉ value.Targets`. `Kind => "perfvalue"`, `Target => "HKCU\\key\\name"`. After = `{value, kind}`. Broadcast after Apply and Undo when `value.Broadcast`. `static PerfValueChange FromRecord(ChangeRecord, IRegistry, ISettingBroadcast)` – hive/key/name from Before must match `PerfValues.Find`, target = int from `After["value"]` and must be in `Targets`; else `InvalidOperationException("Dieser Wert gehört nicht zu den Leistungs-Einstellungen – Rückgängig abgelehnt.")`.

- [ ] **Step 1: Write failing tests** in `PerfValueTests.cs`: `Apply_and_undo_restore_exactly` (seeded DWORD 1 → apply 0 → undo → 1; missing value → apply → undo deletes it, key created on apply), `Missing_or_odd_values_are_handled` (REG_BINARY refused with the message; missing `GameBar` key works), `Drift_is_detected`, `Transparency_broadcasts_on_apply_and_undo` (FakeBroadcast count 2), `FromRecord_rejects_unlisted` (other key; `BackgroundRoot\a\b`; `BackgroundRoot\..`; target 5 for Transparency; HKLM hive for Transparency), `Target_outside_list_throws`, `Background_app_values_accepted_only_for_direct_children`. Keep `PrivacyTests` untouched.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~PerfValueTests"` → FAIL (types missing).
- [ ] **Step 3: Implement** `RegDword`, refactor `RegValueChange` to use it, then `PerfValues`, `SettingBroadcast`, `PerfValueChange`.
- [ ] **Step 4: Run** `PerfValueTests` and `PrivacyTests` → PASS; full suite → PASS.
- [ ] **Step 5: Commit** `feat(performance): perfvalue change kind with fixed whitelist`.

### Task 2: `powermode` change kind

**Files:**
- Create: `src/WinWaechter.Core/Performance/PowerModes.cs`, `tests/WinWaechter.Tests/PowerModeTests.cs`
- Modify: `tests/WinWaechter.Tests/Fakes/FakePerformance.cs` (FakePowerModes)

**Interfaces:**
- Produces:
  - `enum PowerSource { Ac, Dc }`.
  - `static class PowerModeIds { Guid BestEfficiency = 961cc777-2547-4f9d-8174-7d86181b8a7a; Guid Balanced = 00000000-0000-0000-0000-000000000000; Guid BestPerformance = ded574b5-45a0-4f42-8737-46345c09c238; IReadOnlyList<Guid> All; string Label(Guid) }` – labels „Beste Energieeffizienz“, „Ausgewogen“, „Beste Leistung“, other → „Benutzerdefiniert“.
  - `interface IPowerModes { bool HasBattery { get; } PowerSource Current { get; } Guid? Get(PowerSource s); void Set(PowerSource s, Guid mode); }`
  - `WinPowerModes(IRegistry reg)`: Get/Set `HKLM\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes` `ActiveOverlayAcPowerScheme` / `ActiveOverlayDcPowerScheme` (REG_SZ, GUID string „D“ format); missing → `Balanced`; unparsable → null. After Set, if `s == Current`, call `PowerSetActiveOverlayScheme(ref Guid)` (powrprof). `HasBattery`/`Current` from `GetSystemPowerStatus` (`BatteryFlag` 128 or 255 → no battery; `ACLineStatus` 0 → Dc).
  - `sealed class PowerModeChange : IChange` – `PowerModeChange(IPowerModes, PowerSource, Guid target)`; target must be in `PowerModeIds.All`. Kind `powermode`, Target `"Energiemodus (Netzbetrieb)"` / `"Energiemodus (Akku)"`, Description `"Energiemodus (Netzbetrieb) auf „Beste Energieeffizienz“"`. Before `{source, mode}` (mode may be any GUID string or empty), After `{mode}`. Undo writes the old GUID only if `Guid.TryParse` succeeds, else throws. Drift: `Get(s) != target` → Changed. `static FromRecord(ChangeRecord, IPowerModes)` validates source ∈ {ac,dc} and After mode ∈ `All`.

- [ ] **Step 1: Write failing tests**: `Apply_and_undo_switch_mode`, `Drift_is_detected`, `FromRecord_rejects_unlisted` (unknown GUID target; source „x“; Before mode „not-a-guid“ → undo throws), `Labels_for_known_and_custom`.
- [ ] **Step 2: Run** → FAIL.
- [ ] **Step 3: Implement** `PowerModes.cs`.
- [ ] **Step 4: Run** `PowerModeTests` + full suite → PASS.
- [ ] **Step 5: Commit** `feat(performance): power mode change for AC and battery`.

### Task 3: `spi` and `hibernate` change kinds

**Files:**
- Create: `src/WinWaechter.Core/Performance/AnimationChange.cs`, `src/WinWaechter.Core/Performance/Hibernation.cs`, `tests/WinWaechter.Tests/AnimationHibernateTests.cs`
- Modify: `tests/WinWaechter.Tests/Fakes/FakePerformance.cs` (FakeAnimationSetting, FakeHibernation)

**Interfaces:**
- Consumes: `IProcessRunner` (`RunAsync(exe, args, ct)`), `IRegistry`.
- Produces:
  - `interface IAnimationSetting { bool Get(); void Set(bool on); }`; `WinAnimationSetting` = `SystemParametersInfoW(SPI_GETCLIENTAREAANIMATION 0x1042 / SPI_SETCLIENTAREAANIMATION 0x1043, 0, ref/ptr, SPIF_UPDATEINIFILE|SPIF_SENDCHANGE)`; failure → `Win32Exception`.
  - `sealed class AnimationChange : IChange` – `AnimationChange(IAnimationSetting, bool target)`, Kind `spi`, Target `"Animationseffekte"`, Before `{on}`, After `{on}`, drift by `Get() != target`, `static FromRecord(ChangeRecord, IAnimationSetting)` (target from After `on` ∈ {true,false}).
  - `interface IHibernation { bool IsEnabled { get; } long FileBytes { get; } void Set(bool on); }`
  - `WinHibernation(IRegistry reg, IProcessRunner runner, string systemRoot, string systemDrive)`: `IsEnabled` = HKLM `SYSTEM\CurrentControlSet\Control\Power` `HibernateEnabled` == 1; `FileBytes` = length of `<systemDrive>\hiberfil.sys` (0 if missing/unreadable); `Set` runs `Path.Combine(systemRoot,"System32","powercfg.exe")` with exactly `/hibernate off` or `/hibernate on` (synchronously via `.GetAwaiter().GetResult()`, 30 s timeout), then requires exit 0 **and** `IsEnabled == on`, else `InvalidOperationException("powercfg hat den Ruhezustand nicht geändert (Rückgabewert {exit}).")`.
  - `sealed class HibernateChange : IChange` – `HibernateChange(IHibernation, bool target)`, Kind `hibernate`, Target `"Ruhezustand"`, Description „Ruhezustand ausschalten (gibt {size} frei)“ / „Ruhezustand einschalten“, Before `{on, bytes}`, drift by `IsEnabled != target`, `FromRecord(ChangeRecord, IHibernation)`.

- [ ] **Step 1: Write failing tests**: `Animation_apply_undo_and_drift`, `Hibernate_apply_undo_and_drift`, `Hibernate_uses_only_fixed_powercfg_arguments` (WinHibernation with FakeProcessRunner + FakeRegistry: args exactly `/hibernate off`, exe under the given systemRoot; registry not updated → throws with the message), `FromRecord_rejects_bad_values` (After on = „vielleicht“).
- [ ] **Step 2: Run** → FAIL.
- [ ] **Step 3: Implement** both files.
- [ ] **Step 4: Run** test class + full suite → PASS.
- [ ] **Step 5: Commit** `feat(performance): animation (SPI) and hibernation changes`.

### Task 4: Settings scanner and registration

**Files:**
- Create: `src/WinWaechter.Core/Performance/PerformanceScanner.cs`, `src/WinWaechter.Core/Performance/AppDisplayNames.cs`, `src/WinWaechter.Core/Performance/PerformanceRegistration.cs`, `tests/WinWaechter.Tests/PerformanceScannerTests.cs`
- Modify: `tests/WinWaechter.Tests/Fakes/FakePerformance.cs` (FakeAppDisplayNames)

**Interfaces:**
- Consumes: Tasks 1–3.
- Produces:
  - `sealed record PerfOption(int Value, string Label)`.
  - `sealed record PerfSetting(string Id, string Group, string Title, string Explanation, IReadOnlyList<PerfOption> Options, int Current, bool Available, string? UnavailableText, string? Hint, int? Recommended, bool NeedsRestart, string? Link)` with method-less data; changes are made by the scanner: `IReadOnlyList<IChange> PerformanceScanner.ChangesFor(PerfSetting s, int chosen)` (empty if `chosen == s.Current` or not Available).
  - Ids and groups (group ∈ `power`, `visual`, `gaming`, `storage`, `background`): `power-ac`, `power-dc`, `transparency`, `animation`, `gamemode`, `gpu`, `hibernate`, `storagesense` (Options 0 Aus / 1 An), `storagesense-frequency` (Options 1 Täglich, 7 Wöchentlich, 30 Monatlich, 0 „Bei wenig Speicherplatz“; Available only when storagesense Current == 1), `bg:<app>` (Options 1 „Erlaubt“, 0 „Nicht erlaubt“; Current 0 when `Disabled` == 1).
  - Power options: Value = index into `PowerModeIds.All` (0/1/2); current custom GUID → Current = -1 and an extra option (-1, „Benutzerdefiniert“) that cannot be chosen as target.
  - `PerformanceScanner(IRegistry reg, IPowerModes power, IAnimationSetting anim, IHibernation hib, IAppDisplayNames names, bool animationOwnAccount, ISettingBroadcast broadcast)`; `IReadOnlyList<PerfSetting> Scan()` – every source wrapped so one failing source makes only its rows `Available=false, UnavailableText="Nicht lesbar"` and logs once via `FileLog.Warn`.
  - Spec copy to use verbatim: power-dc Hint „Beste Leistung im Akkubetrieb leert den Akku spürbar schneller.“; gpu without value → Available false, UnavailableText „Wird von Windows für Ihre Grafikkarte automatisch geregelt“, Link `ms-settings:display-advancedgraphics`, NeedsRestart true; animation when not own account → UnavailableText „Nur im eigenen Benutzerkonto änderbar“; hibernate Explanation „Gibt {size} frei. Dann fehlen der Ruhezustand und der Schnellstart; der Rechner startet nach dem Herunterfahren etwas langsamer.“ and, with battery, Hint „Bei leerem Akku sichert Windows Ihre Arbeit sonst über den Ruhezustand.“; storagesense Recommended 1, Link `ms-settings:storagepolicies`; visual Link `ms-settings:easeofaccess-visualeffects`. `power-dc` only when `HasBattery`. Hibernate only offered when `IsEnabled` or `FileBytes > 0`; its options 1 An / 0 Aus.
  - `interface IAppDisplayNames { string Resolve(string packageFamilyName); }`; `WinAppDisplayNames(IRegistry reg)` – looks under HKCU `Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages` for a subkey whose name starts with `<name-part>_` and ends with `_<publisher-id>` of the family name, reads `DisplayName`, resolves `@{…}`/`ms-resource:` via `SHLoadIndirectString`; fallback: part before the first `_`.
  - `static class PerformanceRegistration { static void Register(ChangeFactory f, IRegistry reg, IPowerModes power, IAnimationSetting anim, IHibernation hib, ISettingBroadcast broadcast) }` registering `perfvalue`, `powermode`, `spi`, `hibernate`.

- [ ] **Step 1: Write failing tests**: `Scan_reads_current_values_and_defaults` (seeded registry + fakes; missing GameBar → Current 1), `No_battery_hides_power_dc_and_hint`, `Gpu_without_value_is_info_only`, `Frequency_only_available_when_storage_sense_on`, `ChangesFor_returns_only_differences` (same value → empty; storagesense 1→0 → one perfvalue change; power-ac 2→0 → one powermode change with BestEfficiency; custom option -1 cannot be chosen → ArgumentException), `Background_apps_listed_with_names_and_state`, `Failing_source_greys_only_its_rows`, `Registered_kinds_rehydrate` (apply via change, `factory.Rehydrate(record)` for all four kinds returns an IChange whose Undo works).
- [ ] **Step 2: Run** → FAIL.
- [ ] **Step 3: Implement** the three files.
- [ ] **Step 4: Run** test class + full suite → PASS.
- [ ] **Step 5: Commit** `feat(performance): settings scanner and change registration`.

### Task 5: Live sampler

**Files:**
- Create: `src/WinWaechter.Core/Performance/SystemSampler.cs`, `tests/WinWaechter.Tests/SystemSamplerTests.cs`
- Modify: `tests/WinWaechter.Tests/Fakes/FakePerformance.cs` (FakeSystemSources)

**Interfaces:**
- Produces:
  - `sealed record Consumer(string Name, int Count, double? CpuPercent, long MemoryBytes)`.
  - `sealed record SystemSample(double? CpuPercent, long? MemoryUsedBytes, long? MemoryTotalBytes, double? DiskPercent, IReadOnlyList<Consumer> Top)`.
  - `sealed record CpuTimes(long Idle, long Kernel, long User)`; `sealed record ProcTime(int Pid, string Name, TimeSpan? Cpu, long WorkingSet)`.
  - `static class CpuMath { static double? Busy(CpuTimes prev, CpuTimes now) }` – `(kernel+user−idle deltas)/(kernel+user deltas)*100`, clamp 0–100, null when total delta ≤ 0 (kernel time includes idle).
  - `static class TopConsumers { static IReadOnlyList<Consumer> Compute(IReadOnlyList<ProcTime> prev, IReadOnlyList<ProcTime> now, TimeSpan interval, int cores, int take = 5) }` – per PID delta (PID missing in prev → CPU null), skip `Idle` and PID 0, group by Name (case-insensitive) summing CPU (null if all null) and memory, order by CPU desc then memory desc.
  - `interface ISystemSources { CpuTimes? Cpu(); (long Used, long Total)? Memory(); double? DiskBusy(); IReadOnlyList<ProcTime> Processes(); int Cores { get; } }`; `WinSystemSources`: `GetSystemTimes`, `GlobalMemoryStatusEx`, PDH `PdhOpenQuery`+`PdhAddEnglishCounterW("\PhysicalDisk(_Total)\% Idle Time")`, value = 100 − idle clamped; `Process.GetProcesses()` with `TotalProcessorTime` in try/catch (null on access denied) and `WorkingSet64`; each method catches and returns null/empty.
  - `sealed class SystemSampler(ISystemSources src)` – `SystemSample Next(TimeSpan sinceLast)`; first call gives CPU null and Top with CPU null; logs a source failure once per source (`FileLog.Warn`), not every call.

- [ ] **Step 1: Write failing tests**: `Cpu_busy_from_deltas` (prev 100/200/100, now 150/300/150 → 50 %; zero delta → null), `Top_groups_by_name_and_sorts` (two chrome PIDs summed with Count 2; Idle hidden; new PID → CPU null), `Failing_source_gives_null_reading_once_logged`, `Real_sources_smoke` (WinSystemSources returns without throwing; Cores ≥ 1).
- [ ] **Step 2: Run** → FAIL.
- [ ] **Step 3: Implement** `SystemSampler.cs`.
- [ ] **Step 4: Run** test class + full suite → PASS.
- [ ] **Step 5: Commit** `feat(performance): live system sampler`.

### Task 6: Boot report

**Files:**
- Create: `src/WinWaechter.Core/Performance/BootReport.cs`, `tests/WinWaechter.Tests/BootReportTests.cs`
- Modify: `src/WinWaechter.Core/WinWaechter.Core.csproj` (PackageReference `System.Diagnostics.EventLog`, current stable 10.x), `tests/WinWaechter.Tests/Fakes/FakePerformance.cs` (FakeBootEventSource)

**Interfaces:**
- Produces:
  - `sealed record BootEvent(int Id, DateTime Time, string? Name, long? BootTimeMs, long? DegradationMs)`.
  - `sealed record BootDelay(string Name, TimeSpan Delay)`.
  - `sealed record BootReport(IReadOnlyList<TimeSpan> LastBoots, TimeSpan? Average, IReadOnlyList<BootDelay> Slowest, string? Message)`; `static BootReport From(IReadOnlyList<BootEvent> events, DateTime now)`: event 100 newest first, up to 5 → LastBoots, Average of those; events 101/102/103 within 30 days → per Name max DegradationMs, top 5 desc; no event 100 → Message „Noch keine Messung. Windows misst die Startzeit nach einem Neustart.“
  - `interface IBootEventSource { IReadOnlyList<BootEvent> Read(DateTime since); }` – throws `UnauthorizedAccessException` when not allowed.
  - `WinBootEventSource`: `EventLogReader` on `Microsoft-Windows-Diagnostics-Performance/Operational` with XPath `*[System[(EventID=100 or EventID=101 or EventID=102 or EventID=103) and TimeCreated[@SystemTime>='{since:o}']]]`, newest first, max 200; values from `EventData/Data[@Name='BootTime']`, `'DegradationTime'`, name from `'FriendlyName'` or else `'Name'`.
  - `static BootReport BootReport.Load(IBootEventSource src, DateTime now)` – `UnauthorizedAccessException` → Message „Nur mit Adminrechten lesbar.“; any other exception → Message „Die Startzeit ließ sich nicht lesen.“ + one log entry.

- [ ] **Step 1: Write failing tests**: `Report_from_events` (3 boots → LastBoots order and Average; old 101 > 30 days ignored; same name twice → max), `No_measurement_message`, `Access_denied_gives_message`, `Real_source_smoke` (WinBootEventSource.Read does not throw except UnauthorizedAccessException).
- [ ] **Step 2: Run** → FAIL.
- [ ] **Step 3: Implement** package reference and `BootReport.cs`.
- [ ] **Step 4: Run** test class + full suite → PASS.
- [ ] **Step 5: Commit** `feat(performance): boot time report from the diagnostics log`.

### Task 7: PerformanceViewModel

**Files:**
- Create: `src/WinWaechter.App/ViewModels/PerformanceViewModel.cs`, `tests/WinWaechter.Tests/PerformanceViewModelTests.cs`

**Interfaces:**
- Consumes: Tasks 4–6, `OptimizerEngine.ApplyAsync(title, findings, progress, ct)`, `ChangeGate`, `IDialogService`, `ISystemShell.OpenLink`, `Labels.Size`.
- Produces:
  - `PerformanceViewModel(Func<CancellationToken, Task<(IReadOnlyList<PerfSetting> Settings, Func<PerfSetting,int,IReadOnlyList<IChange>> ChangesFor)>> scan, SystemSampler sampler, Func<BootReport> boot, OptimizerEngine engine, IDialogService dialogs, ISystemShell shell, bool canChange, ChangeGate gate, Action openOptimize, ITickSource ticks)`.
  - `interface ITickSource { void Start(TimeSpan every, Action tick); void Stop(); }` (App: `DispatcherTimer`; tests: manual fake).
  - Properties: `Settings` (ObservableCollection<PerfSettingViewModel>), `BackgroundApps` (same type, group `background`), `CpuText`, `MemoryText` („12,3 von 31,6 GB“), `DiskText`, `CpuValue`/`MemoryValue`/`DiskValue` (0..1 for the scale), `Top` (ObservableCollection<ConsumerViewModel> with `Name` „Chrome (12)“, `CpuText`, `MemoryText`), `BootText`, `BootAverageText`, `BootSlowest`, `BootMessage`, `PendingCount`, `ApplyText` („Änderungen anwenden (n)“), `IsBusy`, `ProgressText`, `LastSummary`, `GateHint`, `ShowAdminNotice`, `AdminNotice`, `event ChangesApplied`.
  - `PerfSettingViewModel`: `Setting`, `Options`, `[ObservableProperty] Chosen` (int), `IsChanged => Chosen != Setting.Current`, `CanChange` (Available && page canChange), `StateText` (label of Current), `NeedsRestart`, `Hint`, `Link`, `OpenLinkCommand`.
  - Commands: `RefreshCommand` (scan + boot on the pool), `ApplyCommand` (confirm dialog listing each change's Description, final=false; gate; `ApplyAsync("Leistung", …)` with Findings `new Finding($"perf|{c.Target}", FindingArea.Einstellung, c.Description, c.Target, [], Rating.Info, true, c)`; summary dialog incl. „Einige Änderungen gelten erst nach einem Neustart.“ when any NeedsRestart; raise ChangesApplied; refresh), `ResetCommand` (Chosen = Current for all), `OpenOptimizeCommand`.
  - `void OnShown()` / `void OnHidden()` – start/stop `ticks` with 2 s; each tick runs `sampler.Next` on the pool and updates readouts (formatting: CPU „37 %“, null → „–“).

- [ ] **Step 1: Write failing tests**: `Nothing_pending_after_refresh`, `Choosing_values_counts_pending_and_apply_journals_changes` (FakePerfValues via real PerfValueChange on FakeRegistry; History-style undo via engine restores), `Apply_disabled_without_admin`, `Monitor_runs_only_while_visible` (fake ticks: OnShown starts, OnHidden stops, no Next calls after stop), `Readouts_format_and_null_shows_dash`, `Restart_note_after_gpu_change`, `Boot_message_shown`.
- [ ] **Step 2: Run** → FAIL.
- [ ] **Step 3: Implement** the view model.
- [ ] **Step 4: Run** test class + full suite → PASS.
- [ ] **Step 5: Commit** `feat(performance): page view model`.

### Task 8: View, navigation, wiring

**Files:**
- Create: `src/WinWaechter.App/Views/PerformanceView.xaml(.cs)`, `src/WinWaechter.App/Services/DispatcherTicks.cs` (`ITickSource` with `DispatcherTimer`; also stops while `Application.Current.MainWindow.WindowState == Minimized`)
- Modify: `src/WinWaechter.App/ViewModels/ShellViewModel.cs` (constructor gets `PerformanceViewModel performance`; NavItem „Leistung“ glyph `\uEC4A` after „Optimieren“; first visit runs `RefreshCommand`; `OnShown`/`OnHidden` when the page becomes/stops being current; `performance.ChangesApplied` → history), `src/WinWaechter.App/Views/ShellWindow.xaml` (DataTemplate), `src/WinWaechter.App/Services/AppServices.cs` (compose real sources; `PerformanceRegistration.Register` next to `FirewallChange.Register`; `animationOwnAccount` = current process SID == `user.Sid`; `WinHibernation(reg, new ProcessRunner(), Windows folder, system drive)`), `src/WinWaechter.Core/Sys/SystemLinks.cs` (four links from Global Constraints), `docs/design-notes.md` (nav glyph), `tests/WinWaechter.Tests/ViewSmokeTests.cs` (render page light/dark with fake sources; extend Shell constructor calls), any other test constructing `ShellViewModel`.

Layout per spec §3: header; „Jetzt“ readout bar (three readouts, `Scale` ticks style used on the overview „Wird frei“ readout) and „Größte Verbraucher“ rows; „Start“ block with „Autostart-Programme prüfen“ button; „Einstellungen“ in `SurfaceBox` with `RowDivider` rows: title, explanation (`Text.Small`, `MaxWidth=ProseWidth`), state chip, hint (Watch), „Gilt nach einem Neustart“ chip, link button, right side `ComboBox` (Options, DisplayMemberPath `Label`, SelectedValuePath `Value`, SelectedValue = Chosen) – checkbox not used, one control type for all; background apps in an `Expander`-like toggle („Hintergrund-Apps (n erlaubt)“); fixed footer like PrivacyView with `Button.Primary` ApplyText and „Auswahl zurücksetzen“.

- [ ] **Step 1: Write failing test**: extend `ViewSmokeTests` (page renders in both themes; the sort-box label assertion pattern applied to one settings ComboBox) and a ShellViewModel test `Leistung_nav_starts_and_stops_monitor`.
- [ ] **Step 2: Run** → FAIL.
- [ ] **Step 3: Implement** view, ticks, nav, wiring, links, design note.
- [ ] **Step 4: Run** full suite → PASS; `$DOTNET build src\WinWaechter.App -c Debug` without warnings in new files; launch the Debug exe once with `WINWAECHTER_NO_DIALOGS=1`, open „Leistung“, confirm no exception in `%LOCALAPPDATA%\WinWaechter-Dev\Logs`, close it.
- [ ] **Step 5: Commit** `feat(performance): Leistung page, navigation and wiring`.

### Task 9: Release preparation (controller, with the user)

- [ ] Version `0.4.0` in `Directory.Build.props`, `## 0.4.0` in `CHANGELOG.md` (German, user-facing), roadmap entry.
- [ ] Build installer locally (`build-release.ps1 -Version 0.4.0`, restore `rules/rules.json.sig` afterwards).
- [ ] On the user's machine, with consent: verify spec §4.3 (Windows-Einstellungen shows the mode WinWächter set, also after a restart) and §4.9 (user sets one app to „Nie“ in „Hintergrund-App-Berechtigungen“; compare `Disabled`/`DisabledByUser`). If §4.9 does not match, remove the background block (hide group `background`) and note it in the roadmap.
- [ ] Apply and undo one change each from power, visual, gaming, storage; hibernate only with explicit consent.
- [ ] Publish only after the user says so.
