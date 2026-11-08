# WinWächter Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Installable Windows 11 app (German UI) that previews and reversibly cleans autostarts/services/tasks/temp files, scans for malware via Microsoft Defender plus own miner/suspicion heuristics, and updates itself from signed GitHub Releases.

**Architecture:** `WinWaechter.Core` holds all logic behind small system interfaces (registry, services, tasks, files, signatures, Defender, network) so scanners, changes, journal and updater are unit-testable with fakes. `WinWaechter.App` is a WPF/MVVM shell over Core. Every system change is an `IChange` with Apply/Undo/CheckDrift, journalled per session under `%ProgramData%\WinWaechter`. Inno Setup packages a self-contained publish; `tools/ReleaseTool` signs `SHA256SUMS` with ECDSA P-256.

**Tech Stack:** C# 13 / .NET 10 (`net10.0-windows`), WPF, CommunityToolkit.Mvvm, System.Management (WMI), TaskScheduler (dahall, MIT), xUnit, Inno Setup 6, `gh`.

**Spec:** `docs/superpowers/specs/2026-11-08-winwaechter-design.md`

## Global Constraints

- Target `net10.0-windows`, `win-x64`, self-contained publish. SDK: `%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe` (10.0.403).
- Single version source: `Directory.Build.props` `<Version>`; first release `0.1.0`.
- App manifest `requireAdministrator`. Install dir `C:\Program Files\WinWaechter`. Data dir `%ProgramData%\WinWaechter` (Sitzungen, Ablage, Updates, Logs).
- Only NuGet deps: CommunityToolkit.Mvvm, System.Management, TaskScheduler, xUnit (+ test SDK). No telemetry; only network target is GitHub.
- Protected list wins over every other rule: Microsoft/Windows, Dell, Lexware/Haufe, Intel, Realtek, drivers, Defender/security software; anything under `C:\Windows` is never proposed by the optimizer.
- Services only Automatic → Manual (never Disabled, never stopped). Autostarts via `StartupApproved` (`03` = off). Files go to the Ablage (never recycle bin, never hard delete except Ablage purge, default 14 days). Temp files only if older than 2 days.
- Unknown items are shown, never preselected. Findings always list reasons. Ratings: Info / Auffällig / Verdächtig (heuristics); Entbehrlich / Unerwünscht (rules).
- Defender clean = one button for all active threats; UI promises no undo for it.
- Update: HTTPS only; allowed hosts `api.github.com`, `github.com`, `objects.githubusercontent.com`, `release-assets.githubusercontent.com`; repo `yIrtIcIkurt/winwaechter`; assets `WinWaechter-Setup-<v>.exe`, `SHA256SUMS`, `SHA256SUMS.sig`; ECDSA P-256/SHA-256 over `SHA256SUMS`; signed file carries version; must be > installed. No download without click. Private key only in `C:\Users\tahsi\dev\winwaechter-keys\`, never committed.
- All UI copy German. No self-made graphics; icon prompts go to the user (Task 15).
- No system changes on the user's machine without their chat approval (applies to manual tests in Task 16).

## Review Focus

1. **Command lines with quotes, args, env vars** (`"C:\Program Files\X\x.exe" --min`, `%LOCALAPPDATA%\X\x.exe`, `rundll32.exe foo.dll,Entry`) → executable path resolved correctly so signature/protection checks hit the real file. Test in Task 3.
2. **Drift before undo** (user re-enabled an autostart in Task Manager, task deleted, service changed) → undo reports "geändert seit Anwendung" and skips that item, rest proceeds. Tests in Tasks 5–8.
3. **Failure mid-apply** (item 3 of 5 throws) → items 1–2 `Applied`, item 3 `Failed`, 4–5 not run, session undo restores 1–2. Test in Task 10.
4. **Locked / access-denied temp files and very long paths** → skipped and counted, no crash; undo restores only what moved. Test in Task 8.
5. **Update failures** (offline, HTTP 403 rate limit, no release, asset missing, redirect to foreign host, tampered setup, older version) → German message, nothing executed. Tests in Task 13.

---

## File Structure

```
WinWaechter.sln
Directory.Build.props                     Version, common props
.gitignore
rules/rules.json                          knowledge base (copied to output)
src/WinWaechter.Core/
  Model/Finding.cs                        Finding, FindingArea, Rating
  Model/ChangeRecord.cs                   ChangeRecord, ChangeStatus, DriftState
  Changes/IChange.cs                      IChange, SessionContext
  Rules/RuleSet.cs                        load + Classify
  Rules/ItemFacts.cs
  Sys/CommandLineParser.cs                exe path from command line
  Sys/IRegistry.cs + WinRegistry.cs
  Sys/IServiceControl.cs + WinServiceControl.cs
  Sys/ITaskStore.cs + WinTaskStore.cs
  Sys/IFileOps.cs + WinFileOps.cs
  Sys/ISignatureVerifier.cs + WinTrustVerifier.cs   (WinVerifyTrust + catalog)
  Sys/IUserContext.cs + InteractiveUserContext.cs
  Sys/SecureDataDir.cs                    ProgramData ACL
  Journal/SessionJournal.cs + SessionStore.cs
  Optimizer/Autostart/AutostartScanner.cs + StartupApprovedChange.cs
  Optimizer/Services/ServiceScanner.cs + ServiceStartModeChange.cs
  Optimizer/Tasks/TaskScanner.cs + TaskDisableChange.cs
  Optimizer/Storage/TempScanner.cs + MoveToAblageChange.cs + AblageJanitor.cs
  Optimizer/RestorePointService.cs
  Optimizer/OptimizerEngine.cs            scan all, apply, undo
  Security/Defender/IDefender.cs + DefenderClient.cs + PuaProtectionChange.cs
  Security/Heuristics/*.cs                probes + HeuristicScanner
  Update/UpdateChecker.cs, ReleaseVerifier.cs, SemVer.cs, UpdateInstaller.cs, UpdateKeys.cs
  Logging/FileLog.cs
src/WinWaechter.App/                      WPF shell, Views, ViewModels, Theme
tools/ReleaseTool/                        keygen, sign
tests/WinWaechter.Tests/                  xUnit + Fakes/
installer/winwaechter.iss
build-release.ps1
```

---

### Task 1: Solution scaffold

**Files:** Create `WinWaechter.sln`, `Directory.Build.props`, `.gitignore`, `src/WinWaechter.Core/WinWaechter.Core.csproj`, `src/WinWaechter.App/WinWaechter.App.csproj` (+ `App.xaml`, `MainWindow.xaml`, `app.manifest`), `tests/WinWaechter.Tests/WinWaechter.Tests.csproj`, `tools/ReleaseTool/ReleaseTool.csproj`, `rules/rules.json` (empty skeleton).

**Interfaces:** Produces project layout; `Directory.Build.props`: `TargetFramework=net10.0-windows`, `Nullable=enable`, `ImplicitUsings=enable`, `Version=0.1.0`, `Company=WinWächter`. App: `UseWPF`, `ApplicationManifest=app.manifest` with `requireAdministrator`, `RuntimeIdentifier=win-x64`. `rules/rules.json` linked into Core output as `Content`/`CopyToOutputDirectory=PreserveNewest`. `.gitignore` covers `bin/ obj/ publish/ dist/ *.user` and `*.pem`/`*.key`.

- [ ] **Step 1:** `dotnet new sln/classlib/wpf/xunit/console`, add references (App→Core, Tests→Core, ReleaseTool→Core), add NuGet packages.
- [ ] **Step 2:** Add smoke test `Core_assembly_version_matches_props`: `Assert.Equal("0.1.0", typeof(Finding).Assembly.GetName().Version!.ToString(3))` (create empty `Finding` record placeholder in Model).
- [ ] **Step 3:** Run `dotnet build WinWaechter.sln -c Debug` → 0 errors; `dotnet test` → 1 passed.
- [ ] **Step 4:** Commit `chore: solution scaffold`.

### Task 2: Model, changes contract, journal, secure data dir

**Files:** Create `Model/Finding.cs`, `Model/ChangeRecord.cs`, `Changes/IChange.cs`, `Journal/SessionJournal.cs`, `Journal/SessionStore.cs`, `Sys/SecureDataDir.cs`; Test `tests/.../JournalTests.cs`.

**Interfaces – Produces:**
```csharp
public enum FindingArea { Autostart, Dienst, Aufgabe, Speicher, Prozess, Verbindung, Defender, Einstellung }
public enum Rating { Info, Auffaellig, Verdaechtig, Entbehrlich, Unerwuenscht }
public sealed record Finding(string Id, FindingArea Area, string Title, string Location,
    IReadOnlyList<string> Reasons, Rating Rating, bool Preselected, IChange? Change,
    long? SizeBytes = null, string? Publisher = null);

public enum ChangeStatus { Pending, Applied, Failed, Undone, SkippedDrift }
public enum DriftState { Unchanged, Changed, Gone }
public sealed record ChangeRecord(string Id, string Kind, string Target, string Description,
    Dictionary<string,string?> Before, Dictionary<string,string?> After,
    ChangeStatus Status, DateTimeOffset At, string? Error = null);

public sealed class SessionContext { public string SessionDir {get;} public string BackupDir {get;} public string AblageDir {get;} }
public interface IChange {
    string Kind { get; }              // "autostart", "service", "task", "file", "pua"
    string Target { get; }
    string Description { get; }       // German, shown in preview + history
    ChangeRecord Apply(SessionContext ctx);          // backs up first, returns Applied record
    void Undo(ChangeRecord record, SessionContext ctx);
    DriftState CheckDrift(ChangeRecord record);      // compare live state with record.After
}
public interface IChangeFactory { IChange Rehydrate(ChangeRecord r); } // registered per Kind, used for undo from disk

public sealed class SessionStore {
    public SessionStore(string root);                 // root = %ProgramData%\WinWaechter
    public SessionJournal Create(string title);       // folder Sitzungen\yyyy-MM-dd_HHmmss[_n]
    public IReadOnlyList<SessionJournal> List();      // newest first
}
public sealed class SessionJournal {
    public string Title {get;} public DateTimeOffset Created {get;} public SessionContext Context {get;}
    public IReadOnlyList<ChangeRecord> Records {get;}
    public void Upsert(ChangeRecord r);               // atomic write: journal.json.tmp → File.Replace
}
public static class SecureDataDir { public static string Ensure(string path); } // owner=Administrators, protected DACL: Admins+SYSTEM FullControl, Users ReadAndExecute
```

- [ ] **Step 1: Tests** `Create_makes_unique_folders_with_subdirs` (two creates in same second → distinct dirs, each has `Sicherung\`, `Ablage\`); `Upsert_persists_and_reloads` (upsert Pending then Applied with same Id → `List()[0].Records` has 1 record with `Applied`); `Corrupt_tmp_file_does_not_break_reload` (leftover `journal.json.tmp` ignored).
- [ ] **Step 2:** Run → FAIL (types missing).
- [ ] **Step 3:** Implement with `System.Text.Json` (enum as string, indented). `SecureDataDir.Ensure` uses `DirectorySecurity`: `SetOwner(BuiltinAdministratorsSid)`, `SetAccessRuleProtection(true,false)`, purge all rules, add the three rules with inheritance `ContainerInherit|ObjectInherit`. (Not unit-tested – verified in Task 16.)
- [ ] **Step 4:** Run → PASS. Commit `feat(core): model, journal, secure data dir`.

### Task 3: Rules and command-line parsing

**Files:** Create `Rules/ItemFacts.cs`, `Rules/RuleSet.cs`, `Sys/CommandLineParser.cs`, fill `rules/rules.json`; Test `RuleSetTests.cs`, `CommandLineParserTests.cs`.

**Interfaces – Produces:**
```csharp
public sealed record ItemFacts(string Name, string? ExePath, string? Publisher, string? CommandLine);
public enum RuleVerdict { Protected, Expendable, Pua, Miner, Unknown }
public sealed class RuleSet {
    public static RuleSet Load(string jsonPath); public static RuleSet Default { get; } // loads rules.json next to Core assembly
    public RuleVerdict Classify(ItemFacts f);       // order: Protected > Miner > Pua > Expendable > Unknown
    public IReadOnlySet<int> PoolPorts {get;}
    public bool IsMinerCommandLine(string cmd);
}
public static class CommandLineParser { public static string? ExtractExePath(string commandLine); } // expands env vars; handles quotes; rundll32/regsvr32 → the DLL; returns null if empty
```
`rules.json` schema: `{ "protected":[Matcher], "expendableAutostart":[Matcher], "pua":[Matcher], "minerNames":[string], "minerCommandLine":[string], "poolPorts":[int] }`; `Matcher = { "name"?, "publisher"?, "path"? }` – case-insensitive substring; all given fields must match. Protected entries (minimum): publisher `Microsoft`, `Dell`, `Intel`, `Realtek`, `Haufe`, `Lexware`, `NVIDIA` *(drivers)*, `Advanced Micro Devices`; path `\Windows\`, `\Lexware\`, `\Dell\`, `\Windows Defender\`; name `Lexware`, `Dell`, `SecurityHealth`. Expendable: Steam, EpicGamesLauncher, EADesktop/Origin, Ubisoft Connect/upc, Battle.net, RiotClient, GalaxyClient, Discord, Spotify, Teams (personal, name `MicrosoftTeams`/`ms-teams` but **not** if publisher Microsoft → protected wins, so match by name only for HKCU Run), Adobe/Google updater autostarts, OneDrive is **not** expendable. PUA: Glary, `GUDelayStartup`, Driver Booster, IObit, Advanced SystemCare, Bundle installer patterns `*_Bundle*`, `FileZilla*setup*bundle*`. Miner names: xmrig, xmr-stak, nicehash, excavator, phoenixminer, t-rex, lolminer, nbminer, gminer, teamredminer, ethminer, cpuminer, ccminer, srbminer, nanominer. Miner cmdline: `stratum+tcp`, `stratum+ssl`, `--donate-level`, `-a randomx`, `--coin`. Pool ports: 3333, 4444, 5555, 6666, 7777, 8888, 9999, 14433, 14444, 45560, 45700.

- [ ] **Step 1: Tests** `Protected_beats_expendable` (`new ItemFacts("Dell Update", null, "Dell Inc", null)` → Protected even if "Update" were expendable); `Lexware_is_protected` (path `C:\Program Files (x86)\Lexware\x.exe` → Protected); `Steam_is_expendable`; `Glary_is_pua`; `Xmrig_is_miner`; `Unknown_is_unknown`; `Miner_cmdline_detected` (`"-o stratum+tcp://pool:3333"` → true); parser cases: `"\"C:\\Program Files\\X\\x.exe\" --min"` → `C:\Program Files\X\x.exe`; `%LOCALAPPDATA%\X\x.exe /bg` → expanded path; `rundll32.exe C:\a\b.dll,Entry` → `C:\a\b.dll`; `C:\Tools\app.exe -s` (no quotes) → `C:\Tools\app.exe`; `""` → null.
- [ ] **Step 2:** Run → FAIL. **Step 3:** Implement. **Step 4:** Run → PASS. Commit `feat(core): rules and command line parsing`.

### Task 4: System abstractions and real implementations

**Files:** Create the `Sys/I*.cs` interfaces + `Win*.cs` implementations, `Sys/IUserContext.cs` + `InteractiveUserContext.cs`; Test fakes `tests/.../Fakes/FakeRegistry.cs, FakeServiceControl.cs, FakeTaskStore.cs, FakeFileOps.cs, FakeSignatures.cs`; Test `SignatureSmokeTests.cs`.

**Interfaces – Produces:**
```csharp
public enum Hive { CurrentUser, LocalMachine }
public interface IRegistry {
    IReadOnlyList<string> ValueNames(Hive h, string key);
    object? Get(Hive h, string key, string name);
    void Set(Hive h, string key, string name, object value, RegistryValueKind kind);
    void Delete(Hive h, string key, string name);
    void Export(Hive h, string key, string file);   // reg.exe export /y
}
// WinRegistry maps Hive.CurrentUser → HKU\<IUserContext.Sid> (not HKCU), 64-bit view.
public interface IUserContext { string Sid {get;} string ProfileDir {get;} string TempDir {get;} string StartupDir {get;} }
// InteractiveUserContext: owner of explorer.exe in the active console session (WTSGetActiveConsoleSessionId + process token), fallback current user.

public enum StartMode { Automatic, Manual, Disabled, Boot, System }
public sealed record ServiceInfo(string Name, string DisplayName, StartMode Mode, bool DelayedStart,
    bool Running, string? ImagePath, string? ServiceDll);
public interface IServiceControl { IReadOnlyList<ServiceInfo> List(); ServiceInfo? Get(string name); void SetStartMode(string name, StartMode mode, bool delayed); }
// Win: registry HKLM\SYSTEM\CurrentControlSet\Services (Start, DelayedAutostart, ImagePath, Parameters\ServiceDll) + ServiceController for Running; SetStartMode via ChangeServiceConfig P/Invoke + DelayedAutostart via ChangeServiceConfig2.

public sealed record TaskInfo(string Path, string Name, bool Enabled, bool Hidden, string? ActionExe, string? ActionArgs, string? Author);
public interface ITaskStore { IReadOnlyList<TaskInfo> List(); TaskInfo? Get(string path); string ExportXml(string path); void SetEnabled(string path, bool enabled); void RegisterXml(string path, string xml); }

public interface IFileOps { IEnumerable<FileInfo> EnumerateOlderThan(string dir, TimeSpan age); void Move(string src, string dst); bool Exists(string path); }
// WinFileOps: skips reparse points; enumerates with IgnoreInaccessible; Move creates parent dirs, uses \\?\ long paths.

public enum SignatureState { Valid, Unsigned, Invalid, Unknown }
public sealed record SignatureInfo(SignatureState State, string? Publisher, bool IsMicrosoft);
public interface ISignatureVerifier { SignatureInfo Verify(string path); }
// WinTrustVerifier: WinVerifyTrust(WINTRUST_ACTION_GENERIC_VERIFY_V2, WTD_REVOKE_NONE, cache-only); on TRUST_E_NOSIGNATURE do catalog lookup
// (CryptCATAdminAcquireContext2 SHA256 → CalcHashFromFileHandle2 → EnumCatalogFromHash → verify with WTD_CHOICE_CATALOG). Publisher = leaf cert simple name.
// Missing file / exception → Unknown. Results cached per path+lastWrite.
```

- [ ] **Step 1: Tests (run on Windows, real APIs)** `Notepad_is_microsoft_signed` (`C:\Windows\System32\notepad.exe` → Valid, IsMicrosoft); `Catalog_signed_system_file_is_valid` (`C:\Windows\System32\drivers\etc\hosts` is not PE – use `C:\Windows\System32\ntoskrnl.exe` or `cmd.exe`; assert not Unsigned); `Temp_file_is_unsigned` (write bytes `MZ` stub to temp → Unsigned or Invalid, never Valid); `Missing_file_is_unknown`.
- [ ] **Step 2:** FAIL → **Step 3:** implement interfaces, Win impls, and fakes (in-memory dictionaries; `FakeServiceControl` etc. record calls) → **Step 4:** PASS. Commit `feat(core): system abstractions`.

### Task 5: Autostart scanner and change

**Files:** Create `Optimizer/Autostart/AutostartScanner.cs`, `StartupApprovedChange.cs`; Test `AutostartTests.cs`.

**Interfaces:** Consumes `IRegistry`, `IUserContext`, `ISignatureVerifier`, `RuleSet`, `CommandLineParser`. Produces:
```csharp
public sealed class AutostartScanner { public AutostartScanner(IRegistry r, IUserContext u, ISignatureVerifier s, RuleSet rules, IFileOps f);
    public IReadOnlyList<Finding> Scan(); }
public sealed class StartupApprovedChange : IChange { /* Kind "autostart" */
    public StartupApprovedChange(IRegistry r, Hive hive, string approvedKey, string valueName); }
