# WinWächter 0.6.0 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** WinWächter gets program updates via winget, a device-health page, a repair page, a weekly check that only reports (Windows notification), and a storage map. Each finding comes with a one-click fix where one is technically possible. The result is version 0.6.0; it is released only after the owner approves.

**Architecture:** Same pattern as 0.5.
- Almost everything new is an *Aktion* (`SystemAction` through the App-side `ActionRunner`). The reversible exceptions are `MoveToAblageChange` (existing, storage map) and the new `wingettelemetry` change.
- Long actions run outside the app-wide `ChangeGate`. Each kind has its own `ActivityLock`.
- winget is read through its CLI table, parsed by column positions.
- The weekly check is a scheduled task that starts `WinWaechter.App.exe --pruefen` elevated and headless. It writes `wartung\letzte-pruefung.json` and, when needed, starts a small unelevated helper `WinWaechter.Notify.exe` in the user's session. The helper shows the Windows notification.
- A click on the notification opens `winwaechter:verbessern` through the protocol registered by the installer.

**Tech Stack:**
- C# 13 / .NET 10, WPF, CommunityToolkit.Mvvm, xUnit.
- Main projects stay `net10.0-windows`. Only the new helper project uses `net10.0-windows10.0.19041.0` for WinRT notifications.
- Only Windows' own tools: `winget.exe`, `dism.exe`, `sfc.exe`, `chkdsk.exe`, `defrag.exe`, `fsutil.exe`, `netsh.exe`, `ipconfig.exe`, `powercfg.exe`, WMI/CIM through `System.Management`, Task Scheduler through the existing `TaskScheduler` package, and the WinRT `ToastNotificationManager`.

**Spec:** `docs/superpowers/specs/2026-11-14-winwaechter-0.6-design.md`. Entries marked **[Entscheidung Claude]** are binding. Style reference: `docs/superpowers/plans/2026-11-12-winwaechter-0.5.md`. Design notes: `docs/design-notes.md`.

**One deviation from the spec, decided here and allowed by spec §4.4 "Risiko":** notifications always go through the unelevated helper. They are never shown from the elevated process. This removes the device-dependent spike and keeps the main projects (and therefore the test project) on `net10.0-windows`. The spec's sentence about a central TFM change no longer applies.

## Global Constraints

- **Branch and release:** work on branch `feature/v0.6` (checked out; the spec is already committed as 9f72c1f). Do not switch branches, do not push, no release or publish.
- **Tools:**
  - `$DOTNET = 'C:\Users\tahsi\AppData\Local\Microsoft\dotnet\dotnet.exe'`, `$SLN = 'C:\Users\tahsi\dev\winwaechter\WinWaechter.sln'`.
  - Set env `DOTNET_CLI_TELEMETRY_OPTOUT=1` and `DOTNET_NOLOGO=1`.
  - One class: `& $DOTNET test $SLN --filter "FullyQualifiedName~<Class>"`. Full suite: `& $DOTNET test $SLN`. It has 1066 tests at the start and must be green after every task.
  - Close a running `WinWaechter.App.exe` from `bin\Debug` before building.
- **PowerShell gotchas:**
  - The PowerShell tool resets the cwd, so use absolute paths.
  - `Set-Content -Encoding utf8` writes a BOM; use `[IO.File]::WriteAllText(path, text, [Text.UTF8Encoding]::new($false))`.
  - Never name a helper function `R`.
- **Commits:** `git -C C:/Users/tahsi/dev/winwaechter commit -m "<subject>" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"`.
- **Packages:** no new NuGet packages. The only new project is `src/WinWaechter.Notify` (Task 13). Nothing sends data anywhere except winget/DISM talking to Microsoft when the user starts them.
- **Never touched:** Dell, Lexware and Haufe. For updates the new local section `updateBlock` decides; it also blocks Intel, NVIDIA, AMD and Realtek. Microsoft packages are offered. Everywhere else `rules.json` → `protected` decides as before. WinWächter itself is never updated, moved or repaired.
- **Local-only rules:** `updateBlock` is local-only, like `privacy` and `bloatApps`. `RuleSet.UnionWith` keeps this set's list.
- **Weekly check:** changes nothing (owner decision „Nur melden“). No `ChangeGate`, no restore point, no write fakes called, no window, no UAC request.
- **Confirmations:**
  - A final action carries `Final = true` and a confirm text naming duration, restart need and what is lost. `ActionRunner.FinalNote` is appended.
  - Harmless actions are click-only: DNS-Cache leeren, Laufwerk optimieren, `chkdsk /scan`, Akkubericht speichern.
- **Restore points:** before program updates (one per run), before „Windows Update reparieren“ and before „Netzwerk komplett zurücksetzen“, through the `Func<string, RestorePointResult>` already used by `OptimizerEngine`.
  - If a point younger than 24 h exists, continue without asking and show „Ein Wiederherstellungspunkt von <HH:mm> besteht schon.“
  - Ask only on a real failure.
- **Locks:** long actions (updates, DISM/SFC, `chkdsk /scan`, defrag) set `SkipGate = true` and a `LockKey`. Short actions use `ChangeGate` as before.
- **Admin:** without admin rights everything readable is shown; buttons are locked with `AppEnvironment.AdminNotice`. Features that act on the signed-in user's account (winget telemetry, weekly check) also need `ownAccount`.
- **External programs:**
  - Fixed absolute paths under `%SystemRoot%\System32`. For winget, the path resolved once via `%LOCALAPPDATA%\Microsoft\WindowsApps\winget.exe`; if missing, „winget fehlt“.
  - Arguments are built only from validated values:
    - winget IDs match `^[A-Za-z0-9][A-Za-z0-9.+_-]{0,127}$`;
    - drive letters match `^[A-Z]$`.
  - No `cmd.exe`, no `reg.exe`, no PowerShell for the new features.
- **Tests:** use fakes and temp dirs. No test runs winget, DISM, sfc, chkdsk, defrag, netsh, ipconfig, fsutil, powercfg, writes the real registry or tasks, or shows a notification. Read-only smoke reads of WMI must tolerate failure.
- **Copy and design:**
  - UI copy is German, Sie-Form, short, one „warum“ sentence per row.
  - Design follows `docs/design-notes.md` and the existing pages (tokens, `DynamicResource`, `SurfaceBox`/`RowDivider` rows, no cards, shadows or gradients). Do not restyle.
  - New pages scroll as a whole (one page `ScrollViewer`, no nested scrolling; lesson from 0.5.1).
- **Device:** device steps in tasks are read-only. Anything that changes the owner's system is in the final checklist (Task 15) and happens only with the owner's consent.

## Review Focus

1. **The winget table shifts:**
   - Cases: a name with wide characters (e.g. „Ü“, CJK), a truncated name ending in „…“, a progress/spinner line before the header, a second table („Die folgenden Pakete haben einen Pin“), and English headers.
   - Expected: parse by the dash line and the header word starts, discard only the broken row, never misread a version as an ID.
   - Test `Parser_survives_spinner_unicode_names_truncation_and_second_table` (Task 2).
2. **The same package appears in both lists:**
   - Cases: in `--scope user` and the full list; or the package disappears (already updated elsewhere) between listing and install.
   - Expected: scope user wins, the package is installed once, and „bereits aktuell“ (winget `0x8A15002B`) counts as success, not failure.
   - Test `Duplicate_scope_entries_install_once_and_no_update_found_is_success` (Task 3).
3. **The weekly check runs while the app is open, or twice at once:**
   - Expected: the check has its own mutex, never takes the app's, and writes the result atomically; a second check exits at once.
   - Test `Second_check_exits_and_result_is_written_atomically` (Task 12).
4. **The storage scan meets cycles, cloud and access problems:**
   - Cases: a junction cycle (`AppData\Local\Application Data`), OneDrive placeholders, a folder without access, a file deleted while scanning.
   - Expected: no infinite loop, placeholders count 0 bytes, „kein Zugriff“ is counted, the scan finishes.
   - Test `Scanner_skips_reparse_points_counts_placeholders_zero_and_survives_access_denied` (Task 10).
5. **The network reset meets fixed settings:**
   - Case: a static IP or fixed DNS servers on an adapter (and a VPN adapter).
   - Expected: the confirmation names the adapter and the values before anything runs.
   - Test `Network_reset_confirmation_lists_static_ip_and_dns` (Task 6).

---

## File Structure

**Core** (`src/WinWaechter.Core/`)
- `Sys/ProcessRunner.cs`: modify; line callback overload.
- `Sys/ITaskStore.cs`, `Sys/WinTaskStore.cs`: modify; `Delete`.
- `Programs/UserProcessRunner.cs`: new; wait and exit code in user context.
- `Actions/SystemAction.cs`: modify; `SkipGate`, `LockKey`, `Stamp`.
- `Actions/ActionStamps.cs`: new; `aktionen.json`.
- `Optimizer/RestorePointService.cs`: modify; recent point.
- `Updates/`: new.
  - `WingetTable.cs`: parser.
  - `WingetClient.cs`: `IWingetClient`, `WingetClient`.
  - `UpdateFilter.cs`.
  - `WingetUpdateService.cs`.
  - `WingetExitCodes.cs`.
  - `WingetTelemetryChange.cs`.
- `Repair/`: new.
  - `RepairActions.cs`: factory for the six tools.
  - `NetworkConfigReader.cs`.
  - `RepairChecks.cs`: DISM CheckHealth, dirty bit.
- `Device/`: new.
  - `BatteryInfo.cs`.
  - `DiskHealth.cs`.
  - `VolumeInfo.cs`.
  - `DeviceScanner.cs`: rows and levels.
- `Storage/Map/`: new.
  - `StorageScanner.cs`.
  - `StorageNode.cs`.
  - `PlaceKind.cs`: location classifier.
  - `StorageOffers.cs`.
  - `Treemap.cs`: squarified layout, pure.
- `Maintenance/`: new.
  - `MaintenanceTask.cs`: task XML, register and delete.
  - `CheckResult.cs`: JSON, atomic.
  - `NotifyRule.cs`.
  - `ToastContent.cs`: XML string.
  - `MaintenanceCheck.cs`: headless run.
- `Rules/RuleSet.cs`, `rules/rules.json`: `updateBlock`.
- `Improvements/ImprovementCollector.cs`: new `ImprovementArea.Geraet`. Action sources are wired in AppServices.

