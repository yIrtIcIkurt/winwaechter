# WinWächter 0.2.0 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extend WinWächter with a cache cleaner (browsers, Windows Update, dumps, error reports, old installers), uninstall-leftover detection (orphaned entries and folders) and a new „Datenschutz“ page with single, reversible registry settings.

**Architecture:** Everything stays inside the 0.1 pattern: scanners return `Finding`s, every system change is an `IChange` journalled by `OptimizerEngine` and undoable from disk via `ChangeFactory`. New pieces: `MoveToAblageChange` gets the kinds `cache` (multi-folder, age-filtered, single file) and `folder` (one `Directory.Move`); `AblageJanitor` purges per kind; a new `RegValueChange` (`regvalue`) only accepts values listed under `privacy` in `rules.json`. The App gets new Optimize groups (via `Finding.Group`), a „Wird frei“ readout and a `PrivacyViewModel`/`PrivacyView` page.

**Tech Stack:** C# 13 / .NET 10 (`net10.0-windows`), WPF, CommunityToolkit.Mvvm, xUnit. No new packages.

**Spec:** `docs/superpowers/specs/2026-11-09-winwaechter-0.2-design.md` (§9 overrides §4–7) on top of `docs/superpowers/specs/2026-11-08-winwaechter-design.md`. Design notes: `docs/design-notes.md`.

## Global Constraints

