# WinWächter 0.5.0 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** WinWächter fixes what it finds („Macher statt Beobachter“): Security and Performance fix things themselves, five new tools (Werbe-Apps, Windows-Werbung, Computerschutz, Browser-Hintergrund, Rechtsklick-Menü), small gaps closed, and one button „Rechner in Ordnung bringen“ on the overview that collects and applies everything – released as 0.5.0 after owner approval.

**Architecture:** Same pattern as 0.1–0.4. Every reversible fix is an `IChange` (Plan/Apply/Undo/CheckDrift/FromRecord against a fixed whitelist) applied via `OptimizerEngine.ApplyAsync` and registered in `ChangeFactory`; six new kinds `secvalue`, `fwrule`, `powerplan`, `appx`, `sysprotect`, `shellext`. One-off handlings without undo are *Aktionen* (`SystemAction`) run by an App-side `ActionRunner` (confirm, `FileLog`, error message). A Core `ImprovementCollector` calls the existing area scanners and maps their recommended findings to one list for 4.1; area pages keep their own scanners. All system access behind interfaces with fakes.

**Tech Stack:** C# 13 / .NET 10 (`net10.0-windows`), WPF, CommunityToolkit.Mvvm, xUnit; Windows-own interfaces only: Registry, WMI (`System.Management`), COM (`HNetCfg.FwPolicy2`, `Microsoft.Update.Session`), powrprof, Windows PowerShell Appx/Defender cmdlets.

**Spec:** `docs/superpowers/specs/2026-11-12-winwaechter-0.5-design.md` (entries marked **[Entscheidung Claude]** are binding). Style reference: `docs/superpowers/plans/2026-11-10-winwaechter-0.4.md`. Design notes: `docs/design-notes.md`.

## Global Constraints

- Branch `feature/v0.5` (checked out). Do not switch branches, do not push, no release/publish (spec §7: „Release erst nach Freigabe“).
- `$DOTNET = 'C:\Users\tahsi\AppData\Local\Microsoft\dotnet\dotnet.exe'`, `$SLN = 'C:\Users\tahsi\dev\winwaechter\WinWaechter.sln'`, env `DOTNET_CLI_TELEMETRY_OPTOUT=1`, `DOTNET_NOLOGO=1` (PowerShell). One class: `& $DOTNET test $SLN --filter "FullyQualifiedName~<Class>"`; full suite: `& $DOTNET test $SLN` (783 tests at start, green after every task). Close a running `WinWaechter.App.exe` from `bin\Debug` before building.
- Commits: `git -C C:/Users/tahsi/dev/winwaechter commit -m "<subject>" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"` – every commit message ends with that line.
- No third-party packages, no new packages at all. The TFM stays `net10.0-windows`: 4.4 uses the built-in Windows PowerShell Appx cmdlets (the alternative the spec allows), so no versioned Windows-SDK TFM is needed. Nothing sends data anywhere.
- Dell, Lexware and everything on `rules.json` → `protected` (plus WinWächter itself) is never touched, listed as removable, ended, blocked or disabled – in every new scanner, including 4.4, 4.7, 4.8, „Beenden“. Only exception (spec 4.4): for packages named in `bloatApps` the protected entries `{"publisher":"Microsoft"}` and the two `…\WindowsApps\` paths do not apply; all other protected entries and the code Nie-Liste stay absolute.
- Nothing changes without an explicit user click with a confirmation that says what happens (harmless Aktionen – Definitionen aktualisieren, Explorer neu starten – need only the click). Nothing is preselected except what spec 4.1 names.
- Every change is journalled and undoable via the Verlauf, except what is labelled: Aktionen say „endgültig“ (`ActionRunner.FinalNote`); `sysprotect` says „Nur teilweise rückgängig“, `appx` without image copy says „Nur über den Store wiederherstellbar“ – in the confirmation and in the Verlauf (`IUndoLimit`).
- Targets come only from fixed lists (code whitelists or local `rules.json` sections). `FromRecord` rejects everything not on its list; target values come from the list, never from the journal. `privacy` and the new `bloatApps` are local-only: `RuleSet.UnionWith` never takes them from downloaded rules (the network may only add protection).
- Policy rule (spec §3): only values under `HKLM\SOFTWARE\Policies\…` / `HKCU\SOFTWARE\Policies\…` count as policy (`…\CurrentVersion\Policies\System` does not). A policy value is „von WinWächter“ when the newest journal record for exactly that value is Applied; otherwise it is foreign → no fix offered, text „Von einer Richtlinie festgelegt“. Cleaning foreign policies is not a goal.
- Without admin rights: everything visible, every fix/Aktion locked with the existing `AppEnvironment.AdminNotice`.
- UI copy German, Sie-Form, sachlich, one „warum“ sentence per row (model: Datenschutz page), no percentage promises. Anti-goals (spec §2): no registry cleaner, RAM booster, „Internet-Booster“, driver databases, mass service changes, process priorities; never break Xbox/Game-Pass sign-in or Game Bar.
- Fixed external programs only: `%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe` (Defender/Appx cmdlets with validated arguments) and `%SystemRoot%\System32\shutdown.exe` with exactly `/r /t 0`; no `reg.exe`.
- Shell URIs only via `SystemLinks.IsAllowed` or the new `SystemLinks.IsStoreLink` (validated `ms-windows-store://pdp/?PFN=<family>`); no web addresses.
- Tests use fakes and temp dirs; no test writes the real registry, firewall, services, packages, power plans or runs PowerShell/shutdown. Read-only smoke reads of real sources are allowed and must tolerate failure.
- Design per `docs/design-notes.md` and the existing pages (tokens, `DynamicResource`, `SurfaceBox`/`RowDivider` rows, no cards/shadows/gradients). Do not restyle.
- Device steps in tasks are **read-only**. Anything that changes the owner's system is in the final checklist (Task 19) and only with the owner's consent.

## Review Focus

1. **A policy value WinWächter wrote earlier was undone, or a foreign tool writes the same value later** → ownership follows the newest record for exactly that value; an Undone or missing record means foreign, so no fix is offered. Test `Newest_record_decides_ownership` (Task 1).
2. **4.1: `sysprotect` or one change in the middle fails** → the session still applies the rest; the summary names each skipped item with its error; the Computerschutz step failing does not stop the main session. Test `Failure_in_first_step_or_middle_continues_and_is_reported` (Task 18).
3. **Package removed by the user (or the Store) between scan and apply, or the app is running** → `appx` Apply fails with a clear German message, record Failed, nothing pretends to be undoable. Test `Package_gone_before_apply_fails_cleanly` (Task 11).
4. **„Beenden“ on a name shared by several processes** (e.g. `RuntimeBroker`, a copy under `C:\Windows`, a service host, WinWächter itself) → only processes passing `ProcessGuard` are closed/killed. Test `Same_name_in_windows_folder_or_service_is_never_ended` (Task 9).
5. **Tampered journal or hostile names reaching PowerShell/COM** (package full name with `'`/`;`, firewall rule without the `WinWächter: ` prefix, shellext key outside the six locations, `..` in a verb) → rejected before any system call. Tests `Package_names_with_metacharacters_are_rejected` (Task 11), `FromRecord_rejects_unlisted` (Tasks 3, 4, 6, 8, 15).

---

## File Structure

Core (`src/WinWaechter.Core/`):
- `Sys/RegDword.cs` (modify: REG_SZ write/drift), `Sys/PolicyValues.cs` (new: policy path rule, `IPolicyOwnership`, `JournalPolicyOwnership`), `Sys/SystemLinks.cs` (store link), `Sys/DiskSpace.cs` (new).
- `Changes/IChange.cs` (retry data, `IUndoLimit`, `ChangeText`), `Model/ChangeRecord.cs` (retry fields), `Model/Finding.cs` (`BlockingProcess`, `FindingGroups.ContextMenu`), `Optimizer/OptimizerEngine.cs` (continue on error, retry, appx undoability).
- `Actions/SystemAction.cs` (new: `SystemAction`, `ActionOutcome`), `Actions/ProcessEnding.cs` (new: `IProcessEnder`, `ProcessGuard`, `WinProcessEnder`), `Actions/SystemActions.cs` (new: restart, Explorer restart).
- `Security/Checks/SecurityValues.cs` + `SecValueChange.cs` (`secvalue`), `FirewallRules.cs` + `FirewallRuleChange.cs` (`fwrule`), `WindowsUpdateInstall.cs`, `SecurityActionSet.cs`, `SystemProtection.cs` (`sysprotect`); modify `SecurityChecks.cs`, `SecurityCheck.cs`, `SecurityItem.cs`, `Sources.cs`, `Defender/IDefender.cs`, `Defender/DefenderClient.cs`.
- `Performance/PerfValues.cs`, `PerformanceScanner.cs`, `PowerPlans.cs` (new, `powerplan`), `StartupMatcher.cs` (new), `BootReport.cs`, `PerformanceRegistration.cs`, `Sys/Native/PowerNative.cs`.
- `Programs/BloatApps.cs` (new: Nie-Liste, `IAppPackages`, `WinAppPackages`, `AppxChange`, `BloatAppScanner`), `Rules/RuleSet.cs`, `Rules/PrivacyDefinitions.cs`, `rules/rules.json`.
- `Optimizer/ContextMenu/ShellExtScanner.cs` + `ShellExtChange.cs` (new, `shellext`), `Optimizer/Storage/CacheScanner.cs`.
- `Improvements/ImprovementCollector.cs` (new: `Improvement`, `ActionSuggestion`, `ImprovementScan`, mappers).

App (`src/WinWaechter.App/`): `Services/ActionRunner.cs` (new), `ViewModels/ImproveViewModel.cs` (new), modify `OverviewViewModel`, `SecurityViewModel`, `PerformanceViewModel`, `PrivacyViewModel`, `ProgramsViewModel`, `OptimizeViewModel`, `FindingItemViewModel`, `HistoryViewModel`, `ProtectViewModel`, `ShellViewModel`, the matching `Views/*.xaml`, `Services/AppServices.cs`, `Services/WpfDialogService.cs` (`ExplorerShell.OpenLink`).

Tests (`tests/WinWaechter.Tests/`): `BuildingBlocksTests.cs`, `ActionRunnerTests.cs`, `SecValueTests.cs`, `FirewallRuleTests.cs`, `SecurityActionTests.cs`, `SystemProtectionTests.cs`, `PowerPlanTests.cs`, `StartupMatcherTests.cs`, `ProcessEndingTests.cs`, `BloatAppTests.cs`, `BrowserBackgroundTests.cs`, `ShellExtTests.cs`, `ImprovementCollectorTests.cs`, `ImproveViewModelTests.cs`; fakes `Fakes/FakeFix.cs` (FakePolicyOwnership, FakeFirewallRules, FakeUpdateInstaller, FakeRestarter, FakeRestoreConfig, FakePowerPlans, FakeProcessEnder, FakeAppPackages, FakeDiskSpace) and `Fakes/RecordingDialogs.cs` (records Confirm/Ask/Inform calls with title, message, items, final; configurable answers); extend existing VM test classes and `ViewSmokeTests.cs`.

---

### Task 1: Shared building blocks (REG_SZ, policy ownership, undo limits, engine options)

**Files:**
- Create: `src/WinWaechter.Core/Sys/PolicyValues.cs`, `tests/WinWaechter.Tests/BuildingBlocksTests.cs`, `tests/WinWaechter.Tests/Fakes/FakeFix.cs` (FakePolicyOwnership only), `tests/WinWaechter.Tests/Fakes/RecordingDialogs.cs`
- Modify: `src/WinWaechter.Core/Sys/RegDword.cs`, `src/WinWaechter.Core/Changes/IChange.cs`, `src/WinWaechter.Core/Model/ChangeRecord.cs`, `src/WinWaechter.Core/Optimizer/OptimizerEngine.cs`; retry data in `Performance/PerfValueChange.cs`, `Privacy/RegValueChange.cs`, `Optimizer/Autostart/StartupApprovedChange.cs`, `Optimizer/Services/ServiceStartModeChange.cs`, `Optimizer/Tasks/TaskDisableChange.cs`, `Security/Checks/FirewallChange.cs`