**App** (`src/WinWaechter.App/`)
- `Services/ActivityLock.cs`: new.
- `Services/ActionRunner.cs`: modify.
- `Services/AppServices.cs`, `Services/AppSettings.cs`, `App.xaml.cs`: arguments, check mode, activation pipe.
- `Services/ActivationPipe.cs`: new.
- `ViewModels/UpdatesViewModel.cs`: new, embedded in Programme.
- `ViewModels/DeviceViewModel.cs`, `RepairViewModel.cs`, `StorageMapViewModel.cs`: new.
- Modify `ProgramsViewModel`, `PrivacyViewModel`, `SettingsViewModel`, `OverviewViewModel`, `ShellViewModel`.
- `Views/DeviceView.xaml`, `RepairView.xaml`, `StorageMapView.xaml`: new.
- `Views/Controls/TreemapPanel.cs`: new.
- Modify `ProgramsView.xaml`, `PrivacyView.xaml`, `SettingsView.xaml`, `OverviewView.xaml`, `ShellWindow.xaml`.

**Helper:** `src/WinWaechter.Notify/`: new.
- `WinWaechter.Notify.csproj`: `net10.0-windows10.0.19041.0`, WinExe, `asInvoker`.
- `Program.cs`.

**Installer:** `installer/winwaechter.iss`: `AppUserModelID`, protocol, removing the task on uninstall. `build-release.ps1`: publish Notify into the same folder.

**Tests** (`tests/WinWaechter.Tests/`)
- New:
  - `BuildingBlocks06Tests.cs`
  - `WingetTableTests.cs`
  - `WingetUpdateTests.cs`
  - `WingetTelemetryTests.cs`
  - `RepairTests.cs`
  - `DeviceTests.cs`
  - `StorageMapTests.cs`
  - `MaintenanceTests.cs`
  - `UpdatesViewModelTests.cs`
  - `RepairViewModelTests.cs`
  - `DeviceViewModelTests.cs`
  - `StorageMapViewModelTests.cs`
- Fakes in `Fakes/Fake06.cs`: `FakeWinget`, `FakeUserProcessRunner`, `FakeBattery`, `FakeDiskHealth`, `FakeVolumes`, `FakeNetworkConfig`, `FakeNotifierLauncher`.
- Fixtures in `Fixtures/winget/*.txt`: invented output modelled on real tables. **Never the owner's real program list** (the repo is public).
- Extend `ViewSmokeTests.cs`.

---

### Task 1: Building blocks (process lines, task delete, user process wait, stamps, locks, restore point)

**Files:**
- Modify:
  - `src/WinWaechter.Core/Sys/ProcessRunner.cs`
  - `src/WinWaechter.Core/Sys/ITaskStore.cs`
  - `src/WinWaechter.Core/Sys/WinTaskStore.cs`
  - `src/WinWaechter.Core/Actions/SystemAction.cs`
  - `src/WinWaechter.Core/Optimizer/RestorePointService.cs`
  - `src/WinWaechter.App/Services/ActionRunner.cs`
  - `tests/WinWaechter.Tests/Fakes/FakeProcessRunner.cs`
  - `tests/WinWaechter.Tests/Fakes/FakeTaskStore.cs`
- Create:
  - `src/WinWaechter.Core/Programs/UserProcessRunner.cs`
  - `src/WinWaechter.Core/Actions/ActionStamps.cs`
  - `src/WinWaechter.App/Services/ActivityLock.cs`
  - `tests/WinWaechter.Tests/BuildingBlocks06Tests.cs`
  - `tests/WinWaechter.Tests/Fakes/Fake06.cs` (`FakeUserProcessRunner` only)

**Interfaces:**
- Produces:
  - **`IProcessRunner` line overload:** `Task<(int Exit, string StdOut, string StdErr)> RunAsync(string exe, string args, Action<string>? onLine, CancellationToken ct)`, as a default interface member that calls the old overload and then replays the lines.
    - `ProcessRunner` implements it for real with `OutputDataReceived`. It also splits on `\r`, because winget and DISM redraw progress with CR.
    - `FakeProcessRunner` feeds `onLine` from the handler's stdout.
  - **Task delete:** `void ITaskStore.Delete(string path)`, a no-op when the task is missing. `FakeTaskStore.DeleteCalls`.
  - **User process wait:**
    - `interface IUserProcessRunner { Task<int> RunAsUserAsync(string exe, string args, CancellationToken ct); }`
    - `sealed class UserProcessRunner(string? userSid)`:
      - uses `UserProcess.StartSuspended`, resumes, waits on the process handle (a `ManualResetEvent` with a `SafeWaitHandle` duplicate) and returns `GetExitCodeProcess`;
      - cancellation stops the wait only;
      - throws `UserLaunchException` like `StartAsUser`.
  - **`SystemAction` init properties:**
    - `bool SkipGate`: run without `ChangeGate`; requires a non-null `LockKey`, checked in `ActionRunner`.
    - `string? LockKey`: `"updates"`, `"repair"` or `"disk"`.
    - `bool Stamp`: record the outcome in `ActionStamps`.
  - **Stamps:**
    - `sealed record ActionStamp(string Id, DateTimeOffset At, bool Success, string Summary)`.
    - `sealed class ActionStampStore(string path)` with `ActionStamp? Get(string id)` and `void Set(ActionStamp s)`. The JSON dictionary is written atomically (tmp + move). An unreadable file means empty, plus a log warning.
    - Path: `Path.Combine(env.DataRoot, "aktionen.json")`.
  - **`ActivityLock`** (App, ObservableObject, UI thread):
    - `ActivityLock(string busyText)` with `BusyText`, `IsRunning`, `bool TryBegin()` and `void End()`, the same semantics as `DefenderActivity` without `Others`.
    - Instances:
      - `"updates"` → „Es werden gerade Programme aktualisiert.“
      - `"repair"` → „Eine Reparatur läuft gerade.“
      - `"disk"` → „Das Laufwerk wird gerade geprüft oder optimiert.“
  - **`ActionRunner`:**
    - New constructor `ActionRunner(IDialogService dialogs, ChangeGate gate, DefenderActivity? defender = null, IReadOnlyDictionary<string, ActivityLock>? locks = null, ActionStampStore? stamps = null)`.
    - Busy lock → `ActionOutcome(false, lock.BusyText)` before the confirmation, and `TryBegin` again after it.
    - `SkipGate` actions run directly; the log, catch and error dialog stay as before.
    - `Stamp` → `stamps.Set(new(a.Id, now, outcome.Success, outcome.Message))`.
  - **Restore point:**
    - `RestorePointResult(bool Created, string Message, bool RecentExists = false, DateTimeOffset? RecentAt = null)`.
    - `RestorePointService.TryCreate`: when no new point appears and the newest `SystemRestore.CreationTime` is under 24 h, it returns `RecentExists = true` with that time and the message `$"Ein Wiederherstellungspunkt von {at:HH:mm} besteht schon."`.
    - New helper `static class RestorePointGate { Task<bool> EnsureAsync(Func<string,RestorePointResult> create, string title, IDialogService dialogs) }` in App/Services:
      - Created or RecentExists → true without asking;
      - otherwise `dialogs.Ask(title, "Es konnte kein Wiederherstellungspunkt angelegt werden. Trotzdem fortfahren?")`.

- [ ] **Step 1: Write failing tests** in `BuildingBlocks06Tests`:
  - `Line_overload_of_fake_replays_stdout_lines_in_order`: lines `["a","b"]`.
  - `ProcessRunner_splits_carriage_return_progress`: test the pure static splitter `ProcessRunner.SplitLines("10%\r20%\r\nok\n")` == `["10%","20%","ok"]`. No real process is started.
  - `Skip_gate_action_runs_while_gate_is_held_and_respects_its_lock`: hold the gate with a pending `TaskCompletionSource`. A `SkipGate` action with `LockKey "repair"` runs. A second one with the same key returns `"Eine Reparatur läuft gerade."` and its RunAsync is never called.
  - `Skip_gate_without_lock_key_throws_ArgumentException`.
  - `Stamped_action_writes_stamp_and_store_survives_corrupt_file`: write `"{nope"` first; `Get` → null; after the run, `Get("x").Success == true`.
  - `Recent_restore_point_continues_without_asking` and `Failed_restore_point_asks_and_respects_no`: use `RestorePointGate` with `RecordingDialogs`.
  - `Delete_on_fake_records_path`.
- [ ] **Step 2:** Run `--filter "FullyQualifiedName~BuildingBlocks06Tests"`. Expected: FAIL (does not compile).
- [ ] **Step 3:** Implement the interfaces above.
  - `UserProcessRunner` has no unit test, because it needs a real user token. Keep it under 60 lines and reuse `UserProcess` internals (make `StartSuspended` reachable through an `internal` overload that takes `exe, args`).
- [ ] **Step 4:** Run the class, then the full suite. Expected: PASS, 1066 plus the new tests.
- [ ] **Step 5:** Commit `feat(core): building blocks for 0.6 (line output, task delete, user wait, stamps, activity locks, recent restore point)`.

### Task 2: winget list – parser, client, update filter, `updateBlock`

**Files:**
- Create:
  - `src/WinWaechter.Core/Updates/WingetTable.cs`
  - `src/WinWaechter.Core/Updates/WingetClient.cs`
  - `src/WinWaechter.Core/Updates/UpdateFilter.cs`
  - `tests/WinWaechter.Tests/WingetTableTests.cs`
  - `tests/WinWaechter.Tests/Fixtures/winget/{de-basic,en-basic,de-unknown,de-pinned,de-spinner-unicode,de-truncated,de-empty,de-source-agreement}.txt`. Set them to copy to the output dir in the test csproj.
- Modify:
  - `src/WinWaechter.Core/Rules/RuleSet.cs`: `updateBlock` DTO, validation, `UnionWith` keeps local.
  - `rules/rules.json`: `rulesVersion` +1, `updateBlock`.
  - `tests/WinWaechter.Tests/Fakes/Fake06.cs`: `FakeWinget`.