```
Locations → approved key: HKCU `Software\Microsoft\Windows\CurrentVersion\Run` → `...\Explorer\StartupApproved\Run`; HKLM same `Run` → HKLM `StartupApproved\Run`; HKLM `Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run` → HKLM `StartupApproved\Run32`; user Startup folder `.lnk`/files → HKCU `StartupApproved\StartupFolder` (value name = file name); common Startup folder → HKLM `StartupApproved\StartupFolder`. Approved value = 12 bytes: byte0 `0x02` enabled / `0x03` disabled (also treat `0x06` as enabled), bytes 4..11 = FILETIME UTC now. Already-disabled entries are listed as Info with no change. Finding: Expendable → `Rating.Entbehrlich`, preselected; Pua → `Unerwuenscht`, preselected; Protected → not listed; Unknown → `Info`, not preselected; reasons include signature state + publisher; `.lnk` targets resolved via `WScript.Shell` COM (`IShellLink`).

- [ ] **Step 1: Tests** with fakes: `Steam_in_HKCU_run_is_preselected_entbehrlich`; `Dell_entry_is_not_listed`; `Already_disabled_entry_has_no_change` (approved byte0 = 3); `Apply_writes_03_and_records_before` (before value absent → Before["value"] = null; after byte0 == 3); `Undo_restores_previous_bytes` (before `02 00..` → exact bytes back); `Undo_deletes_value_if_absent_before`; `Drift_detected_when_reenabled` (after apply, set byte0 = 2 → `CheckDrift == Changed`).
- [ ] **Step 2–4:** FAIL → implement (Before/After stored as hex strings) → PASS. Commit `feat(optimizer): autostarts`.

### Task 6: Service scanner and change

**Files:** `Optimizer/Services/ServiceScanner.cs`, `ServiceStartModeChange.cs`; Test `ServiceTests.cs`.

**Interfaces:** `ServiceScanner(IServiceControl, ISignatureVerifier, RuleSet).Scan()`; `ServiceStartModeChange(IServiceControl, string name)` Kind `"service"`, Before = `{mode, delayed}`, After = `{Manual, false}`.
Candidates: `Mode == Automatic`, image path (or `ServiceDll` for svchost) not under `C:\Windows`, signature not Microsoft, verdict not Protected. Signature `Unknown` → Info only, never preselected. Preselect only Expendable/Pua verdicts (e.g. Glary service, game launcher services like `EasyAntiCheat` are **not** expendable → Info; `Steam Client Service` is Manual by default anyway). Unsigned services outside Program Files get reason „unsigniert, ungewöhnlicher Ort“.

- [ ] **Step 1: Tests** `Microsoft_signed_service_not_listed`; `Service_under_windows_dir_not_listed` (even if Unsigned); `Unknown_signature_is_info_not_preselected`; `Apply_sets_manual_and_undo_restores_delayed_automatic`; `Drift_when_mode_changed_externally`.
- [ ] **Step 2–4.** Commit `feat(optimizer): services`.

### Task 7: Scheduled task scanner and change

**Files:** `Optimizer/Tasks/TaskScanner.cs`, `TaskDisableChange.cs`, `Sys/WinTaskStore.cs` (TaskScheduler NuGet, `TaskService.Instance.AllTasks`); Test `TaskTests.cs`.

**Interfaces:** `TaskScanner(ITaskStore, ISignatureVerifier, RuleSet).Scan()`; `TaskDisableChange(ITaskStore, string path)` Kind `"task"`: Apply exports XML to `ctx.BackupDir\task_<sanitized>.xml` (path in `Before["xml"]`), disables. Undo: if task exists → enable; if gone → `RegisterXml` from backup, then ensure enabled state as before. Drift: Gone / Changed (enabled again).
Candidates: path not starting with `\Microsoft\`, enabled, not Protected (by action exe publisher/path/name). Reasons: „versteckt“ if Hidden; „Ziel unsigniert“; „Ziel in AppData/Temp“. Preselect: Expendable/Pua, or Hidden **and** target unsigned in AppData/Temp (the Cua-Driver case).

- [ ] **Step 1: Tests** `Microsoft_folder_tasks_ignored`; `Hidden_unsigned_appdata_task_preselected_with_reasons`; `Lexware_task_not_listed`; `Undo_reregisters_deleted_task_from_backup`; `Drift_when_reenabled`.
- [ ] **Step 2–4.** Commit `feat(optimizer): scheduled tasks`.

### Task 8: Temp files, Ablage, janitor

**Files:** `Optimizer/Storage/TempScanner.cs`, `MoveToAblageChange.cs`, `AblageJanitor.cs`; Test `StorageTests.cs`.

**Interfaces:** `TempScanner(IFileOps, IUserContext).Scan()` → one Finding per folder (`%TEMP%` of interactive user, `C:\Windows\Temp`), preselected, `SizeBytes` = sum, Change = `MoveToAblageChange(IFileOps, string folder, TimeSpan minAge = 2 days)`. Apply moves each file to `ctx.AblageDir\<n>\<relative path>`, records moved list in a sidecar `ablage_<id>.json` (not inline in journal – may be thousands), skips locked/denied files and counts them (`After["skipped"]`). Undo moves back each file whose original path is free; conflicts reported. `AblageJanitor.Purge(SessionStore store, TimeSpan maxAge)` deletes Ablage folders of sessions older than maxAge (default 14 days) and marks those file records non-undoable (`ChangeStatus` stays, `After["purged"]="true"`); `PurgeNow()` deletes all.

- [ ] **Step 1: Tests** (real temp dir under test folder): `Only_files_older_than_two_days_move`; `Locked_file_is_skipped_and_counted` (open with `FileShare.None`); `Undo_restores_files_to_original_paths`; `Undo_skips_when_original_exists_again`; `Janitor_purges_only_old_sessions`.
- [ ] **Step 2–4.** Commit `feat(optimizer): temp files and Ablage`.

### Task 9: Restore point service

**Files:** `Optimizer/RestorePointService.cs`.

**Interfaces:** `public sealed record RestorePointResult(bool Created, string Message);` `RestorePointService.TryCreate(string description) -> RestorePointResult`: list `root\default:SystemRestore` sequence numbers before, call `SystemRestore.CreateRestorePoint(description, 12 /*MODIFY_SETTINGS*/, 100)`, list after; Created = new sequence number appeared. Messages (German): „Wiederherstellungspunkt erstellt.“ / „Kein neuer Wiederherstellungspunkt (Computerschutz aus oder Windows-Sperre von 24 Stunden). Die eigene Sicherung ist trotzdem angelegt.“ Never throws.

- [ ] **Step 1:** No unit test (WMI, admin). Implement; verified in Task 16. Commit `feat(optimizer): restore point`.

### Task 10: Optimizer engine

**Files:** `Optimizer/OptimizerEngine.cs`, `Changes/ChangeFactory.cs`; Test `OptimizerEngineTests.cs`.

**Interfaces – Produces:**
```csharp
public sealed record ApplyResult(SessionJournal Session, int Applied, int Failed, RestorePointResult RestorePoint);
public sealed record UndoResult(int Undone, int SkippedDrift, int Failed, IReadOnlyList<string> Messages);
public sealed class OptimizerEngine {
    public OptimizerEngine(IEnumerable<Func<IReadOnlyList<Finding>>> scanners, SessionStore store, IChangeFactory factory, Func<string, RestorePointResult> restorePoint);
    public Task<IReadOnlyList<Finding>> ScanAsync(IProgress<string>? p, CancellationToken ct);
    public Task<ApplyResult> ApplyAsync(string title, IReadOnlyList<Finding> selected, IProgress<string>? p, CancellationToken ct);
    public UndoResult UndoSession(SessionJournal s);            // reverse order, only Applied
    public UndoResult UndoOne(SessionJournal s, string recordId);
}
```
Apply: restore point → for each change: Upsert Pending → Apply → Upsert Applied | on exception Upsert Failed + **stop** (no further changes) → return. Cancellation between items stops cleanly. Undo: Drift `Changed/Gone` (Gone only allowed for task kind) → `SkippedDrift` + German message „<Beschreibung>: seit der Änderung verändert – übersprungen“. `ChangeFactory` maps Kind → rehydration (needs the system interfaces).

- [ ] **Step 1: Tests** with a `FakeChange` (configurable throw/drift): `Failure_mid_apply_stops_and_records` (5 changes, #3 throws → statuses Applied, Applied, Failed, none for 4–5; `ApplyResult.Failed==1`); `UndoSession_reverses_order` (order of Undo calls 2,1); `Drift_item_is_skipped_others_undone`; `UndoOne_only_touches_that_record`; `Cancellation_before_item_stops_without_partial_record`.
- [ ] **Step 2–4.** Commit `feat(optimizer): engine with journal and undo`.

### Task 11: Defender client

**Files:** `Security/Defender/IDefender.cs`, `DefenderClient.cs`, `PuaProtectionChange.cs`, `Sys/ProcessRunner.cs`; Test `DefenderParsingTests.cs`.

**Interfaces – Produces:**
```csharp
public sealed record DefenderStatus(bool Available, bool RealTime, bool TamperProtected, int PuaMode /*0 off,1 on,2 audit*/,
    DateTimeOffset? SignatureUpdated, int SignatureAgeDays, DateTimeOffset? LastQuickScan, DateTimeOffset? LastFullScan, string? Note);