**Interfaces:**
- Produces:
  - `RegDword.WriteString(IRegistry, Hive, string key, string name, string value)` (creates key, writes `RegistryValueKind.String`); `RegDword.DriftString(IRegistry, Hive, string key, string name, string target)` (Unchanged only for REG_SZ equal ordinal-ignore-case). `Snapshot`/`Restore` unchanged (already handle REG_SZ; a value missing before is deleted on undo).
  - `static class PolicyValues { const string ForeignText = "Von einer Richtlinie festgelegt"; static bool IsPolicyKey(string key); static PolicyState State(IRegistry reg, IPolicyOwnership own, Hive h, string key, string name); }`, `enum PolicyState { None, Ours, Foreign }`. `IsPolicyKey`: key (trimmed `\`) starts with `SOFTWARE\Policies\` (ignore case); `…\CurrentVersion\Policies\System` is false. `State`: value missing → None; present and `own.IsOurs` → Ours; else Foreign. Non-policy key → always None.
  - `interface IPolicyOwnership { bool IsOurs(Hive h, string key, string name); }`; `sealed class JournalPolicyOwnership(SessionStore store) : IPolicyOwnership` – reads `store.List()` once (lazily, per instance); for each (hive,key,name) taken from `Before["hive"/"key"/"name"]` of records of kinds `perfvalue`, `secvalue`, `regvalue`, the newest record by `At` decides: Applied → ours. Callers take a `Func<IPolicyOwnership>` so each scan sees fresh journals.
  - In `IChange.cs`: `interface IUndoLimit { string? UndoLimit { get; } }`; `static class UndoLimits { const string Partial = "Nur teilweise rückgängig"; const string StoreOnly = "Nur über den Store wiederherstellbar"; }`; `static class ChangeText { static string ConfirmLine(IChange c) }` → `Description` plus ` (UndoLimit)` when the change is an `IUndoLimit` with non-null limit. Default interface member on `IChange`: `(Dictionary<string,string?> Before, Dictionary<string,string?> After)? RetryData() => null;` – the identity an Applied record would carry so the kind's own `FromRecord` can rebuild it (no old-state values).
  - `ChangeRecord(…, string? Error = null, Dictionary<string,string?>? RetryBefore = null, Dictionary<string,string?>? RetryAfter = null)` – old journals load unchanged (null).
  - `OptimizerEngine.ApplyAsync(string title, IReadOnlyList<Finding> selected, IProgress<string>? p, CancellationToken ct, bool continueOnError = false)` – with `continueOnError` a failed change is journalled Failed and the loop goes on (no `break`); default behaviour unchanged. On every failure the Failed record gets `RetryBefore/RetryAfter = change.RetryData()`.
  - `static bool OptimizerEngine.CanRetry(ChangeRecord r)` (Failed and `RetryBefore` not null); `Task<ApplyResult> OptimizerEngine.RetryAsync(ChangeRecord failed, IProgress<string>? p, CancellationToken ct)` – `InvalidOperationException("Für diesen Eintrag ist kein erneuter Versuch möglich.")` unless `CanRetry`; rebuilds via `_factory.Rehydrate(failed with { Before = RetryBefore, After = RetryAfter ?? new(), Status = Applied })` (so the whitelist check of `FromRecord` runs) and applies it in a new session titled `$"Erneut versuchen: {failed.Description}"`.
  - `RetryData` for existing kinds: perfvalue `{hive,key,name}` / `{value}`; regvalue `{hive,key,name}` / `{}`; autostart `{hive,approvedKey,valueName}` / `{}`; service, task `{}`/`{}` (they rebuild from `Target`); firewall `{profile, enabled="false"}` / `{enabled="true"}`. Ablage kinds, regkey, pua: null (no retry).

- [ ] **Step 1: Write failing tests** in `BuildingBlocksTests.cs`: `String_value_write_drift_and_undo` (missing → WriteString "Warn" → DriftString Unchanged; Restore with existed=false deletes; seeded "Off" restored as REG_SZ "Off"), `Policy_key_rule` (`SOFTWARE\Policies\Microsoft\Edge` true, `HKCU Software\Policies\…` true, `SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System` false), `Newest_record_decides_ownership` (temp `SessionStore`: Applied then Undone for the same value → not ours; Undone then Applied → ours; other name → not ours; State on a missing value → None), `Continue_on_error_applies_the_rest` (3 changes, 2nd throws: default → Applied 1/Failed 1, 3rd never applied; `continueOnError: true` → Applied 2/Failed 1), `Failed_record_keeps_retry_data_and_retry_rebuilds_through_whitelist` (perfvalue change failing via FakeRegistry throwing key → record has RetryBefore; `RetryAsync` after fixing the fake applies in a new session; record with RetryBefore key outside `PerfValues` → InvalidOperationException from FromRecord; Ablage record → `CanRetry` false), `Confirm_line_names_undo_limit`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~BuildingBlocksTests"` → FAIL (members missing).
- [ ] **Step 3: Implement** the members above; `RecordingDialogs` for later tasks (no test of its own).
- [ ] **Step 4: Run** `BuildingBlocksTests`, `OptimizerEngineTests`, `JournalTests`, `PrivacyTests`, `PerfValueTests` → PASS; full suite → PASS.
- [ ] **Step 5: Commit** `feat(core): REG_SZ values, policy ownership, undo limits, continue-on-error and retry in the engine`.

### Task 2: Aktion layer

**Files:**
- Create: `src/WinWaechter.Core/Actions/SystemAction.cs`, `src/WinWaechter.App/Services/ActionRunner.cs`, `tests/WinWaechter.Tests/ActionRunnerTests.cs`

**Interfaces:**
- Consumes: `ChangeGate.RunAsync(owner, Func<Task>)`, `IDialogService`, `FileLog`.
- Produces:
  - `sealed record ActionOutcome(bool Success, string Message, string? Link = null)` (Link: an allowed system link to offer afterwards).
  - `sealed record SystemAction(string Id, string Title, string ButtonText, string? ConfirmText, bool Final, Func<IProgress<string>?, CancellationToken, Task<ActionOutcome>> RunAsync, IReadOnlyList<string>? Items = null)` – `ConfirmText == null` means harmless (click only); ctor throws `ArgumentException` when `Final && ConfirmText is null`.
  - `sealed class ActionRunner(IDialogService dialogs, ChangeGate gate)`; `public const string FinalNote = "Diese Aktion ist endgültig und lässt sich nicht im Verlauf rückgängig machen.";` `Task<ActionOutcome?> RunAsync(object owner, SystemAction a, IProgress<string>? progress, CancellationToken ct = default)`: Confirm(`a.Title`, Final ? `$"{ConfirmText} {FinalNote}"` : ConfirmText, `a.ButtonText`, `a.Items`, final: `a.Final`) unless harmless; declined → null; runs inside `gate.RunAsync(owner, …)` – gate taken → `ActionOutcome(false, ChangeGate.BusyHint)`; logs `FileLog.Info($"Aktion: {Title}")` and the outcome message; exception → `FileLog.Error`, `dialogs.Inform($"{Title} fehlgeschlagen", ex.Message)`, returns `ActionOutcome(false, ex.Message)`. Callers must not call it from inside their own `gate.RunAsync` (the gate is not reentrant).

- [ ] **Step 1: Write failing tests**: `Harmless_action_runs_without_confirm`, `Final_action_confirm_says_endgueltig_and_final_flag` (RecordingDialogs: message ends with FinalNote, `final == true`, items passed), `Declined_runs_nothing`, `Busy_gate_returns_busy_hint`, `Exception_is_logged_and_reported` (Inform title „… fehlgeschlagen“), `Final_without_confirm_text_throws`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~ActionRunnerTests"` → FAIL.
- [ ] **Step 3: Implement** both files.
- [ ] **Step 4: Run** class + full suite → PASS.
- [ ] **Step 5: Commit** `feat(actions): shared confirm/log/error layer for one-off actions`.

### Task 3: `secvalue` – SmartScreen, UAC, Remotedesktop beheben (4.2)

**Files:**
- Create: `src/WinWaechter.Core/Security/Checks/SecurityValues.cs`, `src/WinWaechter.Core/Security/Checks/SecValueChange.cs`, `tests/WinWaechter.Tests/SecValueTests.cs`
- Modify: `src/WinWaechter.Core/Security/Checks/SecurityChecks.cs` (`SmartScreen`, `Uac`, `RemoteDesktop`), `src/WinWaechter.Core/Security/Checks/SecurityItem.cs`, `src/WinWaechter.Core/Security/Checks/SecurityCheck.cs`, `src/WinWaechter.Core/Security/Checks/Sources.cs`

**Interfaces:**
- Consumes: Task 1 (`RegDword` incl. string, `PolicyValues`, `IPolicyOwnership`, `RetryData`).
- Produces:
  - `sealed record SecValue(string Id, Hive Hive, string Key, string Name, RegistryValueKind Kind, object Target)` and `static class SecurityValues { SecValue SmartScreen; SecValue UacEnableLua; SecValue UacConsentAdmin; SecValue UacSecureDesktop; SecValue RdpDeny; static SecValue? Find(Hive, string key, string name); }`: SmartScreen HKLM `SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer` `SmartScreenEnabled` String `"Warn"`; UAC under HKLM `SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System`: `EnableLUA` 1, `ConsentPromptBehaviorAdmin` 5, `PromptOnSecureDesktop` 1 (DWord); RdpDeny HKLM `SYSTEM\CurrentControlSet\Control\Terminal Server` `fDenyTSConnections` 1.
  - `sealed class SecValueChange(IRegistry reg, SecValue value, string description) : IChange` – Kind `secvalue`, Target `"HKLM\\key\\name"`, Before = `RegDword.Snapshot`, After `{value, kind}`, Apply via `Write`/`WriteString`, Undo `Restore`, Drift `Drift`/`DriftString` against `value.Target`. `static FromRecord(ChangeRecord, IRegistry)` – hive/key/name must match `SecurityValues.Find`, target always from the list; else `InvalidOperationException("Dieser Wert gehört nicht zu den Sicherheits-Einstellungen – Rückgängig abgelehnt.")`. `static void Register(ChangeFactory f, IRegistry reg)`. `RetryData` `{hive,key,name}`.
  - `SecurityItem(…, SystemAction? Act = null, string? ChangeLabel = null, bool Recommended = false)`; `SecurityActionKind.Action` added (used from Task 5).
  - `SecuritySources(…, ExtensionPageOpener? ExtensionPages = null, Func<IPolicyOwnership>? Policies = null)`; later tasks append their own optional parameters in this order: Task 4 `IFirewallRules? FirewallRules = null, RuleSet? Rules = null, string? WindowsDir = null`; Task 5 `SecurityActionSet? Actions = null`; Task 6 `ISystemRestoreConfig? RestoreConfig = null, IServiceControl? Services = null`. Null keeps today's rows.
  - `SecurityChecks.Firewall`: red rows get `Recommended = true` (so 4.1 collects them).
  - `SecurityChecks.SmartScreen(IRegistry reg, IPolicyOwnership? policies = null)`: off and policy `HKLM\SOFTWARE\Policies\Microsoft\Windows\System\EnableSmartScreen` State != None → yellow, Finding „Ausgeschaltet – Von einer Richtlinie festgelegt“, no change, no link; off without policy → `Action Change`, ActionLabel „Einschalten“, `Change = new SecValueChange(reg, SecurityValues.SmartScreen, "SmartScreen einschalten (warnen)")`, Recommended true. Green rows keep the link.
  - `SecurityChecks.Uac(IRegistry)`: the first defect only gets its value: EnableLUA → „Benutzerkontensteuerung einschalten“ with explanation suffix „Wirkt erst nach einem Neustart.“; Consent → „Admins wieder fragen“; SecureDesktop → „Abfrage auf dem sicheren Desktop“; ActionLabel „Beheben“, Recommended true.
  - `SecurityChecks.RemoteDesktop(IRegistry)`: open (non-Home) → ActionLabel „Ausschalten“, change RdpDeny „Remotedesktop ausschalten“, Recommended true; Home unchanged (grey).

- [ ] **Step 1: Write failing tests** in `SecValueTests.cs`: `SmartScreen_apply_undo_and_drift_as_string` (seeded "Off" → "Warn" REG_SZ → undo "Off"; missing → undo deletes), `Uac_fix_touches_only_the_defect_value`, `Foreign_smartscreen_policy_gets_no_fix` (policy value 0 present, FakePolicyOwnership empty → Change null, Finding contains `PolicyValues.ForeignText`), `Rdp_open_gets_switch_off`, `FromRecord_rejects_unlisted` (other key; HKCU hive; After value "Off" for SmartScreen still yields target "Warn" from the list), `Registered_kind_rehydrates`. Extend `SecurityCoreTests` expectations where rows now carry a change.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~SecValueTests"` → FAIL.
- [ ] **Step 3: Implement**; in `SecurityCheck.RunAsync` pass `s.Policies?.Invoke()` to `SmartScreen`.
- [ ] **Step 4: Run** `SecValueTests`, `SecurityCoreTests`, `SecurityViewModelTests` + full suite → PASS.
- [ ] **Step 5: Commit** `feat(security): secvalue fixes for SmartScreen, UAC and Remote Desktop`.

### Task 4: `fwrule` – eingehende Verbindungen eines lauschenden Programms sperren (4.2)

**Files:**
- Create: `src/WinWaechter.Core/Security/Checks/FirewallRules.cs`, `src/WinWaechter.Core/Security/Checks/FirewallRuleChange.cs`, `tests/WinWaechter.Tests/FirewallRuleTests.cs`
- Modify: `src/WinWaechter.Core/Security/Checks/SecurityChecks.cs` (`Listeners`, `ListenerRow`), `src/WinWaechter.Core/Security/Checks/SecurityCheck.cs`, `tests/WinWaechter.Tests/Fakes/FakeFix.cs` (FakeFirewallRules)

**Interfaces:**
- Consumes: `ISignatureVerifier`, `RuleSet.Classify(ItemFacts)`, `SecuritySources.FirewallRules/Rules`.
- Produces:
  - `sealed record FirewallRuleInfo(string Name, string? Program, bool Inbound, bool Block, bool Enabled)`; `interface IFirewallRules { FirewallRuleInfo? Get(string name); void AddInboundBlock(string name, string program); void Remove(string name); }`; `FwPolicy2Rules` via `HNetCfg.FwPolicy2`.`Rules` and `HNetCfg.FWRule` (Direction 1 = in, Action 0 = block, Enabled true, Profiles 0x7FFFFFFF, ApplicationName = program, Grouping „WinWächter“), COM objects released like `FwPolicy2Firewall`.
  - `sealed class FirewallRuleChange : IChange` – `FirewallRuleChange(IFirewallRules rules, string program, string windowsDir)`; ctor throws `ArgumentException` when program is not rooted, is under `windowsDir`, or contains control chars. `const string Prefix = "WinWächter: "`; rule name `Prefix + Path.GetFileNameWithoutExtension(program)`. Kind `fwrule`, Target = rule name, Description „Eingehende Verbindungen für „{file}“ sperren“. Apply: a rule with that name exists → `InvalidOperationException("Eine Firewall-Regel „{name}“ gibt es schon.")`; else add; Before `{name, program}`, After `{present="true"}`. Undo removes the rule only if `Get(name)` still has the same Program (ignore case), else throws. Drift: rule missing → Gone; program differs → Changed. `static FromRecord(ChangeRecord, IFirewallRules, string windowsDir)` – name must equal `Prefix + file name of Before program`, else `InvalidOperationException("Diese Firewall-Regel stammt nicht von WinWächter – Rückgängig abgelehnt.")`. `static Register(ChangeFactory, IFirewallRules, string windowsDir)`. `RetryData` `{name, program}`.
  - `SecurityChecks.Listeners(ports, procs, sig, IFirewallRules? rules = null, RuleSet? ruleSet = null, string? windowsDir = null)`: each **yellow** child keeps `Action DefenderScan` and gets `Change = new FirewallRuleChange(...)`, `ChangeLabel = "Eingehende Verbindungen sperren"` unless: path under windowsDir, signature Valid with publisher starting „Microsoft“, `ruleSet.Classify(new ItemFacts(file, path, publisher, null)) == Protected`, or a rule with that name already exists (then explanation suffix „Eingehende Verbindungen sind von WinWächter gesperrt.“). Not Recommended (stays out of 4.1).

- [ ] **Step 1: Write failing tests**: `Apply_adds_block_rule_and_undo_removes_exactly_it`, `Existing_name_refuses_apply`, `Undo_refuses_rule_that_points_elsewhere`, `Drift_is_detected`, `FromRecord_rejects_unlisted` (name without prefix; prefix but other file; program under Windows), `Listener_rows_offer_block_only_when_allowed` (unsigned AppData exe → ChangeLabel set; Microsoft-signed → none; Dell path (protected) → none; Windows folder → none; green rows → none).
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~FirewallRuleTests"` → FAIL.
- [ ] **Step 3: Implement**; `SecurityCheck` passes the new sources.
- [ ] **Step 4: Run** class, `SecurityCoreTests` + full suite → PASS.
- [ ] **Step 5: Commit** `feat(security): block inbound connections of a listening program (fwrule)`.

### Task 5: Security-Aktionen – Echtzeitschutz, Definitionen, Updates, Neustart (4.2)

**Files:**
- Create: `src/WinWaechter.Core/Security/Checks/WindowsUpdateInstall.cs`, `src/WinWaechter.Core/Security/Checks/SecurityActionSet.cs`, `src/WinWaechter.Core/Actions/SystemActions.cs`, `tests/WinWaechter.Tests/SecurityActionTests.cs`
- Modify: `src/WinWaechter.Core/Security/Defender/IDefender.cs`, `src/WinWaechter.Core/Security/Defender/DefenderClient.cs`, `src/WinWaechter.Core/Security/Checks/SecurityChecks.cs` (`Defender`, `Updates`), `tests/WinWaechter.Tests/Fakes/FakeDefender.cs`, `tests/WinWaechter.Tests/Fakes/FakeFix.cs` (FakeUpdateInstaller, FakeRestarter)

**Interfaces:**
- Consumes: Task 2 `SystemAction`, `ActionOutcome`; `IProcessRunner`; `IDefender.UpdateSignaturesAsync`.
- Produces:
  - `IDefender.EnableRealTimeAsync()` (`Task`); `DefenderClient` runs `Set-MpPreference -DisableRealtimeMonitoring $false` through the existing `RunPs` (wrapper of `MSFT_MpPreference`).
  - `sealed record UpdateFacts(string Id, string Title, bool AutoSelectOnWebSites, bool BrowseOnly, bool EulaAccepted, IReadOnlyList<string> Categories, int Type)`; `sealed record UpdateInstallResult(int Installed, int Failed, int NeedsWindowsUpdate, bool RebootRequired)`; `interface IWindowsUpdateInstaller { Task<UpdateInstallResult> InstallAsync(IProgress<string>? p, CancellationToken ct); }`; `WuaUpdateInstaller` – MTA thread like `WuaUpdateSearch`, Online search with `const string Criteria = "IsInstalled=0 and IsHidden=0 and Type='Software' and BrowseOnly=0 and AutoSelectOnWebSites=1"`, then `static IReadOnlyList<UpdateFacts> Select(IReadOnlyList<UpdateFacts> all)`: keeps Type 1, AutoSelect, not BrowseOnly, no category „Upgrades“ (feature upgrades), `EulaAccepted` true – updates needing a licence acceptance are counted in `NeedsWindowsUpdate`, never accepted by WinWächter. Progress „Updates werden gesucht …“, „Updates werden geladen …“, „Updates werden installiert …“ (`UpdateDownloader`, `UpdateInstaller` COM); `RebootRequired` from the install result.
  - `interface IRestarter { void Restart(); }`; `WinRestarter(IProcessRunner runner, string systemRoot)` runs `<systemRoot>\System32\shutdown.exe` with exactly `/r /t 0`.
  - `sealed record SecurityActionSet(SystemAction EnableRealTime, SystemAction UpdateSignatures, SystemAction InstallUpdates, SystemAction Restart)`; `static SecurityActionSet Create(IDefender d, IWindowsUpdateInstaller u, IRestarter r)`:
    - EnableRealTime: Title „Echtzeitschutz einschalten“, Button „Einschalten“, ConfirmText „WinWächter schaltet den Echtzeitschutz von Defender ein. WinWächter schaltet Virenschutz nie wieder ab.“, Final true; run → `EnableRealTimeAsync`, re-read status; still off → `ActionOutcome(false, "Windows hat das Einschalten blockiert, vermutlich durch den Manipulationsschutz. Bitte schalten Sie den Echtzeitschutz in der Windows-Sicherheit ein.", SystemLinks.DefenderThreat)`.
    - UpdateSignatures: harmless, Title „Virendefinitionen aktualisieren“, Button „Jetzt aktualisieren“; exit ≠ 0 → failure „… (Code n)“.
    - InstallUpdates: Final, Button „Updates installieren“, ConfirmText „Updates lassen sich nicht über WinWächter zurücknehmen; einzelne können Sie in Windows unter Updateverlauf deinstallieren.“; message „n installiert“, plus „m nur in Windows Update installierbar“, plus „Ein Neustart ist nötig.“.
    - Restart: Final, Button „Jetzt neu starten“, ConfirmText „Speichern Sie offene Arbeit. Der Rechner startet sofort neu.“.
  - `SecurityChecks.Defender(DefenderStatus s, IReadOnlyList<SecurityProduct> av, SecurityActionSet? actions = null)`: RealTime off → `Action Action`, `Act = EnableRealTime`, label „Einschalten“; Tamper off → stays link, explanation „Den Manipulationsschutz lassen Programme absichtlich nicht einschalten – nur Sie selbst in der Windows-Sicherheit.“; signatures > 3 days → `Act = UpdateSignatures`.
  - `SecurityChecks.Updates(bool reboot, WindowsUpdateState? s, SecurityActionSet? actions = null)`: reboot → `Act = Restart`; pending > 0 → `Act = InstallUpdates`; others keep the link. `SecurityChecks.Encryption`: explanation adds „Einschalten nur in Windows: Den Wiederherstellungsschlüssel müssen Sie selbst sichern.“.

- [ ] **Step 1: Write failing tests**: `Only_automatic_updates_are_selected` (optional/BrowseOnly, Upgrades category, driver Type 2, EULA not accepted → excluded; NeedsWindowsUpdate counts the EULA one), `Realtime_still_off_after_enable_gives_message_and_link` (FakeDefender keeps RealTime false), `Realtime_enable_succeeds`, `Restart_uses_only_fixed_arguments` (FakeProcessRunner: exe under systemRoot, args exactly `/r /t 0`), `Rows_carry_actions` (Defender off/old signatures, updates pending/reboot), `Install_updates_confirm_text_is_exact`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~SecurityActionTests"` → FAIL.
- [ ] **Step 3: Implement**; `SecurityCheck` passes `s.Actions`.
- [ ] **Step 4: Run** class, `DefenderParsingTests`, `SecurityCoreTests` + full suite → PASS.
- [ ] **Step 5: Commit** `feat(security): actions for real-time protection, signatures, updates and restart`.

### Task 6: `sysprotect` – Computerschutz einschalten (4.6)

**Files:**
- Create: `src/WinWaechter.Core/Security/Checks/SystemProtection.cs`, `tests/WinWaechter.Tests/SystemProtectionTests.cs`
- Modify: `src/WinWaechter.Core/Security/Checks/SecurityItem.cs` (`SecurityIds.SystemProtection = "sysprotect"`, `SecurityTitles.SystemProtection = "Computerschutz"`), `src/WinWaechter.Core/Security/Checks/SecurityCheck.cs`, `tests/WinWaechter.Tests/Fakes/FakeFix.cs` (FakeRestoreConfig)

**Interfaces:**
- Consumes: `IServiceControl` (`Get`, `SetStartMode`), `PolicyValues`, Task 1 `IUndoLimit`.
- Produces:
  - `interface ISystemRestoreConfig { bool? IsEnabled(string drive); void Enable(string drive); }`; `WinSystemRestoreConfig(IRegistry reg)`: `IsEnabled` from HKLM `SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore` `RPSessionInterval` (1 → true, 0 → false, missing → null; adjust per Step 1); `Enable` = WMI `\\.\root\default` `SystemRestore.Enable(Drive)`, ReturnValue ≠ 0 → `InvalidOperationException("Der Computerschutz ließ sich nicht einschalten (Code n).")`. Disk-space limit untouched.
  - `static class SystemProtection { static readonly string[] Services = ["VSS", "swprv"]; const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore"; static SecurityItem Check(IServiceControl sc, ISystemRestoreConfig cfg, IRegistry reg, IPolicyOwnership own, string drive); }` – `DisableSR`/`DisableConfig` State ≠ None → yellow, „Von einer Richtlinie festgelegt“, no change; protection off or any listed service Disabled → red „Ausgeschaltet“ / „Ein nötiger Dienst ist deaktiviert“, explanation „Ohne Computerschutz gibt es keine Wiederherstellungspunkte, auch nicht vor Änderungen durch WinWächter.“, ActionLabel „Einschalten“, `Change = SystemProtectChange`, Recommended true; else green.
  - `sealed class SystemProtectChange(IServiceControl sc, ISystemRestoreConfig cfg, string drive, IReadOnlyList<string> disabledServices) : IChange, IUndoLimit` – Kind `sysprotect`, Target „Computerschutz {drive}“, Description „Computerschutz für {drive} einschalten“, `UndoLimit = UndoLimits.Partial`. Apply: listed services Disabled → `SetStartMode(Manual, false)`; `IsEnabled != true` → `Enable`. Before `{drive, services="VSS,swprv", protection}`, After `{mode="Manual", protection="true"}`. Undo sets only the listed services back to Disabled; never switches protection off. Drift: any listed service not Manual → Changed. `static FromRecord(ChangeRecord, IServiceControl, ISystemRestoreConfig)` – drive must match `^[A-Z]:\\$`, services ⊆ `SystemProtection.Services`. `static Register(...)`. `RetryData` `{drive, services}`.

- [ ] **Step 1: Read-only device check (owner's PC, build 26200, elevated PowerShell, changes nothing):**
  `Get-Service VSS, swprv | Select-Object Name, StartType, Status`;
  `Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore' | Select-Object RPSessionInterval, DisableSR`;
  `Get-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore' -ErrorAction SilentlyContinue`.
  Note which service is `Disabled` in the roadmap. If neither is Disabled, keep both candidates (code checks both). If `RPSessionInterval` does not reflect the state shown in „Computerschutz“ (Systemeigenschaften), `IsEnabled` returns null and the row says „Nicht prüfbar“ for the protection part (the service fix stays); note it in the roadmap. If a `DisableSR`/`DisableConfig` policy exists, the row shows the policy text – note it.
- [ ] **Step 2: Write failing tests**: `Disabled_service_is_set_to_manual_and_protection_enabled`, `Undo_restores_only_the_service`, `Undo_never_switches_protection_off` (FakeRestoreConfig records no disable), `Policy_blocks_fix`, `Change_is_marked_partial` (`ChangeText.ConfirmLine` ends with „(Nur teilweise rückgängig)“), `FromRecord_rejects_unlisted` (service `wuauserv`; drive `C:\..`), `Check_rows` (green / red with change / grey when unreadable).
- [ ] **Step 3: Run** `--filter "FullyQualifiedName~SystemProtectionTests"` → FAIL.
- [ ] **Step 4: Implement**; `SecurityCheck` adds the row after `Updates` (the page has no group headings; the row sits with the other protection rows).
- [ ] **Step 5: Run** class + full suite → PASS.
- [ ] **Step 6: Commit** `feat(security): switch on system protection (sysprotect, partial undo)`.

### Task 7: Sicherheit-Seite behebt selbst (UI + wiring for Tasks 3–6)

**Files:**
- Modify: `src/WinWaechter.App/ViewModels/SecurityViewModel.cs`, `src/WinWaechter.App/Views/SecurityView.xaml`, `src/WinWaechter.App/Services/AppServices.cs`, `tests/WinWaechter.Tests/SecurityViewModelTests.cs`, `tests/WinWaechter.Tests/ViewSmokeTests.cs`

**Interfaces:**
- Consumes: Tasks 2–6.
- Produces:
  - `SecurityViewModel(…, Action<string>? copyText = null, ActionRunner? actions = null)`; `SecurityItemViewModel.HasChangeButton` (Change and ChangeLabel set) and `ChangeLabel`; `RunChangeCommand(SecurityItemViewModel)` applies `Item.Change` like today's `ApplyChangeAsync`; `RunActionCommand` case `SecurityActionKind.Action` → `actions.RunAsync(this, item.Act, progress)`, outcome with Link → `dialogs.Confirm(title, message, "Windows-Sicherheit öffnen")` → `shell.OpenLink`; then refresh and `LastSummary = outcome.Message`. Every change/Aktion needs `CanChange` (link and extension page do not).
  - Confirm text for changes uses `ChangeText.ConfirmLine(change)`; sysprotect confirm adds „Den Computerschutz schaltet WinWächter auch beim Rückgängigmachen nicht wieder ab, weil Windows dabei alle Wiederherstellungspunkte löscht.“
  - AppServices: `SecValueChange.Register`, `FirewallRuleChange.Register(factory, fwRules, windowsDir)`, `SystemProtectChange.Register`; `SecuritySources` gets `() => new JournalPolicyOwnership(store)`, `new FwPolicy2Rules()`, `rules`, `SecurityActionSet.Create(defender, new WuaUpdateInstaller(), new WinRestarter(new ProcessRunner(), windowsDir))`, `new WinSystemRestoreConfig(reg)`, `sc`; one `ActionRunner(dialogs, gate)` instance shared by all pages.

- [ ] **Step 1: Write failing tests** in `SecurityViewModelTests`: `Fix_button_applies_change_and_journals` (SmartScreen row → engine session „Sicherheit“ with one secvalue record), `Child_row_has_scan_and_block_buttons`, `Action_row_runs_through_runner_and_refreshes`, `Realtime_blocked_offers_windows_security`, `Fixes_locked_without_admin` (canChange false → RunChange/RunAction(Action) cannot execute, Link can), `Sysprotect_confirm_says_partial` (RecordingDialogs). `ViewSmokeTests`: Security page with a fix row, an action row and a child with two buttons renders light/dark.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~SecurityViewModelTests"` → FAIL.
- [ ] **Step 3: Implement** VM, view (second button on child rows, `Button.Primary`-free row buttons as today), wiring.
- [ ] **Step 4: Run** class, `ViewSmokeTests` + full suite → PASS; build `src\WinWaechter.App` Debug without new warnings.
- [ ] **Step 5: Commit** `feat(security): page fixes instead of links`.

### Task 8: Leistung – Schnellstart, Energiesparplan, Umstellen auf „Ausgewogen“ (4.3 Core)

**Files:**
- Create: `src/WinWaechter.Core/Performance/PowerPlans.cs`, `tests/WinWaechter.Tests/PowerPlanTests.cs`
- Modify: `src/WinWaechter.Core/Performance/PerfValues.cs`, `src/WinWaechter.Core/Performance/PerformanceScanner.cs`, `src/WinWaechter.Core/Performance/PerformanceRegistration.cs`, `src/WinWaechter.Core/Sys/Native/PowerNative.cs`, `tests/WinWaechter.Tests/Fakes/FakeFix.cs` (FakePowerPlans), `tests/WinWaechter.Tests/PerformanceScannerTests.cs`

**Interfaces:**
- Consumes: `PerfValueChange`, `IHibernation.IsEnabled`, `PowerModeIds.BalancedPlan`.
- Produces:
  - `PerfValues.FastStartup` = HKLM `SYSTEM\CurrentControlSet\Control\Session Manager\Power` `HiberbootEnabled` targets [0,1], default 0 (same reading as `FastStartupActive`), added to `Fixed`.
  - `sealed record PowerPlan(Guid Id, string Name)`; `interface IPowerPlans { IReadOnlyList<PowerPlan> List(); Guid? Active(); void SetActive(Guid id); }`; `WinPowerPlans` via powrprof `PowerEnumerate(…, ACCESS_SCHEME 16, …)`, `PowerReadFriendlyName`, `PowerGetActiveScheme`, `PowerSetActiveScheme` (non-zero → `Win32Exception`).
  - `sealed class PowerPlanChange(IPowerPlans plans, Guid target) : IChange` – target must be in `plans.List()` (else `ArgumentException`); Kind `powerplan`, Target „Energiesparplan“, Description „Energiesparplan auf „{Name}“ umstellen“, Before `{plan}`, After `{plan}`; Undo sets the old GUID only if it parses and is in `List()`, else throws; Drift `Active() != target` → Changed. `static FromRecord(ChangeRecord, IPowerPlans)` (both GUIDs must be existing plans). `RetryData` `{}`/`{plan}`.
  - `PerformanceScanner(…, ISettingBroadcast broadcast, IPowerPlans? plans = null, Func<IPolicyOwnership>? policies = null)` (policies used in Task 14). New rows: `faststartup` (group `power`, „Schnellstart“, Options An/Aus, Recommended null, Explanation „Windows fährt schneller hoch, weil es beim Herunterfahren einen Teil des Systems sichert. Dafür wirken manche Updates und Treiberwechsel erst nach „Neu starten“, und das Gerät ist nie ganz aus.“; Available only when `hib.IsEnabled`, else UnavailableText „Nur mit eingeschalteter Ruhezustand-Datei“); `powerplan` (group `power`, „Energiesparplan“, Options = index into the plans captured at `Scan`, Label = plan name; unreadable → „Nicht lesbar“). `public const string PlanLocked` (today private) so the VM can detect the locked mode rows. `ChangesFor` handles both ids. `PerformanceRegistration.Register(…, IPowerPlans plans)` registers `powerplan`.

- [ ] **Step 1: Write failing tests** in `PowerPlanTests`: `Apply_undo_and_drift`, `FromRecord_rejects_unlisted` (unknown GUID target; Before „not-a-guid“ → Undo throws), `Target_must_be_existing_plan`. In `PerformanceScannerTests`: `Fast_startup_row_follows_hibernation`, `Power_plan_row_lists_plans_and_changes_active`, `Registered_kinds_rehydrate` extended by `powerplan` and the FastStartup `perfvalue`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~PowerPlanTests|FullyQualifiedName~PerformanceScannerTests"` → FAIL.
- [ ] **Step 3: Implement**.
- [ ] **Step 4: Run** both classes + full suite → PASS.
- [ ] **Step 5: Commit** `feat(performance): fast startup and power plan settings`.

### Task 9: Start-Bericht-Zuordnung, Beenden, Datenträger fast voll (4.3 Core)

**Files:**
- Create: `src/WinWaechter.Core/Performance/StartupMatcher.cs`, `src/WinWaechter.Core/Actions/ProcessEnding.cs`, `src/WinWaechter.Core/Sys/DiskSpace.cs`, `tests/WinWaechter.Tests/StartupMatcherTests.cs`, `tests/WinWaechter.Tests/ProcessEndingTests.cs`
- Modify: `src/WinWaechter.Core/Performance/BootReport.cs`, `tests/WinWaechter.Tests/Fakes/FakeFix.cs` (FakeProcessEnder, FakeDiskSpace), `tests/WinWaechter.Tests/BootReportTests.cs`

**Interfaces:**
- Consumes: `Finding` (Area, Change, ExePath), `RuleSet.Classify`, `IProcessList`, `IServiceControl.List`, `ISignatureVerifier`.
- Produces:
  - `BootEvent(…, long? DegradationMs, string? FileName = null)`, `BootDelay(string Name, TimeSpan Delay, string? FileName = null)`; `WinBootEventSource` fills FileName from `Data[@Name='Name']` when FriendlyName is used; `BootReport.From` carries it.
  - `sealed record StartupMatch(Finding? Finding, string? Note)`; `static class StartupMatcher { const string NotChangeable = "Teil von Windows oder ein Treiber – nicht änderbar"; const string ProtectedNote = "Geschützt (z. B. Dell)"; const string AlreadyOff = "Startet bereits nicht mehr mit Windows"; static StartupMatch Match(BootDelay d, IReadOnlyList<Finding> startup, RuleSet rules); }` – candidates: Area Autostart/Aufgabe/Dienst whose `ExePath` file name equals `d.FileName` (ignore case), else whose Title equals `d.Name`; with Change → Finding; candidate without Change → AlreadyOff; none: `rules.Classify(new ItemFacts(d.Name, null, null, null))` or by FileName Protected → ProtectedNote, else NotChangeable. Never a Finding whose `IsProtectedVendor` is true.
  - `interface IProcessEnder { IReadOnlyList<RunningProcess> Find(string processName); int CloseWindows(IReadOnlyList<RunningProcess> p); int Kill(IReadOnlyList<RunningProcess> p); bool AnyRunning(IReadOnlyList<RunningProcess> p); }` (name without `.exe`, ignore case); `WinProcessEnder(IProcessList procs)` (`CloseMainWindow`, `Kill`, by PID, each in try/catch).
  - `sealed record ProcessGuardContext(string WindowsDir, int SelfPid, string? SelfExe, IReadOnlySet<string> ServiceImages, Func<RunningProcess, bool> IsProtected)`; `static class ProcessGuard { static bool MayEnd(RunningProcess p, ProcessGuardContext c); static bool MayEndBrowser(RunningProcess p, ProcessGuardContext c); static readonly string[] Browsers = ["msedge", "chrome", "opera", "firefox"]; }` – `MayEnd`: ExePath known, not under WindowsDir, not SelfPid/SelfExe, not a service image (normalized), not `IsProtected`. `MayEndBrowser` (4.9 only, fixed list): name in `Browsers` and the same checks except `IsProtected` (Edge lives under the protected `…\Microsoft\` path; closing it is the user's explicit, named request).
  - `sealed record DriveSpace(string Drive, long FreeBytes, long TotalBytes)`; `interface IDiskSpace { DriveSpace? SystemDrive(); }`; `WinDiskSpace` (`DriveInfo` of the Windows drive, null on error); `static bool DiskSpace.IsLow(DriveSpace d)` = Free < Total/10 **or** Free < 15 GiB (15·1024³).

- [ ] **Step 1: Write failing tests**: `StartupMatcherTests`: `Autostart_task_and_service_are_matched_by_file_name`, `Disabled_autostart_says_already_off`, `Dell_is_protected_without_button`, `Unknown_is_windows_or_driver`; `ProcessEndingTests`: `Same_name_in_windows_folder_or_service_is_never_ended` (three „helper“ processes: `C:\Windows\…`, a service image, AppData → only the AppData one allowed), `WinWaechter_itself_is_never_ended`, `Protected_vendor_is_never_ended_but_browser_close_skips_only_that_rule`, `Low_disk_threshold` (9 % of 1 TB → low; 14 GB of 100 GB → low; 20 GB of 100 GB → not); `BootReportTests`: `File_name_is_carried`.
- [ ] **Step 2: Run** the three classes → FAIL.
- [ ] **Step 3: Implement** the three files and the BootReport fields.
- [ ] **Step 4: Run** classes + full suite → PASS.
- [ ] **Step 5: Commit** `feat(performance): match slow-start entries, safe process ending, low disk check`.

### Task 10: Leistung-Seite beseitigt Bremsen (4.3 UI)

**Files:**
- Modify: `src/WinWaechter.App/ViewModels/PerformanceViewModel.cs`, `src/WinWaechter.App/Views/PerformanceView.xaml`, `src/WinWaechter.App/Services/AppServices.cs`, `tests/WinWaechter.Tests/PerformanceViewModelTests.cs`, `tests/WinWaechter.Tests/ViewSmokeTests.cs`

**Interfaces:**
- Consumes: Tasks 2, 8, 9; `OptimizerEngine.ApplyAsync`.
- Produces:
  - `sealed record PerformanceTools(Func<IReadOnlyList<Finding>> StartupFindings, RuleSet Rules, IProcessEnder Ender, Func<ProcessGuardContext> Guard, ActionRunner Actions, Func<DriveSpace?> Disk, Func<TimeSpan, CancellationToken, Task>? Delay = null)`; ctor `PerformanceViewModel(…, Func<bool>? fastStartup = null, PerformanceTools? tools = null)`.
  - `BootSlowest` becomes `ObservableCollection<BootDelayViewModel>` (`Text`, `Note`, `CanDisable`, `DisableCommand` → confirm „{Description}“ + ConfirmNote → `ApplyAsync("Leistung", [finding])` under the gate → refresh). The „Autostart-Programme prüfen“ jump stays; the fast-startup note now reads „Der Schnellstart ist an … – siehe „Schnellstart“ unter Einstellungen.“.
  - `ConsumerViewModel` becomes a class: `Name`, `CpuText`, `MemoryText`, `CanEnd`, `EndCommand`, `CanStopAutostart`, `StopAutostartCommand` (autostart finding with Change whose exe file name equals the process name). End flow: `SystemAction` Final, Title „„{Name}“ beenden“, ConfirmText „Nicht gespeicherte Arbeit geht verloren.“; run: allowed = `Find(name)` filtered by `ProcessGuard.MayEnd`; none → outcome „Dieses Programm beendet WinWächter nicht (Windows, Dienst oder geschützt).“; `CloseWindows`, wait 5 s (`Delay`), still running → `dialogs.Ask("Erzwingen?", "„{Name}“ reagiert nicht. Jetzt erzwingen? Nicht gespeicherte Arbeit geht verloren.", "Erzwingen", "Abbrechen")` → `Kill`.
  - `PerfSettingViewModel.ShowSwitchToBalanced` (id `power-ac`/`power-dc` with `UnavailableText == PerformanceScanner.PlanLocked`); `SwitchToBalancedCommand` sets the `powerplan` row's `Chosen` to the option of `PowerModeIds.BalancedPlan` (then applied with the normal „Änderungen anwenden“).
  - `LowDiskText` (null when not low) „Auf Laufwerk C: sind nur noch 8,2 GB frei.“ with lines „Zwischenspeicher und temporäre Dateien (Optimieren)“, „Ablage im Verlauf leeren“, and „Ruhezustand-Datei: {size} (Einstellungen → Ruhezustand-Datei)“ when `FileBytes > 0`; `MakeSpaceCommand` = existing `openOptimize` plus starting the Optimize scan (passed in as `Action openOptimize`).
  - AppServices: `PerformanceTools(TraceScan, rules, new WinProcessEnder(procs), guard factory (service images from `sc.List()`, `ProcessGuardContext.IsProtected` = `rules.Classify(ItemFacts(name, exe, sig publisher, null)) == Protected`), actions, new WinDiskSpace().SystemDrive)`; `PerformanceRegistration.Register(…, new WinPowerPlans())`; scanner gets plans and policies.

- [ ] **Step 1: Write failing tests**: `Slow_start_entry_offers_disable_and_journals`, `Protected_slow_start_entry_has_no_button`, `End_closes_gently_then_asks_to_force` (FakeProcessEnder still running after delay → Ask; „Abbrechen“ → no Kill), `End_refused_for_windows_process`, `Consumer_with_autostart_offers_stop`, `Switch_to_balanced_marks_plan_row_pending`, `Low_disk_band_lists_hibernation_file`, `Actions_locked_without_admin`. `ViewSmokeTests`: page with boot buttons, consumer buttons, low-disk band, light/dark.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~PerformanceViewModelTests"` → FAIL.
- [ ] **Step 3: Implement** VM, view, wiring.
- [ ] **Step 4: Run** class, `ViewSmokeTests` + full suite → PASS.
- [ ] **Step 5: Commit** `feat(performance): fix slow starts and heavy consumers from the page`.

### Task 11: Werbe-Apps – Regeln, Paket-Schnittstelle, `appx` (4.4 Core)

**Files:**
- Create: `src/WinWaechter.Core/Programs/BloatApps.cs`, `tests/WinWaechter.Tests/BloatAppTests.cs`
- Modify: `src/WinWaechter.Core/Rules/RuleSet.cs`, `src/WinWaechter.Core/Rules/PrivacyDefinitions.cs` (record `BloatAppRule`), `rules/rules.json` (`bloatApps`, `rulesVersion` 3), `src/WinWaechter.Core/Sys/SystemLinks.cs`, `src/WinWaechter.Core/Optimizer/OptimizerEngine.cs` (`IsUndoable`), `tests/WinWaechter.Tests/Fakes/FakeFix.cs` (FakeAppPackages), `tests/WinWaechter.Tests/RuleSetTests.cs`

**Interfaces:**
- Consumes: `IProcessRunner`, `RuleSet`, `IUndoLimit`.
- Produces:
  - `sealed record BloatAppRule(string Package, string Title, bool Recommended)` (`Package` = package Name, optional trailing `*`); `RuleSet.BloatApps`, `BloatAppRule? RuleSet.FindBloatApp(string packageName)`, `bool RuleSet.IsProtectedApp(ItemFacts f)` (all protected matchers except `{publisher: Microsoft}` and the `%ProgramFiles%\WindowsApps\` / `%ProgramFiles(x86)%\WindowsApps\` paths). Load throws `InvalidDataException` for an entry without package/title, a `*` not at the end, or a package on the Nie-Liste. `UnionWith` keeps `this.BloatApps` (like `Privacy`).
  - `static class BloatApps { static readonly string[] Never; static bool IsNever(string packageName); }` – Never: `Microsoft.GamingApp`, `Microsoft.XboxIdentityProvider`, `Microsoft.XboxGamingOverlay`, `Microsoft.XboxGameOverlay`, `Microsoft.XboxSpeechToTextOverlay`, `Microsoft.Xbox.TCUI`, `Microsoft.GamingServices`, `Microsoft.WindowsStore`, `Microsoft.StorePurchaseApp`, `Microsoft.DesktopAppInstaller`, `Microsoft.Windows.Photos`, `Microsoft.WindowsCalculator`, `Microsoft.WindowsNotepad`, `Microsoft.WindowsTerminal`, `Microsoft.Paint`, `Microsoft.ScreenSketch`, `Microsoft.WindowsCamera`, `Microsoft.WindowsAlarms`, `Microsoft.SecHealthUI`, `Microsoft.MicrosoftEdge*`, `Microsoft.VCLibs*`, `Microsoft.NET*`, `Microsoft.UI.Xaml*`, `Microsoft.WindowsAppRuntime*`, `Microsoft.Services.Store.Engagement`, and any name containing `Dell` or `Lexware`/`Haufe`.
  - `sealed record AppPackage(string Name, string FullName, string FamilyName, string? InstallLocation, bool IsFramework, bool NonRemovable, string SignatureKind, bool InImage)`; `interface IAppPackages { IReadOnlyList<AppPackage> List(); void Remove(string fullName); void Register(string manifestPath); bool IsInstalled(string familyName); }`; `WinAppPackages(IProcessRunner runner, string userSid, string programFiles)` – PowerShell: `Get-AppxPackage -User '<sid>' | Select-Object Name,PackageFullName,PackageFamilyName,InstallLocation,IsFramework,NonRemovable,SignatureKind | ConvertTo-Json -Compress`, `Get-AppxProvisionedPackage -Online` (DisplayName → `InImage`), `Remove-AppxPackage -Package '<full>'`, `Add-AppxPackage -Register '<manifest>' -DisableDevelopmentMode`; non-zero exit → `InvalidOperationException` with stderr. `static bool IsValidFullName(string)` = `^[A-Za-z0-9.\-]+_[0-9.]+_[A-Za-z0-9]+_[A-Za-z0-9.\-~]*_[a-z0-9]{13}$`, `IsValidFamily` = `^[A-Za-z0-9.\-]+_[a-z0-9]{13}$`, SID `^S-1-5-21(-\d+){4}$`, manifest must be `<programFiles>\WindowsApps\<FullName>\AppxManifest.xml` without `'` – checked before every command.
  - `sealed class AppxChange(IAppPackages pkgs, AppPackage pkg, BloatAppRule rule, RuleSet rules) : IChange, IUndoLimit` – Kind `appx`, Target = FamilyName, Description „„{Title}“ entfernen“, `UndoLimit = pkg.InImage ? null : UndoLimits.StoreOnly`. Apply re-checks rule/Nie-Liste/NonRemovable/`SignatureKind != "System"`/framework/`IsProtectedApp`, and `IsInstalled` (gone → `InvalidOperationException("„{Title}“ ist nicht mehr installiert.")`); a running app is not ended first; `Remove` (failure → `InvalidOperationException("„{Title}“ ließ sich nicht entfernen: {Fehlertext}")`); After `{restorable = manifest file exists ? "true" : "false", manifest}`; Before `{name, fullName, family, title}`. Undo: restorable → `Register(manifest)`, else `InvalidOperationException(UndoLimits.StoreOnly)`. Drift: installed again → Changed. `static FromRecord(ChangeRecord, IAppPackages, RuleSet)` (name on `bloatApps`, not Never, valid full name/family/manifest path). `RetryData` `{name, fullName, family, title}`. `OptimizerEngine.IsUndoable` is false for `appx` records with `restorable == "false"`.
  - `sealed record BloatAppRow(BloatAppRule Rule, AppPackage Package, AppxChange Change)`; `BloatAppScanner(IAppPackages pkgs, RuleSet rules)` → `IReadOnlyList<BloatAppRow> Scan()` (only rules-listed, never Never/System/framework/NonRemovable/protected, ordered recommended first then title).
  - `SystemLinks.StoreLink(string family)` → `ms-windows-store://pdp/?PFN={family}` (throws when `!IsValidFamily`); `SystemLinks.IsStoreLink(string?)`.
  - `rules.json` `bloatApps`: recommended – Candy Crush/King (`king.com.*`), TikTok, Spotify, Disney+, Facebook, Instagram, LinkedIn, WhatsApp, `Microsoft.MicrosoftSolitaireCollection`, `Microsoft.BingNews`, `Microsoft.BingWeather`, `Microsoft.Getstarted`, `Microsoft.WindowsFeedbackHub`, `Microsoft.GetHelp`, `Microsoft.People`, `Clipchamp.Clipchamp`, `Microsoft.Windows.DevHome`; offered, not recommended – `Microsoft.Copilot`, `Microsoft.OutlookForWindows`, `MSTeams`, `Microsoft.MicrosoftOfficeHub`, `MicrosoftCorporationII.QuickAssist`, `Microsoft.MicrosoftStickyNotes`. Titles German („Microsoft News“, „Wetter“, „Tipps“, „Feedback-Hub“, „Hilfe anfordern“, „Kontakte“, „Kurznotizen“, „Teams (privat)“, „Outlook (neu)“ …).

- [ ] **Step 1: Read-only device check** (owner's PC, PowerShell as the signed-in user): `Get-AppxPackage | Where-Object { -not $_.IsFramework -and $_.SignatureKind -ne 'System' } | Select-Object Name, PackageFamilyName, SignatureKind, NonRemovable | Sort-Object Name`. For the installed candidates (spec: Clipchamp, Copilot, Outlook neu, Teams, Microsoft 365, Dev Home, Quick Assist, Kurznotizen) use the exact `Name` shown. A candidate shown with `NonRemovable = True` or `SignatureKind = System` is dropped and noted in the roadmap. Candidates not installed keep the well-known names above. Nothing on the Nie-Liste may appear in `bloatApps`.
- [ ] **Step 2: Write failing tests** in `BloatAppTests`: `Only_listed_packages_are_offered` (unlisted Microsoft package; Xbox app even if listed → load throws; framework; System; NonRemovable; Dell-named package), `Microsoft_rule_does_not_block_listed_packages_but_other_protection_does`, `Network_rules_never_add_bloat_apps_or_privacy` (`UnionWith` with a downloaded set containing both → unchanged), `Apply_removes_and_marks_restorable`, `Store_only_package_is_marked_before_and_not_undoable` (InImage false → UndoLimit StoreOnly, `IsUndoable` false), `Undo_reregisters_from_manifest`, `Package_gone_before_apply_fails_cleanly`, `Package_names_with_metacharacters_are_rejected` (full name with `'`/`;`, manifest outside WindowsApps → no FakeProcessRunner call), `Powershell_commands_are_exact` (WinAppPackages + FakeProcessRunner), `Store_link_is_validated`.
- [ ] **Step 3: Run** `--filter "FullyQualifiedName~BloatAppTests|FullyQualifiedName~RuleSetTests"` → FAIL.
- [ ] **Step 4: Implement**; update tests that pin the bundled `rulesVersion`. Signing/publishing the new rules file belongs to the release (owner), not to this plan.
- [ ] **Step 5: Run** classes + full suite → PASS.
- [ ] **Step 6: Commit** `feat(programs): removable preinstalled apps from a fixed local list (appx)`.

### Task 12: „Vorinstallierte Apps“ auf der Programme-Seite, Store-Knopf im Verlauf (4.4 UI)

**Files:**
- Modify: `src/WinWaechter.App/ViewModels/ProgramsViewModel.cs`, `src/WinWaechter.App/Views/ProgramsView.xaml`, `src/WinWaechter.App/ViewModels/HistoryViewModel.cs`, `src/WinWaechter.App/Views/HistoryView.xaml`, `src/WinWaechter.App/Services/WpfDialogService.cs` (`ExplorerShell.OpenLink` also accepts `SystemLinks.IsStoreLink`), `src/WinWaechter.App/Services/AppServices.cs`, `tests/WinWaechter.Tests/ProgramsViewModelTests.cs`, `tests/WinWaechter.Tests/ViewModelTests.cs` (history), `tests/WinWaechter.Tests/ViewSmokeTests.cs`

**Interfaces:**
- Consumes: Task 11; `ChangeText.ConfirmLine`.
- Produces:
  - `ProgramsViewModel(…, ChangeGate gate, Func<IReadOnlyList<BloatAppRow>>? apps = null, bool sameAccount = true)`; `Apps` (`ObservableCollection<BloatAppItemViewModel>`: `Title`, `IsRecommended`, `IsSelected` default false, `UndoText` „Rückgängig im Verlauf“ or „Nur über den Store wiederherstellbar“); `AppsLocked` / `AppsLockedText` = !CanChange → AdminNotice; !sameAccount → „WinWächter läuft unter einem anderen Konto als dem angemeldeten. Apps lassen sich nur im eigenen Konto entfernen.“; `SelectRecommendedAppsCommand`; `RemoveAppsCommand` „Ausgewählte entfernen (n)“ → confirm with one `ConfirmLine` per app → `ApplyAsync("Vorinstallierte Apps entfernen", …)` → reload apps → `ChangesApplied`. Section at the top of the page (spec 4.4), list read on first visit with the programs.
  - `RecordViewModel.StoreLink` (appx with `restorable == "false"`: `SystemLinks.StoreLink(Before["family"])`), `Hint` „Nur über den Store wiederherstellbar“; for `sysprotect` Hint „Nur teilweise rückgängig: Der Computerschutz bleibt an.“; `HistoryViewModel.OpenStoreCommand(RecordViewModel)` → `ISystemShell.OpenLink` (new optional ctor param `ISystemShell? shell = null`).
  - AppServices: `AppxChange` registration (`f.Register("appx", r => AppxChange.FromRecord(r, pkgs, rules))`), `new WinAppPackages(new ProcessRunner(), user.Sid, programFiles)`, `sameAccount` = existing `ownAccount`.

- [ ] **Step 1: Write failing tests**: `Apps_section_lists_rows_unselected`, `Remove_confirm_marks_store_only`, `Apps_locked_for_other_account_and_without_admin`, `History_store_only_record_offers_store_link_not_undo`, `Sysprotect_record_says_partial`; `ViewSmokeTests` for Programs (section) and History (store button) light/dark.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~ProgramsViewModelTests"` → FAIL.
- [ ] **Step 3: Implement**.
- [ ] **Step 4: Run** classes, `ViewSmokeTests` + full suite → PASS.
- [ ] **Step 5: Commit** `feat(programs): remove preinstalled apps, store link in the history`.

### Task 13: Windows-Werbung abschalten (4.5)

**Files:**
- Modify: `src/WinWaechter.Core/Rules/PrivacyDefinitions.cs`, `src/WinWaechter.Core/Rules/RuleSet.cs`, `rules/rules.json` (`privacy`), `src/WinWaechter.App/ViewModels/PrivacyViewModel.cs`, `src/WinWaechter.App/Views/PrivacyView.xaml`, `tests/WinWaechter.Tests/PrivacyTests.cs`, `tests/WinWaechter.Tests/PrivacyViewModelTests.cs`, `tests/WinWaechter.Tests/RuleSetTests.cs`

**Interfaces:**
- Produces:
  - `PrivacySetting(…, PrivacyEffective? Effective, string? Group = null)`; JSON field `group`; `const string PrivacyGroups.Ads = "werbung"`. Load throws `InvalidDataException` when one (hive,key,name) occurs twice in `privacy` (`FindPrivacyValue` takes the first match).
  - Existing ids keep their ids and values and only get `"group": "werbung"`: `vorschlaege`, `tipps`, `start-empfehlungen`, `vorschlags-apps`. New entries (all `recommended`, target 0, `group` werbung): `start-kontohinweise` (HKCU `Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced` `Start_AccountNotifications`), `systemvorschlaege` (HKCU `…\ContentDeliveryManager` `SystemPaneSuggestionsEnabled`), `sperrbildschirm-tipps` (`…\ContentDeliveryManager` `RotatingLockScreenOverlayEnabled`), `explorer-werbung` (`…\Explorer\Advanced` `ShowSyncProviderNotifications`), `begruessung-nach-updates` (HKCU `Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement` `ScoobeSystemSettingEnabled`) – each with a one-sentence German explanation.
  - `PrivacyViewModel.Groups` (`ObservableCollection<PrivacyGroupViewModel(Title, Rows)>`; null group → „Datenschutz“, `werbung` → „Werbung & Vorschläge“); `Rows` stays the flat list (selection, apply and tests unchanged).

- [ ] **Step 1: Read-only device check** (owner's PC, as the signed-in user): `Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager','HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced','HKCU:\Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement' -ErrorAction SilentlyContinue | Format-List`. Record which candidate names exist. A candidate whose key does not exist at all on 26200 is dropped now and noted in the roadmap. The before/after toggle comparison in the Windows-Einstellungen changes settings and is therefore item 3 of the Task 19 checklist (with consent); any candidate that does not match there is removed before release.
- [ ] **Step 2: Write failing tests**: `Existing_ids_keep_values_and_move_to_ads_group`, `New_ad_values_are_listed_once` (no duplicate triple across `privacy`), `Duplicate_privacy_values_are_rejected`, `Ads_group_shown_on_page` (VM groups and titles).
- [ ] **Step 3: Run** `--filter "FullyQualifiedName~PrivacyTests|FullyQualifiedName~PrivacyViewModelTests|FullyQualifiedName~RuleSetTests"` → FAIL.
- [ ] **Step 4: Implement** rules, parsing, groups in VM and view (group heading style as on the Optimize page).
- [ ] **Step 5: Run** classes, `ViewSmokeTests` + full suite → PASS.
- [ ] **Step 6: Commit** `feat(privacy): group „Werbung & Vorschläge“ with more Windows ad switches`.

### Task 14: Versteckte Hintergrund-Bremsen der Browser (4.7)

**Files:**
- Create: `tests/WinWaechter.Tests/BrowserBackgroundTests.cs`
- Modify: `src/WinWaechter.Core/Performance/PerfValues.cs`, `src/WinWaechter.Core/Performance/PerformanceScanner.cs`, `src/WinWaechter.App/ViewModels/PerformanceViewModel.cs`, `src/WinWaechter.App/Views/PerformanceView.xaml`

**Interfaces:**
- Consumes: Task 1 `PolicyValues`, Task 8 scanner ctor (`Func<IPolicyOwnership>? policies`), Task 10 `PerformanceTools.StartupFindings`.
- Produces:
  - `PerfValues.EdgeStartupBoost` (HKLM `SOFTWARE\Policies\Microsoft\Edge` `StartupBoostEnabled`), `EdgeBackgroundMode` (same key `BackgroundModeEnabled`), `ChromeBackgroundMode` (HKLM `SOFTWARE\Policies\Google\Chrome` `BackgroundModeEnabled`) – targets **[0]** only, default 1 (missing = browser default „an“). Switching back on happens only by undo in the Verlauf (that deletes the value or restores the old one), so WinWächter never writes a policy that switches something on.
  - Scanner group `browser-background` (view title „Im Hintergrund“), rows `edge-startupboost` („Edge: Start-Boost“), `edge-background` („Edge: Erweiterungen und Apps im Hintergrund, wenn Edge geschlossen ist“), `chrome-background` („Chrome: Apps im Hintergrund weiter ausführen“); only when the browser is installed (HKLM `SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe` / `chrome.exe` exists); Options An(1)/Aus(0), Recommended 0, Explanation „Spart Arbeitsspeicher und Akku; der Browser startet nur Sekundenbruchteile langsamer.“. `PolicyValues.State` Foreign → Available false, UnavailableText `PolicyValues.ForeignText` (state shown, not changed); Ours with 0 → Available false, UnavailableText „Aus – wieder einschalten über den Verlauf“. When the browser's policy key has no values and no subkeys: `Hint` = `PerformanceScanner.ManagedHint(browser)` = „{Edge|Chrome} zeigt danach „Von Ihrer Organisation verwaltet“; das ist normal und verschwindet beim Rückgängigmachen.“ (also put in the confirm line by the VM).
  - `PerformanceViewModel.BackgroundLaunchersHint` (null unless an active autostart finding – Change not null – has a title starting with one of `["Microsoft Teams", "Teams", "Steam", "EA", "Ubisoft", "Epic Games"]`): „{Namen} starten mit Windows und laufen im Hintergrund. Das regeln Sie unter Optimieren → Autostart.“ with the existing `OpenOptimizeCommand`.

- [ ] **Step 1: Read-only device check**: `Get-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\Edge','HKLM:\SOFTWARE\Policies\Google\Chrome' -ErrorAction SilentlyContinue | Format-List`. Expected on the owner's PC: 11 foreign Edge policies incl. `StartupBoostEnabled` → that row must show „Von einer Richtlinie festgelegt“. Opera: no official, documented machine policy for background running is known → Opera is not offered; note „Opera: kein gleichwertiger Schalter“ in the roadmap (only add it if such a documented policy is found, then as one more whitelist entry).
- [ ] **Step 2: Write failing tests**: `Rows_only_for_installed_browsers`, `Foreign_policy_is_shown_not_changed`, `Own_policy_off_points_to_history`, `Apply_creates_policy_and_undo_deletes_it` (missing key → apply 0 → undo deletes value), `Managed_hint_only_when_browser_had_no_policy`, `FromRecord_rejects_target_1_for_browser_values`, `Background_launchers_hint_names_active_autostarts`.
- [ ] **Step 3: Run** `--filter "FullyQualifiedName~BrowserBackgroundTests"` → FAIL.
- [ ] **Step 4: Implement** whitelist, rows, VM hint, view group.
- [ ] **Step 5: Run** class, `PerformanceScannerTests`, `ViewSmokeTests` + full suite → PASS.
- [ ] **Step 6: Commit** `feat(performance): browser background switches via official policies`.

### Task 15: Rechtsklick-Menü entrümpeln (4.8)

**Files:**
- Create: `src/WinWaechter.Core/Optimizer/ContextMenu/ShellExtScanner.cs`, `src/WinWaechter.Core/Optimizer/ContextMenu/ShellExtChange.cs`, `tests/WinWaechter.Tests/ShellExtTests.cs`
- Modify: `src/WinWaechter.Core/Model/Finding.cs` (`FindingGroups.ContextMenu = "Rechtsklick-Menü"`), `src/WinWaechter.Core/Actions/SystemActions.cs` (Explorer restart), `src/WinWaechter.App/ViewModels/OptimizeViewModel.cs`, `src/WinWaechter.App/Services/AppServices.cs`, `tests/WinWaechter.Tests/ViewModelTests.cs`

**Interfaces:**
- Consumes: `IRegistry` (default value = name `""`), `ISignatureVerifier`, `RuleSet.Classify`, `CommandLineParser`, `RegDword.Snapshot/WriteString/Restore`, Task 2 `ActionRunner`.
- Produces:
  - `static class ShellExtLocations { static readonly (string Key, string Label)[] All = [("*", "Dateien"), ("AllFilesystemObjects", "Dateien und Ordner"), ("Directory", "Ordner"), ("Directory\\Background", "Hintergrund"), ("Folder", "Ordner"), ("Drive", "Laufwerke")]; const string BlockedKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked"; }`; class roots HKLM `SOFTWARE\Classes`, HKCU `Software\Classes`.
  - `ShellExtScanner(IRegistry reg, ISignatureVerifier sig, RuleSet rules, string windowsDir, string ownDir)` → `IReadOnlyList<Finding> Scan()`: handlers under `<root>\<loc>\shellex\ContextMenuHandlers\<name>` (CLSID = name if GUID else default value), DLL from `<root>\CLSID\{clsid}\InprocServer32` default (env expanded), title = CLSID default value or handler name, publisher from signature; static verbs `<root>\<loc>\shell\<verb>` with `command` default value (exe via `CommandLineParser`), skipping Windows verbs (`open`, `opennewwindow`, `edit`, `print`, `printto`, `runas`, `find`, `explore`, `cmd`, `powershell`, `pintohome`, `Share`) and verbs with `LegacyDisable`/`ProgrammaticAccessOnly`. Hidden: file under windowsDir, signed Microsoft, `Classify == Protected`, under ownDir, already blocked (CLSID value in BlockedKey). Findings: `FindingArea.Einstellung`, `Group = FindingGroups.ContextMenu`, `Rating.Info`, `Preselected false`, Location = Ort label(s), Reasons = [signature line, „Ort: Dateien, Ordner“], ExePath = DLL/exe, Change = `ShellExtChange`; one finding per CLSID/verb (locations merged).
  - `sealed class ShellExtChange : IChange` – `static ShellExtChange Block(IRegistry reg, Guid clsid, string title)` (HKLM BlockedKey, value name `clsid.ToString("B")`, REG_SZ ""), `static ShellExtChange LegacyDisable(IRegistry reg, Hive hive, string verbKey, string title)` (value `LegacyDisable` REG_SZ "" on that verb key). Kind `shellext`, Description „„{title}“ aus dem Rechtsklick-Menü entfernen“. Apply refuses when the value already exists; Before = Snapshot; Undo deletes exactly that value (Restore with existed=false); Drift: value missing → Gone. `static FromRecord(ChangeRecord, IRegistry)`: block → hive HKLM, key == BlockedKey, name a `{GUID}`; legacy → name `LegacyDisable`, key == `<root>\<loc>\shell\<verb>` with root/loc from the lists and verb one segment (no `\`, `/`, `..`, control chars); else `InvalidOperationException("Dieser Eintrag gehört nicht zum Rechtsklick-Menü – Rückgängig abgelehnt.")`. `static Register(ChangeFactory, IRegistry)`. `RetryData` `{hive,key,name}`.
  - `SystemActions.RestartExplorer(IProcessEnder ender, Action<string> startAsUser, string windowsDir)` – harmless `SystemAction`, Title „Explorer neu starten“; kills `explorer` processes whose ExePath is `<windowsDir>\explorer.exe`, waits 3 s, starts it via `startAsUser` when none came back (AppServices: `p => UserProcess.StartAsUser(p, "", user.Sid)`).
  - OptimizeViewModel: group order gets `ContextMenu` with hint „Windows 11 zeigt viele dieser Einträge erst unter „Weitere Optionen anzeigen“. Einträge aus App-Paketen (neues Menü) lassen sich so nicht abschalten und fehlen hier.“; ctor `OptimizeViewModel(…, ChangeGate gate, ActionRunner? actions = null, SystemAction? restartExplorer = null)`; after an apply with ≥ 1 applied `shellext` change: `dialogs.Ask("Explorer neu starten", "Damit die Änderung sofort wirkt, startet WinWächter den Explorer neu. Offene Explorer-Fenster schließen sich.", "Neu starten", "Später")` → `actions.RunAsync` (outside the gate held by the apply).
  - AppServices: scanner added to the engine scanner list; `ShellExtChange.Register`.

- [ ] **Step 1: Write failing tests**: `Third_party_handlers_and_verbs_are_listed_with_place`, `Windows_microsoft_protected_and_own_entries_are_hidden`, `Nothing_is_preselected`, `Block_apply_and_undo_remove_exactly_the_value`, `Legacy_disable_apply_and_undo`, `Already_blocked_is_not_offered`, `FromRecord_rejects_unlisted` (other key; `shell\..\x`; HKCU Blocked), `Explorer_restart_offered_after_apply` (ViewModelTests, RecordingDialogs Ask).
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~ShellExtTests"` → FAIL.
- [ ] **Step 3: Implement**.
- [ ] **Step 4: Run** class, `ViewModelTests`, `ViewSmokeTests` + full suite → PASS.
- [ ] **Step 5: Commit** `feat(optimize): context menu cleanup via the official block mechanism (shellext)`.

### Task 16: Kleinere Lücken (4.9)

**Files:**
- Modify: `src/WinWaechter.Core/Model/Finding.cs` (`string? BlockingProcess = null`), `src/WinWaechter.Core/Optimizer/Storage/CacheScanner.cs`, `src/WinWaechter.App/ViewModels/OptimizeViewModel.cs`, `src/WinWaechter.App/ViewModels/FindingItemViewModel.cs`, `src/WinWaechter.App/Views/OptimizeView.xaml`, `src/WinWaechter.App/ViewModels/ProgramsViewModel.cs`, `src/WinWaechter.App/Views/ProgramsView.xaml`, `src/WinWaechter.App/ViewModels/HistoryViewModel.cs`, `src/WinWaechter.App/Views/HistoryView.xaml`, `src/WinWaechter.App/ViewModels/ProtectViewModel.cs`, `src/WinWaechter.App/Views/ProtectView.xaml`, `src/WinWaechter.App/Services/AppServices.cs`, tests `CacheScannerTests.cs`, `ViewModelTests.cs`, `ProgramsViewModelTests.cs`

**Interfaces:**
- Consumes: Task 1 `CanRetry`/`RetryAsync`, Task 2 `ActionRunner`, Task 5 `SecurityActionSet.EnableRealTime`, Task 9 `IProcessEnder`, `ProcessGuard.MayEndBrowser`.
- Produces:
  - Blocked browser cache findings carry `BlockingProcess` = `msedge`/`chrome`/`opera`/`firefox`. `FindingItemViewModel.CanCloseBrowser`; `OptimizeViewModel.CloseBrowserCommand(FindingItemViewModel)`: Aktion Title „Browser schließen und leeren“, ConfirmText „WinWächter schließt {Browser} und prüft den Zwischenspeicher danach erneut.“ (not Final: nothing is deleted yet); closes windows of allowed processes, polls up to 10 s, then Ask „Erzwingen“ with „Offene Eingaben gehen verloren.“ → Kill; then `ScanCommand` (cache row comes back preselected). Ctor gets `IProcessEnder? ender = null, Func<ProcessGuardContext>? guard = null, Func<TimeSpan, CancellationToken, Task>? delay = null`.
  - `ProgramsViewModel.UnwantedCount` (Flag Pua or Miner and `CanUninstall`), `UninstallUnwantedText` „Alle unerwünschten entfernen (n)“, `UninstallUnwantedCommand`: one Confirm with the names, message `ProgramsViewModel.Irreversible + " " + ActionRunner.FinalNote`, final: true; then inside **one** `gate.RunAsync` each program through the existing uninstall core refactored to `RunUninstallAsync(InstalledProgram p, bool confirm)` (confirm false skips only the per-program question; signature check, user-rights start, „Ist die Deinstallation abgeschlossen?“ stay); afterwards one leftover search over all removed programs via `ShowLeftoversAsync(IReadOnlyList<InstalledProgram> removed)` (title „Reste von n Programmen“ when > 1); `FileLog.Info` per program.
  - `RecordViewModel.CanRetry` (`OptimizerEngine.CanRetry`); `HistoryViewModel.RetryCommand(RecordViewModel)` → confirm `$"„{Description}“ noch einmal anwenden?"` → gate → `engine.RetryAsync` → `ShowNewestAsync`.
  - `ProtectViewModel(…, ActionRunner? actions = null, SystemAction? enableRealTime = null)`: `ShowRealTimeFix` (status available, RealTime off), `EnableRealTimeCommand` (same SystemAction as 4.2; outcome link → offer Windows-Sicherheit); „Letzter Scan“ tile button „Jetzt scannen“ bound to the existing `QuickScanCommand`.

- [ ] **Step 1: Write failing tests**: `Blocked_browser_cache_names_its_process` (CacheScannerTests), `Close_browser_waits_then_offers_force_and_rescans`, `Uninstall_unwanted_confirms_once_with_list_and_runs_each` (FakeProgramActions: two Pua, one Miner, one protected → three runs, one confirm with final), `Uninstall_unwanted_hidden_when_none`, `Retry_failed_record_starts_new_session`, `Retry_not_offered_for_old_records_without_template`, `Protect_realtime_off_offers_fix`, `Protect_last_scan_tile_runs_quick_scan`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~ViewModelTests|FullyQualifiedName~ProgramsViewModelTests|FullyQualifiedName~CacheScannerTests"` → FAIL.
- [ ] **Step 3: Implement**.
- [ ] **Step 4: Run** classes, `ViewSmokeTests` (Optimize, Programs, History, Protect light/dark) + full suite → PASS.
- [ ] **Step 5: Commit** `feat: close browser to clean, remove all unwanted programs, retry failed changes, real-time fix on Schutz`.

### Task 17: Empfehlungs-Sammler (4.1 Core)

**Files:**
- Create: `src/WinWaechter.Core/Improvements/ImprovementCollector.cs`, `tests/WinWaechter.Tests/ImprovementCollectorTests.cs`

**Interfaces:**
- Consumes: `Finding`, `PrivacyRow`, `PerfSetting` + `ChangesFor`, `SecurityItem` (Change, Recommended, Act, Children), `BloatAppRow`, `SystemProtectChange`.
- Produces:
  - `enum ImprovementArea { Aufraeumen, AutostartHintergrund, DatenschutzWerbung, Sicherheit, Leistung }`; `static string ImprovementAreas.Title(ImprovementArea)` → „Aufräumen“, „Autostart & Hintergrund“, „Datenschutz & Werbung“, „Sicherheit“, „Leistung“.
  - `sealed record Improvement(string Id, ImprovementArea Area, string Title, string Why, IReadOnlyList<IChange> Changes, bool RunFirst = false)` (RunFirst: `sysprotect`).
  - `sealed record ActionSuggestion(string Id, string Title, string Why, SystemAction Action)`.
  - `sealed record ImprovementScan(IReadOnlyList<Improvement> Items, IReadOnlyList<ActionSuggestion> Actions, IReadOnlyList<string> Unchecked, DateTimeOffset At)`.
  - `sealed record ImprovementSource(string Name, Func<CancellationToken, Task<(IReadOnlyList<Improvement> Items, IReadOnlyList<ActionSuggestion> Actions)>> ReadAsync)`.
  - `sealed class ImprovementCollector(IReadOnlyList<ImprovementSource> sources)` – `Task<ImprovementScan> CollectAsync(IProgress<string>? p, CancellationToken ct)`: runs sources (progress „Prüfe {Name} …“), a failing source → `Unchecked` „{Name}: nicht prüfbar“ + `FileLog.Warn`; merges duplicates by (Change.Kind, Change.Target) keeping the first; orders by area then title.
  - `static class ImprovementMap`:
    - `FromFindings(IEnumerable<Finding> f)` – only `Preselected && Change != null && !IsProtectedVendor` (Heuristik: the scanner already preselects only `Verdaechtig` and not protected); Speicher → Aufraeumen; Autostart/Dienst/Aufgabe → AutostartHintergrund; Prozess/Verbindung → Sicherheit; Einstellung (shellext) never (not preselected). Why = first reason.
    - `FromPrivacy(IEnumerable<PrivacyRow>)` – `Setting.Recommended && Changes.Count > 0` → DatenschutzWerbung, Why = Explanation.
    - `FromPerformance(IEnumerable<PerfSetting>, Func<PerfSetting,int,IReadOnlyList<IChange>> changesFor)` – `Available && Recommended is { } r && r != Current` → `changesFor(s, r)`; group `background`/`browser-background` → AutostartHintergrund, else Leistung.
    - `FromSecurity(IEnumerable<SecurityItem>)` – items and children with `Recommended && Change != null` → Sicherheit (`sysprotect` → RunFirst); `Act != null && Light != Green` → ActionSuggestion (Updates installieren, Neustart, Echtzeitschutz, Definitionen) – never an Improvement.
    - `FromBloatApps(IEnumerable<BloatAppRow>, bool allowed)` – `Rule.Recommended` → Aufraeumen; none when `!allowed` (other account / no admin).

- [ ] **Step 1: Write failing tests**: `Only_recommendations_with_a_change_are_collected` (Info finding, preselected without change, protected vendor, shellext, privacy not recommended, perf without Recommended → all out), `Areas_are_mapped`, `Actions_are_separate_and_never_improvements` (Defender real-time Act → ActionSuggestion only), `Sysprotect_runs_first`, `Duplicates_by_kind_and_target_are_merged` (heuristic and optimizer offer the same autostart), `Failing_source_is_listed_unchecked`, `Bloat_apps_only_when_allowed`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~ImprovementCollectorTests"` → FAIL.
- [ ] **Step 3: Implement**.
- [ ] **Step 4: Run** class + full suite → PASS.
- [ ] **Step 5: Commit** `feat(improvements): one collector for all area recommendations`.

### Task 18: „Rechner in Ordnung bringen“ auf der Übersicht (4.1 UI)

**Files:**
- Create: `src/WinWaechter.App/ViewModels/ImproveViewModel.cs`, `tests/WinWaechter.Tests/ImproveViewModelTests.cs`
- Modify: `src/WinWaechter.App/ViewModels/OverviewViewModel.cs`, `src/WinWaechter.App/Views/OverviewView.xaml`, `src/WinWaechter.App/ViewModels/ShellViewModel.cs`, `src/WinWaechter.App/Services/AppServices.cs`, `tests/WinWaechter.Tests/ViewModelTests.cs`, `tests/WinWaechter.Tests/ViewSmokeTests.cs` (and every test constructing `OverviewViewModel`/`ShellViewModel`)

**Interfaces:**
- Consumes: Task 17, Task 1 (`continueOnError`, `ChangeText.ConfirmLine`), Task 2 `ActionRunner`, Task 9 `DiskSpace.IsLow`.
- Produces:
  - `ImproveViewModel(Func<IProgress<string>?, CancellationToken, Task<ImprovementScan>> collect, OptimizerEngine engine, IDialogService dialogs, ActionRunner actions, bool canChange, ChangeGate gate, Func<DriveSpace?>? disk = null)`.
    - `CheckCommand` (button „Rechner in Ordnung bringen“) → collect → `Groups` (`ObservableCollection<ImprovementGroupViewModel(Title, Items)>`, `ImprovementItemViewModel`: Title, Why, `IsSelected` = true), `Suggestions` („Außerdem empfohlen“, `ActionSuggestionViewModel` with own `RunCommand` via ActionRunner), `UncheckedText`, `CheckedAt`.
    - `SelectedCount`, `ImproveText` „Jetzt verbessern (n)“, `ImproveCommand`: Confirm(„Rechner in Ordnung bringen“, „{n} Verbesserungen werden angewendet. Sie stehen danach im Verlauf und lassen sich dort als Ganzes oder einzeln rückgängig machen.“, „Jetzt verbessern“, one `ConfirmLine` per change) → one `gate.RunAsync`: selected RunFirst items → `engine.ApplyAsync("Computerschutz einschalten", …)`; the rest → `engine.ApplyAsync("Rechner in Ordnung bringen", …, continueOnError: true)`; summary `ResultText` „{applied} verbessert, {failed} übersprungen“ + `SkippedReasons` (Description + Error from the Failed records of both sessions); then `CheckCommand` again; `event ChangesApplied`.
    - `IsAllGood` / `AllGoodText` „Ihr Rechner ist in Ordnung (geprüft am dd.MM.yyyy HH:mm).“ when no items and no suggestions; `OpenCount` = items + suggestions (null before the first check); `LowDiskText`/`MakeSpaceCommand` (runs `CheckCommand`); everything locked without admin (check still runs).
  - `OverviewViewModel(OptimizeViewModel, ProtectViewModel, SecurityViewModel, UpdateViewModel, ImproveViewModel improve, Action<object> navigate)` – `Improve` property; „Prüfen“ button and `CheckCommand` removed (the new button checks); `OpenSuggestions`/`OpenSuggestionsText` from `improve.OpenCount` („–“ before the first check); tile commands `OpenProtectCommand`, `OpenSecurityCommand`, `OpenOptimizeCommand`, `OpenHistoryCommand` via `navigate`; Statement's suggestion sentence uses `OpenCount`.
  - `ShellViewModel(…, UpdateViewModel update, ImproveViewModel improve, bool isElevated)`; `improve.ChangesApplied` → `history.ShowNewestAsync()`.
  - AppServices sources: „Optimieren“ (`engine.ScanAsync(null, ct)` → `FromFindings`), „Schutz“ (heuristics scan on the pool → `FromFindings`), „Datenschutz“ (same scan as the page → `FromPrivacy`), „Leistung“ (`perfScanner.Scan()` + `ChangesFor` → `FromPerformance`), „Sicherheit“ (`new SecurityCheck(securitySources).RunAsync` → `FromSecurity`), „Vorinstallierte Apps“ (`BloatAppScanner.Scan` → `FromBloatApps(rows, canChange && ownAccount)`).

- [ ] **Step 1: Write failing tests** in `ImproveViewModelTests`: `Check_groups_recommendations_preselected`, `Improve_confirms_once_with_summary_and_one_session` (RecordingDialogs: one Confirm; store has one session „Rechner in Ordnung bringen“), `Sysprotect_runs_as_own_first_session`, `Failure_in_first_step_or_middle_continues_and_is_reported` (sysprotect fails, 2nd of 3 fails → 2 applied, 2 skipped with errors, both sessions exist), `Actions_never_in_the_collect_button` (suggestion change count 0; its own button runs the action), `All_good_text_with_date`, `Locked_without_admin`, `Partial_and_store_only_marked_in_confirm`. `ViewModelTests`: `Overview_tiles_navigate`, `Open_suggestions_counts_all_areas`, update the existing Overview tests to the new ctor. `ViewSmokeTests`: overview with list, suggestions, all-good state and low-disk band, light/dark.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~ImproveViewModelTests"` → FAIL.
- [ ] **Step 3: Implement** VM, overview view (one big button `Button.Large`, list in `SurfaceBox` with group headings, „Außerdem empfohlen“ below, fixed footer button), wiring.
- [ ] **Step 4: Run** class, `ViewModelTests`, `ViewSmokeTests` + full suite → PASS; build `src\WinWaechter.App` Debug; launch the Debug exe once with `WINWAECHTER_NO_DIALOGS=1`, press „Rechner in Ordnung bringen“ (check only, nothing applied), confirm no exception in `%LOCALAPPDATA%\WinWaechter-Dev\Logs`, close it.
- [ ] **Step 5: Commit** `feat(overview): „Rechner in Ordnung bringen“ – one check, one list, one apply`.

### Task 19: Version 0.5.0, CHANGELOG, Roadmap, Abschlussprüfung (no release)

**Files:**
- Modify: `Directory.Build.props` (`<Version>0.5.0</Version>`), `CHANGELOG.md` (`## 0.5.0`), `docs/roadmap.md`, `docs/design-notes.md` (new row/button patterns, if any)

- [ ] **Step 1:** Version `0.5.0`; `## 0.5.0` in `CHANGELOG.md` (German, user-facing, Sie-Form): „Rechner in Ordnung bringen“, Sicherheit behebt selbst, Leistungsbremsen beseitigen, Werbe-Apps, Windows-Werbung, Computerschutz, Browser-Hintergrund, Rechtsklick-Menü, kleinere Lücken; „endgültig“/„nur teilweise rückgängig“ named honestly.
- [ ] **Step 2:** Roadmap: section „0.5.0“ with spec/plan paths, test count, results of the read-only device checks (Tasks 6, 11, 13, 14: which service, package names, dropped candidates, Opera), open checklist below. Then „0.6.0 (Reihenfolge laut Nutzer)“: Programm-Updates (winget), Gerätezustand (Akku, SSD), Reparatur-Seite (SFC/DISM, Update-Reparatur, Netzwerk-Reset, Druckerwarteschlange, Symbol-Cache, Datenträgerprüfung), automatische Wartung (mit Benachrichtigungen), Speicherplatz-Karte; „Danach“: Sprachen DE/EN/TR.
- [ ] **Step 3: Run** full suite `& $DOTNET test $SLN` → all PASS (783 + new); `& $DOTNET build $SLN -c Release` without new warnings. Then the controller runs the single-task and whole-branch reviews as in 0.4.
- [ ] **Step 4: Commit** `chore: version 0.5.0, changelog, roadmap`.
- [ ] **Step 5: Device checklist for the owner** (spec §7, each item only with the owner's consent; nothing here is done by the implementer alone; release/publish only after the owner's approval):
  1. Computerschutz über WinWächter einschalten; danach klappt ein Wiederherstellungspunkt (kein 0x80070422 mehr).
  2. „Rechner in Ordnung bringen“ einmal komplett durchlaufen, Ergebnis ansehen, im Verlauf als Ganzes zurücknehmen.
  3. 4.5: je Kandidat Vorher/Nachher-Vergleich in den Windows-Einstellungen (Schalter umlegen, Wert lesen, zurücklegen); nicht passende Einträge vor dem Release entfernen; eine Werbe-Änderung anwenden und zurücknehmen.
  4. 4.2: eine Behebung (z. B. Firewall-Regel für ein lauschendes Programm) anwenden und zurücknehmen.
  5. 4.4: eine Werbe-App entfernen und zurücknehmen (bzw. Store-Knopf prüfen).
  6. 4.7: Edge-/Chrome-Hintergrund ausschalten, „verwaltet“-Hinweis prüfen, zurücknehmen; fremde `StartupBoostEnabled`-Richtlinie bleibt unangetastet.
  7. 4.8: einen Rechtsklick-Eintrag abschalten, „Explorer neu starten“, zurücknehmen.
  8. Updates installieren nur, wenn der Nutzer es will.
  9. Neue/geänderte Seiten hell/dunkel, 150 %, Tastatur ansehen.