**Interfaces:**
- Produces:
  - `sealed record WingetPackage(string Name, string Id, string Installed, string Available, bool UnknownVersion, bool UserScope)`.
  - `static class WingetTable { static IReadOnlyList<(string Name, string Id, string Version, string Available, string Source)> Parse(string stdout, Action<string>? onDiscard = null); }`. The algorithm is not fully determined by the tests, so here it is:
    1. Split the text into lines on `\r`/`\n`.
    2. Find the first line that is only `-` (length ≥ 10). The header is the line above it.
    3. Column starts are the indexes in the header where a non-space follows a space (plus index 0). Five columns are expected (Name, ID, Version, Verfügbar/Available, Quelle/Source). Fewer than 5 → empty plus a discard note.
    4. Data rows follow until the first empty line or a line without a token at the ID column.
    5. Widths are in **text elements**: `StringInfo` per line, because winget pads by display cells. East-Asian wide characters count 2. Use `char.IsSurrogate`- and `EastAsianWidth`-free logic: treat code points U+1100–U+115F, U+2E80–U+A4CF, U+AC00–U+D7A3, U+F900–U+FAFF, U+FE30–U+FE4F, U+FF00–U+FF60 and U+FFE0–U+FFE6 as width 2.
    6. A row whose ID contains `…` or fails the ID regex is discarded (`onDiscard(row)`).
    7. A second table (another dash line) is parsed only when the caller asks. Pinned packages are never offered, so `Parse` stops at the first table.
  - `enum WingetListStatus { Ok, Missing, NeedsSourceAgreement, Failed }`.
    - `NeedsSourceAgreement`: detected by exit ≠ 0 and stdout containing `--accept-source-agreements` (not by a guessed exit code).
  - `sealed record WingetList(WingetListStatus Status, IReadOnlyList<WingetPackage> Packages, string? Error)`.
  - `interface IWingetClient`:
    - `Task<WingetList> ListUpgradesAsync(bool acceptSourceAgreements, CancellationToken ct)`;
    - `Task<int> UpgradeAsync(WingetPackage p, Action<string>? onLine, CancellationToken ct)`.
  - `WingetClient(IProcessRunner runner, IUserProcessRunner userRunner, string? wingetPath, string userTempDir)`:
    - **List** runs `upgrade --source winget --include-unknown --disable-interactivity` and the same with `--scope user`, plus `--accept-source-agreements` only when asked. `UserScope` = the ID appears in the user list. `UnknownVersion` = Installed starts with `<` or equals `Unknown`/`Unbekannt`.
    - **Upgrade** runs `upgrade --id <Id> --exact --source winget --silent --disable-interactivity --accept-package-agreements --accept-source-agreements`.
      - User-scope packages go through `userRunner` with `--log "<userTempDir>\winwaechter-winget-<guid>.log"`; the log is read afterwards for `onLine` and then deleted.
      - The ID is re-validated by the regex before any start; invalid → `ArgumentException`.
  - `sealed class UpdateFilter(RuleSet rules, Func<IReadOnlyList<InstalledProgram>> installed)` with `(IReadOnlyList<WingetPackage> Offered, IReadOnlyList<WingetPackage> Hidden) Split(IReadOnlyList<WingetPackage> all)`.
    - Hidden when `rules.IsUpdateBlocked(name, idVendor, publisher: null)`, or a program in `installed()` has the same display name (ordinal-ignore-case, trimmed) and `IsUpdateBlocked(name, publisher, installLocation)`, or the ID starts with `TahsinYilmaz.WinWaechter` or the name starts with `WinWächter`.
    - `idVendor` = the part of the ID before the first `.`.
  - `RuleSet.IsUpdateBlocked(string? name, string? publisher, string? path)` uses matchers of `updateBlock` (`{name|publisher|path|idPrefix}`); `idPrefix` is matched against `idVendor + "."`. `UpdateBlock` is local-only in `UnionWith`.
  - `rules.json` `updateBlock`:
    - `{publisher:"Dell"}`, `{idPrefix:"Dell."}`, `{name:"Dell"}`;
    - `{publisher:"Lexware"}`, `{name:"Lexware"}`, `{idPrefix:"Lexware."}`, `{path:"\\Lexware\\"}`;
    - `{publisher:"Haufe"}`, `{idPrefix:"Haufe."}`;
    - `{idPrefix:"Intel."}`, `{idPrefix:"Nvidia."}`, `{idPrefix:"AMD."}`, `{idPrefix:"Realtek."}`, `{publisher:"Intel"}`, `{publisher:"NVIDIA"}`, `{publisher:"Advanced Micro Devices"}`, `{publisher:"Realtek"}`.
    - Matching is ordinal-ignore-case "contains", like `protected`.
  - `FakeWinget : IWingetClient` with a configurable `WingetList`, an upgrade result per ID, and an `Upgraded` list.

- [ ] **Step 1: Write fixtures and failing tests:**
  - `Parses_german_and_english_tables_to_the_same_rows`: 3 rows, IDs `7zip.7zip`, `Notepad++.Notepad++`, `Microsoft.VCRedist.2015+.x64`; columns Version/Available exact.
  - `Parser_survives_spinner_unicode_names_truncation_and_second_table`: the spinner `-\|/` and a `█▒` bar before the header. Names „Größenprüfer“ and „图片工具“. One row has ID `Very.Long.Id…`. A pinned table follows. Expected: 3 rows, 1 discard, no row from the second table.
  - `Unknown_version_is_flagged`: `< 175.1.0.13416` and `Unknown`.
  - `Empty_or_no_table_gives_ok_and_no_packages` (`de-empty`: „Es wurden keine installierten Pakete gefunden…“).
  - `Source_agreement_prompt_gives_NeedsSourceAgreement`.
  - `Missing_winget_path_gives_Missing_without_running_anything`.
  - `List_marks_user_scope_from_second_call_and_adds_accept_flag_only_when_asked`: check the `FakeProcessRunner.Calls` args exactly.
  - `Upgrade_rejects_ids_with_metacharacters`: `a&b`, `x y`, `..\z`, `"q"`.
  - `Filter_hides_dell_by_id_and_name_lexware_by_installed_publisher_drivers_and_winwaechter_but_offers_microsoft`. Packages:
    - `Dell.Optimizer`;
    - `Dell Display and Peripheral Manager` with ID `Dell.DisplayAndPeripheralManager`;
    - `Lexware Office` with ID `Foo.Bar`, plus an installed program whose publisher is `Haufe-Lexware GmbH & Co. KG`;
    - `Intel.IntelDriverAndSupportAssistant`;
    - `Microsoft.VCRedist.2015+.x64` and `Microsoft.PowerToys`.
    
    Expected: 2 offered, 4 hidden.
  - `UpdateBlock_from_network_is_ignored_by_UnionWith`.
- [ ] **Step 2:** Run the new classes. Expected: FAIL.
- [ ] **Step 3:** Implement. Use `RuleSet` validation like `bloatApps`: a matcher needs at least one field, and `idPrefix` matches `^[A-Za-z0-9][A-Za-z0-9+_-]*\.$`.
- [ ] **Step 4:** Run the full suite. Expected: PASS.
- [ ] **Step 5:** Read-only device check: run `winget upgrade --source winget --include-unknown --disable-interactivity` once, feed it to `WingetTable.Parse` in a scratch console or test with `[Trait("Device","1")]` that is skipped by default, and compare the count with the winget footer. Do **not** commit the output.
- [ ] **Step 6:** Commit `feat(updates): read winget upgrades with a column parser and the local updateBlock list`.

### Task 3: Installing updates (service, exit codes, restore point)

**Files:**
- Create: `src/WinWaechter.Core/Updates/WingetUpdateService.cs`, `src/WinWaechter.Core/Updates/WingetExitCodes.cs`, `tests/WinWaechter.Tests/WingetUpdateTests.cs`

**Interfaces:**
- Consumes: Task 1 (`SystemAction` flags, `RestorePointResult`) and Task 2 (`IWingetClient`, `UpdateFilter`, `WingetPackage`).
- Produces:
  - `sealed record UpdateResult(WingetPackage Package, UpdateOutcome Outcome, int ExitCode, string Text)`, with `enum UpdateOutcome { Updated, AlreadyCurrent, RestartNeeded, Failed, Skipped }`.
  - `static class WingetExitCodes { static (UpdateOutcome Outcome, string Text) Map(int code); }`. Map exactly these codes, so nothing is guessed:
    - `0` → Updated;
    - `0x8A15002B` (no applicable update) → AlreadyCurrent „War bereits aktuell.“;
    - `0x8A150109` (reboot required to finish) and `0x8A15010A` (reboot initiated) → RestartNeeded „Neustart nötig, damit das Update fertig wird.“;
    - `0x8A150101` (installer in use / application running) → Failed „Das Programm läuft gerade. Schließen Sie es und versuchen Sie es erneut.“;
    - `0x8A150014` (download failed) → Failed „Download fehlgeschlagen. Prüfen Sie die Internetverbindung.“;
    - `0x8A150011` (installer hash mismatch) → Failed „Die heruntergeladene Datei stimmt nicht. Das Update wurde aus Sicherheitsgründen nicht installiert.“;
    - `0x8A15000F` (no applicable installer) → Failed „Für diesen Rechner gibt es kein passendes Installationsprogramm.“;
    - anything else → Failed with `$"Fehlgeschlagen (Code 0x{code:X8}), Details im Protokoll."`.
    
    **Before writing:** verify each constant against `https://github.com/microsoft/winget-cli/blob/master/doc/windows/package-manager/winget/returnCodes.md` (read-only web fetch). Drop any code whose meaning differs, and note that in the commit.
  - `sealed class WingetUpdateService(IWingetClient client, UpdateFilter filter, Func<string, RestorePointResult> restorePoint)`:
    - `Task<(WingetList List, IReadOnlyList<WingetPackage> Offered, IReadOnlyList<WingetPackage> Hidden)> LoadAsync(bool acceptSourceAgreements, CancellationToken ct)`;
    - `SystemAction InstallAction(IReadOnlyList<WingetPackage> selected, Func<IReadOnlyList<UpdateResult>, Task> onDone)`: `Id "winget-upgrade"`, `Final = true`, `SkipGate = true`, `LockKey "updates"`, `Stamp = true`.
      - ConfirmText = the three sentences from spec §4.1 „Bestätigung“, starting with `$"{n} Programme werden aktualisiert."`. `Items` = the names with „alt → neu“.
      - The full signature is `InstallAction(IReadOnlyList<WingetPackage> selected, Func<Task<bool>> ensureRestorePoint, Func<IReadOnlyList<UpdateResult>, Task> onDone)`. The restore point is taken after the confirmation, as the first thing inside RunAsync, through `ensureRestorePoint` (in production `RestorePointGate.EnsureAsync`).
      - Run: dedupe by ID (user scope wins), then before each package re-run `filter.Split` on it (protection check). Hidden → `Skipped` „Geschützt, ausgelassen.“.
      - Progress `$"Paket {i} von {n}: {name} …"`. `ct` is checked **between** packages only.
      - Log each result as `FileLog.Info($"winget {id} {installed} -> {available}: 0x{code:X8}")`.
      - Outcome message: `$"{updated} aktualisiert, {failed} fehlgeschlagen"`, plus `", Neustart nötig"` when any result needs a restart.