public sealed record DefenderThreat(long ThreatId, string Name, int Severity, IReadOnlyList<string> Resources, bool Active, string StatusText);
public enum ScanKind { Quick = 1, Full = 2 }
public interface IDefender {
    Task<DefenderStatus> GetStatusAsync(); Task<IReadOnlyList<DefenderThreat>> GetThreatsAsync();
    Task<int> UpdateSignaturesAsync(CancellationToken ct); Task<int> ScanAsync(ScanKind kind, CancellationToken ct); // ct → "MpCmdRun -Scan -Cancel"
    Task<int> ScanPathAsync(string path, CancellationToken ct); Task<int> RemoveActiveThreatsAsync(); Task SetPuaModeAsync(int mode);
}
public interface IProcessRunner { Task<(int Exit, string StdOut, string StdErr)> RunAsync(string exe, string args, CancellationToken ct); }
```
Status/threats via WMI `root\Microsoft\Windows\Defender`: `MSFT_MpComputerStatus` (`RealTimeProtectionEnabled`, `IsTamperProtected`, `AntivirusSignatureLastUpdated`, `AntivirusSignatureAge`, `QuickScanEndTime`, `FullScanEndTime`, `AMServiceEnabled`), `MSFT_MpPreference.PUAProtection`, `MSFT_MpThreat` + `MSFT_MpThreatDetection` (join on ThreatID; `ThreatStatusID`/`Resources`). MpCmdRun path from `HKLM\SOFTWARE\Microsoft\Windows Defender\InstallLocation` + `MpCmdRun.exe`. Remove/SetPua via `powershell.exe -NoProfile -NonInteractive -Command "Remove-MpThreat"` / `"Set-MpPreference -PUAProtection <n>"`. WMI not available → `Available=false`, Note „Defender ist nicht aktiv – möglicherweise ist ein anderer Virenschutz installiert.“ `PuaProtectionChange` Kind `"pua"`: Before = current mode, After = 1; Undo sets Before; Drift if live ≠ After.

- [ ] **Step 1: Tests** (parsing helpers made internal + `InternalsVisibleTo`): `Threat_join_marks_active_by_status` (map of ThreatStatusID: 1 = active/detected, 2/3 = cleaned/quarantined, etc. → `Active`); `MpCmdRun_args` (Quick → `-Scan -ScanType 1`, cancel → `-Scan -Cancel`, path → `-Scan -ScanType 3 -File "<p>" -DisableRemediation`); `Pua_change_undo_restores_mode` (fake IDefender).
- [ ] **Step 2–4.** Commit `feat(security): Defender client`.

### Task 12: Heuristic scanner

**Files:** `Security/Heuristics/ProcessSampler.cs`, `TcpTable.cs`, `HeuristicScanner.cs`, `Scoring.cs`; Test `HeuristicTests.cs`.

**Interfaces:**
```csharp
public sealed record ProcessSample(int Pid, string Name, string? ExePath, string? CommandLine, double CpuPercentAvg, bool SustainedHigh);
public interface IProcessSource { Task<IReadOnlyList<ProcessSample>> SampleAsync(TimeSpan window, CancellationToken ct); } // 2 samples, 10 s apart; SustainedHigh = both ≥ 25 % of total CPU
public sealed record TcpConn(int Pid, string RemoteAddress, int RemotePort, string State);
public interface IConnectionSource { IReadOnlyList<TcpConn> Established(); } // GetExtendedTcpTable TCP_TABLE_OWNER_PID_ALL, IPv4+IPv6
public sealed class HeuristicScanner {
    public HeuristicScanner(IProcessSource p, IConnectionSource c, ISignatureVerifier s, RuleSet rules, AutostartScanner a, TaskScanner t, ServiceScanner sv);
    public Task<IReadOnlyList<Finding>> ScanAsync(IProgress<string>? p, CancellationToken ct);
}
public static class Scoring { public static Rating ToRating(int points); } // <3 Info, 3–5 Auffaellig, ≥6 Verdaechtig
```
Points: sustained load 2; exe in AppData/Temp/ProgramData/Users\Public 1; unsigned/invalid there +2 (Unknown +0); miner name 5; miner cmdline 5; pool-port connection 2; hidden task 1; autostart/task/service pointing to unsigned exe in AppData/Temp 2; PUA rule 3. Protected verdict → finding dropped. One Finding per executable path (merge reasons from all probes). Changes offered: for autostart/task/service findings reuse their `IChange`; for files `MoveToAblageChange` single-file variant (`MoveToAblageChange.ForFile(path)`); process kill offered as UI action (not an `IChange`, labelled „nicht umkehrbar“). Nothing preselected unless Verdaechtig.

- [ ] **Step 1: Tests** (fake sources): `Xmrig_with_pool_port_is_verdaechtig`; `Signed_busy_process_is_info_or_dropped` (e.g. signed game, high CPU → Info, ≤2 points); `Unsigned_appdata_exe_is_auffaellig`; `Protected_publisher_is_dropped_even_with_load`; `Reasons_merged_for_same_path`.
- [ ] **Step 2–4.** Commit `feat(security): heuristics`.

### Task 13: Updater and release tool

**Files:** `Update/SemVer.cs`, `UpdateKeys.cs`, `ReleaseVerifier.cs`, `UpdateChecker.cs`, `UpdateInstaller.cs`; `tools/ReleaseTool/Program.cs`; Test `UpdateTests.cs`.

**Interfaces:**
```csharp
public readonly record struct SemVer(int Major, int Minor, int Patch) : IComparable<SemVer> { public static bool TryParse(string s, out SemVer v); } // accepts leading "v"
public sealed record UpdateInfo(SemVer Version, string Notes, Uri SetupUrl, Uri SumsUrl, Uri SigUrl, string SetupName);
public sealed record UpdateCheckResult(UpdateInfo? Available, string Message);   // German message
public sealed class UpdateChecker { public UpdateChecker(HttpClient http, SemVer installed, string repo = "yIrtIcIkurt/winwaechter"); public Task<UpdateCheckResult> CheckAsync(CancellationToken ct); }
public sealed record VerifiedUpdate(string SetupPath, SemVer Version);
public sealed class ReleaseVerifier { public ReleaseVerifier(string publicKeyPem, SemVer installed);
    public VerifiedUpdate Verify(string sumsPath, string sigPath, string setupPath, UpdateInfo info); } // throws UpdateVerificationException(German message)