- Branch `feature/v0.2` (already checked out). Do not switch branches, do not push.
- `$DOTNET` = `C:\Users\tahsi\AppData\Local\Microsoft\dotnet\dotnet.exe`, always with `DOTNET_CLI_TELEMETRY_OPTOUT=1` and `DOTNET_NOLOGO=1`. Test command: `$DOTNET test tests\WinWaechter.Tests\WinWaechter.Tests.csproj` (add `--filter "FullyQualifiedName~<Class>"` for one class). The full suite (~340 tests) must stay green after every task.
- No new NuGet packages. No telemetry. All UI copy German, sachlich, „Sie“-Anrede or impersonal, nicht alarmierend.
- Everything reversible via journal + Ablage/undo. Nothing is deleted except by `AblageJanitor`: Kind `cache` auto-purged after **1 day**; Kind `temp`/`file` after the setting (default 14 days); Kind `folder` **never** auto-purged, only via „Ablage jetzt leeren“.
- Never touch cookies, history, passwords, sessions of browsers – only cache folders. No `DISM /ResetBase`, no recycle-bin emptying, no Delivery-Optimization cmdlets. The app never terminates a browser.
- Protected list (`RuleSet.Classify` → `Protected`) wins over every new rule. Paths under `C:\Windows` are allowed only for the fixed cache targets (Task 4); orphan entries and leftover folders never under `C:\Windows`.
- Nothing preselected unless the spec says so: preselected = browser caches, WU downloads, dumps, error reports, orphaned entries. Never preselected = old installers, leftover folders, every privacy row.
- `regvalue` changes only for (hive, key, name) triples listed under `privacy` in `rules.json`; the target value always comes from `rules.json`, never from a journal. HKCU always means `HKU\<SID>` of the interactive user (`WinRegistry` already does this).
- Tests use fakes and temp dirs only; no test changes the real registry, services or files outside its temp dir (read-only smoke reads are allowed).
- Version `0.2.0` in `Directory.Build.props` + `## 0.2.0` section in `CHANGELOG.md` (Task 8).
- Every commit message ends with the trailer line `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Design per `docs/design-notes.md` (tokens, Bahnschrift readouts, `Button.Final` for endgültige Aktionen, no new colours).

## Review Focus

1. **Browser process with unreadable path** (elevated app cannot read `ExePath` of a protected process, `RunningProcess.ExePath == null` but name `msedge.exe`) → treated as running, no change offered. Test `Browser_counts_as_running_when_process_path_is_unreadable` in Task 4.
2. **Registry read fails or returns nothing** (no Uninstall entries readable) → leftover scanner must not flag every folder in `Program Files`; it returns no findings. Test `No_uninstall_entries_means_no_leftover_findings` in Task 6.
3. **Tampered journal for `regvalue`** (record names a key outside the privacy list, or a different target) → rehydrate refuses, nothing written; undo restores the recorded old value only for listed triples. Test `FromRecord_refuses_value_not_in_privacy_list` in Task 2.
4. **Undo after the original path is occupied again** (program reinstalled into the same folder, or a cache folder recreated) → folder undo reports a conflict and leaves the folder in the Ablage; cache undo restores per file as in 0.1. Test `ForFolder_undo_reports_conflict_when_original_exists_again` in Task 3.
5. **Mixed retention in one purge** (one session with a `cache` record and a `folder` record, 2 days old) → auto-purge deletes only the cache subfolder; the folder subfolder stays and is counted in the purge dialog. Test `Auto_purge_removes_cache_after_one_day_keeps_folder_kind` in Task 3.

---

## File Structure

```
rules/rules.json                                   + "privacy", "leftoverIgnore"
src/WinWaechter.Core/
  Model/Finding.cs                                 + Group (optional), FindingGroups constants
  Sys/IRegistry.cs, WinRegistry.cs                 + GetKind, SubKeyNames
  Sys/IFileOps.cs, WinFileOps.cs                   + IsVolumeReady, EnumerateTopLevelDirs, InspectFolder, MoveDirectory, FolderFacts
  Sys/IProcessList.cs (new)                        RunningProcess, IProcessList, WinProcessList
  Sys/ProgramPresence.cs (new)                     Present / Missing / Unknown for a program path
  Sys/CleanerPaths.cs (new)                        per-user + system roots; DownloadsFolder resolver
  Rules/RuleSet.cs                                 + Privacy, LeftoverIgnore, IsLeftoverIgnored, FindPrivacyValue
  Rules/PrivacyDefinitions.cs (new)                PrivacySetting, PrivacyValue, PrivacyEffective
  Changes/ChangeFactory.cs                         + regvalue, cache, folder
  Privacy/RegValueChange.cs (new)
  Privacy/PrivacyScanner.cs (new)                  rows + states
  Privacy/RecallProbe.cs (new)
  Optimizer/Storage/MoveToAblageChange.cs          + ForCache, ForCacheFile, ForFolder, Measure
  Optimizer/Storage/AblageJanitor.cs               per-kind retention, preview folder count
  Optimizer/Storage/CacheScanner.cs (new)          browsers, WU, dumps, WER
  Optimizer/Storage/InstallerScanner.cs (new)      old installers in Downloads
  Optimizer/Storage/LeftoverFolderScanner.cs (new) + InstalledProgramIndex.cs (new)
  Optimizer/OptimizerEngine.cs                     MovedNothing covers cache/folder
  Optimizer/Autostart|Services|Tasks/*Scanner.cs   orphan detection
src/WinWaechter.App/
  Services/AppServices.cs                          wiring
  ViewModels/OptimizeViewModel.cs, FindingItemViewModel.cs   groups, sums, hint, SelectedBytes
  ViewModels/OverviewViewModel.cs, Views/OverviewView.xaml   „Wird frei“
  ViewModels/HistoryViewModel.cs                   purge dialog names folder count
  ViewModels/PrivacyViewModel.cs (new), Views/PrivacyView.xaml(.cs) (new)
  ViewModels/ShellViewModel.cs, Views/ShellWindow.xaml       nav „Datenschutz“
tests/WinWaechter.Tests/
  SysExtensionTests.cs, PrivacyTests.cs, AblageKindsTests.cs, CacheScannerTests.cs,
  OrphanTests.cs, LeftoverTests.cs, PrivacyViewModelTests.cs (new); Fakes/FakeProcessList.cs (new);
  Fakes/FakeRegistry.cs, Fakes/FakeFileOps.cs, ViewModelTests.cs, ViewSmokeTests.cs, HeuristicTests.cs (modified)
```

---

### Task 1: System layer extensions

**Files:**
- Modify: `src/WinWaechter.Core/Sys/IRegistry.cs`, `Sys/WinRegistry.cs`, `Sys/IFileOps.cs`, `Sys/WinFileOps.cs`
- Create: `src/WinWaechter.Core/Sys/IProcessList.cs`, `Sys/ProgramPresence.cs`, `Sys/CleanerPaths.cs`
- Modify: `tests/WinWaechter.Tests/Fakes/FakeRegistry.cs`, `Fakes/FakeFileOps.cs`; Create `Fakes/FakeProcessList.cs`
- Test: `tests/WinWaechter.Tests/SysExtensionTests.cs`

**Interfaces:**
- Consumes: existing `IRegistry`, `IFileOps`, `IUserContext`, `PathNormalizer.NormalizePath`, `SysSmokeTests.MakeJunction(link, target)`.
- Produces:
```csharp
// IRegistry (new members)
RegistryValueKind? GetKind(Hive h, string key, string name);    // null when key or value missing
IReadOnlyList<string> SubKeyNames(Hive h, string key);           // empty when key missing

// IFileOps (new members)
bool IsVolumeReady(string path);                                  // false for UNC, unready drive, any error
IEnumerable<DirectoryInfo> EnumerateTopLevelDirs(string dir);     // incl. reparse-point dirs; empty if missing
FolderFacts? InspectFolder(string dir);                           // null: missing, itself a reparse point, or any access denied
void MoveDirectory(string src, string dst);                       // one Directory.Move; IOException if src is a reparse point,
                                                                  // dst exists, or Path.GetPathRoot differs; creates dst parent chain
public sealed record FolderFacts(long Bytes, int Files, DateTime LatestWriteUtc, bool HasBinaries, bool HasReparsePoints);

// Sys/IProcessList.cs
public sealed record RunningProcess(string Name, string? ExePath);   // Name incl. ".exe", e.g. "msedge.exe"
public interface IProcessList { IReadOnlyList<RunningProcess> Snapshot(); }
public sealed class WinProcessList : IProcessList;                    // never throws; ExePath null when unreadable

// Sys/ProgramPresence.cs
public enum Presence { Present, Missing, Unknown }
public static class ProgramPresence { public static Presence Check(string? path, IFileOps files); }

// Sys/CleanerPaths.cs
public sealed record CleanerPaths(string LocalAppData, string RoamingAppData, string SystemRoot, string ProgramData,
    string ProgramFiles, string ProgramFilesX86)
{ public static CleanerPaths For(IUserContext u); }   // Local/Roaming = u.ProfileDir\AppData\{Local,Roaming}; rest from Environment
public static class DownloadsFolder
{
    public const string KnownFolderId = "{374DE290-123F-4565-9164-39C4925E467B}";
    public static string Resolve(IRegistry reg, IUserContext u);
}
// Tests: FakeRegistry.SeedKey(Hive, string) creates an empty key; FakeFileOps.NotReady (HashSet<string> of roots),
// FakeFileOps.LockedDirs (MoveDirectory throws IOException), FakeFileOps.DirMoveCalls,
// FakeFileOps.DeniedDirs (InspectFolder returns null); FakeProcessList(params RunningProcess[]) with settable List.
```

- [ ] **Step 1: Write the failing tests** in `SysExtensionTests.cs` (temp dir root like `StorageTests`):
  - `WinRegistry_reads_kind_and_subkeys_read_only`: `new WinRegistry(new FakeUserContext(_root))`; `GetKind(Hive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuild") == RegistryValueKind.String`; `GetKind(..., "NoSuchValue_ww") is null`; `SubKeyNames(Hive.LocalMachine, @"SOFTWARE\Microsoft")` contains `"Windows"` (OrdinalIgnoreCase); `SubKeyNames(Hive.LocalMachine, @"SOFTWARE\NoSuchKey_ww")` is empty.
  - `FakeRegistry_GetKind_and_SubKeyNames`: seed `Hive.CurrentUser, @"A\B", "v", 1, DWord` and `SeedKey(Hive.CurrentUser, @"A\C")` → `GetKind == DWord`; `SubKeyNames(Hive.CurrentUser, "A")` equals `{"B","C"}` (any order).
  - `InspectFolder_reports_size_latest_write_and_binaries`: folder with `x\a.dll` (3 bytes, write −40 d) and `b.txt` (2 bytes, write −35 d), folder timestamps set to −40 d → `Bytes == 5`, `Files == 2`, `HasBinaries`, `!HasReparsePoints`, `LatestWriteUtc` within 1 min of −35 d.
  - `InspectFolder_flags_inner_junction_and_returns_null_for_junction_root`: inner `MakeJunction(folder\link, outside)` → `HasReparsePoints == true` and `Bytes` excludes files in `outside`; `InspectFolder(link)` → `null`.
  - `MoveDirectory_moves_whole_tree_and_refuses_existing_target_and_junction`: move `src` (with nested file) to `dst\sub\src` → file exists at new place, `src` gone; second move onto existing dst → `IOException`; `MoveDirectory(junction, …)` → `IOException`, junction still there.
  - `IsVolumeReady_false_for_unc_and_true_for_temp`: `IsVolumeReady(@"\\server\share\x.exe") == false`; `IsVolumeReady(_root) == true`.
  - `ProgramPresence_cases`: null/"" → `Unknown`; `"relative.exe"` → `Unknown`; UNC → `Unknown`; root in `FakeFileOps.NotReady` → `Unknown`; existing temp file → `Present`; missing file in temp dir → `Missing`; `"%TEMP%\\nope_ww.exe"` (normalized) → `Missing`.
  - `DownloadsFolder_resolves_user_shell_folder_with_profile_of_interactive_user`: seed HKCU `Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders` value `KnownFolderId` = `"%USERPROFILE%\\Daten\\Downloads"` (ExpandString) → `Resolve == Path.Combine(u.ProfileDir, "Daten", "Downloads")`; without any value → `Path.Combine(u.ProfileDir, "Downloads")`.
  - `WinProcessList_snapshot_contains_own_process`: snapshot contains an entry whose `ExePath` equals `Environment.ProcessPath` (OrdinalIgnoreCase) and whose `Name` ends with `.exe`.
- [ ] **Step 2: Run** `$DOTNET test … --filter "FullyQualifiedName~SysExtensionTests"` → FAIL (members missing; build error).
- [ ] **Step 3: Implement** the members above. `WinRegistry.GetKind`: `OpenSubKey` + `GetValueKind` inside try (IOException → null). `InspectFolder`: manual depth-first walk with `EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false }`, never descending into reparse points, `UnauthorizedAccessException`/`IOException` anywhere → `null`; binaries = extension `.exe` or `.dll`; latest = max of files' and dirs' `LastWriteTimeUtc` (root included). `MoveDirectory` reuses `CreateChain` and `LongPath`. `ProgramPresence.Check`: `PathNormalizer.NormalizePath`, then UNC (`\\`) or not rooted → Unknown, `!IsVolumeReady` → Unknown, `files.Exists` → Present else Missing. `WinProcessList`: `Process.GetProcesses()`, `ProcessName + ".exe"`, path via `QueryFullProcessImageNameW` on a `PROCESS_QUERY_LIMITED_INFORMATION` handle (add P/Invoke to `Sys/Native/UserNative.cs`), dispose every `Process`. `DownloadsFolder.Resolve`: `User Shell Folders` → `Shell Folders` → `ProfileDir\Downloads`; replace `%USERPROFILE%` (OrdinalIgnoreCase) with `u.ProfileDir` before `ExpandEnvironmentVariables`. Update both fakes accordingly (FakeFileOps works on real dirs; `Junctions` count as reparse points in `InspectFolder`/`MoveDirectory`).
- [ ] **Step 4: Run** the filter, then the full suite → all PASS.
- [ ] **Step 5: Commit** `feat(core): registry kind/subkeys, folder facts, process list, presence check`.

---

### Task 2: Privacy rules, RegValueChange, PrivacyScanner

**Files:**
- Create: `src/WinWaechter.Core/Rules/PrivacyDefinitions.cs`, `Privacy/RegValueChange.cs`, `Privacy/PrivacyScanner.cs`, `Privacy/RecallProbe.cs`
- Modify: `Rules/RuleSet.cs`, `rules/rules.json`, `Changes/ChangeFactory.cs`, `src/WinWaechter.App/Services/AppServices.cs` (only the `CreateDefault` call)
- Test: `tests/WinWaechter.Tests/PrivacyTests.cs`

**Interfaces:**
- Consumes: `IRegistry.GetKind` (Task 1), `IProcessRunner.RunAsync(exe, args, ct)`, `FakeProcessRunner.Handler`.
- Produces:
```csharp
public sealed record PrivacyValue(Hive Hive, string Key, string Name, int Target);          // always REG_DWORD
public sealed record PrivacyEffective(Hive Hive, string Key, string Name, int AtMost);
public sealed record PrivacySetting(string Id, string Title, string Explanation, bool Recommended, string? Requires,
    IReadOnlyList<PrivacyValue> Values, PrivacyEffective? Effective);
// RuleSet
public IReadOnlyList<PrivacySetting> Privacy { get; }
public IReadOnlyList<string> LeftoverIgnore { get; }
public bool IsLeftoverIgnored(string folderName);     // "X*" = prefix, else exact; OrdinalIgnoreCase
public PrivacyValue? FindPrivacyValue(Hive h, string key, string name);   // key/name OrdinalIgnoreCase, key trimmed of '\'

public sealed class RegValueChange : IChange
{
    public RegValueChange(IRegistry reg, PrivacyValue value, string settingTitle);
    public static RegValueChange FromRecord(ChangeRecord r, IRegistry reg, RuleSet rules);
    // Kind "regvalue"; Target $"{HKCU|HKLM}\{Key}\{Name}"; Description $"{settingTitle}: „{Name}“ auf {Target} setzen"
    // Before: hive ("HKCU"/"HKLM"), key, name, existed ("true"/"false"), value (invariant string or null), kind (RegistryValueKind name or null)
    // After:  value (target), kind "DWord"
}
public enum PrivacyState { On, Partial, Off, AlreadyMinimal }
public sealed record PrivacyRow(PrivacySetting Setting, PrivacyState State, IReadOnlyList<RegValueChange> Changes);
public sealed class PrivacyScanner
{
    public PrivacyScanner(IRegistry reg, RuleSet rules);
    public IReadOnlyList<PrivacyRow> Scan(Func<string, bool> requirementMet);
}
public static class RecallProbe { public static Task<bool> IsPresentAsync(IProcessRunner runner, CancellationToken ct); }
// ChangeFactory.CreateDefault(IRegistry reg, IServiceControl sc, ITaskStore ts, IFileOps fo, RuleSet rules) registers "regvalue"
```

`rules.json` → `"privacy"` (all `"recommended": true` unless noted; CDM = `Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager`, ADV = `Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced`):

| id | title | hive / key | values → target |
|---|---|---|---|
| `werbe-id` | Werbe-ID für Apps | HKCU `Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo` | `Enabled` → 0 |
| `angebote-diagnose` | Maßgeschneiderte Angebote aus Diagnosedaten | HKCU `Software\Microsoft\Windows\CurrentVersion\Privacy` | `TailoredExperiencesWithDiagnosticDataEnabled` → 0 |
| `vorschlaege` | Vorschläge in Einstellungen und auf dem Sperrbildschirm | HKCU CDM | `SubscribedContent-338393Enabled`, `SubscribedContent-353694Enabled`, `SubscribedContent-353696Enabled`, `SubscribedContent-338388Enabled` → 0 |
| `vorschlags-apps` | Automatisch installierte Vorschlags-Apps | HKCU CDM | `SilentInstalledAppsEnabled` → 0 |
| `tipps` | Tipps und Hinweise | HKCU CDM | `SubscribedContent-338389Enabled`, `SoftLandingEnabled` → 0 |
| `start-empfehlungen` | Empfehlungen im Startmenü | HKCU ADV | `Start_IrisRecommendations` → 0 |
| `programmstarts` | Programmstarts verfolgen (für Startmenü) | HKCU ADV | `Start_TrackProgs` → 0, **`"recommended": false`** |
| `websuche` | Bing-/Webvorschläge in der Suche | HKCU `Software\Policies\Microsoft\Windows\Explorer` | `DisableSearchBoxSuggestions` → 1 |
| `sprachliste` | Sprachliste für Websites | HKCU `Control Panel\International\User Profile` | `HttpAcceptLanguageOptOut` → 1 |
| `feedback` | Feedback-Abfragen | HKCU `Software\Microsoft\Siuf\Rules` | `NumberOfSIUFInPeriod` → 0 |
| `recall` | Recall (Momentaufnahmen speichern) | HKCU `Software\Policies\Microsoft\Windows\WindowsAI` | `DisableAIDataAnalysis` → 1, `"requires": "recall"` |
| `diagnosedaten` | Diagnosedaten auf Minimum | HKLM `SOFTWARE\Policies\Microsoft\Windows\DataCollection` | `AllowTelemetry` → 1; `"effective": { HKLM, SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\DataCollection, AllowTelemetry, atMost 1 }` |

Explanations (German, one or two sentences) must state: `programmstarts` changes „Meistverwendet“; `websuche` is a Richtlinie, also removes recent search suggestions in Explorer and takes effect after Explorer restart/Abmelden; `recall` is a Richtlinie that also blocks turning Recall on; `diagnosedaten` = „nur erforderliche Diagnosedaten“; every policy row (`websuche`, `recall`, `diagnosedaten`) adds that the Einstellungen-App then shows „Von deiner Organisation verwaltet“ and locks the switch. JSON shape per row: `{ "id", "title", "explanation", "recommended", "requires"?, "effective"?, "values": [ { "hive": "HKCU", "key", "name", "target" } ] }`. Also add `"leftoverIgnore"` (used in Task 6): `Microsoft*`, `Windows*`, `Common Files`, `Packages`, `Temp`, `Programs`, `Package Cache`, `Comms`, `ConnectedDevicesPlatform`, `D3DSCache`, `PlaceholderTileLogoFolder`, `Publishers`, `Mozilla`, `Google`, `Opera Software`, `npm`, `pip`, `uv`, `NuGet`, `dotnet`, `.NET*`, `JetBrains`, `Steam`, `WindowsApps`, `ModifiableWindowsApps`, `Reference Assemblies`, `MSBuild`, `Internet Explorer`, `Uninstall Information`, `Dell`, `Intel`, `Realtek`, `NVIDIA*`, `AMD`, `Lexware`, `Haufe*`, `WinWaechter`.

- [ ] **Step 1: Write the failing tests** (`PrivacyTests.cs`, `FakeRegistry`, `RuleSet.Default`):
  - `Rules_privacy_list_matches_spec`: `Privacy.Count == 12`; ids in table order; `vorschlaege` has 4 values and no `SystemPaneSuggestionsEnabled`; no row id `aktivitaetsverlauf`; `programmstarts.Recommended == false`, all others true; `diagnosedaten.Effective!.AtMost == 1`; every value `Hive` is CurrentUser except `diagnosedaten` (LocalMachine); `IsLeftoverIgnored("Microsoft Office")`, `IsLeftoverIgnored("common files")`, `!IsLeftoverIgnored("Foo")`.
  - `Load_rejects_unknown_hive_in_privacy`: temp `rules.json` copy with `"hive": "HKCR"` → `RuleSet.Load` throws `InvalidDataException`.
  - `Apply_sets_dword_and_undo_restores_old_value_and_kind`: seed `Enabled` = 1 (DWord) → Apply → `Get == 0`, kind DWord, `Before["existed"]=="true"`, `Before["value"]=="1"`; `CheckDrift == Unchanged`; Undo → `Get == 1`, kind DWord.
  - `Undo_deletes_value_that_was_missing`: no seed → Apply → `Before["existed"]=="false"`; Undo → `DeleteCalls` contains the name, `Get` is null.
  - `Undo_restores_string_kind_exactly`: seed `"1"` as `RegistryValueKind.String` → Apply/Undo → value `"1"`, kind String.
  - `Apply_refuses_binary_kind_and_writes_nothing`: seed `byte[]` Binary → `InvalidOperationException`, `SetCalls` empty.
  - `CheckDrift_changed_when_value_differs_from_target`: after Apply set value to 1 → `Changed`.
  - `FromRecord_refuses_value_not_in_privacy_list`: record with Before key `Software\Evil`, name `X` → `InvalidOperationException`; record for `AdvertisingInfo\Enabled` with After value `"5"` rehydrates and Apply writes `0` (target from rules).
  - `Factory_rehydrates_regvalue`: `ChangeFactory.CreateDefault(reg, sc, ts, fo, RuleSet.Default).Rehydrate(record).Kind == "regvalue"`.
  - `Scanner_states_on_partial_off`: `tipps` with both values 0 → `Off`, `Changes` empty; one value 0 → `Partial`, 1 change; none set → `On`, 2 changes.
  - `Scanner_diagnostics_already_minimal_from_effective_value`: effective `AllowTelemetry` = 1 → `AlreadyMinimal`, no changes; effective 3 → `On`, 1 change.
  - `Scanner_hides_recall_row_without_component`: `Scan(_ => false)` has no row `recall`; `Scan(r => r == "recall")` has it.
  - `RecallProbe_true_only_for_exit_zero_with_feature_name`: handler returns `(0, "Feature Name : Recall\r\nState : Enabled", "")` → true; `(87, "", "")` → false; handler throws → false; called exe ends with `dism.exe`, args contain `/FeatureName:Recall`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~PrivacyTests"` → FAIL.
- [ ] **Step 3: Implement.** RuleSet DTO maps `"HKCU"`→`Hive.CurrentUser`, `"HKLM"`→`Hive.LocalMachine`, anything else → `InvalidDataException`. `RegValueChange.Apply`: read `GetKind`/`Get` → Before; kinds allowed DWord, QWord, String, ExpandString (else throw, nothing written); `Set(…, Target, DWord)`. Undo parses `value` by `kind` (invariant culture). `FromRecord` looks up `rules.FindPrivacyValue(hive, key, name)` from `Before`, throws `InvalidOperationException("Dieser Registry-Wert steht nicht in der Datenschutz-Liste – Rückgängig abgelehnt.")` when null; uses the record's `Description`. Value at target = `GetKind == DWord && Convert.ToInt32(Get) == Target`. `RecallProbe` runs `%SystemRoot%\System32\dism.exe` with `/English /Online /Get-FeatureInfo /FeatureName:Recall`, catches all exceptions. Update the `CreateDefault` call in `AppServices` to pass `rules`.
- [ ] **Step 4: Run** filter + full suite → PASS.
- [ ] **Step 5: Commit** `feat(core): privacy list, regvalue change and privacy scanner`.

---

### Task 3: Ablage kinds `cache` and `folder`, per-kind retention

**Files:**
- Modify: `src/WinWaechter.Core/Model/Finding.cs`, `Optimizer/Storage/MoveToAblageChange.cs`, `Optimizer/Storage/AblageJanitor.cs`, `Optimizer/OptimizerEngine.cs`, `Changes/ChangeFactory.cs`
- Test: `tests/WinWaechter.Tests/AblageKindsTests.cs`

**Interfaces:**
- Consumes: `IFileOps.MoveDirectory`, `InspectFolder` (Task 1).
- Produces:
```csharp
// Finding: new last optional parameter
public sealed record Finding(…, bool IsProtectedVendor = false, string? Group = null);
public static class FindingGroups
{
    public const string BrowserCache = "Browser-Cache", WindowsRemnants = "Windows-Reste", OldInstallers = "Alte Installer",
        OrphanEntries = "Verwaiste Einträge", OrphanFolders = "Verwaiste Ordner";
}
// MoveToAblageChange
public static MoveToAblageChange ForCache(IFileOps f, string root, IReadOnlyList<string> includeDirs, TimeSpan minAge,
    bool ageByNewerTimestamp = false);   // Kind "cache"; every includeDir must be under/equal root (else ArgumentException)
public static MoveToAblageChange ForCacheFile(IFileOps f, string path);   // Kind "cache", single file, Before["single"]="true"
public static MoveToAblageChange ForFolder(IFileOps f, string folder);    // Kind "folder", whole folder, one MoveDirectory
public (int Files, long Bytes) Measure();   // what Apply would move now (folder kind: InspectFolder totals)
// FromRecord: "file" → ForFile; "temp" → folder ctor; "cache" → Before["single"]=="true" ? ForCacheFile : ForCache(Target,[Target],Zero);
//             "folder" → ForFolder
// Plan/Apply Before: "path" always; "single" for ForCacheFile; "include" = includeDirs joined by '|' for ForCache
// AblageJanitor
public static readonly TimeSpan CacheMaxAge = TimeSpan.FromDays(1);
public long Purge(TimeSpan maxAge);   // cache kind: CacheMaxAge, temp/file: maxAge, folder: never
public sealed record AblagePreview(long PurgeableBytes, int KeptFolders, int ProgramFolders);  // ProgramFolders = folder-kind subfolders PurgeNow deletes
// OptimizerEngine.MovedNothing: Kind is "temp" or "file" or "cache" or "folder" && After["moved"]=="0"
// ChangeFactory.CreateDefault registers "cache" and "folder" → MoveToAblageChange.FromRecord
```

- [ ] **Step 1: Write the failing tests** (`AblageKindsTests.cs`, real temp dirs, `WinFileOps`, `SessionContext(Path.Combine(_root,"session"))`):
  - `ForCache_moves_only_included_dirs_and_keeps_relative_layout`: root `ud` with `Default\Cache\f1` and `Default\Preferences` (both −1 d), include `[ud\Default\Cache]`, `minAge Zero` → `f1` stored at `ablage\<id>\Default\Cache\f1`; `Preferences` untouched; `Kind == "cache"`, `Before["include"]` equals the include path; undo restores `f1`.
  - `ForCache_ageByNewer_uses_newer_of_creation_and_write`: file with write −10 d but creation now → not moved with `minAge 7 d, ageByNewerTimestamp true`; file with both −10 d → moved.
  - `ForCache_rejects_include_outside_root`: `ArgumentException`.
  - `ForCacheFile_records_single_and_rehydrates_as_single`: `MEMORY.DMP` in temp “windows” dir → Apply → `Before["single"] == "true"`, `Kind == "cache"`; `MoveToAblageChange.FromRecord(rec, ops).Undo(rec, ctx)` restores the file.
  - `ForFolder_moves_whole_folder_in_one_step_and_undo_restores`: folder `pf\Foo` with nested files; `FakeFileOps` → exactly one `DirMoveCalls` entry, zero `MoveCalls`; `After["moved"] == "1"`, `After["bytes"]` = total bytes; undo → folder and files back at `pf\Foo`.
  - `ForFolder_failure_moves_nothing_and_reports_reason`: `FakeFileOps.LockedDirs` contains folder → Apply throws `InvalidOperationException` whose message contains `„Foo“` and `nichts verschoben`; folder still in place.
  - `ForFolder_undo_reports_conflict_when_original_exists_again`: Apply, recreate `pf\Foo` → Undo throws `InvalidOperationException` containing `existiert wieder`; Ablage copy still exists.
  - `ForFolder_refuses_junction_folder`: `FakeFileOps.Junctions` contains the folder → Apply throws, `DirMoveCalls` empty.
  - `Auto_purge_removes_cache_after_one_day_keeps_folder_kind`: session created, records `cache` and `folder` (Applied) and `temp`; set the journal's created time 2 days back (as `Janitor_purges_only_old_sessions` does) → `Purge(TimeSpan.FromDays(14))` deletes the cache subfolder only; folder and temp subfolders remain; cache record gets `After["purged"] == "true"`.
  - `PurgeNow_deletes_folder_kind_and_preview_counts_it`: `PreviewNow().ProgramFolders == 1` before; `PurgeNow()` removes it; afterwards `ProgramFolders == 0`.
  - `MovedNothing_covers_cache_and_folder`: records with Kind `cache`/`folder` and `moved == "0"` → `OptimizerEngine.MovedNothing == true`.
  - `Factory_rehydrates_cache_and_folder`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~AblageKindsTests"` → FAIL.
- [ ] **Step 3: Implement.** Keep one class: private ctor with `(kind, path, includes, minAge, ageByNewer, single)`; `SourceRoot` = root (or parent dir for single). Collect iterates every include through `EnumerateOlderThan`, then filters `CreationTimeUtc < cutoff` when `ageByNewer`. Folder Apply: reject when `HasReparsePointInChain(folder, parent)` or `InspectFolder(folder) is null`, measure bytes, write sidecar with one entry (`original` = folder, `stored` = `ablage\<id>\<folderName>`) **before** `MoveDirectory`, catch `IOException`/`UnauthorizedAccessException` → truncate sidecar and throw `InvalidOperationException($"Ordner „{name}“ konnte nicht verschoben werden ({ex.Message}). Es wurde nichts verschoben.")`. Folder Undo: original exists → `InvalidOperationException($"Ordner „{name}“ existiert wieder am Originalort. Er bleibt in der Ablage: {stored}")`; else `MoveDirectory(stored, original)`. Janitor: `Classify` yields every subfolder with its owners' kinds and the session age; auto-purge rule per subfolder = all owners Applied/Undone **and** (any owner `folder` → never; any owner `cache` → age > `CacheMaxAge`; else age > maxAge). `PurgeNow` unchanged in reach (all kinds). Add `using`s as needed; `FromRecord` defaults stay compatible with 0.1 journals.
- [ ] **Step 4: Run** filter + full suite (incl. existing `StorageTests`) → PASS.
- [ ] **Step 5: Commit** `feat(core): Ablage kinds cache and folder with per-kind retention`.

---

### Task 4: Cache and old-installer scanners

**Files:**
- Create: `src/WinWaechter.Core/Optimizer/Storage/CacheScanner.cs`, `Optimizer/Storage/InstallerScanner.cs`
- Test: `tests/WinWaechter.Tests/CacheScannerTests.cs`

**Interfaces:**
- Consumes: `CleanerPaths`, `IProcessList`, `RunningProcess` (Task 1); `ForCache`, `ForCacheFile`, `ForFile`, `Measure`, `FindingGroups` (Task 3); `IServiceControl.Get(name)?.Running`; `IRegistry.KeyExists/Get`; `ISignatureVerifier`.
- Produces:
```csharp
public sealed class CacheScanner
{
    public CacheScanner(IFileOps f, CleanerPaths paths, IProcessList procs, IServiceControl sc, IRegistry reg);
    public IReadOnlyList<Finding> Scan();
}
public sealed class InstallerScanner
{
    public static readonly string[] Extensions = [".exe", ".msi", ".msix", ".msixbundle", ".appx"];
    public InstallerScanner(IFileOps f, ISignatureVerifier sig, string downloadsDir);
    public IReadOnlyList<Finding> Scan();
}
```
Targets (Area `Speicher`, Rating `Entbehrlich`, preselected, Id `cache|<title>`, Reasons first line `"{n} Dateien"` or `"{n} Dateien älter als {d} Tage"`; skip a target when it has no files; Info finding without change when blocked):

| Title | Group | Root → includes | Age |
|---|---|---|---|
| Browser-Cache Edge / Chrome | BrowserCache | `Local\Microsoft\Edge\User Data` / `Local\Google\Chrome\User Data` → per profile (`Default`, `^Profile \d+$`, or dir with a `Preferences` file): `Cache`, `Code Cache`, `GPUCache`, `DawnGraphiteCache`, `DawnWebGPUCache`; at root: `ShaderCache`, `GrShaderCache`, `GraphiteDawnCache`, `DawnGraphiteCache`, `DawnWebGPUCache` (existing dirs only) | 0 |
| Browser-Cache Opera | BrowserCache | parent of `LocalAppData` → `Local\Opera Software\Opera Stable\Default\{Cache,Code Cache,GPUCache,DawnGraphiteCache,DawnWebGPUCache}`, `Local\Opera Software\Opera Stable\{Cache,Code Cache,GPUCache}`, `Roaming\Opera Software\Opera Stable\{ShaderCache,GrShaderCache,GraphiteDawnCache}` (no `Preferences` check) | 0 |
| Browser-Cache Firefox | BrowserCache | `Local\Mozilla\Firefox\Profiles` → `*\cache2` | 0 |
| Windows-Update-Downloads | WindowsRemnants | `SystemRoot\SoftwareDistribution\Download` → itself, `ageByNewerTimestamp: true` | 7 d |
| Absturzabbilder (Programme) | WindowsRemnants | `Local\CrashDumps` | 2 d |
| Absturzabbilder (Windows) | WindowsRemnants | `SystemRoot` → `Minidump`, `LiveKernelReports` | 2 d |
| Speicherabbild (MEMORY.DMP) | WindowsRemnants | `ForCacheFile(SystemRoot\MEMORY.DMP)` only if `LastWriteTimeUtc` older than 2 d | 2 d |
| Fehlerberichte (Windows) / (Benutzer) | WindowsRemnants | `ProgramData\Microsoft\Windows\WER` / `Local\Microsoft\Windows\WER` → `ReportArchive`, `ReportQueue` | 2 d |

Browser running = any `RunningProcess` with `Name` equal (OrdinalIgnoreCase) to `msedge.exe` / `chrome.exe` / `opera.exe` / `firefox.exe` whose `ExePath` is null **or** under the browser's install dirs: Edge `ProgramFilesX86|ProgramFiles\Microsoft\Edge\Application`; Chrome `ProgramFiles|ProgramFilesX86\Google\Chrome\Application`, `Local\Google\Chrome\Application`; Opera `Local\Programs\Opera`, `ProgramFiles\Opera`; Firefox `ProgramFiles|ProgramFilesX86\Mozilla Firefox`. `msedgewebview2.exe` never counts. Blocked reasons: Edge `"Edge läuft (auch im Hintergrund). Edge vollständig schließen und erneut prüfen."`; others `"Browser ist geöffnet – zum Aufräumen schließen"`. WU blocked (Info, no change) when `RebootRequired` key `SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired` exists or `SYSTEM\CurrentControlSet\Control\Session Manager` `PendingFileRenameOperations` is a non-empty `string[]` → `"Ein Neustart nach Updates steht aus – erst neu starten"`; `TrustedInstaller` running, or `wuauserv` running **and** a file in the folder newer than 1 h → `"Windows Update arbeitet gerade"`.

Installers: top-level files of `downloadsDir` with an extension in `Extensions` and `LastWriteTimeUtc` older than 30 d; one finding per file: Id `installer|<path>`, Group `OldInstallers`, Title file name, Location path, Rating `Info`, not preselected, Change `ForFile`, `SizeBytes`, `Publisher` = signature publisher, Reasons `[$"Zuletzt geändert am {dd.MM.yyyy}", <"Signatur: gültig, Herausgeber X" | "Signatur: nicht signiert" | "Signatur: ungültig" | "Signatur: nicht prüfbar">]`.

- [ ] **Step 1: Write the failing tests** (temp-dir `CleanerPaths`, `FakeProcessList`, `FakeServiceControl`, `FakeRegistry`, `FakeSignatures`):
  - `Edge_cache_offered_with_profiles_and_shader_caches`: `Default\Cache\a` (3 B), `Profile 2\Code Cache\b` (2 B), `ShaderCache\c` (1 B), `Default\Cookies` (9 B) → one finding `Browser-Cache Edge`, `Group == BrowserCache`, `Preselected`, `SizeBytes == 6`, Change Kind `cache`.
  - `Custom_named_profile_with_Preferences_counts`: `Arbeit\Preferences` + `Arbeit\Cache\x` → included.
  - `Edge_running_blocks_with_background_hint`: process `("msedge.exe", <ProgramFilesX86>\Microsoft\Edge\Application\msedge.exe)` → finding has `Change == null`, `Rating.Info`, `!Preselected`, reason exact Edge text above.
  - `Webview2_does_not_block_edge`: only `msedgewebview2.exe` running → change offered.
  - `Browser_counts_as_running_when_process_path_is_unreadable`: `("chrome.exe", null)` → Chrome blocked.
  - `Chrome_from_other_path_does_not_block`: `("chrome.exe", @"D:\Portable\chrome.exe")` → offered.
  - `Opera_uses_local_default_and_roaming_shader_caches`: files in `Local\Opera Software\Opera Stable\Default\Cache` and `Roaming\Opera Software\Opera Stable\GraphiteDawnCache` → one finding, both counted.
  - `Firefox_cache2_per_profile`.
  - `WU_downloads_offered_when_idle_and_older_than_7_days`: file −10 d (creation and write) → offered; `ageByNewer` respected (file with fresh creation not counted in size).
  - `WU_blocked_when_reboot_pending` (seed key `RebootRequired`) and `WU_blocked_by_pending_rename` (`PendingFileRenameOperations` = `["\\??\\C:\\x", ""]` MultiString) → Info, no change, reboot text.
  - `WU_blocked_when_trustedinstaller_runs_or_wuauserv_busy`: TrustedInstaller running → blocked; `wuauserv` running + file written 10 min ago → blocked; `wuauserv` running + all files −10 d → offered.
  - `Dumps_and_wer_targets_and_memory_dmp_single`: `CrashDumps\a.dmp` −3 d, `Minidump\m.dmp` −3 d, `LiveKernelReports\k.dmp` −3 d, `WER\ReportQueue\r` −3 d, `MEMORY.DMP` −3 d, fresh `CrashDumps\new.dmp` → titles present as in table, all `Group == WindowsRemnants`, MEMORY.DMP change has `Kind == "cache"`; fresh dump not counted; `MEMORY.DMP` −1 d → no MEMORY finding.
  - `Missing_folders_give_no_findings`: empty paths root → `Scan()` empty.
  - `Old_installers_one_per_file_never_preselected`: `setup.exe` −40 d (signed "Foo GmbH"), `tool.msi` −31 d, `new.exe` −5 d, `doc.pdf` −90 d, `sub\old.exe` −90 d → exactly 2 findings, `!Preselected`, `Rating.Info`, `Group == OldInstallers`, `Publisher == "Foo GmbH"` for setup.exe, reason contains `"Signatur: gültig, Herausgeber Foo GmbH"`, Change Kind `file`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~CacheScannerTests"` → FAIL.
- [ ] **Step 3: Implement** both scanners per the table; build each change with `ForCache`/`ForCacheFile`, size and count via `Measure()`. Never call `RuleSet` here (fixed targets only).
- [ ] **Step 4: Run** filter + full suite → PASS.
- [ ] **Step 5: Commit** `feat(core): cache and old-installer scanners`.

---

### Task 5: Orphaned autostart, task and service entries

**Files:**
- Modify: `src/WinWaechter.Core/Optimizer/Autostart/AutostartScanner.cs`, `Optimizer/Services/ServiceScanner.cs`, `Optimizer/Tasks/TaskScanner.cs`, `src/WinWaechter.App/Services/AppServices.cs` (constructor calls), `tests/WinWaechter.Tests/HeuristicTests.cs` (constructor calls)
- Test: `tests/WinWaechter.Tests/OrphanTests.cs`

**Interfaces:**
- Consumes: `ProgramPresence.Check` (Task 1), `FindingGroups.OrphanEntries` (Task 3).
- Produces: `public ServiceScanner(IServiceControl sc, ISignatureVerifier sig, RuleSet rules, IRegistry reg, IFileOps files)`; `public TaskScanner(ITaskStore store, ISignatureVerifier sig, RuleSet rules, IFileOps files)`; `public const string OrphanReason = "Programmdatei existiert nicht mehr (Deinstallationsrest)";` in a new `static class OrphanRules` (`Optimizer/OrphanRules.cs`) with `public static bool IsOrphan(string? exe, IFileOps files, string windowsDir)` = `ProgramPresence.Check(exe, files) == Presence.Missing` and normalized path not under `windowsDir`.

Rule in every scanner: after the protected check, if `OrphanRules.IsOrphan(exe, …)` → Finding with the usual Id/Area/Title/Location and change, `Rating.Entbehrlich`, `Preselected = true`, `Group = FindingGroups.OrphanEntries`, Reasons `[OrphanReason]`, no signature call. Autostart „Bereits deaktiviert“ still wins (no change, Info, not preselected, but Group still `OrphanEntries`). Tasks without program action (COM handler, `ActionExe` empty) are never orphaned. Unknown presence (UNC, unready drive, relative) → normal 0.1 handling.

- [ ] **Step 1: Write the failing tests** (`OrphanTests.cs`, existing fakes, temp dir for missing paths):
  - `Autostart_with_missing_exe_is_orphan_and_preselected`: HKCU Run `Foo` = `"\"<tmp>\\gone\\foo.exe\" --min"` → `Rating.Entbehrlich`, `Preselected`, `Group == OrphanEntries`, `Reasons == [OrphanReason]`, Change not null, `FakeSignatures.VerifyCalls` empty.
  - `Disabled_orphan_autostart_offers_no_change`: StartupApproved `03…` → Change null, `!Preselected`.
  - `Protected_orphan_stays_hidden`: Run `Dell Update` with missing exe → no finding.
  - `Unc_or_unready_drive_is_not_orphan`: Run value `\\\\nas\\x\\a.exe` and path on a root in `FakeFileOps.NotReady` → Group null (normal handling).
  - `Service_with_missing_image_is_orphan`: Automatic service, ImagePath `"<tmp>\\gone\\svc.exe" -k` → orphan finding with `ServiceStartModeChange`.
  - `Task_with_missing_exe_is_orphan_but_com_handler_task_is_not`: task A `ActionExe = <tmp>\gone\t.exe` → orphan; hidden task B `ActionExe = null` → `Reasons` contains `"keine Programmaktion"`, Group null.
  - `Missing_exe_under_windows_is_not_orphan`: Run value `%SystemRoot%\gone_ww.exe` → not orphan (rules protect it anyway → no finding).
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~OrphanTests"` → FAIL.
- [ ] **Step 3: Implement** `OrphanRules` and the branch in the three scanners; pass `fo` in `AppServices` and `HeuristicTests`.
- [ ] **Step 4: Run** filter + full suite (Autostart/Service/Task/Heuristic tests unchanged in outcome) → PASS.
- [ ] **Step 5: Commit** `feat(core): flag orphaned autostarts, tasks and services`.

---

### Task 6: Orphaned folder scanner

**Files:**
- Create: `src/WinWaechter.Core/Optimizer/Storage/InstalledProgramIndex.cs`, `Optimizer/Storage/LeftoverFolderScanner.cs`
- Test: `tests/WinWaechter.Tests/LeftoverTests.cs`

**Interfaces:**
- Consumes: `IRegistry.SubKeyNames`, `IFileOps.EnumerateTopLevelDirs/InspectFolder`, `IProcessList`, `CleanerPaths` (Task 1); `RuleSet.IsLeftoverIgnored`, `Classify` (Task 2); `ForFolder`, `FindingGroups.OrphanFolders` (Task 3).
- Produces:
```csharp
public sealed class InstalledProgramIndex
{
    public static InstalledProgramIndex Load(IRegistry reg);   // Uninstall: HKLM SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall,
                                                              // HKLM SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall, HKCU same;
                                                              // fields DisplayName, Publisher, InstallLocation, DisplayIcon, UninstallString;
                                                              // Appx: HKCU Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages subkey names
    public int UninstallEntryCount { get; }
    public bool Matches(string folderName);
    internal static string Normalize(string s);   // lower-invariant, every run of non-letter/digit → one space, trimmed
}
public sealed class LeftoverFolderScanner
{
    public static readonly TimeSpan MinQuiet = TimeSpan.FromDays(30);
    public const string GroupHint = "Vor dem Verschieben prüfen – manche Programme hinterlassen absichtlich Daten (z. B. Spielstände, Lizenzen).";
    public LeftoverFolderScanner(IFileOps f, IRegistry reg, RuleSet rules, IProcessList procs, IReadOnlyList<string> roots);
    public static IReadOnlyList<string> DefaultRoots(CleanerPaths p);   // [ProgramFiles, ProgramFilesX86, LocalAppData\Programs], distinct
    public IReadOnlyList<Finding> Scan();
}
```
`Matches`: `n = Normalize(folderName)`; `n.Length < 3` → true; true when any normalized field contains `n` at index 0 or after a space. Orphan when all hold: `!IsLeftoverIgnored(name)`; `Classify(new ItemFacts(name, dir + "\\", null, null)) != Protected`; `!index.Matches(name)`; `InspectFolder(dir)` not null, `!HasReparsePoints`, `HasBinaries`, `LatestWriteUtc < now − 30 d`; no `RunningProcess.ExePath` under `dir`. Top-level dirs that are reparse points are skipped. `UninstallEntryCount == 0` → `Scan()` returns empty. Finding: Id `leftover|<dir>`, Area `Speicher`, Group `OrphanFolders`, Title folder name, Location dir, Reasons `["Kein installiertes Programm gefunden", $"Zuletzt geändert am {dd.MM.yyyy}"]`, `Rating.Info`, not preselected, Change `ForFolder`, `SizeBytes = facts.Bytes`.

- [ ] **Step 1: Write the failing tests** (temp root `pf`, `FakeRegistry` seeded with one Uninstall entry `{Bar}` DisplayName `"Bar Studio 2.1"` Publisher `"Baz Ltd"`, `FakeFileOps`, `FakeProcessList`, `RuleSet.Default`; helper sets all timestamps −40 d):
  - `Folder_without_program_and_with_binaries_is_offered`: `pf\Foo\foo.dll` → one finding, `!Preselected`, `Rating.Info`, `Group == OrphanFolders`, Change Kind `folder`, reason `"Kein installiertes Programm gefunden"`.
  - `Matching_display_name_publisher_or_install_location_is_kept`: `pf\Bar Studio`, `pf\Baz`, `pf\Qux` with InstallLocation `C:\Apps\Qux\` → none offered.
  - `Word_start_match_only`: DisplayName `"Superfoo"` does not protect folder `foo` (no word start) → offered; DisplayName `"Foo-Tools"` protects `foo`.
  - `Appx_package_name_protects_folder`: seed key `…\Repository\Packages\Contoso.Widget_1.0.0.0_x64__abc` → folder `Contoso Widget` kept.
  - `Ignored_protected_recent_running_or_without_binaries_are_skipped`: `Common Files\x.dll`; `Dell\x.dll`; `Fresh\x.dll` written −5 d; `Running\r.exe` with process ExePath inside; `DataOnly\save.dat` → none offered.
  - `Folder_with_inner_junction_or_access_denied_is_skipped`: `FakeFileOps.Junctions` contains `pf\J\inner`; `InspectFolder` null for `pf\Denied` (fake: `DeniedDirs` set) → none offered.
  - `No_uninstall_entries_means_no_leftover_findings`: empty registry, `pf\Foo\foo.dll` → empty.
  - `Default_roots_are_program_files_and_local_programs_only`: `DefaultRoots(paths)` equals `[ProgramFiles, ProgramFilesX86, LocalAppData\Programs]`; contains neither `ProgramData` nor `RoamingAppData` nor `LocalAppData` itself.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~LeftoverTests"` → FAIL.
- [ ] **Step 3: Implement** both classes.
- [ ] **Step 4: Run** filter + full suite → PASS.
- [ ] **Step 5: Commit** `feat(core): orphaned program folder scanner`.

---

### Task 7: App – Optimize groups, „Wird frei“, purge dialog, wiring

**Files:**
- Modify: `src/WinWaechter.App/ViewModels/OptimizeViewModel.cs`, `ViewModels/FindingItemViewModel.cs`, `ViewModels/OverviewViewModel.cs`, `Views/OverviewView.xaml`, `Views/OptimizeView.xaml`, `ViewModels/HistoryViewModel.cs`, `Services/AppServices.cs`
- Test: `tests/WinWaechter.Tests/ViewModelTests.cs`, `ViewSmokeTests.cs`

**Interfaces:**
- Consumes: `Finding.Group`, `FindingGroups`, `AblagePreview.ProgramFolders` (Task 3); `CacheScanner`, `InstallerScanner` (Task 4); `LeftoverFolderScanner` (Task 6); `WinProcessList`, `CleanerPaths.For`, `DownloadsFolder.Resolve` (Task 1).
- Produces:
```csharp
// FindingGroupViewModel(string title, IEnumerable<FindingItemViewModel> items, string? hint = null)
public string? Hint { get; }            // LeftoverFolderScanner.GroupHint for "Verwaiste Ordner"
public string? SizeText { get; }        // Labels.Size(sum SizeBytes) or null when no item has a size; updates when items are removed
// OptimizeViewModel
public long SelectedBytes { get; }      // sum SizeBytes of selected items with a change; raised with SelectedCount
// OverviewViewModel
public string FreeText { get; }         // "–" before a scan, else Labels.Size(Optimize.SelectedBytes)
public const string FreeNote = "Caches werden nach 1 Tag endgültig entfernt, oder sofort über Verlauf → Ablage jetzt leeren.";
// HistoryViewModel
internal static string? ProgramFoldersText(int n);  // n<=0 null; 1 → "Darin enthalten: 1 Programmordner aus „Verwaiste Ordner“."; n → "Darin enthalten: {n} Programmordner aus „Verwaiste Ordner“."
```
Group order: `Autostart`, `Dienste`, `Aufgaben`, `Speicher` (findings with `Group == null`, by Area as today), then `Browser-Cache`, `Windows-Reste`, `Alte Installer`, `Verwaiste Einträge`, `Verwaiste Ordner` (by `Group`). Group headers in `OptimizeView.xaml` show `SizeText` right-aligned (Readout style, small) and `Hint` below the title (`Text.Small`, `InkMuted`) when set. Overview: new readout „Wird frei“ in the Messleiste (same pattern as „Offene Vorschläge“: `Rectangle` divider + `StackPanel`) with `FreeNote` as `Text.Small` under the bar, visible only after a scan. Purge confirm message appends `ProgramFoldersText(preview.ProgramFolders)` (keep `KeptText`). Wiring in `AppServices`: scanners list `[autostart, services, tasks, temp, cache, installers, leftovers]`.

- [ ] **Step 1: Write the failing tests** (`ViewModelTests.cs`, existing `F(...)`/`Engine` helpers extended with `group`/`size` params):
  - `Groups_follow_fixed_order_and_sum_sizes`: findings Autostart, `Group=OrphanFolders` (size 10), `Group=BrowserCache` (sizes 3 and 4), Speicher without group → `Groups.Select(g => g.Title)` == `["Autostart","Speicher","Browser-Cache","Verwaiste Ordner"]`; Browser-Cache `SizeText == Labels.Size(7)`; Verwaiste Ordner `Hint == LeftoverFolderScanner.GroupHint`; Autostart `SizeText == null`.
  - `Orphan_entries_group_collects_entries_from_all_areas`: Autostart + Dienst + Aufgabe findings with `Group=OrphanEntries` → single group „Verwaiste Einträge“ with 3 items; no „Autostart“ group.
  - `SelectedBytes_counts_selected_items_with_change`: preselected 5 B, not preselected 7 B, preselected without change 9 B → `SelectedBytes == 5`; select the 7 B item → 12.
  - `Overview_free_text_dash_before_scan_then_size`.
  - `ProgramFoldersText_wording`: 0 → null, 1 and 3 → exact strings above.
  - `Purge_dialog_names_program_folders`: store with one Applied `folder` record + Ablage content, `PurgeAblageCommand` with recording dialog → confirm message contains `"Darin enthalten: 1 Programmordner"`.
  - `ViewSmokeTests.All_pages_render_in_light_and_dark` still passes with grouped findings (add one `Group=OrphanFolders` finding to its list).
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~ViewModelTests|FullyQualifiedName~ViewSmokeTests"` → FAIL.
- [ ] **Step 3: Implement** VMs, XAML and wiring per above (DynamicResource colours only, tokens from `Tokens.xaml`).
- [ ] **Step 4: Run** filter + full suite → PASS; `$DOTNET build WinWaechter.sln -c Debug` → 0 errors.
- [ ] **Step 5: Commit** `feat(app): cleaner groups with sizes, "Wird frei" readout, folder count in purge dialog`.

---

### Task 8: App – Datenschutz page, version 0.2.0

**Files:**
- Create: `src/WinWaechter.App/ViewModels/PrivacyViewModel.cs`, `Views/PrivacyView.xaml`, `Views/PrivacyView.xaml.cs`
- Modify: `ViewModels/ShellViewModel.cs`, `Views/ShellWindow.xaml`, `Services/AppServices.cs`, `Directory.Build.props`, `CHANGELOG.md`, `tests/WinWaechter.Tests/ViewSmokeTests.cs`
- Test: `tests/WinWaechter.Tests/PrivacyViewModelTests.cs`

**Interfaces:**
- Consumes: `PrivacyScanner`, `PrivacyRow`, `PrivacyState`, `RegValueChange`, `RecallProbe` (Task 2); `OptimizerEngine.ApplyAsync(title, findings, p, ct)`; `ChangeGate`; `IDialogService.Confirm/Inform`.
- Produces:
```csharp
public sealed partial class PrivacyRowViewModel : ObservableObject
{
    public PrivacyRow Row { get; }
    public string Title { get; }  public string Explanation { get; }  public bool IsRecommended { get; }
    public string StateText { get; }   // On "An", Partial "Teilweise", Off "Aus", AlreadyMinimal "Aus (bereits minimal)"
    public Tone StateTone { get; }     // On Watch, Partial Watch, Off/AlreadyMinimal Good
    public bool CanSelect { get; }     // Row.Changes.Count > 0
    [ObservableProperty] bool _isSelected;   // never true when !CanSelect; starts false
}
public sealed partial class PrivacyViewModel : ObservableObject
{
    public PrivacyViewModel(Func<CancellationToken, Task<IReadOnlyList<PrivacyRow>>> scan, OptimizerEngine engine,
        IDialogService dialogs, bool canChange, ChangeGate gate);
    public ObservableCollection<PrivacyRowViewModel> Rows { get; }
    public int SelectedCount { get; }  public string ApplyText { get; }   // $"Ausgewählte anwenden ({SelectedCount})"
    public const string RestartNote = "Manche Änderungen wirken erst nach Ab-/Anmelden oder Neustart.";
    public IAsyncRelayCommand RefreshCommand { get; }
    public IRelayCommand SelectRecommendedCommand { get; }   // selects IsRecommended && CanSelect rows only
    public IAsyncRelayCommand ApplyCommand { get; }          // CanExecute: canChange && !busy && !gate.IsBusy && SelectedCount > 0
    public event EventHandler? ChangesApplied;
}
// ShellViewModel(OptimizeViewModel, ProtectViewModel, PrivacyViewModel, HistoryViewModel, SettingsViewModel, UpdateViewModel, bool)
// PrimaryNav: Übersicht, Optimieren, Schutz, Datenschutz (glyph "\uE72E"), Verlauf; privacy.ChangesApplied → history.ShowNewestAsync()
```
Apply: confirm dialog („Datenschutz-Einstellungen ändern“, button „Anwenden“) lists every `RegValueChange.Description` of the selected rows; then `engine.ApplyAsync("Datenschutz", changes.Select(c => new Finding($"privacy|{c.Target}", FindingArea.Einstellung, c.Description, c.Target, [], Rating.Info, true, c)).ToList(), …)` inside `gate.RunAsync`; summary via `Inform` as in `OptimizeViewModel`; then `ChangesApplied` and `RefreshCommand`. Refresh is triggered the first time the page is shown (`ShellViewModel.OnSelected` calls `privacy.RefreshCommand` when `Rows.Count == 0` and not running). `AppServices`: `scan = async ct => { var recall = await RecallProbe.IsPresentAsync(new ProcessRunner(), ct); return new PrivacyScanner(reg, rules).Scan(r => r == "recall" && recall); }` (run on `Task.Run`). View: page title „Datenschutz“, intro line, `RestartNote`, buttons „Empfohlene auswählen“ / `ApplyText`, list rows (checkbox „ausschalten“, Title, Explanation, state chip, „empfohlen“ chip) following `OptimizeView` styles. Admin notice like Optimize when `!canChange`.

Version: `Directory.Build.props` `<Version>0.2.0</Version>`; `CHANGELOG.md` new top section:
```
## 0.2.0
- Aufräumen: Browser-Cache (Edge, Chrome, Opera, Firefox), Windows-Update-Downloads, Absturzabbilder und Fehlerberichte; Caches werden nach 1 Tag aus der Ablage entfernt.
- Alte Installer im Download-Ordner werden angezeigt (nie vorausgewählt).
- Deinstallationsreste: verwaiste Autostarts, Aufgaben und Dienste sowie verwaiste Programmordner (ganzer Ordner in die Ablage, rückgängig machbar).
- Neue Seite „Datenschutz“: einzelne Windows-Einstellungen ausschalten, jede im Verlauf rückgängig zu machen.
- Übersicht zeigt „Wird frei“.
```

- [ ] **Step 1: Write the failing tests** (`PrivacyViewModelTests.cs`; rows from `PrivacyScanner` over a `FakeRegistry`, real `OptimizerEngine` with `SessionStore(_root)` and a `ChangeFactory.CreateDefault(...)`, recording dialogs, `ChangeGate`):
  - `Nothing_preselected_after_refresh`: `SelectedCount == 0`, all `IsSelected == false`.
  - `Select_recommended_skips_not_recommended_and_off_rows`: `tipps` already Off → after command `programmstarts` and `tipps` unselected, every other selectable row selected; `ApplyText == $"Ausgewählte anwenden ({n})"`.
  - `Apply_writes_one_change_per_value_and_journals_them`: select `vorschlaege` only → confirm list has 4 entries; after apply `FakeRegistry.SetCalls.Count == 4`; newest session `Title == "Datenschutz"` with 4 Applied `regvalue` records; row state after refresh `"Aus"`; `ChangesApplied` raised.
  - `Undo_session_restores_privacy_values`: after the apply above, `engine.UndoSession(session)` → `Undone == 4`, values back to seeded state.
  - `Already_minimal_row_cannot_be_selected`: effective `AllowTelemetry` = 0 → row `StateText == "Aus (bereits minimal)"`, `CanSelect == false`, setting `IsSelected = true` stays false.
  - `Apply_disabled_without_admin`: `canChange: false` → `ApplyCommand.CanExecute(null) == false` even with a selection.
  - `Shell_nav_has_datenschutz_between_schutz_and_verlauf`: titles `["Übersicht","Optimieren","Schutz","Datenschutz","Verlauf"]`.
  - `ViewSmokeTests.All_pages_render_in_light_and_dark` renders `PrivacyView` too (update its `ShellViewModel` construction).
  - `ScaffoldTests.Core_assembly_version_matches_props` passes with 0.2.0 (no edit needed, just run).
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~PrivacyViewModelTests|FullyQualifiedName~ViewSmokeTests"` → FAIL.
- [ ] **Step 3: Implement** VM, view, shell/nav, wiring, version, CHANGELOG.
- [ ] **Step 4: Run** full suite → PASS; `$DOTNET build WinWaechter.sln -c Release` → 0 errors.
- [ ] **Step 5: Commit** `feat(app): Datenschutz page; release 0.2.0 version and changelog`.

---

### Task 9: Installer build, on-machine test, release (controller only – needs the user's chat approval; not for subagents)

**Files:** none changed unless a defect is found (then fix in a new task with its own test).

- [ ] **Step 1:** Ask the user for approval, then build locally without publishing: `powershell -File build-release.ps1 -Version 0.2.0` → tests green, `dist\WinWaechter-Setup-0.2.0.exe`, `SHA256SUMS`, `SHA256SUMS.sig`, self-check OK.
- [ ] **Step 2:** With approval, on the user's machine (installed 0.1.1): update to 0.2.0 via the app's update path after the release exists, or install the setup directly if the user prefers; then check: Optimieren shows the new groups with sizes; Browser-Cache with Edge open shows the Edge hint; apply one cache group + undo from Verlauf; create a test folder `C:\Program Files\WW-Testrest\dummy.dll` (timestamps −40 d) → appears under „Verwaiste Ordner“, move + undo restores it, then delete the test folder by hand; Datenschutz: two settings (e.g. `werbe-id`, `feedback`) apply → state „Aus“, undo → previous state; Übersicht „Wird frei“ visible; Ablage purge dialog names folder count. Screenshots light/dark.
- [ ] **Step 3:** Only after explicit approval: `powershell -File build-release.ps1 -Version 0.2.0 -Publish`; verify the update 0.1.1 → 0.2.0 inside the app; merge/push decisions stay with the user.

---

## Self-Review

- **Spec coverage:** §4 targets → Task 4 (incl. §9 Opera/Dawn/LiveKernelReports/MEMORY.DMP, §9.5 WU checks, §9.7 Edge text); installers → Task 4; §5.1 → Task 5 (incl. COM handler, PathNormalizer, UNC/unready = unknown); §5.2 + §9.3/9.4 → Tasks 1, 3, 6; §6 + §9.6 + Datenschutz corrections → Tasks 2, 8; §7 groups/nav/tile → Tasks 7, 8; §9.1 retention + dialog count → Tasks 3, 7; §9.2 tile text → Task 7; §8 version/CHANGELOG/release/manual test → Tasks 8, 9. Spec §6 state „Nicht vorhanden“ is replaced by hiding the row (§9.6 Recall) – no other row can be absent.
- **Types:** `Finding.Group`, `FindingGroups.*`, `ForCache/ForCacheFile/ForFolder/Measure`, `AblagePreview.ProgramFolders`, `ChangeFactory.CreateDefault(…, RuleSet)`, `ServiceScanner/TaskScanner(…, IFileOps)`, `PrivacyRow/PrivacyState`, `ShellViewModel(…, PrivacyViewModel, …)` are used with the same names in all consuming tasks.
- **Review Focus:** each of the five lines has its named test in Tasks 4, 6, 2, 3, 3.