- [ ] **Step 1: Failing tests:**
  - `Duplicate_scope_entries_install_once_and_no_update_found_is_success`.
  - `Exit_codes_map_to_outcomes`: one assertion per mapped code plus an unknown code.
  - `Cancel_stops_after_current_package`: cancel inside the 1st upgrade, so the 2nd never runs and the 1st has a result.
  - `Protected_package_slipped_into_selection_is_skipped_not_installed`.
  - `Restore_point_declined_runs_nothing`.
  - `Install_action_is_final_skips_gate_and_lists_versions`: `Items[0] == "7-Zip: 26.00 → 26.06"`.
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run the full suite. Expected: PASS.
- [ ] **Step 5:** Commit `feat(updates): install selected winget updates one by one with clear results`.

### Task 4: „Updates verfügbar“ on the Programme page (+ suggestion)

**Files:**
- Create: `src/WinWaechter.App/ViewModels/UpdatesViewModel.cs`, `tests/WinWaechter.Tests/UpdatesViewModelTests.cs`
- Modify:
  - `src/WinWaechter.App/ViewModels/ProgramsViewModel.cs`: property `Updates`; ctor parameter `UpdatesViewModel updates`.
  - `src/WinWaechter.App/Views/ProgramsView.xaml`: box at the top, above „Vorinstallierte Apps“.
  - `src/WinWaechter.App/Services/AppServices.cs`.
  - `src/WinWaechter.App/ViewModels/ShellViewModel.cs`: the first Programme visit also loads Updates.
  - `tests/WinWaechter.Tests/ViewSmokeTests.cs`.

**Interfaces:**
- Consumes: Task 3 (`WingetUpdateService`) and Task 1 (`ActionRunner`, `RestorePointGate`).
- Produces:
  - **`UpdatesViewModel`:**
    - Constructor: `(WingetUpdateService service, ActionRunner runner, IDialogService dialogs, IExplorerShell shell, bool canChange, Func<Task<bool>> ensureRestorePoint)`.
    - Properties:
      - `ObservableCollection<UpdateRowViewModel> Rows` (`Name`, `Versions` = „26.00 → 26.06“, `IsSelected`, `UnknownVersion`, `Note`);
      - `Hidden` (names) and `HiddenText` = `$"{n} Programme bewusst ausgelassen (geschützt)"`;
      - `Status` (`WingetListStatus`), `IsLoading`, `HasLoaded`, `ProgressText`, `ResultText`.
    - Commands:
      - `RefreshCommand` (`AsyncRelayCommand`);
      - `UpdateSelectedCommand`: CanExecute = `canChange && selected > 0 && !lock busy`;
      - `UpdateOneCommand(UpdateRowViewModel)`;
      - `SelectAllCommand`;
      - `AcceptSourceAndSearchCommand`: Confirm text „Damit nehmen Sie die Bedingungen der winget-Quelle von Microsoft an.“;
      - `OpenStoreCommand`: opens `ms-windows-store://pdp/?ProductId=9NBLGGH4NNS1`. Extend `SystemLinks.IsAllowed` with exactly this one URI.
    - Preselected = `!UnknownVersion`. The header text is `$"Updates verfügbar ({Rows.Count})"`, or „Alle Programme sind aktuell“ when 0.
    - After an install run, `RefreshCommand` runs again.
  - **Improvement source:** in AppServices, `new ImprovementSource("Programm-Updates", …)` returns an `ActionSuggestion("updates", $"{n} Programm-Updates verfügbar", "Veraltete Programme sind ein häufiges Einfallstor.", navigateAction)`.
    - `navigateAction` is a click-only `SystemAction("updates-open", …, ButtonText "Ansehen", ConfirmText null, Final false)` whose RunAsync navigates to Programme and returns `ActionOutcome(true, "")`. In `ImproveViewModel`, an empty-message outcome must not show a result dialog; check that and adjust if needed.
    - Nothing is suggested for 0 updates or when the status is not Ok.

- [ ] **Step 1: Failing tests:**
  - `Loads_rows_preselects_known_versions_and_counts_hidden`.
  - `Missing_winget_shows_store_button_and_no_rows`.
  - `Needs_source_agreement_asks_then_reloads_with_accept_flag`.
  - `Update_selected_locked_without_admin_and_while_updates_lock_busy`.
  - `After_install_list_is_reloaded_and_result_text_set`: `"1 aktualisiert, 0 fehlgeschlagen"`.
  - Smoke: `ProgramsView_with_updates_renders_light_and_dark_and_list_reachable_in_low_window`, reusing `AssertProgramListReachable`.
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3:** Implement. XAML: the box uses `SurfaceBox`, rows are `RowDivider`, and there is no inner `ScrollViewer`.
- [ ] **Step 4:** Run the full suite. Expected: PASS.
- [ ] **Step 5:** Commit `feat(app): program updates box on Programme and a suggestion in Rechner in Ordnung bringen`.

### Task 5: winget usage data off (`wingettelemetry`)

**Files:**
- Create: `src/WinWaechter.Core/Updates/WingetTelemetryChange.cs`, `tests/WinWaechter.Tests/WingetTelemetryTests.cs`
- Modify: `src/WinWaechter.App/ViewModels/PrivacyViewModel.cs`, `src/WinWaechter.App/Views/PrivacyView.xaml`, `src/WinWaechter.App/Services/AppServices.cs` (register the kind and the improvement source)

**Interfaces:**
- Produces:
  - **Settings path:** `static class WingetSettingsFile { static string PathFor(string userProfile) => …\AppData\Local\Packages\Microsoft.DesktopAppInstaller_8wekyb3d8bbwe\LocalState\settings.json; static TelemetryState Read(IFileOps f, string path); }`, with `enum TelemetryState { On, Off, Unreadable }`.
    - A missing file counts as On.
    - Parse with `JsonDocumentOptions { CommentHandling = Skip, AllowTrailingCommas = true }`. `telemetry.disable == true` → Off.
  - **Text edit:** `static string? WingetSettingsFile.SetDisable(string text, bool disable)` returns the new text, or null when the spot is ambiguous. Rules:
    - The text has a top-level `"telemetry"` object with `"disable"`: replace that literal.
    - It has `"telemetry"` without `"disable"`: insert `"disable": true` as the first member.
    - Neither: insert `"telemetry": { "disable": true },` after the top-level `{`.
    - An empty or missing file → `{\n    "telemetry": { "disable": true }\n}`.
    - Comments, other members and formatting stay byte-identical.
    - It finds tokens with a small scanner that skips strings and `//` / `/* */` comments. Do not build the edit on `JsonNode`.
  - **`WingetTelemetryChange : IChange`:**
    - `Kind "wingettelemetry"`, `Target` = the path, `Description` „winget-Nutzungsdaten abschalten“.
    - `Apply` stores the full prior text in `Before["text"]` (null when the file was missing) and writes atomically.
    - `Undo` writes the prior text back, or deletes the file when it was missing.
    - `CheckDrift`: Unchanged while Off.
    - `FromRecord` accepts only a `Target` equal to `PathFor(<signed-in user profile>)`.
    - `RetryData` → `({}, {})`.
  - **Privacy page:** a new section „Programme“ with one row, title „winget-Nutzungsdaten abschalten“.
    - Why: „winget schickt sonst Nutzungsdaten an Microsoft.“
    - Shown only when winget exists. Locked without `ownAccount` or `canChange`; Unreadable → note „Die winget-Einstellungen sind nicht eindeutig lesbar und bleiben unverändert.“
    - Improvement source „winget“ → `Improvement("winget-telemetry", DatenschutzWerbung, …)` when the state is On.

- [ ] **Step 1: Failing tests:**
  - `SetDisable_keeps_comments_and_other_members_byte_identical`: the default winget file with its `// For documentation…` comment plus `"visual": {"progressBar":"rainbow"}`.
  - `SetDisable_replaces_existing_false`.
  - `SetDisable_inserts_into_existing_telemetry`.
  - `SetDisable_returns_null_for_two_telemetry_objects_or_broken_text`.
  - `Missing_file_apply_creates_and_undo_deletes`.
  - `Apply_undo_restores_exact_prior_text`.
  - `FromRecord_rejects_other_paths`.
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run the full suite. Expected: PASS.
- [ ] **Step 5:** Commit `feat(privacy): turn off winget usage data as an undoable change`.

### Task 6: Repair tools (Core)

**Files:**
- Create:
  - `src/WinWaechter.Core/Repair/RepairActions.cs`
  - `src/WinWaechter.Core/Repair/NetworkConfigReader.cs`
  - `src/WinWaechter.Core/Repair/RepairChecks.cs`
  - `tests/WinWaechter.Tests/RepairTests.cs`
  - fakes `FakeNetworkConfig` in `Fake06.cs`