public static class HostPolicy { public static bool IsAllowed(Uri u); }   // https + 4 hosts
public sealed class UpdateDownloader { public Task<(string sums,string sig,string setup)> DownloadAsync(UpdateInfo i, string targetDir, IProgress<double>? p, CancellationToken ct); }
public static class UpdateInstaller { public static void Launch(VerifiedUpdate u); } // Process.Start setup "/SILENT /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS", then Application.Shutdown by caller
```
`HttpClient` built with `SocketsHttpHandler { AllowAutoRedirect = false }`; downloader follows redirects manually (max 5) checking `HostPolicy` on every hop. User-Agent `WinWaechter-Updater`. API errors: offline → „Keine Verbindung zu GitHub.“; 403/429 → „GitHub-Abfragelimit erreicht, später erneut versuchen.“; 404 → „Noch kein Release veröffentlicht.“; missing asset → „Release unvollständig.“
`SHA256SUMS` format (exact):
```
version: 0.1.1
<64 hex lowercase>  WinWaechter-Setup-0.1.1.exe
```
Signature: `ECDsa.SignData(bytesOfSums, SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)`, `.sig` = Base64 text. Verify order: signature → version line == info.Version and > installed → setup file name listed → SHA-256 match. Public key PEM constant in `UpdateKeys.PublicKeyPem` (filled by Task 13 step 5 after keygen).
ReleaseTool CLI: `ReleaseTool keygen <dir>` writes `winwaechter-update.key.pem` (private, PKCS#8) + `.pub.pem`; refuses to overwrite. `ReleaseTool sign <keyPem> <version> <setupExe> <outDir>` writes `SHA256SUMS` + `SHA256SUMS.sig`.

- [ ] **Step 1: Tests** (keys generated in test): `SemVer_compare_and_parse` (`v0.1.10 > 0.1.9`); `Valid_release_verifies`; `Tampered_setup_rejected` (flip one byte → exception message contains „Prüfsumme“); `Wrong_key_signature_rejected`; `Older_or_equal_version_rejected`; `Version_line_mismatch_rejected`; `HostPolicy_rejects_http_and_foreign_hosts` (`http://github.com/...` false, `https://evil.com` false, `https://release-assets.githubusercontent.com/x` true); `Checker_maps_403_to_rate_limit_message` and `Checker_maps_404` and `Checker_offline` (fake `HttpMessageHandler`); `Checker_picks_assets_by_name`.
- [ ] **Step 2–4:** FAIL → implement → PASS.
- [ ] **Step 5:** Run `dotnet run --project tools/ReleaseTool -- keygen C:\Users\tahsi\dev\winwaechter-keys`; paste `.pub.pem` into `UpdateKeys.PublicKeyPem`; confirm private key file is outside repo (`git status` clean of `*.pem`). Tell the user to back up the key folder.
- [ ] **Step 6:** Commit `feat(update): signed GitHub updater and release tool`.

### Task 14: WPF app shell and pages

**REQUIRED SKILL first:** `frontend-design` (direction: calm, precise, “instrument panel” rather than generic dashboard; own palette and type scale; light/dark from Windows `AppsUseLightTheme`; no gradients-for-decoration). Then implement.

**Files:** `src/WinWaechter.App/` – `App.xaml(.cs)` (DI composition root, single-instance mutex, unhandled-exception logging), `Theme/Tokens.xaml`, `Theme/Light.xaml`, `Theme/Dark.xaml`, `Theme/Controls.xaml`, `Views/ShellWindow.xaml`, `Views/OverviewView.xaml`, `OptimizeView.xaml`, `ProtectView.xaml`, `HistoryView.xaml`, `SettingsView.xaml`, `ViewModels/*ViewModel.cs`, `Services/AppSettings.cs` (JSON in `%ProgramData%\WinWaechter\settings.json`: `CheckUpdatesOnStart=true`, `AblageDays=14`), `Logging` via Core `FileLog` (7-day rotation).

**Interfaces:** Consumes Tasks 2–13. Startup: `SecureDataDir.Ensure`, `AblageJanitor.Purge`, optional update check (non-blocking, banner).

Pages / behaviour (German copy):
- **Übersicht:** Defender tile (Echtzeitschutz, Signaturalter, letzter Scan), offene Vorschläge count, primary button „Prüfen“ (runs optimizer scan + heuristics, navigates to results), update banner if available.
- **Optimieren:** grouped list (Autostart, Dienste, Aufgaben, Speicher) with checkbox, title, location, rating chip, reasons expander; footer „Ausgewählte anwenden (n)“ → confirm dialog listing changes → progress → summary incl. restore point message.
- **Schutz:** Defender status + „Signaturen aktualisieren“, „Schnellscan“, „Vollscan“, „Abbrechen“, elapsed time; Defender findings with „Alle aktiven Funde bereinigen“ (dialog text: „Defender entfernt oder isoliert die Funde. Das lässt sich nicht immer rückgängig machen.“); PUA-Schutz toggle suggestion; heuristic findings with actions: „Von Defender prüfen lassen“, „Deaktivieren/In Ablage“ (via engine, journalled), „Prozess beenden“ (labelled „nicht umkehrbar“).
- **Verlauf:** sessions list → records with status; „Alles rückgängig“, per-record „Rückgängig“; Ablage size + „Ablage jetzt leeren“ (confirm: „endgültig“).
- **Einstellungen:** version, „Nach Updates suchen“, toggle check on start, Ablage days, open data folder, open log folder.
All long operations async with cancel; no UI freeze.