**Interfaces:**
- Consumes: Task 1. Also existing: `IProcessRunner`, `IServiceControl` (check its name and members in `Sys/`; use its stop/start/state methods), `IFileOps`, `SystemActions.RestartExplorer` (reuse its Explorer-ending path and the 15 s hint).
- Produces:
  - **Network config:** `sealed record AdapterConfig(string Name, bool StaticIp, IReadOnlyList<string> Ips, bool StaticDns, IReadOnlyList<string> Dns)`, `interface INetworkConfig { IReadOnlyList<AdapterConfig> Read(); }` and `WmiNetworkConfig`.
    - Source: `Win32_NetworkAdapterConfiguration WHERE IPEnabled=TRUE`.
    - `StaticIp = !DHCPEnabled`.
    - `StaticDns` = the `NameServer` registry value under `HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{SettingID}` is non-empty.
  - **`sealed class RepairActions`:**
    - Constructor: `(IProcessRunner run, IServiceControl services, IFileOps files, INetworkConfig net, Func<Task<bool>> ensureRestorePoint, string windowsDir, string localAppData, DateTime today)`.
    - All actions have `Stamp = true`.
    - **1.** `SystemAction SystemFiles()`: Id `repair-sfc`. `SkipGate`, `LockKey "repair"`, not final (it repairs Windows' own files), with ConfirmText „Prüft die Windows-Systemdateien und repariert beschädigte. Dauer meist 10–30 Minuten, Internet nötig (Ersatzdateien kommen über Windows Update).“
      - Runs `System32\dism.exe /Online /Cleanup-Image /RestoreHealth`, then `System32\sfc.exe /scannow`, with the line overload.
      - Progress: the last `(\d{1,3}(\.\d)?)%` match.
      - `ct` is checked only between the two programs.
      - Result text from the exit codes and output:
        - sfc output containing `keine Integritätsverletzungen` or `did not find any integrity violations` → „Keine Fehler gefunden.“;
        - `erfolgreich repariert` or `successfully repaired` → „Fehler gefunden und repariert. Ein Neustart wird empfohlen.“;
        - `konnte einige … nicht reparieren` or `unable to fix` → „Einige Fehler ließen sich nicht reparieren. Details: C:\Windows\Logs\CBS\CBS.log“;
        - DISM exit ≠ 0 → „DISM ist fehlgeschlagen (Code …). sfc wurde trotzdem ausgeführt.“
    - **2.** `SystemAction WindowsUpdate()`: Id `repair-wu`, Final, ConfirmText from spec §4.3.2.
      - First `ensureRestorePoint`; declined → `ActionOutcome(false, "Abgebrochen.")`.
      - Stop `wuauserv`, `bits`, `cryptsvc` and remember their running state.
      - Delete older `SoftwareDistribution.bak-????????` and `catroot2.bak-????????` (regex `^(SoftwareDistribution|catroot2)\.bak-\d{8}$`; only those).
      - Rename both to `.bak-{today:yyyyMMdd}`. If that name exists (second run today), append `-2`.
      - Restart the services that were running, always in `finally`.
    - **3a.** `FlushDns()`: Id `repair-dns`, click-only, runs `ipconfig /flushdns`.
    - **3b.** `RenewIp()`: Id `repair-renew`, ConfirmText „Die Verbindung ist kurz weg.“, runs `/release` then `/renew`.
    - **3c.** `NetworkReset()`: Id `repair-net`, Final.
      - ConfirmText = the spec §4.3.3c text, plus one line per adapter with a static IP or DNS: `$"{Name}: IP {string.Join(", ", Ips)}"` / `$"{Name}: DNS {…}"`. These lines go into `Items`.
      - Runs `ensureRestorePoint`, `netsh winsock reset`, `netsh int ip reset`.
      - Outcome: „Netzwerk zurückgesetzt. Bitte starten Sie den Rechner neu.“
    - **4.** `PrintQueue()`: Id `repair-spool`, Final, „Wartende Druckaufträge werden gelöscht.“
      - The action is built only when `…\spool\PRINTERS` holds files. Otherwise `PrintQueueEmpty` = true, and the VM shows „Die Warteschlange ist leer“.
      - Stop Spooler, delete the files (not folders) in that one directory, start Spooler in `finally`.
    - **5.** `IconCache()`: Id `repair-icons`, ConfirmText from spec §4.3.5.
      - End Explorer through the existing path, delete `localAppData\IconCache.db` and `localAppData\Microsoft\Windows\Explorer\{iconcache_*,thumbcache_*}.db`, and wait for Windows to bring the shell back (existing 15 s hint).
      - A locked file is skipped and counted: „n Dateien waren gesperrt und bleiben bis zum nächsten Neustart.“
    - **6.** `ScanDisk(char drive)`: Id `repair-chkdsk-scan`, click-only, `SkipGate`, `LockKey "disk"`, runs `chkdsk X: /scan`.
      - Result parsing: output with `Windows hat das Dateisystem überprüft und keine Probleme gefunden` / `found no problems` → ok. Otherwise `ScanFoundErrors = true`, and the VM then offers `FixDiskOnRestart(drive)`.
      - `FixDiskOnRestart(char drive)`: Id `repair-chkdsk-fix`, Final, ConfirmText from spec §4.3.6. Runs `fsutil dirty set X:`, which is the safe, documented way to get autochk at the next boot.
      - Outcome: „Die Reparatur läuft beim nächsten Neustart.“
  - `sealed class RepairChecks(IProcessRunner run)`:
    - `Task<bool?> ComponentStoreRepairableAsync(CancellationToken ct)` runs `dism /Online /Cleanup-Image /CheckHealth`: `true` when the output contains `reparierbar` or `repairable`, `false` on `Keine Komponentenspeicherbeschädigung` or `No component store corruption`, `null` otherwise.
    - `Task<bool?> IsDirtyAsync(char drive, CancellationToken ct)` runs `fsutil dirty query X:`.

- [ ] **Step 1: Failing tests:** one per tool on the exact command lines (exe path + args, in order) through `FakeProcessRunner`, plus:
  - `Windows_update_repair_restarts_running_services_even_when_rename_fails`.
  - `Windows_update_repair_only_deletes_own_bak_pattern`: a folder `SoftwareDistribution.old` and `catroot2.bak-2026` stay.
  - `Network_reset_confirmation_lists_static_ip_and_dns`.
  - `Print_queue_empty_builds_no_action`.
  - `Icon_cache_counts_locked_files`.
  - `Sfc_result_texts_for_clean_repaired_and_unfixable_in_german_and_english`.
  - `Dism_progress_percent_is_reported`: lines `[==   10.0%   ]` then `[====  42.5% ]` → the last report is `"42.5 %"`.
  - `Chkdsk_scan_error_output_sets_ScanFoundErrors`.
  - `Fix_on_restart_is_final_and_uses_fsutil_dirty_set`.
  - `CheckHealth_parses_three_states`.
  - `Drive_letter_must_be_A_to_Z`.
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3:** Implement. Output matching is done on `ToLowerInvariant()` text.
- [ ] **Step 4:** Run the full suite. Expected: PASS.
- [ ] **Step 5:** Commit `feat(repair): six repair tools as actions with exact commands and plain results`.

### Task 7: Reparatur page

**Files:**
- Create: `src/WinWaechter.App/ViewModels/RepairViewModel.cs`, `src/WinWaechter.App/Views/RepairView.xaml(.cs)`, `tests/WinWaechter.Tests/RepairViewModelTests.cs`
- Modify:
  - `ShellViewModel.cs`: ctor parameter `RepairViewModel repair`; nav item „Reparatur“, glyph `\uE15E` (Repair); `\uE90F` is already taken by Optimieren.
  - `ShellWindow.xaml`: DataTemplate.
  - `AppServices.cs`.
  - `ViewSmokeTests.cs`.

**Interfaces:**
- Consumes: Task 6 (`RepairActions`, `RepairChecks`) and Task 1 (`ActionRunner`, `ActionStampStore`, `ActivityLock "repair"`).
- Produces:
  - **`RepairViewModel`:**
    - Constructor: `(RepairActions actions, RepairChecks checks, ActionRunner runner, ActionStampStore stamps, ActivityLock repairLock, bool canChange, char systemDrive)`.
    - `ObservableCollection<RepairRowViewModel> Rows`, each with `Title`, `When` („Wenn …“), `Duration`, `NeedsRestart`, `LastRun` (`$"Zuletzt: {At:dd.MM.yyyy HH:mm} – {Summary}"` or „Noch nie ausgeführt“), `RunCommand`, `IsRecommended`, `ProgressText`, `ResultText`.
    - The `chkdsk` row shows a second button „Beim nächsten Neustart reparieren“ when `ScanFoundErrors` is set.
    - `LoadAsync()` is called on the first visit. It reads the stamps, runs `ComponentStoreRepairableAsync` (no lock, quick) and `IsDirtyAsync(systemDrive)`. Recommended rows move to the top with „Empfohlen“.
    - Row order and „Wenn …“ texts:
      1. Systemdateien: „Wenn Windows abstürzt, Programme sich seltsam verhalten oder Updates scheitern.“
      2. Windows Update: „Wenn Updates immer wieder fehlschlagen oder hängen.“
      3. DNS: „Wenn Webseiten nicht laden, andere aber schon.“
      4. Verbindung neu: „Wenn das Netzwerk verbunden ist, aber nichts geht.“
      5. Netzwerk komplett: „Wenn nichts anderes geholfen hat.“
      6. Drucker: „Wenn ein Druckauftrag hängt und nichts mehr druckt.“
      7. Symbole: „Wenn Symbole falsch oder leer angezeigt werden.“
      8. Datenträger prüfen: „Wenn Dateien beschädigt sind oder Windows Fehler meldet.“
    - While `repairLock.IsRunning`, every `SkipGate` row's command is disabled with the lock text.
  - **Improvement source** „Reparatur“ (in AppServices): `ActionSuggestion("repair-sfc", "Systemdateien reparieren", "Windows meldet beschädigte Systemdateien.", actions.SystemFiles())` when CheckHealth says repairable. A dirty system drive → `ActionSuggestion("repair-chkdsk", …, actions.ScanDisk(systemDrive))`.

- [ ] **Step 1: Failing tests:**
  - `Rows_show_last_run_from_stamps_or_never`.
  - `Repairable_store_moves_sfc_to_top_as_recommended`.
  - `Running_repair_disables_other_long_rows`.
  - `Scan_errors_reveal_fix_on_restart_button`.
  - `Locked_without_admin`.
  - Smoke: `RepairView_renders_light_dark_and_scrolls_in_low_window`.
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run the full suite. Expected: PASS.
- [ ] **Step 5:** Commit `feat(app): Reparatur page`.

### Task 8: Device health (Core)

**Files:**
- Create:
  - `src/WinWaechter.Core/Device/BatteryInfo.cs`
  - `src/WinWaechter.Core/Device/DiskHealth.cs`
  - `src/WinWaechter.Core/Device/VolumeInfo.cs`
  - `src/WinWaechter.Core/Device/DeviceScanner.cs`
  - `tests/WinWaechter.Tests/DeviceTests.cs`
  - fakes `FakeBattery`, `FakeDiskHealth`, `FakeVolumes`

**Interfaces:**
- Produces:
  - **Battery:**
    - `sealed record BatteryState(long? DesignMWh, long? FullMWh, int? Cycles)`.
    - `interface IBatteryInfo { BatteryState? Read(); }`: null means no battery.
    - `WmiBatteryInfo(Func<bool> hasBattery, IProcessRunner run, string tempDir)`:
      - `root\wmi` classes: `BatteryStaticData.DesignedCapacity`, `BatteryFullChargedCapacity.FullChargedCapacity`, `BatteryCycleCount.CycleCount`.
      - If Design is missing, the fallback runs `powercfg /batteryreport /xml /output <tempDir>\bat.xml` and reads `DesignCapacity`, `FullChargeCapacity` and `CycleCount` (namespace-agnostic `LocalName`), then deletes the file.
  - **Disks:**
    - `sealed record DiskState(string Name, string Media, string Health, ulong? Wear, int? TempC, int? TempMaxC, ulong? ReadErrorsUncorrected, ulong? WriteErrorsUncorrected, ulong? PowerOnHours)`.
    - `interface IDiskHealth { IReadOnlyList<DiskState> Read(); }`.
    - `CimDiskHealth`: `root\Microsoft\Windows\Storage`, `MSFT_PhysicalDisk` (FriendlyName, MediaType 3=HDD / 4=SSD, HealthStatus 0=Healthy / 1=Warning / 2=Unhealthy). Counters come via the association `MSFT_PhysicalDiskToStorageReliabilityCounter` (or `GetRelated("MSFT_StorageReliabilityCounter")`). Missing values → null.
  - **Volumes:**
    - `sealed record VolumeState(char Letter, string FileSystem, ulong Size, ulong Free, bool IsSsd, DateTime? LastOptimized, bool? Dirty, int RecentDiskErrors)`.
    - `interface IVolumeInfo { IReadOnlyList<VolumeState> Read(); }`.
    - `WinVolumeInfo(RepairChecks checks, IEventSource events)`:
      - fixed drives only (`DriveInfo`, `DriveType.Fixed`);
      - `LastOptimized` from `HKLM\SOFTWARE\Microsoft\Dfrg\Statistics\<VolumeId>\LastRunTime` (FILETIME), null if missing;
      - `RecentDiskErrors` = the count of System log events from providers `Ntfs`, `disk` and `volmgr` at level Error in the last 30 days. Wrap the existing `System.Diagnostics.EventLog` use behind `IEventSource` with fakes. Check whether `WinBootEventSource` can be generalized; otherwise make a new small interface.
  - **Scanner:**
    - `enum DeviceLevel { Good, Attention, Bad, Unknown }`.
    - `sealed record DeviceRow(string Id, string Title, string Detail, DeviceLevel Level, string Why, SystemAction? Fix, SystemAction? Extra)`.
    - `sealed class DeviceScanner(IBatteryInfo battery, IDiskHealth disks, IVolumeInfo volumes, RepairActions repair, IProcessRunner run, string downloadsDir, DateTime now)` with `IReadOnlyList<DeviceRow> Scan()`. Levels are exactly the spec values:
      - **Battery:**
        - wear = 1 − Full/Design: < 20 % Good; 20–40 % Attention; > 40 % Bad „Akku stark gealtert – ein Austausch lohnt sich, wenn die Laufzeit nicht mehr reicht“.
        - Missing Design → Unknown with Detail „Ursprüngliche Kapazität nicht lesbar“.
        - `Fix` = null. `Extra` = „Ausführlichen Akkubericht speichern“: click-only, runs `powercfg /batteryreport /output "<downloads>\Akkubericht-{yyyyMMdd}.html"` and returns `ActionOutcome(true, "Gespeichert in Downloads.", Link: path)`. Allow that path in `SystemLinks` only when it is under the downloads folder and ends with `.html`.
      - **Disk:**
        - Bad when Health ≠ Healthy or Wear ≥ 90: „Sichern Sie Ihre Daten bald.“
        - Attention when Wear ≥ 70, uncorrected errors > 0, or Temp > TempMax.
        - Detail = „Verschleiß x %, y °C, z Betriebsstunden“; missing values are left out; all missing → „Weitere Werte nicht verfügbar“.
      - **Volume:**
        - `LastOptimized` older than 30 days or null → Attention, with Fix = an `Optimize(letter)` action: click-only, `SkipGate`, `LockKey "disk"`, runs `defrag X: /O /U`, progress from `%`.
        - `Dirty == true` or `RecentDiskErrors > 0` → Attention, with Fix = `repair.ScanDisk(letter)`.
        - Free < 10 % → Attention „Wenig freier Platz“, with Fix = null and Extra = navigate to Speicherplatz (a navigation callback passed in; null in tests).
  - `ImprovementArea.Geraet` (Title „Gerät“) is added. It is used only for action suggestions.

- [ ] **Step 1: Failing tests:**
  - `Battery_levels_at_19_20_40_41_percent_wear`.
  - `Battery_without_design_capacity_is_unknown_without_warning`.
  - `No_battery_gives_no_battery_row`.
  - `Disk_levels_for_health_wear_errors_and_temperature`.
  - `Disk_missing_counters_says_not_available`.
  - `Volume_not_optimized_for_31_days_offers_optimize_with_defrag_O_U`.
  - `Dirty_volume_offers_scan_disk`.
  - `Battery_report_link_only_allowed_under_downloads`.
  - `Powercfg_xml_fallback_reads_capacities` (fixture XML with a namespace).
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run the full suite. Expected: PASS.
- [ ] **Step 5:** Read-only device check: read `CimDiskHealth().Read()` and `WmiBatteryInfo` elevated through a skipped-by-default `[Trait("Device","1")]` test. The values must match `Get-PhysicalDisk` (Micron 2650 1 TB, Healthy) and FullChargedCapacity 42818 (± current). Do not commit values.
- [ ] **Step 6:** Commit `feat(device): battery, SSD and volume health with levels and fixes`.

### Task 9: Gerät page (+ suggestions)

**Files:**
- Create: `src/WinWaechter.App/ViewModels/DeviceViewModel.cs`, `src/WinWaechter.App/Views/DeviceView.xaml(.cs)`, `tests/WinWaechter.Tests/DeviceViewModelTests.cs`
- Modify: `ShellViewModel.cs` (nav „Gerät“, glyph `\uE7F8`), `ShellWindow.xaml`, `AppServices.cs`, `ViewSmokeTests.cs`

**Interfaces:**
- Consumes: Task 8 (`DeviceScanner`), `ActionRunner` and `ActivityLock "disk"`.
- Produces:
  - **`DeviceViewModel`:**
    - Constructor: `(Func<CancellationToken, Task<IReadOnlyList<DeviceRow>>> scan, ActionRunner runner, ActivityLock diskLock, bool canChange)`.
    - Groups „Akku“, „Laufwerke (Hardware)“, „Laufwerke (Windows)“.
    - Each row shows Level as a colored dot via the existing light tokens, plus `FixCommand` and `ExtraCommand`.
    - `RefreshCommand` runs on the first visit and after each fix.
  - **Improvement source** „Gerät“: each row with Level ≥ Attention and `Fix != null` → `ActionSuggestion("device:" + Id, Title, Why, Fix)`.

- [ ] **Step 1: Failing tests:**
  - `Groups_rows_and_hides_battery_group_without_battery`.
  - `Fix_runs_through_runner_and_rescans`.
  - `Disk_lock_busy_disables_optimize_and_scan`.
  - `Suggestions_only_for_attention_with_fix`.
  - Smoke: `DeviceView_renders_light_dark`.
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run the full suite. Expected: PASS.
- [ ] **Step 5:** Commit `feat(app): Gerät page and device suggestions`.

### Task 10: Storage scan, places and offers (Core)

**Files:**
- Create:
  - `src/WinWaechter.Core/Storage/Map/StorageScanner.cs`
  - `StorageNode.cs`
  - `PlaceKind.cs`
  - `StorageOffers.cs`
  - `Treemap.cs`
  - `tests/WinWaechter.Tests/StorageMapTests.cs`

**Interfaces:**
- Produces:
  - **`StorageNode`:** `sealed class StorageNode { string Name; string FullPath; bool IsDir; long Bytes; long Files; bool NoAccess; StorageNode? Parent; List<StorageNode> Children; }`, plus `IEnumerable<StorageNode> LargestFiles(int max, long minBytes)` on the root.
  - **`StorageScanner`:** `sealed class StorageScanner(Func<string, IEnumerable<FileSystemInfo>> enumerate)`. The default uses `new DirectoryInfo(p).EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = 0, RecurseSubdirectories = false, ReturnSpecialDirectories = false })`; tests inject their own.
    - `Task<StorageNode> ScanAsync(string root, IProgress<(long Files, long Bytes)>? p, CancellationToken ct)`:
      - iterative (explicit stack, no recursion);
      - does not descend into `ReparsePoint` directories and counts their size as 0;
      - files with `RecallOnDataAccess` (0x400000) or `Offline` count 0 bytes;
      - `UnauthorizedAccessException`, `DirectoryNotFoundException`, `IOException` on a directory → `NoAccess = true` and continue;
      - a file that vanished is skipped;
      - progress at most every 250 ms.
  - **Places:** `enum PlaceKind { Normal, Temp, BrowserCache, WindowsUpdateDownload, RecycleBin, Hibernation, PageFile, WinSxS, Windows, ProgramFiles, ProgramData, OtherUserProfile, OneDrive, UserLibrary, ProgramFolder, Protected }`, with `static PlaceKind Classify(string path, PlaceContext ctx)`.
    - `PlaceContext(string WindowsDir, string ProgramFiles, string ProgramFilesX86, string ProgramData, string UserProfile, IReadOnlyList<string> OneDriveRoots, IReadOnlyList<string> UserLibraries, Func<string, InstalledProgram?> programFor, Func<string,bool> isProtectedPath)`.
    - Order of checks (first wins):
      1. Protected (`rules` path matchers and `\Lexware\`, `isProtectedPath`);
      2. OneDrive;
      3. RecycleBin (`X:\$Recycle.Bin`);
      4. Hibernation (`X:\hiberfil.sys`);
      5. PageFile (`pagefile.sys`, `swapfile.sys`);
      6. WinSxS;
      7. WindowsUpdateDownload (`Windows\SoftwareDistribution\Download`);
      8. Temp (user `%TEMP%`, `Windows\Temp`);
      9. BrowserCache (the cache dirs `CacheScanner` knows; reuse its list);
      10. Windows;
      11. ProgramFolder (`programFor(path) != null`);
      12. ProgramFiles / ProgramData;
      13. OtherUserProfile (under `C:\Users\` but not this profile);
      14. UserLibrary (Downloads, Desktop, Documents, Videos, Pictures, Music of this user);
      15. Normal.
  - **Offers:** `sealed record StorageOffer(string Text, StorageOfferKind Kind)`, with `enum StorageOfferKind { ShowInExplorer, Cleanup, EmptyRecycleBin, OpenHibernation, Explain, Uninstall, MoveToAblage }`, and `static IReadOnlyList<StorageOffer> StorageOffers.For(StorageNode n, PlaceKind k, InstalledProgram? program, int ablageDays)`. Texts:
    - Cleanup „Aufräumen“;
    - EmptyRecycleBin „Papierkorb leeren“;
    - OpenHibernation „Ruhezustand ansehen“;
    - Explain „Von Windows verwaltet, bitte nicht anfassen.“ (PageFile, WinSxS);
    - Uninstall „Programm deinstallieren“ (only when the program is not protected);
    - MoveToAblage „In die Ablage verschieben“, **only** for UserLibrary, with the note `$"Der Platz wird frei, wenn die Ablage geleert wird (nach {ablageDays} Tagen)."`;
    - ShowInExplorer always.
  - **Treemap:** `static IReadOnlyList<(int Index, Rect01 Box)> Treemap.Squarify(IReadOnlyList<long> sizes, double width, double height)`, with `readonly record struct Rect01(double X, double Y, double W, double H)`.
    - Standard squarified algorithm (Bruls et al.): sort descending and keep the original indexes.
    - Sizes ≤ 0 are skipped; the sum of areas equals width × height within 1e-6.

- [ ] **Step 1: Failing tests:**
  - `Scanner_skips_reparse_points_counts_placeholders_zero_and_survives_access_denied`: fake enumeration with a cycle via a reparse dir, a placeholder of 5 GB, a dir throwing `UnauthorizedAccessException`, and a file that throws `FileNotFoundException` on `Length`.
  - `Sizes_roll_up_to_parents`.
  - `Largest_files_respects_min_and_max`.
  - `Classify_order_protected_beats_user_library_and_onedrive_beats_downloads`: e.g. `C:\Users\u\OneDrive\Downloads\x` → OneDrive, and `C:\Users\u\Downloads\Lexware\x` → Protected.
  - `Move_to_ablage_offered_only_in_user_libraries`.
  - `Uninstall_offer_hidden_for_protected_program`.
  - `Squarify_covers_area_and_keeps_indexes`.
  - `Squarify_ignores_zero_sizes`.
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run the full suite. Expected: PASS.
- [ ] **Step 5:** Read-only device check: time `ScanAsync("C:\\")` elevated once through a skipped `[Trait("Device","1")]` test and note the duration in the task report. The spec target is „einige Minuten“; above 5 min, report it to the coordinator before continuing.
- [ ] **Step 6:** Commit `feat(storage): storage scan, place classifier, offers and treemap layout`.

### Task 11: Speicherplatz page

**Files:**
- Create:
  - `src/WinWaechter.App/ViewModels/StorageMapViewModel.cs`
  - `src/WinWaechter.App/Views/StorageMapView.xaml(.cs)`
  - `src/WinWaechter.App/Views/Controls/TreemapPanel.cs`
  - `tests/WinWaechter.Tests/StorageMapViewModelTests.cs`
- Modify: `ShellViewModel.cs` (nav „Speicherplatz“, glyph `\uEDA2`; spec order of the nav: Übersicht, Optimieren, Speicherplatz, Leistung, Gerät, Schutz, Sicherheit, Datenschutz, Programme, Reparatur, Verlauf), `ShellWindow.xaml`, `AppServices.cs`, `OptimizeViewModel.cs` (preselect a group, if missing), `ViewSmokeTests.cs`

**Interfaces:**
- Consumes: Task 10, `OptimizerEngine.ApplyAsync`, `MoveToAblageChange.ForFile` / `ForFolder`, `HistoryViewModel` purge („Ablage jetzt leeren“ already exists in Verlauf, so link to it instead of adding a new button), and the existing uninstall flow of `ProgramsViewModel` (navigate to Programme and select the program; expose `ProgramsViewModel.Select(InstalledProgram)` if missing).
- Produces:
  - **`StorageMapViewModel`:**
    - Constructor: `(StorageScanner scanner, Func<string, PlaceKind> classify, Func<string, InstalledProgram?> programFor, OptimizerEngine engine, IFileOps files, IDialogService dialogs, IExplorerShell shell, ChangeGate gate, bool canChange, int ablageDays, Action<string> navigate)`. `navigate` takes `"optimize:<group>"`, `"programs:<path>"`, `"performance:hibernation"` or `"history:ablage"`.
    - Properties:
      - `Drives` (letter, used/free text, ratio);
      - `SelectedDrive`;
      - `ScanCommand` and `CancelCommand`;
      - `Current` (`StorageNode`) and `Breadcrumbs`;
      - `Items` (children sorted by size, each with Name, size text, share 0–1, `Offers`);
      - `LargestFiles` (top 100 ≥ 100 MB);
      - `OpenCommand(item)` (drill down);
      - `UpCommand`;
      - `OfferCommand(item, offer)`.
    - **MoveToAblage:** confirm `"„{name}“ ({size}) kommt in die Ablage. Sie können es im Verlauf zurückholen."`, then `engine.ApplyAsync("Speicherplatz: in die Ablage", [finding wrapping the change], …)` inside `gate`, then remove the node from the tree and subtract its bytes up the parents.
    - **EmptyRecycleBin:** final confirm „Der Papierkorb wird endgültig geleert.“ Calls `SHEmptyRecycleBin(IntPtr.Zero, root, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND)` behind `IRecycleBin` with a fake.
    - The result is kept only in memory.
  - **`TreemapPanel`:** a `Panel` with dependency property `Sizes`. Children are arranged with `Treemap.Squarify`. Each tile is a `Button` (Name + size, tooltip) bound to `OpenCommand`, with keyboard focus order equal to the list order. Colors use `DynamicResource` accent tints, alternating 2–3 tones from the existing tokens.

- [ ] **Step 1: Failing tests:**
  - `Drill_down_and_up_updates_breadcrumbs`.
  - `Move_to_ablage_applies_change_and_shrinks_parents`.
  - `Move_offer_absent_outside_user_libraries`.
  - `Empty_recycle_bin_is_final_and_calls_fake`.
  - `Cancel_keeps_previous_result`.
  - `Locked_without_admin_except_show_in_explorer`.
  - Smoke: `StorageMapView_renders_with_fake_tree_light_dark_and_low_window`.
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run the full suite. Expected: PASS.
- [ ] **Step 5:** Commit `feat(app): Speicherplatz page with map, list and fixes`.

### Task 12: Weekly check (Core): task, result, rule, toast content, headless run

**Files:**
- Create:
  - `src/WinWaechter.Core/Maintenance/MaintenanceTask.cs`
  - `CheckResult.cs`
  - `NotifyRule.cs`
  - `ToastContent.cs`
  - `MaintenanceCheck.cs`
  - `tests/WinWaechter.Tests/MaintenanceTests.cs`

**Interfaces:**
- Produces:
  - **`MaintenanceTask`:**
    - Constants: `const string Path = @"\WinWaechter\Wöchentliche Prüfung";` and `static string Xml(string exePath, string userSid, DayOfWeek day, TimeOnly time, DateTime firstDate)`.
    - XML details:
      - Task Scheduler 1.4 schema;
      - `WeeklyTrigger` on `day` at `time`;
      - `StartWhenAvailable=true`, `RunOnlyIfNetworkAvailable=true`, `ExecutionTimeLimit=PT30M`, `Priority=7`, `DisallowStartIfOnBatteries=false`, `MultipleInstancesPolicy=IgnoreNew`;
      - Principal `UserId=userSid`, `LogonType=InteractiveToken`, `RunLevel=HighestAvailable`;
      - Action `Exec` `exePath` with arguments `--pruefen`;
      - Author „WinWächter“.
    - Also `void Register(ITaskStore store, …)` (→ `RegisterXml`) and `void Remove(ITaskStore store)` (→ `Delete`).
  - **`CheckResult`:**
    - `sealed record CheckResult(DateTimeOffset At, bool Failed, int Improvements, int Actions, int Updates, int DeviceIssues, bool RepairRecommended, bool AppUpdate, IReadOnlyList<string> Titles)`, with `int Total => Improvements + Actions + Updates`. `DeviceIssues` and repair are already in `Actions` through the collector.
    - `static class CheckResultStore { static CheckResult? Load(string path); static void Save(string path, CheckResult r); }`: atomic, path `<DataRoot>\wartung\letzte-pruefung.json`.
  - **`NotifyRule`:**
    - `static bool ShouldNotify(CheckResult now, CheckResult? lastNotified, DateTimeOffset nowTime)`: false when `Failed` or `Total == 0 && !AppUpdate`; true when `lastNotified` is null, or when the signature changed (sorted `Titles` + counts), or when `nowTime - lastNotified.At >= 7 days`.
    - The last notified result is stored in `<DataRoot>\wartung\zuletzt-gemeldet.json`.
  - **`ToastContent`:**
    - `static string Text(CheckResult r)`: e.g. „6 Empfehlungen, 12 Programm-Updates“. Zero parts are left out; singular forms are „1 Empfehlung“ / „1 Programm-Update“; plus „, WinWächter-Update verfügbar“.
    - `static string Xml(string title, string text)`: `<toast launch="winwaechter:verbessern" activationType="protocol">` with a `ToastGeneric` binding and one action `content="Ansehen" arguments="winwaechter:verbessern" activationType="protocol"`. Text is XML-escaped.
  - **`MaintenanceCheck`:**
    - Constructor: `(ImprovementCollector collector, Func<CancellationToken, Task<int>> updateCount, Func<CancellationToken, Task<bool>> appUpdate, string dataRoot, Action<string, string> launchNotifier, Func<DateTimeOffset> now)`.
    - `Task<CheckResult> RunAsync(CancellationToken ct)`:
      - collects, catches everything into `Failed = true` and logs;
      - saves the result;
      - when `ShouldNotify`, calls `launchNotifier(title "WinWächter", ToastContent.Text(r))` and saves `zuletzt-gemeldet.json`.
    - `launchNotifier` in production is `IUserLauncher.StartAsUser(<app dir>\WinWaechter.Notify.exe, $"--title \"{t}\" --text \"{x}\"")`. Quote with `CommandLineArgs.Quote` (add a tiny helper with tests if there is none).

- [ ] **Step 1: Failing tests:**
  - `Task_xml_has_weekly_trigger_highest_interactive_catch_up_and_limit`: parse the XML and assert each element.
  - `Notify_rule_cases`: nothing to do; first time; same result within 7 days; same result after 7 days; changed result; failed check.
  - `Toast_text_plural_singular_and_escaping` (`<b>&` in a title).
  - `Toast_xml_uses_protocol_activation`.
  - `Check_makes_no_change`: the collector's sources use the write fakes; assert their call lists are empty and the `ChangeGate` was never entered.
  - `Second_check_exits_and_result_is_written_atomically`: the mutex check is App-side (Task 14); here assert that Save writes via `.tmp` and that a concurrent Load never sees a partial file (write 200× while reading).
  - `Failed_collection_sets_Failed_and_does_not_notify`.
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run the full suite. Expected: PASS.
- [ ] **Step 5:** Commit `feat(maintenance): weekly check that only reports (task, result, notification rule)`.

### Task 13: Notification helper, installer, build script

**Files:**
- Create: `src/WinWaechter.Notify/WinWaechter.Notify.csproj`, `src/WinWaechter.Notify/Program.cs`, `src/WinWaechter.Notify/app.manifest`
- Modify: `WinWaechter.sln`, `installer/winwaechter.iss`, `build-release.ps1`

**Interfaces:**
- Consumes: Task 12 `ToastContent.Xml`. Link the Core file as a compiled source (`<Compile Include="..\WinWaechter.Core\Maintenance\ToastContent.cs" Link=… />`) instead of a project reference, so the helper stays tiny.
- Produces:
  - **Helper project:**
    - `OutputType WinExe`, `TargetFramework net10.0-windows10.0.19041.0` (overrides `Directory.Build.props`), manifest `asInvoker`, `UseWPF` false.
    - `Program.Main(args)` parses `--title`/`--text` and runs `ToastNotificationManager.CreateToastNotifier("TahsinYilmaz.WinWaechter").Show(new ToastNotification(xmlDoc))`.
    - It keeps the process alive ≤ 3 s so that delivery succeeds, then exits. Exit 0, or exit 1 and nothing else on failure: no window, no dialog.
    - AUMID constant: `TahsinYilmaz.WinWaechter`.
  - **Installer:**
    - `[Icons]` Start-menu entry gets `AppUserModelID: "TahsinYilmaz.WinWaechter"`.
    - `[Registry]`:
      - `Root: HKA; Subkey: "Software\Classes\winwaechter"`: `(Default)` = „URL:WinWächter“ and `URL Protocol` = "";
      - `…\shell\open\command` = `"{app}\WinWaechter.App.exe" "%1"`;
      - flags `uninsdeletekey`.
    - `[UninstallRun]`: `{sys}\schtasks.exe /Delete /TN "\WinWaechter\Wöchentliche Prüfung" /F`, flags `runhidden`, `RunOnceId: "DelTask"`. A missing task is fine.
  - **`build-release.ps1`:** after the App publish, run `& $dotnet publish (Join-Path $root 'src\WinWaechter.Notify') -c Release -r win-x64 --self-contained -p:PublishSingleFile=false -o $publishDir` and check it with `Assert-Ok`. Check that the existing verify step (embedded signature, SHA256SUMS) still passes; extend its file list if it enumerates exes.

- [ ] **Step 1:** Add the project to the solution and build. Expected: 0 warnings in a Release build of the whole solution.
- [ ] **Step 2:** Add a test `Notify_project_links_toast_content_and_targets_windows_sdk`: read the csproj XML from the repo path and assert the TFM and the linked file. That is the only test, because the helper itself is not unit-testable.
- [ ] **Step 3:** Compile the installer locally through `build-release.ps1 -Version 0.6.0` **only up to the installer step**: run ISCC directly with `/DAppVersion=0.6.0-dev`. Expected: ISCC succeeds. Do not install it.
- [ ] **Step 4:** Commit `feat(maintenance): unelevated notification helper, protocol and task cleanup in the installer`.

### Task 14: App wiring – check mode, protocol activation, settings, overview

**Files:**
- Create: `src/WinWaechter.App/Services/ActivationPipe.cs`
- Modify: `src/WinWaechter.App/App.xaml.cs`, `src/WinWaechter.App/Services/AppServices.cs`, `src/WinWaechter.App/Services/AppSettings.cs`, `src/WinWaechter.App/ViewModels/SettingsViewModel.cs` + `Views/SettingsView.xaml`, `src/WinWaechter.App/ViewModels/OverviewViewModel.cs` + `Views/OverviewView.xaml`, tests in `MaintenanceTests.cs` / existing VM tests

**Interfaces:**
- Consumes: Tasks 4, 7, 9, 11 (improvement sources), Task 12 (`MaintenanceCheck`, `MaintenanceTask`, `CheckResultStore`), Task 13 (AUMID, helper exe name).
- Produces:
  - **`AppSettings`:**
    - `bool MaintenanceEnabled` (false), `DayOfWeek MaintenanceDay` (Sunday), `string MaintenanceTime` ("11:00", parsed as `TimeOnly`; invalid → "11:00" on load), `bool MaintenanceHintDismissed` (false).
    - Invalid enum values are clamped on load.
  - **`AppServices`:**
    - New public `ImprovementCollector Collector`.
    - New public `Func<CancellationToken, Task<int>> UpdateCount`: winget offered count, 0 on any non-Ok status.
    - New public `Func<CancellationToken, Task<bool>> AppUpdateAvailable`: the existing `UpdateService` check, honouring `CheckUpdatesOnStart`.
  - **`App.OnStartup`:**
    - **`--pruefen`:**
      - own mutex `Local\WinWaechter.Check`; when not owned → exit 0;
      - no `ThemeManager`, no window, `ShutdownMode` explicit;
      - `AppServices.Create(...)`, then `MaintenanceCheck.RunAsync`, then `Shutdown(0)`;
      - never touches the main mutex.
    - **`winwaechter:verbessern`** (also `winwaechter:` alone):
      - when the main mutex is taken, `ActivationPipe.Send("verbessern")` and exit;
      - otherwise start normally and navigate to the overview and start `improve.ScanCommand`;
      - any other argument → ignored and logged.
  - **`ActivationPipe`:**
    - Named pipe `WinWaechter.Activate.<user SID>`, ACL current user only.
    - `static bool Send(string verb)` and `IDisposable Listen(Action<string> onVerb)`.
    - The verb must be exactly `verbessern`; anything else is dropped.
    - It is dispatched to the UI thread and also brings the window to the front (`BringExistingToFront` logic).
  - **Settings section „Automatische Prüfung“:**
    - Toggle, day ComboBox (German day names), time ComboBox (full hours 06:00–22:00).
    - Toggle on → `MaintenanceTask.Register` with the signed-in user SID. Toggle off → `Remove`. A changed day or time → re-register.
    - Locked unless `canChange && ownAccount`, with the hint from spec §4.4.
    - Failure → message + log, and the toggle flips back.
  - **Overview:**
    - Line `"Letzte automatische Prüfung: {At:dd.MM.yyyy HH:mm} – {n} Empfehlungen"`, or „– alles in Ordnung“, or „– fehlgeschlagen“ (from `CheckResultStore`).
    - One-time hint card (when `!MaintenanceEnabled && !MaintenanceHintDismissed`): „Wöchentliche Prüfung einschalten“ with buttons „Einschalten“ (same as the toggle) and „Nein danke“ (sets `MaintenanceHintDismissed`).
  - **Improvement sources** wired into the collector in this order: Optimieren, Schutz, Datenschutz, Leistung, Sicherheit, Vorinstallierte Apps, winget (Task 5), Programm-Updates (Task 4), Gerät (Task 9), Reparatur (Task 7).

- [ ] **Step 1: Failing tests:**
  - `Settings_clamp_invalid_day_and_time`.
  - `Toggle_registers_and_removes_task_with_user_sid`.
  - `Changing_day_reregisters`.
  - `Toggle_locked_without_own_account`.
  - `Overview_shows_last_check_line_states`.
  - `Hint_dismissed_is_remembered`.
  - `Activation_verb_other_than_verbessern_is_dropped`.
  - `Startup_args_parser_recognises_pruefen_and_protocol`: extract the pure `StartupArgs.Parse(string[])`, returning `enum StartupMode { Normal, Check, Improve }`.
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run the full suite and the Release build. Expected: PASS, 0 warnings.
- [ ] **Step 5:** Commit `feat(app): weekly check mode, protocol activation, settings and overview line`.

### Task 15: Version 0.6.0, docs, final review, device checklist (no release)

**Files:** `Directory.Build.props` (Version 0.6.0, written without a BOM), `CHANGELOG.md` (`## 0.6.0`, German, user-facing, five parts plus winget telemetry), `docs/roadmap.md` (0.6.0 „gebaut, unveröffentlicht“ with the checklist below; `rules.json` changed → re-sign at release), `docs/design-notes.md` (only if a new pattern needs a note, e.g. `TreemapPanel`).

- [ ] **Step 1:** Update the files. Run the full suite and the Release build. Expected: green, 0 warnings.
- [ ] **Step 2:** Whole-branch review (`superpowers:requesting-code-review`) against the spec and this plan. Fix rounds until „Ready“.
- [ ] **Step 3:** Write the device checklist into the roadmap. Each item needs the owner's consent; the implementer runs none of them alone:
  1. Install the 0.6.0 build (close the running app first).
  2. The Programme update list equals `winget upgrade` minus the Dell packages (and any `updateBlock` driver packages); update 7-Zip through WinWächter.
  3. The Gerät page shows plausible battery (≈42.8 Wh full) and SSD values.
  4. Run DNS-Cache leeren and „Datenträger prüfen“ (`/scan`). Other repairs only on request.
  5. Switch the weekly check on, run the task once with „Jetzt ausführen“ in the Aufgabenplanung, check that the notification appears and that the click opens „Rechner in Ordnung bringen“ (UAC). Then switch it off and confirm the task is gone.
  6. The storage map of C: loads; move a test file in Downloads to the Ablage and bring it back from the Verlauf.
  7. Turn off winget usage data and undo it; the winget `settings.json` keeps its comments.
  8. Check the new pages light and dark, at 150 % and with the keyboard.
  9. At release: re-sign `rules.json.sig` (`build-release.ps1`); release only after approval.
- [ ] **Step 4:** Commit `docs: 0.6.0 changelog, roadmap and version`.