- [ ] **Step 1:** Invoke `frontend-design`, write the direction into `docs/design-notes.md` (palette tokens, type scale, spacing) – short.
- [ ] **Step 2:** Implement theme + shell + pages + view models.
- [ ] **Step 3:** ViewModel tests in `tests/.../ViewModelTests.cs`: `Apply_count_reflects_selection`; `Apply_disabled_while_running`; `Unknown_items_not_selected_by_default`.
- [ ] **Step 4:** `dotnet build` 0 warnings-as-errors; launch app (UAC prompt – user present), take screenshots light/dark at 100 % and 150 % (computer-use), check keyboard navigation. Read-only actions only (scan) unless user approves.
- [ ] **Step 5:** Run `web-design-guidelines`-style self-review on contrast/focus states (WPF equivalent), fix. Commit `feat(app): WPF shell and pages`.

### Task 15: Installer, release script, icon prompts

**Files:** `installer/winwaechter.iss`, `build-release.ps1`, `assets/README.md`.

**Interfaces:** `build-release.ps1 -Version <x.y.z> [-Publish]`: sets version in `Directory.Build.props`, `dotnet test`, `dotnet publish src/WinWaechter.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=false -o publish\<v>`, ISCC with `/DAppVersion=<v> /DSourceDir=publish\<v>` → `dist\WinWaechter-Setup-<v>.exe`, `ReleaseTool sign` → `dist\SHA256SUMS(.sig)`; with `-Publish`: `gh release create v<v> dist\* --repo yIrtIcIkurt/winwaechter --notes-file CHANGELOG section` (only after user approval).
ISS: fixed `AppId={{<new GUID>}}`, `AppName=WinWächter`, `DefaultDirName={autopf}\WinWaechter`, `PrivilegesRequired=admin`, `ArchitecturesAllowed=x64compatible`, `ArchitecturesInstallIn64BitMode=x64compatible`, `CloseApplications=yes`, `RestartApplications=yes`, German language (`compiler:Languages\German.isl`), `[Icons]` Start menu „WinWächter“ + optional desktop task (unchecked), `[Run]` launch after install (`postinstall` + also when `/SILENT` via `skipifnotsilent` second entry), `[Dirs]`/`[Code]` create `{commonappdata}\WinWaechter` and reset ACL via `icacls "<dir>" /setowner *S-1-5-32-544` + `/inheritance:r /grant:r *S-1-5-32-544:(OI)(CI)F *S-1-5-18:(OI)(CI)F *S-1-5-32-545:(OI)(CI)RX`; uninstall asks „Sicherungen und Ablage behalten?“ (default yes) and deletes data dir only on „Nein“. `SetupIconFile`/`UninstallDisplayIcon` use `assets\icon\winwaechter.ico` if present (`#ifexist`).

Icon prompts: write `assets/README.md` with the prompt(s) and exact targets: `C:\Users\tahsi\dev\winwaechter\assets\icon\winwaechter-1024.png` (1024×1024 PNG, transparent). On delivery, convert to `winwaechter.ico` (16/24/32/48/64/128/256) with a small script and wire into App (`ApplicationIcon`) and ISS. Send prompts to the user in chat at this task.

- [ ] **Step 1:** Write ISS + script. **Step 2:** Run `.\build-release.ps1 -Version 0.1.0` → `dist\WinWaechter-Setup-0.1.0.exe`, `SHA256SUMS`, `.sig` exist; `ReleaseVerifier` test harness (`ReleaseTool verify`) confirms. **Step 3:** Commit `build: Inno Setup installer and release script`.

### Task 16: End-to-end on the user's machine (each system change needs chat approval)

- [ ] **Step 1 (approval):** Install `WinWaechter-Setup-0.1.0.exe` → Start menu entry present, app starts with UAC, `%ProgramData%\WinWaechter` ACL = Admins/SYSTEM full, Users RX (`icacls`).
- [ ] **Step 2:** Scan (read-only): overview, optimizer preview, Defender status/threats, heuristics. Check that Dell/Lexware/Microsoft items do not appear as proposals; compare with manual findings from `C:\Users\tahsi\tools\aufraeumen-backup-20261108`.
- [ ] **Step 3 (approval):** Create test fixtures via script `tests/e2e/create-fixtures.ps1` (HKCU Run value `WW-Test` → `%TEMP%\ww-test.exe` copy of an unsigned stub, hidden scheduled task `\WW-Test\Ping`, 3 old temp files) → optimizer proposes them → apply only those → verify states → Verlauf „Alles rückgängig“ → verify restored → `remove-fixtures.ps1`.
- [ ] **Step 4 (approval):** Quick scan via Defender from the app; cancel test on a second run.
- [ ] **Step 5 (approval – public repo):** `gh auth login` (user does device login), `gh repo create yIrtIcIkurt/winwaechter --public`, push, publish `v0.1.0` and then `v0.1.1` (trivial change). Installed 0.1.0 finds 0.1.1, verifies, installs, restarts as 0.1.1. Negative test: local tampered setup through `ReleaseVerifier` → refused.
- [ ] **Step 6:** Uninstall via Windows „Apps“ (approval), check Start menu entry removed and data kept by default; reinstall.
- [ ] **Step 7:** Update memory `projekt-stand`; commit `test: e2e verified`.
