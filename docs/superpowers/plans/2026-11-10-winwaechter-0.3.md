# WinWächter 0.3.0 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a „Programme“ page (list, official uninstall, emergency „Erzwungen entfernen“, leftover search), a „Sicherheit“ traffic-light page, signed rule updates from GitHub, and polish (undo progress, Defender status text, sidebar icon) – released as 0.3.0.

**Architecture:** Same pattern as 0.1/0.2: Core readers return plain records, every system change is an `IChange` journalled by `OptimizerEngine` and undoable via `ChangeFactory`. New Core namespaces: `WinWaechter.Core.Programs` (inventory, uninstall planner/launcher, forced-removal planner, leftover finder, `RegKeyChange` kind `regkey`) and `WinWaechter.Core.Security.Checks` (sources behind interfaces, pure rule functions, `FirewallChange` kind `firewall`, orchestrator). Rules: `HostPolicy` becomes an instance, `RuleSet` gains version/union, a signed cache in `%ProgramData%\WinWaechter\Regeln` is loaded at start. App gets `ProgramsViewModel`/`ProgramsView` and `SecurityViewModel`/`SecurityView`.

**Tech Stack:** C# 13 / .NET 10 (`net10.0-windows`), WPF, CommunityToolkit.Mvvm, System.Management (already referenced), COM late binding via `Type.GetTypeFromProgID` + `InvokeMember`, P/Invoke, xUnit. No new packages.

**Spec:** `docs/superpowers/specs/2026-11-10-winwaechter-0.3-design.md` – **§7 overrides §1–4**. Builds on the 0.1 and 0.2 specs in the same folder. Design notes: `docs/design-notes.md`. Style reference: `docs/superpowers/plans/2026-11-09-winwaechter-0.2.md`.

## Global Constraints

- Branch `feature/v0.3` (checked out). Do not switch branches, do not push.
- `$DOTNET` = `C:\Users\tahsi\AppData\Local\Microsoft\dotnet\dotnet.exe`, always with `DOTNET_CLI_TELEMETRY_OPTOUT=1` and `DOTNET_NOLOGO=1`. Tests: `$DOTNET test tests\WinWaechter.Tests\WinWaechter.Tests.csproj` (`--filter "FullyQualifiedName~<Class>"` for one class). The full suite (~468 tests) stays green after every task.
- No new NuGet packages. No telemetry. UI copy German, sachlich, „Sie“-Anrede or impersonal, nicht alarmierend.
- Everything reversible via journal + Ablage/undo. The **only** irreversible action is the vendor's official uninstaller, and the UI says so: „Das Deinstallationsprogramm des Herstellers wird gestartet. Das lässt sich in WinWächter nicht rückgängig machen.“
- Protected list (`RuleSet.Classify` → `Protected`) wins: no uninstall button, no forced removal, no leftover offer for protected programs/folders.
- Elevated launches (spec §7.1): WinWächter itself starts an uninstaller only when the entry is from HKLM **and** the file is `%SystemRoot%\System32\msiexec.exe` or an `.exe` under `%ProgramFiles%`, `%ProgramFiles(x86)%`, `%SystemRoot%` or `%ProgramData%\Package Cache`. Never `cmd`, `rundll32`, `regsvr32`, `powershell`, `pwsh`, `wscript`, `cscript`, `mshta`, scripts, shells or `QuietUninstallString`; no `UseShellExecute`. Everything else → open `ms-settings:appsfeatures`.
- URIs opened with `UseShellExecute=true` only from the fixed list `SystemLinks.Allowed` (Task 3).
- No `reg.exe` in new code. `regkey` changes only for keys that are **direct** children of `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall`, `HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall`, `HKU\<SID>\Software\Microsoft\Windows\CurrentVersion\Uninstall` – checked in `Apply`, `Undo` and `FromRecord`. `firewall` changes only set `FwPolicy2.FirewallEnabled[profile]` for profile 1, 2 or 4, and „Einschalten“ is never offered while a non-Microsoft firewall is active.
- Remote rules: signature over `"winwaechter-rules\n"` + exact file bytes (ECDSA P-256/SHA-256, IeeeP1363, Base64, key `UpdateKeys.PublicKeyPem`), max 1 MB, **union only** with the bundled lists, `privacy` never from the network, effective from the next start.
- Services: only „auf Manuell“ (never disable, never stop). Folders: `MoveToAblageChange.ForFolder` (Kind `folder`, never auto-purged).
- Tests use fakes and temp dirs; no test uninstalls anything, changes the firewall, or deletes/creates real registry keys (read-only smoke reads are allowed; the launcher smoke test runs `cmd.exe`/`ping.exe` from `System32` only).
- Version `0.3.0` in `Directory.Build.props` + `## 0.3.0` in `CHANGELOG.md` (Task 10).
- Every commit message ends with the trailer line `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Design per `docs/design-notes.md`: existing tokens only, traffic light uses `Good`/`Watch`/`Alarm` and `InkMuted` for Grau, `Button.Final` for „Deinstallieren“, `DynamicResource` colours only.

## Review Focus

1. **Cached rules tampered, oversized or planted** (file edited after download, > 1 MB, sig missing, not owned by Administrators/SYSTEM) → start uses bundled rules, never throws. Test `Load_ignores_tampered_oversized_or_untrusted_cache` in Task 1.
2. **Uninstall entry disappears between listing and click** (user removed it via Windows-Einstellungen meanwhile) → `RegKeyChange.Apply` writes no backup and fails with a clear message; the forced-removal plan says „Eintrag existiert nicht mehr“. Tests `Apply_on_missing_key_writes_nothing` (Task 4) and `Forced_plan_for_vanished_entry_has_no_changes` (Task 4).
3. **Odd `InstallLocation` values** (quoted, trailing `\`, different case, `%ProgramFiles%\Foo`, exactly `C:\Program Files\`, `C:\Program Files\Common Files\X`) → normalized before root/overlap checks; root and Common Files refused. Test `Folder_rules_normalize_and_refuse_roots` in Task 4.
4. **A security source throws or hangs** (SecurityCenter2 namespace missing, WMI blocks, WUA COM hangs) → only that row is Grau „nicht prüfbar“, all other rows appear. Tests `Failing_or_hanging_source_gives_grey_row_only` and `Update_search_timeout_gives_grey` in Task 7.
5. **Browser profile files locked, corrupt or with unresolvable `__MSG_…__` names** (browser running, half-written JSON, missing locale) → that file is skipped / name falls back to the extension id; no exception. Test `Corrupt_or_locked_preferences_are_skipped_and_names_fall_back` in Task 7.

---

## File Structure

```
.gitattributes (new)                                 rules/rules.json(.sig) -text (bytes identical on raw.githubusercontent.com)
rules/rules.json                                     + "rulesVersion": 1, "minAppVersion": "0.3.0"
build-release.ps1, publish-rules.ps1 (new)           sign/verify rules; bump + sign + commit + push (asks)
tools/ReleaseTool/ReleaseCli.cs, ReleaseSigner.cs     sign-rules, verify-rules, verify-rules-embedded
src/WinWaechter.Core/
  Update/HostPolicy.cs                               instance with Updates / Rules
  Update/PolicyFetch.cs (new)                        shared bounded GET with manual, policy-checked redirects
  Update/UpdateChecker.cs, UpdateDownloader.cs        use HostPolicy.Updates / PolicyFetch
  Update/RulesFetcher.cs, RulesUpdater.cs (new)
  Rules/RuleSet.cs                                   RulesVersion, MinAppVersion, Parse, UnionWith
  Rules/RulesSignature.cs, RulesStore.cs, RulesLoader.cs (new)   + RulesInfo, RulesAcceptance
  Model/Finding.cs                                   + ExePath (optional, last)
  Sys/IRegistry.cs, WinRegistry.cs                   + CreateKey, DeleteKeyTree
  Sys/IProcessList.cs                                RunningProcess + Pid (optional, last)
  Sys/SystemLinks.cs (new)                           allow-list of URIs
  Sys/Native/JobNative.cs (new)                      CreateProcessW suspended, job object
  Programs/ (new)  UninstallRoots, InstalledProgram, ProgramInventory, UninstallCommandParser, UninstallPlanner,
                   UninstallLauncher (IUninstallLauncher, JobUninstallLauncher, UninstallExit), RegKeyChange,
                   ProgramFolderRules, ProgramTraces, ForcedRemovalPlanner, UninstallLeftoverFinder, ProgramService
  Optimizer/Storage/InstalledProgramIndex.cs         uses UninstallRoots
  Optimizer/Autostart|Services|Tasks/*Scanner.cs     fill Finding.ExePath
  Optimizer/OptimizerEngine.cs                       undo progress (UndoProgress, IProgressUndo)
  Optimizer/Storage/MoveToAblageChange.cs            implements IProgressUndo
  Changes/ChangeFactory.cs                           + regkey
  Security/Defender/DefenderClient.cs                StatusText(id, active)
  Security/Heuristics/TcpTable.cs                    + IListenerSource, ListeningPort
  Security/Checks/ (new)  SecurityItem, Sources (interfaces + Wmi/COM readers), FirewallChange, SecurityChecks,
                          WindowsUpdateSearch, BrowserExtensionScanner, SecurityCheck
src/WinWaechter.App/
  Services/IDialogService.cs, WpfDialogService.cs    Ask (default impl), ISystemShell.OpenLink
  Services/AppServices.cs, AppEnvironment.cs, App.xaml.cs   wiring, RulesDir, startup checks
  ViewModels/ProgramsViewModel.cs, Views/ProgramsView.xaml(.cs) (new)
  ViewModels/SecurityViewModel.cs, Views/SecurityView.xaml(.cs) (new)
  ViewModels/ShellViewModel.cs, OverviewViewModel.cs, HistoryViewModel.cs, SettingsViewModel.cs
  Views/ShellWindow.xaml, OverviewView.xaml, HistoryView.xaml, SettingsView.xaml, MessageDialog.xaml(.cs)
  WinWaechter.App.csproj                             icon PNG as Resource
tests/WinWaechter.Tests/
  RulesUpdateTests, ProgramInventoryTests, UninstallTests, RegKeyTests, ProgramLeftoverTests,
  SecurityCoreTests, SecurityNetTests, ProgramsViewModelTests, SecurityViewModelTests, PolishTests (new);
  Fakes/FakeFirewall.cs, FakeSecuritySources.cs, FakeUninstallLauncher.cs (new);
  Fakes/FakeRegistry.cs, UpdateTests.cs, DefenderParsingTests.cs, ViewSmokeTests.cs, ViewModelTests.cs,
  PrivacyViewModelTests.cs (modified)
```

---

### Task 1: HostPolicy instances, signed rules (fetch, store, load, union), ReleaseTool and scripts

**Files:**
- Modify: `src/WinWaechter.Core/Update/HostPolicy.cs`, `Update/UpdateChecker.cs`, `Update/UpdateDownloader.cs`, `Rules/RuleSet.cs`, `rules/rules.json`, `tools/ReleaseTool/ReleaseCli.cs`, `tools/ReleaseTool/ReleaseSigner.cs`, `build-release.ps1`, `tests/WinWaechter.Tests/UpdateTests.cs` (HostPolicy calls)
- Create: `src/WinWaechter.Core/Update/PolicyFetch.cs`, `Update/RulesFetcher.cs`, `Update/RulesUpdater.cs`, `Rules/RulesSignature.cs`, `Rules/RulesStore.cs`, `Rules/RulesLoader.cs`, `publish-rules.ps1`, `.gitattributes`
- Test: `tests/WinWaechter.Tests/RulesUpdateTests.cs`

**Interfaces:**
- Consumes: `BoundedRead.CopyAsync`, `UpdateHttp.CreateClient`, `UpdateDownloadException`, `SemVer`, `UpdateKeys.PublicKeyPem`, `FileOwner`.
- Produces:
```csharp
public sealed class HostPolicy
{
    public HostPolicy(IEnumerable<string> hosts);
    public bool IsAllowed(Uri? u);                 // same rules as today: https, exact host, no userinfo, default port
    public static HostPolicy Updates { get; }      // api.github.com, github.com, objects.githubusercontent.com, release-assets.githubusercontent.com
    public static HostPolicy Rules { get; }        // Updates hosts + raw.githubusercontent.com
}
internal static class PolicyFetch
{   // GET with manual redirects (max 5), every hop checked against policy, Content-Length and stream bounded by max
    public static Task FetchAsync(HttpClient http, Uri url, HostPolicy policy, Stream dst, long max, IProgress<double>? p, CancellationToken ct);
}   // throws UpdateDownloadException with the existing German messages; UpdateDownloader.FetchAsync delegates to it

// RuleSet (new members)
public int RulesVersion { get; }                   // 0 when the field is missing
public string? MinAppVersion { get; }              // raw string; null when missing
public static RuleSet Parse(byte[] json);           // InvalidDataException on any error; Load(path) = Parse(File.ReadAllBytes(path))
public RuleSet UnionWith(RuleSet other);            // lists unioned (protected, expendableAutostart, pua, minerNames,
                                                    // minerCommandLine, poolPorts, leftoverIgnore), Privacy = this.Privacy,
                                                    // RulesVersion = Max, MinAppVersion = other's

public static class RulesSignature
{
    public const string Domain = "winwaechter-rules\n";
    public static byte[] Payload(byte[] json);                          // UTF-8 Domain bytes + json
    public static bool Verify(byte[] json, string sigBase64, string publicKeyPem);   // false on any error, sig must be 64 bytes, key 256 bit
}
public enum RulesAcceptance { Accepted, BadSignature, Invalid, NotNewer, AppTooOld }
public sealed record CachedRules(RuleSet Rules, DateTime StoredAtUtc);
public sealed class RulesStore
{
    public const long MaxBytes = 1024 * 1024;
    public const string JsonName = "rules.json", SigName = "rules.json.sig";
    public RulesStore(string dir, string publicKeyPem, Func<string, bool>? isTrusted = null, Action<string>? claimFile = null);
    public string Dir { get; }
    public CachedRules? LoadVerified();             // null (logged) when missing, > MaxBytes, untrusted owner, bad sig, invalid JSON
    public RulesAcceptance TryAccept(byte[] json, string sigBase64, int currentVersion, SemVer app);   // writes both files only on Accepted
}
public sealed record RulesInfo(int Version, bool FromUpdate, DateTime? StoredAtUtc)
{
    public string DisplayText { get; }   // FromUpdate: $"Regeln: Version {v} vom {local:dd.MM.yyyy}", else $"Regeln: Version {v} (mitgeliefert)"
}
public static class RulesLoader
{
    public static (RuleSet Rules, RulesInfo Info) Load(RuleSet bundled, RulesStore store, SemVer app);   // never throws
}
public sealed class RulesFetcher
{
    public static readonly Uri JsonUrl = new("https://raw.githubusercontent.com/yIrtIcIkurt/winwaechter/main/rules/rules.json");
    public static readonly Uri SigUrl  = new("https://raw.githubusercontent.com/yIrtIcIkurt/winwaechter/main/rules/rules.json.sig");
    public RulesFetcher(HttpMessageHandler handler);                     // via UpdateHttp.CreateClient, timeout 30 s
    public Task<(byte[] Json, string Sig)> FetchAsync(CancellationToken ct);   // both with PolicyFetch(HostPolicy.Rules, RulesStore.MaxBytes)
}
public sealed record RulesUpdateResult(RulesAcceptance? Acceptance, string Message);
public sealed class RulesUpdater
{
    public RulesUpdater(RulesFetcher fetcher, RulesStore store, int currentVersion, SemVer app);
    public Task<RulesUpdateResult> RunAsync(CancellationToken ct);   // never throws except OperationCanceledException
}
// ReleaseSigner
public static string SignRules(ECDsa key, string rulesJsonPath);     // writes rulesJsonPath + ".sig" (Base64, ASCII), returns its path
public static string SignRules(string keyPemPath, string rulesJsonPath);
// ReleaseCli commands: sign-rules <keyPem> <rulesJson> | verify-rules <pubPem> <rulesJson> <sig> | verify-rules-embedded <rulesJson> <sig>
```
`RulesUpdater` messages (exact): Accepted `$"Neue Regeln (Version {v}) geladen. Sie gelten ab dem nächsten Start."`; NotNewer `"Die Regeln sind aktuell."`; BadSignature `"Die geladenen Regeln sind nicht gültig signiert und wurden verworfen."`; Invalid `"Die geladenen Regeln sind fehlerhaft und wurden verworfen."`; AppTooOld `$"Die neuen Regeln brauchen WinWächter {min} oder neuer."`; any network/`UpdateDownloadException` → Acceptance null, `$"Regeln konnten nicht geladen werden: {ex.Message}"`. After an Accepted run the updater's current version becomes the new one.
`RulesStore.TryAccept` order: Verify → Parse (Invalid) → `RulesVersion <= currentVersion` (NotNewer) → `MinAppVersion` missing/unparsable (Invalid) or `> app` (AppTooOld) → write `.tmp` files, `File.Move(overwrite)`, `claimFile` each. `LoadVerified` reads with `FileShare.Read`, length check before reading, `isTrusted` on both files.
`RulesLoader.Load`: cached valid **and** `cached.RulesVersion > bundled.RulesVersion` **and** `MinAppVersion <= app` → `(bundled.UnionWith(cached.Rules), new RulesInfo(cached.RulesVersion, true, StoredAtUtc))`; else `(bundled, new RulesInfo(bundled.RulesVersion, false, null))`. Matcher union: distinct by (name, publisher, path) OrdinalIgnoreCase.
`rules.json`: add `"rulesVersion": 1, "minAppVersion": "0.3.0",` as the first two fields. `.gitattributes`: `rules/rules.json -text` and `rules/rules.json.sig -text`, then `git add --renormalize .gitattributes rules/rules.json` (raw.githubusercontent.com must serve exactly the signed bytes). Do **not** create `rules.json.sig` here (the private key is used only by the scripts in Task 11).
`build-release.ps1`: after the tests (step 2) add step „2b. Regeln signieren“: `ReleaseTool sign-rules $KeyPath rules\rules.json` then `verify-rules-embedded rules\rules.json rules\rules.json.sig` (Assert-Ok each), and print `"rules.json.sig aktualisiert – bitte mit committen."`.
`publish-rules.ps1` (param `-KeyPath` default as in build-release): refuse unless `git rev-parse --abbrev-ref HEAD` is `main` and the work tree is clean; bump `"rulesVersion": n` → n+1 by regex on the raw text (rest of the bytes unchanged, UTF-8 without BOM); `sign-rules`; `verify-rules-embedded`; show `git diff --stat`; `Read-Host "Regeln Version n+1 committen und pushen? (j/n)"` – only on `j`: `git add rules/rules.json rules/rules.json.sig`, `git commit -m "rules: version n+1"` (with the trailer), `git push origin main`.

- [ ] **Step 1: Write the failing tests** (`RulesUpdateTests.cs`; temp dir; `ECDsa.Create(nistP256)` test key; a `FakeHandler` like in `UpdateTests`; helper `Rules(string extraFields, string protectedJson = "[{\"name\":\"Base\"}]")` building a minimal valid JSON):
  - `HostPolicy_instances_have_separate_host_lists`: `Updates.IsAllowed("https://raw.githubusercontent.com/x") == false`; `Rules.IsAllowed` same URI true and `https://api.github.com/x` true; both false for `http://raw.githubusercontent.com/x`, `https://u:p@raw.githubusercontent.com/x`, `https://raw.githubusercontent.com:8443/x`, `https://raw.githubusercontent.com.evil.com/x`. Update `UpdateTests.HostPolicy_rejects_http_and_foreign_hosts` to `HostPolicy.Updates`.
  - `RuleSet_reads_version_and_min_app`: `RuleSet.Default.RulesVersion == 1`, `MinAppVersion == "0.3.0"`; minimal JSON without fields → `0` and `null`.
  - `Union_adds_lists_and_never_removes`: remote with protected `{"name":"Foo Remote"}`, pua `{"name":"BadTool"}`, minerNames `["xmrnew"]`, minerCommandLine `["--new-pool"]`, poolPorts `[12345]`, leftoverIgnore `["RemoteKeep"]` → `u = RuleSet.Default.UnionWith(remote)`: `Classify(new ItemFacts("Foo Remote", null, null, null)) == Protected`; `Classify(new ItemFacts("Dell Update", null, null, null)) == Protected`; `Classify(new ItemFacts("BadTool", null, null, null)) == Pua`; `PoolPorts` contains 3333 and 12345; `MinerNames` contains `"xmrnew"` and every bundled name; `IsMinerCommandLine("x --new-pool")` and `IsMinerCommandLine("stratum+tcp://a")`; `IsLeftoverIgnored("RemoteKeep")` and `IsLeftoverIgnored("Common Files")`.
  - `Union_never_takes_privacy_from_remote`: remote has `privacy` with one row id `evil` (HKCU `Software\Evil` `X`) → `u.Privacy.Select(p => p.Id)` equals `RuleSet.Default.Privacy` ids; `u.FindPrivacyValue(Hive.CurrentUser, @"Software\Evil", "X") is null`.
  - `Signature_uses_domain_prefix`: `SignRules(key, path)` → `RulesSignature.Verify(bytes, sig, pub)` true; signature of `key.SignData(bytes, SHA256, IeeeP1363)` without prefix → false; one byte flipped → false; `"%%%"` → false; Base64 of 63 bytes → false.
  - `Store_accepts_only_valid_newer_compatible`: current 1, app 0.3.0: v2/min 0.3.0 good sig → `Accepted`, both files exist; same bytes again with current 2 → `NotNewer`; v3/min 0.4.0 → `AppTooOld`; v3 bad sig → `BadSignature`; v3 good sig but `"protected": []` → `Invalid`; v3 without `minAppVersion` → `Invalid`; after all rejects the stored `rules.json` still has version 2.
  - `Load_prefers_higher_valid_cached_version_and_unions`: bundled v1, cache v2 with protected `Foo Remote` → `Info.Version == 2`, `FromUpdate`, `Classify(Foo Remote) == Protected`, `DisplayText` starts with `"Regeln: Version 2 vom "`; cache v1 → bundled, `DisplayText == "Regeln: Version 1 (mitgeliefert)"`.
  - `Load_ignores_tampered_oversized_or_untrusted_cache` (Review Focus 1): each case returns bundled (`FromUpdate == false`) without throwing: cached `rules.json` edited after accept; a validly signed v2 file padded with spaces to `MaxBytes + 1`; store built with `isTrusted: _ => false`; `.sig` deleted.
  - `Load_rejects_cache_needing_newer_app`: cache v2/min 9.0.0 written directly (valid sig) → bundled.
  - `Fetcher_reads_both_files_with_limit_and_host_policy`: handler serving `JsonUrl`/`SigUrl` → bytes equal, sig text equal; `JsonUrl` → 302 to `https://objects.githubusercontent.com/r` → followed; 302 to `https://evil.com/r` → `UpdateDownloadException`; body of `MaxBytes + 1` bytes without Content-Length → `UpdateDownloadException`.
  - `Updater_reports_messages`: accepted → exact Accepted text for version 2; handler throwing `HttpRequestException("weg")` → `Acceptance == null`, message starts with `"Regeln konnten nicht geladen werden"`.
  - `ReleaseCli_sign_rules_and_verify_rules`: key pair via `ReleaseSigner.GenerateKeyPair(dir)`; `Run(["sign-rules", priv, rules])` → 0, `rules + ".sig"` exists; `Run(["verify-rules", pub, rules, rules + ".sig"])` → 0; after editing one byte → 1.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~RulesUpdateTests|FullyQualifiedName~UpdateTests"` → FAIL (build errors).
- [ ] **Step 3: Implement** the members above; `UpdateChecker`/`UpdateDownloader` switch to `HostPolicy.Updates` and `PolicyFetch`; scripts, `.gitattributes`, `rules.json` fields.
- [ ] **Step 4: Run** filter + full suite → PASS. `powershell -NoProfile -Command "[ScriptBlock]::Create((Get-Content -Raw publish-rules.ps1)) | Out-Null"` → no parse error (same for `build-release.ps1`).
- [ ] **Step 5: Commit** `feat(rules): signed rule updates with union-only merge; HostPolicy instances; sign-rules tooling`.

---

### Task 2: Program inventory

**Files:**
- Create: `src/WinWaechter.Core/Programs/UninstallRoots.cs`, `Programs/InstalledProgram.cs`, `Programs/ProgramInventory.cs`
- Modify: `src/WinWaechter.Core/Optimizer/Storage/InstalledProgramIndex.cs` (use `UninstallRoots.All`)
- Test: `tests/WinWaechter.Tests/ProgramInventoryTests.cs`

**Interfaces:**
- Consumes: `IRegistry.SubKeyNames/Get/GetKind`, `RuleSet.Classify`, `ItemFacts`, `PathNormalizer.NormalizePath`.
- Produces:
```csharp
namespace WinWaechter.Core.Programs;
public static class UninstallRoots
{
    public const string Native = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    public const string Wow64  = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";
    public const string User   = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    public static readonly IReadOnlyList<(Hive Hive, string Key)> All;   // (LM, Native), (LM, Wow64), (CU, User)
    public static bool IsDirectChild(Hive h, string key);   // key = <root>\<name>, name non-empty, no '\', not "." / "..", OrdinalIgnoreCase
}
public enum ProgramFlag { None, Protected, Pua, Miner }
public sealed record InstalledProgram(Hive Hive, string RootKey, string KeyName, string DisplayName, string? Publisher,
    string? Version, DateTime? InstallDate, long? SizeBytes, string? UninstallString, string? InstallLocation,
    string? DisplayIcon, bool WindowsInstaller, ProgramFlag Flag)
{
    public string RegistryKey => RootKey + "\\" + KeyName;
    public string Id { get; }              // $"{(Hive == CurrentUser ? "HKCU" : "HKLM")}\\{RegistryKey}"
    public bool IsMachineEntry => Hive == Hive.LocalMachine;
}
public sealed class ProgramInventory
{
    public ProgramInventory(IRegistry reg, RuleSet rules);
    public IReadOnlyList<InstalledProgram> Read();        // sorted by DisplayName (CurrentCulture, IgnoreCase); unreadable subkeys skipped
    internal static string? IconExe(string? displayIcon); // strips quotes and ",<n>" suffix; null when empty
}
```
Hidden: `SystemComponent` DWord 1, any `ParentKeyName` value, empty/missing `DisplayName`, `ReleaseType` in `Update`, `Hotfix`, `Security Update` (OrdinalIgnoreCase). `InstallDate`: `yyyyMMdd` via `TryParseExact` (invariant), else null. `SizeBytes` = `EstimatedSize` (DWord, KB) × 1024 when > 0, else null. `WindowsInstaller` = DWord 1. Flag: `Classify(new ItemFacts(DisplayName, IconExe(DisplayIcon) ?? (InstallLocation is {} l ? l.Trim('"').TrimEnd('\\') + "\\" : null), Publisher, null))` → Protected/Pua/Miner, else None.

- [ ] **Step 1: Write the failing tests** (`FakeRegistry`, `RuleSet.Default`):
  - `Reads_visible_entries_from_all_three_roots`: one entry under each root → 3 programs, `Hive`/`RootKey` per root, `Id` of the HKCU one starts with `"HKCU\\Software\\Microsoft"`.
  - `Hides_system_components_updates_and_nameless`: entries with `SystemComponent=1`, `ParentKeyName="X"`, no `DisplayName`, `ReleaseType="Security Update"`, plus one normal → only the normal one.
  - `Parses_date_size_and_msi_flag`: `InstallDate="20240131"`, `EstimatedSize=2048` (DWord), `WindowsInstaller=1` → `InstallDate == new DateTime(2024,1,31)`, `SizeBytes == 2_097_152`, `WindowsInstaller`; `InstallDate="31.01.2024"` → null; missing `EstimatedSize` → null.
  - `Flags_from_rules`: Publisher `"Dell Inc."` → `Protected`; DisplayName `"Microsoft Edge"`, Publisher `"Microsoft Corporation"` → `Protected`; DisplayIcon `"\"C:\\Program Files\\Lexware\\x.exe\",0"` → `Protected`; DisplayName matching a bundled `pua` name (take `RuleSet.Default` pua entry used in `RuleSetTests`) → `Pua`; `"Foo Tool"` → `None`.
  - `IconExe_strips_quotes_and_index`: `"\"C:\\a b\\x.exe\",0"` → `C:\a b\x.exe`; `"C:\\x.ico"` → `C:\x.ico`; `""` → null.
  - `IsDirectChild_only_for_children_of_the_three_roots`: `(LM, Native + "\\{A}")` true; `(LM, Native)` false; `(LM, Native + "\\A\\B")` false; `(LM, Native + "\\..")` false; `(CU, Native + "\\A")` false (HKCU uses `User` path, same text but must match the pair); `(CU, User + "\\A")` true; `(LM, @"SOFTWARE\Evil\A")` false.
  - `InstalledProgramIndex` tests (`LeftoverTests`) still pass unchanged.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~ProgramInventoryTests"` → FAIL.
- [ ] **Step 3: Implement** the three files; `InstalledProgramIndex` iterates `UninstallRoots.All`.
- [ ] **Step 4: Run** filter + full suite → PASS.
- [ ] **Step 5: Commit** `feat(programs): read installed programs with flags`.

---

### Task 3: Uninstall command parser, planner, job-object launcher, system links

**Files:**
- Create: `src/WinWaechter.Core/Programs/UninstallCommandParser.cs`, `Programs/UninstallPlanner.cs`, `Programs/UninstallLauncher.cs`, `Sys/Native/JobNative.cs`, `Sys/SystemLinks.cs`
- Create: `tests/WinWaechter.Tests/Fakes/FakeUninstallLauncher.cs`
- Test: `tests/WinWaechter.Tests/UninstallTests.cs`

**Interfaces:**
- Consumes: `InstalledProgram` (Task 2), `CleanerPaths` (SystemRoot, ProgramData, ProgramFiles, ProgramFilesX86), `IFileOps.Exists`.
- Produces:
```csharp
public sealed record UninstallCommand(string Exe, string Arguments);
public static class UninstallCommandParser
{
    public static readonly IReadOnlySet<string> RefusedHosts;   // cmd.exe rundll32.exe regsvr32.exe powershell.exe pwsh.exe
                                                                // wscript.exe cscript.exe mshta.exe (OrdinalIgnoreCase file names)
    public static UninstallCommand? Parse(string? commandLine, Func<string, bool> exists);
        // env vars expanded; quoted → text inside quotes; unquoted → longest prefix ending ".exe" (followed by blank/end)
        // that exists, else the first such prefix; Arguments = rest trimmed; null when no ".exe" program or not rooted
    public static string? MsiProductCode(InstalledProgram p);
        // WindowsInstaller → KeyName if GUID; else GUID from UninstallString matching msiexec(.exe)? /I or /X; GUID regex
        // ^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$
}
public enum UninstallRoute { Launch, OpenSettings }
public sealed record UninstallPlan(UninstallRoute Route, UninstallCommand? Command, string Reason, bool UninstallerMissing);
public static class UninstallPlanner
{
    public static UninstallPlan Plan(InstalledProgram p, CleanerPaths paths, Func<string, bool> exists);
}
public interface IUninstallLauncher { Task<int> RunAsync(UninstallCommand cmd); }   // exit code of the started process
public sealed class JobUninstallLauncher : IUninstallLauncher;   // CreateProcessW(CREATE_SUSPENDED) → job → ResumeThread;
                                                                 // waits until the job has 0 active processes (poll 500 ms),
                                                                 // no KILL_ON_JOB_CLOSE; Win32 errors → InvalidOperationException (German)
public sealed record UninstallOutcome(int ExitCode, bool Cancelled, bool RestartRequired);
public static class UninstallExit { public static UninstallOutcome Interpret(int code); }   // 1602 → Cancelled; 3010, 1641 → RestartRequired
public static class SystemLinks
{
    public const string AppsFeatures = "ms-settings:appsfeatures", WindowsUpdate = "ms-settings:windowsupdate",
        RemoteDesktop = "ms-settings:remotedesktop", DeviceEncryption = "ms-settings:deviceencryption",
        DefenderThreat = "windowsdefender://threat", DefenderAppBrowser = "windowsdefender://appbrowser";
    public static IReadOnlySet<string> Allowed { get; }   // exactly these six
    public static bool IsAllowed(string? uri);           // exact, OrdinalIgnoreCase
}
// Tests: FakeUninstallLauncher { List<UninstallCommand> Calls; int ExitCode = 0; }
```
Planner rules (in this order; `Reason` texts German, Sie-Form):
1. MSI product code found → `Launch` with `Command(Path.Combine(paths.SystemRoot, "System32", "msiexec.exe"), "/X" + guid)` when `IsMachineEntry`; HKCU → `OpenSettings`.
2. `Parse(UninstallString)` null → `OpenSettings`, „Der Deinstallationsbefehl kann nicht sicher gestartet werden.“
3. File name in `RefusedHosts` or extension ≠ `.exe` → `OpenSettings`, same reason.
4. `!exists(exe)` → `OpenSettings`, `UninstallerMissing = true`, „Das Deinstallationsprogramm fehlt.“
5. `!IsMachineEntry` or full path (after `Path.GetFullPath`) not under `ProgramFiles`, `ProgramFilesX86`, `SystemRoot` or `ProgramData\Package Cache` (prefix + `\`, OrdinalIgnoreCase) → `OpenSettings`, „Dieses Programm wurde für Ihr Benutzerkonto installiert oder liegt in einem Ordner, den auch Programme ohne Adminrechte ändern können.“
6. Else `Launch`, Reason `""`.
`JobNative`: `CreateJobObjectW`, `AssignProcessToJobObject`, `QueryInformationJobObject` (class 1, `JOBOBJECT_BASIC_ACCOUNTING_INFORMATION.ActiveProcesses`), `CreateProcessW` (`lpApplicationName` = exe, `lpCommandLine` = `"\"exe\" " + args`, `CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT`, `bInheritHandles=false`), `ResumeThread`, `GetExitCodeProcess`, `WaitForSingleObject`, `CloseHandle` (SafeHandles). If assigning to the job fails, terminate the suspended process and throw.

- [ ] **Step 1: Write the failing tests** (`UninstallTests.cs`; temp dir standing in for `ProgramFiles` etc. via a `CleanerPaths` record; `exists` = `File.Exists`):
  - `Parse_quoted_unquoted_and_args`: `"\"C:\\P F\\u.exe\" /S"` → `("C:\P F\u.exe", "/S")`; unquoted `<tmp>\Foo Bar\unins000.exe /SILENT` with that file existing → exe = full path, args `/SILENT`; `<tmp>\a.exe b.exe x` where only `a.exe b.exe` exists → that one; `"relative.exe"` → null; `"C:\\x\\setup.bat"` → null.
  - `Msi_product_code_from_key_or_string`: `WindowsInstaller` + KeyName `{11111111-2222-3333-4444-555555555555}` → that GUID; UninstallString `MsiExec.exe /I{…}` → GUID; `MsiExec.exe /I{bad}` → null.
  - `Msi_entry_launches_msiexec_with_X`: HKLM MSI entry → `Launch`, `Command.Exe == <SystemRoot>\System32\msiexec.exe`, `Arguments == "/X{…}"`; same entry from HKCU → `OpenSettings`.
  - `Hklm_exe_under_program_files_launches`: `<pf>\Foo\uninst.exe` exists, HKLM → `Launch`.
  - `Package_cache_allowed_only_under_programdata` : `<programdata>\Package Cache\{x}\setup.exe /uninstall` → `Launch`; `<local>\Package Cache\{x}\setup.exe` → `OpenSettings`.
  - `Hklm_entry_with_appdata_uninstaller_opens_settings`: `<profile>\AppData\Local\Foo\uninst.exe` exists, HKLM → `OpenSettings`, `!UninstallerMissing`.
  - `Hkcu_entry_opens_settings_even_under_program_files`.
  - `Refused_hosts_open_settings`: `cmd.exe /c del x`, `rundll32.exe x.dll,Uninstall`, `powershell -File u.ps1`, `"C:\\x\\u.cmd"` → all `OpenSettings`.
  - `Missing_uninstaller_is_flagged`: `<pf>\Gone\uninst.exe` → `OpenSettings`, `UninstallerMissing == true`.
  - `Exit_codes`: `Interpret(1602).Cancelled`; `Interpret(3010).RestartRequired`; `Interpret(1641).RestartRequired`; `Interpret(0)` neither.
  - `SystemLinks_allow_only_fixed_list`: all six true; `"ms-settings:appsfeatures "` false (exact); `"https://evil.com"`, `"ms-settings:privacy"`, `"file:///C:/x.exe"`, null → false.
  - `Launcher_waits_for_child_processes_and_returns_exit_code` (real smoke, no uninstall): `new JobUninstallLauncher().RunAsync(new(<System32>\cmd.exe, "/c start \"\" /b \"%SystemRoot%\\System32\\ping.exe\" -n 3 127.0.0.1 >nul & exit 7"))` → returns 7, elapsed ≥ 1.5 s.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~UninstallTests"` → FAIL.
- [ ] **Step 3: Implement** parser, planner, launcher, `JobNative`, `SystemLinks`, fake launcher.
- [ ] **Step 4: Run** filter + full suite → PASS.
- [ ] **Step 5: Commit** `feat(programs): safe uninstall planning and job-object launcher`.

---

### Task 4: `regkey` change, folder rules, program traces, forced-removal planner

**Files:**
- Modify: `src/WinWaechter.Core/Sys/IRegistry.cs`, `Sys/WinRegistry.cs`, `tests/WinWaechter.Tests/Fakes/FakeRegistry.cs`, `src/WinWaechter.Core/Changes/ChangeFactory.cs`, `Model/Finding.cs`, `Optimizer/Autostart/AutostartScanner.cs`, `Optimizer/Services/ServiceScanner.cs`, `Optimizer/Tasks/TaskScanner.cs`
- Create: `src/WinWaechter.Core/Programs/RegKeyChange.cs`, `Programs/ProgramFolderRules.cs`, `Programs/ProgramTraces.cs`, `Programs/ForcedRemovalPlanner.cs`
- Test: `tests/WinWaechter.Tests/RegKeyTests.cs`

**Interfaces:**
- Consumes: `UninstallRoots`, `InstalledProgram`, `ProgramInventory` (Task 2); `UninstallPlan.UninstallerMissing` (Task 3); `MoveToAblageChange.ForFolder`, `IFileOps.InspectFolder`, `IProcessList`, `CleanerPaths`, `SessionContext.BackupDir/IsTrustedFile/ClaimFile`.
- Produces:
```csharp
// IRegistry (new members); FakeRegistry implements both (+ CreateKeyCalls, DeleteTreeCalls lists)
void CreateKey(Hive h, string key);        // creates the chain; no-op when present
void DeleteKeyTree(Hive h, string key);    // no-op when missing

// Finding: new last optional parameter, filled by the three scanners with the normalized program path they judged
public sealed record Finding(…, string? Group = null, string? ExePath = null);

public sealed class RegKeyChange : IChange
{
    public const int MaxDepth = 8;
    public RegKeyChange(IRegistry reg, Hive hive, string key, string displayName);  // ArgumentException unless UninstallRoots.IsDirectChild
    public static RegKeyChange FromRecord(ChangeRecord r, IRegistry reg);         // InvalidOperationException unless IsDirectChild
    // Kind "regkey"; Target $"{HKCU|HKLM}\{key}"; Description $"Deinstallations-Eintrag „{displayName}“ entfernen"
    // Plan: Before hive, key, name (displayName); After backup = <ctx.BackupDir>\regkey_<id>.json
    // Apply: key missing → InvalidOperationException("Der Eintrag existiert nicht mehr. Es wurde nichts geändert.") before any write;
    //        snapshot (values + subkeys, depth ≤ MaxDepth, else refuse) → JSON file → ClaimFile → After sha256 (hex) → DeleteKeyTree
    // Undo: IsDirectChild; backup trusted (ctx.IsTrustedFile) and SHA-256 equal to After["sha256"], else InvalidOperationException;
    //       key exists again → InvalidOperationException("Der Eintrag existiert wieder. Nichts wiederhergestellt.");
    //       recreate keys and values exactly (subkey names without '\', "." or "..")
    // CheckDrift: key exists → Changed, else Unchanged
}
internal sealed record RegKeySnapshot(string Name, List<RegValueSnapshot> Values, List<RegKeySnapshot> SubKeys);
internal sealed record RegValueSnapshot(string Name, string Kind, string Data);
    // String/ExpandString raw; DWord/QWord invariant number; MultiString JSON array; Binary/None/Unknown Base64

public sealed record FolderCheck(string? Folder, string? Refusal);   // exactly one is non-null
public static class ProgramFolderRules
{
    public static string? Normalize(string? raw);   // trim blanks and quotes, expand env vars, GetFullPath, TrimEnd('\'); null for
                                                    // empty, UNC, not rooted, or on error
    public static FolderCheck Check(string? rawFolder, InstalledProgram self, IReadOnlyList<InstalledProgram> all,
        CleanerPaths paths, RuleSet rules);
}
public static class ProgramTraces
{
    // findings with Change != null whose ExePath is inside folder ("<folder>\" prefix, OrdinalIgnoreCase)
    public static IReadOnlyList<Finding> InFolder(IEnumerable<Finding> scanned, string folder);
    // findings with Change != null and Group == OrphanEntries whose Title or Location contains NameKey(displayName) at a word start
    public static IReadOnlyList<Finding> ByName(IEnumerable<Finding> scanned, string displayName);
    internal static string NameKey(string displayName);  // InstalledProgramIndex.Normalize, trailing tokens that are only digits/dots removed
}
public sealed record ForcedRemovalPlan(InstalledProgram Program, IReadOnlyList<IChange> Changes, string? Folder, IReadOnlyList<string> Notes);
public sealed class ForcedRemovalPlanner
{
    public ForcedRemovalPlanner(IRegistry reg, IFileOps files, RuleSet rules, IProcessList procs, CleanerPaths paths,
        Func<IReadOnlyList<Finding>> traceScan);
    public static bool IsOffered(InstalledProgram p, bool uninstallerMissing, bool finishedButStillListed);
        // Flag != Protected && !WindowsInstaller && (uninstallerMissing || finishedButStillListed)
    public ForcedRemovalPlan Plan(InstalledProgram p, IReadOnlyList<InstalledProgram> all);
}
// ChangeFactory.CreateDefault registers "regkey" → RegKeyChange.FromRecord(r, reg)
```
`ProgramFolderRules.Check` refusals (first that applies, exact texts): no folder → „Kein Installationsordner eingetragen.“; `Normalize` null → „Der Installationsordner ist keine gültige lokale Pfadangabe.“; not a real subfolder (≥ 1 level) of `ProgramFiles`, `ProgramFilesX86` or `LocalAppData\Programs` → „Nur Ordner unter „Programme“ oder „AppData\Local\Programs“ werden angeboten.“; any relative path segment equals `Common Files` or `WindowsApps` → „Gemeinsam genutzte Ordner werden nie angeboten.“; `Classify(new ItemFacts(name, folder + "\\", null, null)) == Protected` → „Der Ordner ist geschützt.“; another entry (`Id != self.Id`) whose normalized `InstallLocation`, `IconExe(DisplayIcon)` folder or `Parse(UninstallString)` exe folder equals, contains or lies inside the folder → „Ein anderes installiertes Programm nutzt diesen Ordner.“
`ForcedRemovalPlanner.Plan` order: entry gone (`!reg.KeyExists`) → no changes, note „Der Eintrag existiert nicht mehr.“; folder = `Check(p.InstallLocation, …)`; if accepted additionally require `InspectFolder` not null, `!HasReparsePoints`, no `RunningProcess.ExePath` inside (notes: „Der Ordner ist nicht vollständig lesbar oder enthält Verknüpfungen.“, „Ein Programm aus diesem Ordner läuft gerade. Bitte zuerst beenden.“); changes = traces (`ProgramTraces.InFolder(traceScan(), folder)`: services, then tasks, then autostarts) → `ForFolder(folder)` → `new RegKeyChange(reg, p.Hive, p.RegistryKey, p.DisplayName)`; refusal texts go into `Notes` with prefix „Ordner nicht angeboten: “.

- [ ] **Step 1: Write the failing tests** (`RegKeyTests.cs`; `FakeRegistry`, temp dirs, `SessionContext(Path.Combine(_root,"s"))`, `RuleSet.Default`, `FakeProcessList`, `FakeFileOps`):
  - `Snapshot_round_trips_all_value_kinds_and_subkeys`: key `UninstallRoots.Native\{A}` with `""` (default, String), `DisplayName` String, `Path` ExpandString `%ProgramFiles%\A`, `EstimatedSize` DWord, `Q` QWord, `M` MultiString `["a","b"]`, `B` Binary `[1,2,3]`, subkey `Sub\Deep` with one value → Apply → key gone (`KeyExists` false); Undo → every value equal in value and kind, subkey restored; `After["sha256"]` is 64 hex chars; backup file exists under `BackupDir`.
  - `Apply_on_missing_key_writes_nothing` (Review Focus 2): no key → `InvalidOperationException` containing `"existiert nicht mehr"`; `BackupDir` has no `regkey_*` file; `DeleteTreeCalls` empty.
  - `Refuses_keys_outside_the_uninstall_roots`: ctor with `@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"` → `ArgumentException`; `Native + @"\A\B"` → `ArgumentException`; `FromRecord` with Before key `@"SOFTWARE\Evil\A"` → `InvalidOperationException`.
  - `Undo_refuses_tampered_backup_or_untrusted_file`: edit one byte of the backup → Undo throws, key not created; context with `isTrustedFile: _ => false` → Undo throws.
  - `Undo_refuses_when_key_exists_again_and_drift_is_changed`: Apply, recreate key → `CheckDrift == Changed`; `engine.UndoSession` skips it (`SkippedDrift == 1`).
  - `Factory_rehydrates_regkey`.
  - `Folder_rules_normalize_and_refuse_roots` (Review Focus 3): `"\"<pf>\\Foo\\\""` → Folder `<pf>\Foo`; `"<PF>\\foo\\"` (upper-cased root) → accepted; `"%ProgramFiles%\\Foo"` with `paths.ProgramFiles` = real `%ProgramFiles%` → accepted path under it; `"<pf>\\"` → refusal „Nur Ordner …“; `"<pf>\\Common Files\\X"` → „Gemeinsam genutzte …“; `"<profile>\\AppData\\Roaming\\Foo"` → „Nur Ordner …“; `"\\\\nas\\x"` → „keine gültige …“; `"<pf>\\Dell\\X"` → „geschützt“.
  - `Folder_rules_refuse_overlap_with_other_entries`: other entry InstallLocation `<pf>\Foo\Plugin` → refused for `<pf>\Foo`; other `<pf>\Foo` → refused for `<pf>\Foo\Sub`; other `<pf>\Foobar` → accepted (prefix, not folder).
  - `Scanners_fill_exe_path`: Run value `"<tmp>\\gone\\foo.exe" --min` → `AutostartScanner` finding `ExePath == <tmp>\gone\foo.exe`; service ImagePath and task ActionExe likewise (existing fakes from `OrphanTests`).
  - `Traces_in_folder_and_by_name`: findings with ExePath `<pf>\Foo\a.exe` (change), `<pf>\Foobar\b.exe`, `<pf>\Foo\c.exe` (Change null) → `InFolder(<pf>\Foo)` returns only the first; orphan finding Title `"FooTool Updater"` and `NameKey("FooTool 2.3.1") == "footool"` → `ByName` returns it; Title `"SuperFooTool"` → not returned.
  - `IsOffered_rules`: Protected → false; WindowsInstaller → false; neither missing nor finished → false; missing → true; finished-but-listed → true.
  - `Forced_plan_lists_traces_folder_then_regkey`: program `<pf>\Foo` (InstallLocation), service + task + autostart traces with ExePath inside → `Changes.Select(c => c.Kind)` == `["service","task","autostart","folder","regkey"]`; `Folder == <pf>\Foo`.
  - `Forced_plan_without_folder_still_removes_entry_and_says_why`: InstallLocation missing → kinds `["regkey"]`, `Notes` contains „Ordner nicht angeboten: Kein Installationsordner eingetragen.“; running process inside → no `folder`, note about running program.
  - `Forced_plan_for_vanished_entry_has_no_changes` (Review Focus 2): key not seeded → `Changes` empty, `Notes == ["Der Eintrag existiert nicht mehr."]`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~RegKeyTests"` → FAIL.
- [ ] **Step 3: Implement.** `WinRegistry.CreateKey` = `CreateSubKey(...).Dispose()`; `DeleteKeyTree` = `DeleteSubKeyTree(sub, throwOnMissingSubKey: false)` on the base key (64-bit view, HKU\SID for CurrentUser). `RegKeyChange` reads via `ValueNames`/`GetKind`/`Get`/`SubKeyNames` only (no `reg.exe`). Scanners pass the path they already compute (`exe`/`file`) as `ExePath`.
- [ ] **Step 4: Run** filter + full suite (incl. `OrphanTests`, `AutostartTests`, `ServiceTests`, `TaskTests`) → PASS.
- [ ] **Step 5: Commit** `feat(programs): regkey change and forced-removal planner`.

---

### Task 5: Leftover finder after uninstall, ProgramService

**Files:**
- Create: `src/WinWaechter.Core/Programs/UninstallLeftoverFinder.cs`, `Programs/ProgramService.cs`
- Test: `tests/WinWaechter.Tests/ProgramLeftoverTests.cs`

**Interfaces:**
- Consumes: Tasks 2–4 (`ProgramInventory`, `ProgramInventory.IconExe`, `UninstallPlanner`, `IUninstallLauncher`, `ProgramFolderRules`, `ProgramTraces`, `ForcedRemovalPlanner`); `MoveToAblageChange.ForFolder`; `FindingGroups.OrphanFolders/OrphanEntries`.
- Produces:
```csharp
public sealed class UninstallLeftoverFinder
{
    public UninstallLeftoverFinder(IRegistry reg, IFileOps files, RuleSet rules, IProcessList procs, CleanerPaths paths,
        Func<IReadOnlyList<Finding>> traceScan);
    public IReadOnlyList<Finding> Find(InstalledProgram removed, IReadOnlyList<InstalledProgram> remaining);
        // empty when reg.KeyExists(removed.Hive, removed.RegistryKey)
}
public interface IProgramActions
{
    IReadOnlyList<InstalledProgram> ReadAll();
    bool IsStillListed(InstalledProgram p);
    UninstallPlan PlanUninstall(InstalledProgram p);
    SignatureInfo? UninstallerSignature(UninstallPlan plan);     // null when no command; never throws
    Task<int> RunUninstallerAsync(UninstallPlan plan);           // InvalidOperationException unless Route == Launch
    IReadOnlyList<Finding> FindLeftovers(InstalledProgram removed);   // reads remaining entries itself
    ForcedRemovalPlan PlanForcedRemoval(InstalledProgram p);          // reads all entries itself
}
public sealed class ProgramService : IProgramActions
{
    public ProgramService(ProgramInventory inventory, IRegistry reg, IFileOps files, ISignatureVerifier sig,
        IUninstallLauncher launcher, UninstallLeftoverFinder leftovers, ForcedRemovalPlanner forced, CleanerPaths paths);
}
```
Leftover rules (§1.4, no 30-day rule): folder candidates `InstallLocation`, else directory of `IconExe(DisplayIcon)`; each through `ProgramFolderRules.Check(raw, removed, remaining, …)`; then `Directory` exists, `InspectFolder` not null, `!HasReparsePoints`, no running process inside, else no folder finding (no message). Folder finding: Id `leftover|<dir>`, Area `Speicher`, Title folder name, Location dir, Reasons `[$"Rest von „{removed.DisplayName}“"]`, `Rating.Info`, **not** preselected, `ForFolder`, `SizeBytes = facts.Bytes`, Group `OrphanFolders`. Trace findings: `InFolder(scan, dir)` ∪ `ByName(scan, DisplayName)` (distinct by Id), each `with { Preselected = f.Group == FindingGroups.OrphanEntries }`. Any exception while scanning → log and return what was found before (folder finding first).

- [ ] **Step 1: Write the failing tests** (`ProgramLeftoverTests.cs`; temp `pf`, `FakeRegistry` without the removed key, `FakeFileOps`, `FakeProcessList`, `traceScan` returning hand-made findings):
  - `Folder_from_install_location_offered_not_preselected_without_age_rule`: `<pf>\Foo\foo.dll` written now → one folder finding, `!Preselected`, Change Kind `folder`, Reasons `["Rest von „Foo 1.0“"]`.
  - `Display_icon_folder_used_when_install_location_missing`: DisplayIcon `"\"<pf>\\Foo\\foo.exe\",0"` → folder `<pf>\Foo`; DisplayIcon `"C:\\Windows\\Installer\\{x}\\i.ico"` → no folder finding.
  - `Still_listed_program_gives_nothing`: key seeded → `Find` returns empty.
  - `Protected_running_shared_or_junction_folders_are_not_offered`: `<pf>\Dell\X`; process running from `<pf>\Foo\a.exe`; remaining entry with InstallLocation `<pf>\Foo`; `FakeFileOps.Junctions` contains `<pf>\Foo\link` → none offered in each case.
  - `Traces_in_folder_and_orphans_by_name`: scan returns autostart ExePath `<pf>\Foo\tray.exe` (not orphan) and orphan task Title `"Foo Updater"` → both returned; autostart `Preselected == false`, task `Preselected == true`.
  - `ProgramService_runs_only_launch_plans`: `FakeUninstallLauncher` → `RunUninstallerAsync(OpenSettings plan)` throws `InvalidOperationException`, `Calls` empty; Launch plan → one call, returns `ExitCode`.
  - `ProgramService_signature_never_throws`: `FakeSignatures` throwing → `UninstallerSignature` returns `SignatureInfo(Unknown, null, false)`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~ProgramLeftoverTests"` → FAIL.
- [ ] **Step 3: Implement** both classes.
- [ ] **Step 4: Run** filter + full suite → PASS.
- [ ] **Step 5: Commit** `feat(programs): targeted leftover search after uninstall`.

---

### Task 6: Security checks core – firewall, security center, Defender, registry checks, encryption

**Files:**
- Create: `src/WinWaechter.Core/Security/Checks/SecurityItem.cs`, `Checks/Sources.cs` (interfaces + records), `Checks/WinSources.cs` (`FwPolicy2Firewall`, `WmiSecurityCenter`, `WmiVolumeEncryption`), `Checks/FirewallChange.cs`, `Checks/SecurityChecks.cs`
- Create: `tests/WinWaechter.Tests/Fakes/FakeFirewall.cs`, `Fakes/FakeSecuritySources.cs`
- Test: `tests/WinWaechter.Tests/SecurityCoreTests.cs`

**Interfaces:**
- Consumes: `DefenderStatus`, `IRegistry`, `ChangeFactory.Register`, `SystemLinks` (Task 3).
- Produces:
```csharp
namespace WinWaechter.Core.Security.Checks;
public enum Light { Green, Yellow, Red, Grey }
public enum SecurityActionKind { None, Link, Change, DefenderScan }
public sealed record SecurityItem(string Id, string Title, Light Light, string Finding, string Explanation,
    SecurityActionKind Action = SecurityActionKind.None, string? ActionLabel = null, string? Link = null,
    IChange? Change = null, string? ScanPath = null, IReadOnlyList<SecurityItem>? Children = null);
public static class SecurityIds
{
    public const string FirewallDomain = "firewall-domain", FirewallPrivate = "firewall-private", FirewallPublic = "firewall-public",
        Defender = "defender", SmartScreen = "smartscreen", Uac = "uac", RemoteDesktop = "rdp", Encryption = "encryption",
        Updates = "updates", Listeners = "listeners", Extensions = "extensions";
}
public enum FirewallProfile { Domain = 1, Private = 2, Public = 4 }
public interface IFirewall { bool IsEnabled(FirewallProfile p); void SetEnabled(FirewallProfile p, bool on); }
public sealed record SecurityProduct(string Name, bool Enabled, bool IsMicrosoft);
public interface ISecurityCenter { IReadOnlyList<SecurityProduct> AntiVirus(); IReadOnlyList<SecurityProduct> Firewalls(); }
public sealed record VolumeEncryptionState(int ProtectionStatus, int ConversionStatus);
public interface IVolumeEncryption { VolumeEncryptionState? SystemDrive(); }   // null = class/volume not available
public sealed class FwPolicy2Firewall : IFirewall;          // COM "HNetCfg.FwPolicy2", InvokeMember("FirewallEnabled", Get/SetProperty, [profile(, value)])
public sealed class WmiSecurityCenter : ISecurityCenter;    // root\SecurityCenter2 AntiVirusProduct/FirewallProduct; Enabled = ((productState >> 12) & 0xF) == 1;
                                                            // IsMicrosoft = displayName starts with "Windows Defender" or "Microsoft Defender"
public sealed class WmiVolumeEncryption : IVolumeEncryption; // root\CIMV2\Security\MicrosoftVolumeEncryption Win32_EncryptableVolume,
                                                             // DriveLetter = system drive ("C:"); ManagementException → null
public sealed class FirewallChange : IChange
{
    public FirewallChange(IFirewall fw, FirewallProfile profile);
    public static FirewallChange FromRecord(ChangeRecord r, IFirewall fw);   // profile must parse to 1, 2 or 4, else InvalidOperationException
    public static void Register(ChangeFactory f, IFirewall fw);              // "firewall"
    // Kind "firewall"; Target $"Firewall {ProfileName}"; Description $"Firewall für das Profil „{ProfileName}“ einschalten"
    // Before profile ("1"/"2"/"4"), enabled ("true"/"false"); After enabled "true"; Undo sets Before value; CheckDrift: live != After → Changed
    public static string ProfileName(FirewallProfile p);   // "Domäne", "Privat", "Öffentlich"
}
public static class SecurityChecks
{
    public static IReadOnlyList<SecurityItem> Firewall(IFirewall fw, IReadOnlyList<SecurityProduct> firewalls);
    public static SecurityItem Defender(DefenderStatus s, IReadOnlyList<SecurityProduct> antiVirus);
    public static SecurityItem SmartScreen(IRegistry reg);
    public static SecurityItem Uac(IRegistry reg);
    public static SecurityItem RemoteDesktop(IRegistry reg);
    public static SecurityItem Encryption(VolumeEncryptionState? s);
    public static SecurityItem Unavailable(string id, string title, string? detail = null);   // Grey, Finding "Nicht prüfbar" (+ " – detail")
}
// Tests: FakeFirewall { Dictionary<FirewallProfile,bool> State; List<(FirewallProfile,bool)> SetCalls; bool Throw; }
//        FakeSecurityCenter { List<SecurityProduct> Av, Fw; bool Throw; }, FakeVolumeEncryption { VolumeEncryptionState? State; bool Throw; }
```
Rules (Finding texts exact; Explanation one or two sentences, Sie-Form, your wording):
- Firewall, per profile in order Domain, Private, Public, Title `$"Firewall ({ProfileName})"`: an enabled non-Microsoft product → Green, Finding `$"Übernommen von {name}"`, no action; else enabled → Green „Eingeschaltet“; off → Red „Ausgeschaltet“, Action `Change`, ActionLabel „Einschalten“, `Change = new FirewallChange(fw, p)`.
- Defender (Title „Microsoft Defender“, Link `DefenderThreat`, ActionLabel „Windows-Sicherheit öffnen“): enabled non-Microsoft AV → Green `$"Übernommen von {name}"` (no link); `!Available` → Grey „Nicht aktiv oder nicht lesbar“; `!RealTime` → Red „Echtzeitschutz ist aus“; `!TamperProtected` → Red „Manipulationsschutz ist aus“; `SignatureAgeDays > 3` → Yellow `$"Virendefinitionen sind {n} Tage alt"`; else Green „Echtzeit- und Manipulationsschutz an“.
- SmartScreen (Link `DefenderAppBrowser`, ActionLabel „Einstellungen öffnen“): policy `HKLM SOFTWARE\Policies\Microsoft\Windows\System` `EnableSmartScreen` DWord 0, or `HKLM SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer` `SmartScreenEnabled` string `Off` (OrdinalIgnoreCase) → Yellow „Ausgeschaltet“; else Green „Eingeschaltet“.
- UAC (`HKLM SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System`, hint only): `EnableLUA` 0 → Red „Ausgeschaltet“; `ConsentPromptBehaviorAdmin` 0 → Yellow „Admins werden nicht gefragt“; `PromptOnSecureDesktop` 0 → Yellow „Abfrage ohne sicheren Desktop“; else Green „Eingeschaltet“ (missing values = defaults on).
- Remote desktop (Link `RemoteDesktop`): `HKLM SOFTWARE\Microsoft\Windows NT\CurrentVersion` `EditionID` starting with `Core` → Grey „Nicht verfügbar (Home)“, no link; `HKLM SYSTEM\CurrentControlSet\Control\Terminal Server` `fDenyTSConnections` 0 → Yellow „Eingeschaltet“; else Green „Ausgeschaltet“.
- Encryption (Title „Geräteverschlüsselung“, Link `DeviceEncryption`): null → Grey „Nicht verfügbar“ (no link); ProtectionStatus 1 → Green „Systemlaufwerk verschlüsselt“; ConversionStatus ≠ 0 → Yellow „Verschlüsselt, wartet auf Aktivierung“; else Yellow „Systemlaufwerk nicht verschlüsselt“.

- [ ] **Step 1: Write the failing tests** (`SecurityCoreTests.cs`; fakes above, `FakeRegistry`, `SessionContext` temp):
  - `Firewall_off_profile_is_red_with_change`: Private off → item `firewall-private` `Light.Red`, `Action == Change`, `Change!.Kind == "firewall"`; Domain/Public on → Green, `Action == None`.
  - `Third_party_firewall_takes_over_without_action`: Fw `[("Contoso Firewall", true, false)]` and Private off → Private Green, Finding `"Übernommen von Contoso Firewall"`, `Change == null`; disabled third-party product → Red again.
  - `FirewallChange_apply_undo_and_drift`: off → Apply → `State[Private] == true`, `Before["enabled"] == "false"`; `CheckDrift == Unchanged`; set off externally → `Changed`; Undo after re-enable → `false`.
  - `FirewallChange_FromRecord_refuses_unknown_profile`: Before profile `"3"` → `InvalidOperationException`; factory with `FirewallChange.Register` rehydrates `"firewall"`.
  - `Defender_rules`: third-party AV enabled → Green „Übernommen von …“; `Available false` → Grey; RealTime off → Red; Tamper off → Red; sig 4 days → Yellow „Virendefinitionen sind 4 Tage alt“; sig 3 days → Green; Link `windowsdefender://threat` on the non-takeover rows.
  - `SmartScreen_rules`: nothing → Green; `SmartScreenEnabled="Off"` → Yellow; policy `EnableSmartScreen=0` → Yellow; Link `windowsdefender://appbrowser`.
  - `Uac_rules`: nothing → Green; `EnableLUA=0` → Red; `ConsentPromptBehaviorAdmin=0` → Yellow; `PromptOnSecureDesktop=0` → Yellow; `Action == None`.
  - `Remote_desktop_rules`: `EditionID="Core"` → Grey „Nicht verfügbar (Home)“; `"Professional"` + `fDenyTSConnections=0` → Yellow; `=1` → Green.
  - `Encryption_rules`: null → Grey; `(1,1)` → Green; `(0,1)` → Yellow „Verschlüsselt, wartet auf Aktivierung“; `(0,0)` → Yellow „Systemlaufwerk nicht verschlüsselt“.
  - `Every_link_is_in_SystemLinks`: all items built in this class with `Link != null` satisfy `SystemLinks.IsAllowed`.
  - `Real_sources_smoke_read_only`: `new WmiSecurityCenter().AntiVirus()` and `new WmiVolumeEncryption().SystemDrive()` do not throw out of a try that tolerates `ManagementException`/`UnauthorizedAccessException`/`COMException`; `new FwPolicy2Firewall().IsEnabled(FirewallProfile.Private)` returns a bool (no `SetEnabled` call).
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~SecurityCoreTests"` → FAIL.
- [ ] **Step 3: Implement** the files above.
- [ ] **Step 4: Run** filter + full suite → PASS.
- [ ] **Step 5: Commit** `feat(security): firewall, Defender, SmartScreen, UAC, RDP and encryption checks`.

---

### Task 7: Security checks – Windows Update, listening programs, browser extensions, orchestrator

**Files:**
- Modify: `src/WinWaechter.Core/Security/Heuristics/TcpTable.cs`, `Sys/IProcessList.cs` (`RunningProcess.Pid`)
- Create: `src/WinWaechter.Core/Security/Checks/WindowsUpdateSearch.cs`, `Checks/BrowserExtensionScanner.cs`, `Checks/SecurityCheck.cs`; extend `Checks/SecurityChecks.cs`, `Fakes/FakeSecuritySources.cs`
- Test: `tests/WinWaechter.Tests/SecurityNetTests.cs`

**Interfaces:**
- Consumes: Task 6 types; `IDefender.GetStatusAsync`; `ISignatureVerifier`; `IProcessList`; `CleanerPaths`; `IRegistry`.
- Produces:
```csharp
public sealed record RunningProcess(string Name, string? ExePath, int Pid = 0);   // WinProcessList fills Pid
public sealed record ListeningPort(int Pid, string LocalAddress, int LocalPort);
public interface IListenerSource { IReadOnlyList<ListeningPort> Listening(); }   // TcpTableConnections implements it (state LISTEN = 2, IPv4+IPv6)
public sealed record WindowsUpdateState(int? Pending, DateTime? LastSearchUtc, bool TimedOut);
public interface IWindowsUpdateSearch { WindowsUpdateState Search(TimeSpan timeout); }
public sealed class WuaUpdateSearch : IWindowsUpdateSearch;
    // dedicated MTA thread: Microsoft.Update.Session → CreateUpdateSearcher, Online=false,
    // BeginSearch("IsInstalled=0 and IsHidden=0 and Type='Software' and BrowseOnly=0", null, null), poll job.IsCompleted every 250 ms,
    // EndSearch → Updates.Count; after timeout job.RequestAbort() → TimedOut; LastSearchUtc from Microsoft.Update.AutoUpdate
    // Results.LastSearchSuccessDate; any COM error → (null, null, false). If BeginSearch rejects a null callback, pass a minimal
    // [ComVisible(true)] callback object instead – keep the polling.
public enum ExtensionOrigin { Store, Policy, External, Builtin }
public sealed record BrowserExtension(string Browser, string Profile, string Id, string Name, ExtensionOrigin Origin);
public sealed class BrowserExtensionScanner
{
    public const long MaxFileBytes = 20 * 1024 * 1024;
    public BrowserExtensionScanner(CleanerPaths paths, IRegistry reg);
    public IReadOnlyList<BrowserExtension> Scan();   // Builtin left out; never throws
}
// SecurityChecks (new)
public static SecurityItem Updates(bool rebootRequired, WindowsUpdateState? s);
public static SecurityItem Listeners(IReadOnlyList<ListeningPort> ports, IReadOnlyList<RunningProcess> procs, ISignatureVerifier sig);
public static SecurityItem Extensions(IReadOnlyList<BrowserExtension> exts);
public sealed record SecuritySources(IFirewall Firewall, ISecurityCenter Center, IDefender Defender, IRegistry Registry,
    IVolumeEncryption Encryption, IWindowsUpdateSearch Updates, IListenerSource Listeners, IProcessList Processes,
    ISignatureVerifier Signatures, BrowserExtensionScanner Extensions);
public sealed class SecurityCheck
{
    public static readonly TimeSpan SourceTimeout = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan UpdateSearchTimeout = TimeSpan.FromSeconds(30);
    public SecurityCheck(SecuritySources s, TimeSpan? sourceTimeout = null);
    public Task<IReadOnlyList<SecurityItem>> RunAsync(CancellationToken ct);
        // rows in SecurityIds order (3 firewall rows, defender, smartscreen, uac, rdp, encryption, updates, listeners, extensions);
        // each check runs on its own Task.Run; exception → Unavailable(id, title); not done within sourceTimeout →
        // Unavailable(id, title, "Zeitüberschreitung"); security-center failure → treated as "no third-party products"
}
```
Rules:
- Updates (Title „Windows-Updates“, Link `WindowsUpdate`, ActionLabel „Windows Update öffnen“): `RebootRequired` = key `HKLM SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired` exists. reboot → Yellow „Neustart nötig“; else s null / TimedOut / Pending null → Grey „Nicht prüfbar“; Pending > 0 → Yellow `$"{n} Updates ausstehend"`; 0 → Green „Keine ausstehenden Updates“. Finding gets suffix `$" (Stand der letzten Windows-Suche: {local:dd.MM.yyyy HH:mm})"` when `LastSearchUtc` is set.
- Listeners (Title „Im Netz lauschende Programme“): only ports whose `LocalAddress` parses and `!IPAddress.IsLoopback` (0.0.0.0 and `::` count). Group by process path (pid when path null); child Title = file name, or `$"PID {pid}"`; Finding `$"Port {list}"` (ascending, distinct, `", "`). Pid 4 or path null → Grey „Pfad nicht lesbar“; signature Unsigned/Invalid → Yellow „Nicht signiert“ / „Signatur ungültig“; path containing `\AppData\` or `\Temp\` (OrdinalIgnoreCase) → Yellow „Läuft aus einem Benutzerordner“; else Green `$"Signiert: {publisher}"`. Yellow children with path: Action `DefenderScan`, ActionLabel „Von Defender prüfen lassen“, `ScanPath` = path. Parent: Yellow if any child Yellow, else Green; Finding `$"{n} Programme"` or „Keine Programme lauschen im Netz“.
- Extensions (Title „Browser-Erweiterungen“, hint only): child per extension Title `$"{Browser} ({Profile}): {Name}"`, Policy → Yellow „Per Richtlinie erzwungen“, External → Yellow „Von außen installiert“, Store → Green „Aus dem Store“. Parent Yellow if any child Yellow; Finding `$"{n} Erweiterungen"`; Explanation names „im Browser unter Erweiterungen entfernen“.
- Scanner sources: Edge `LocalAppData\Microsoft\Edge\User Data`, Chrome `LocalAppData\Google\Chrome\User Data` (profiles = `Default`, `^Profile \d+$`, or any subfolder with a `Preferences` file), Opera `RoamingAppData\Opera Software\Opera Stable` (the folder itself is the profile „Standard“); read `Secure Preferences` then `Preferences` (later file does not override an id already seen), `extensions.settings.<id>.location`: 5/10 → Builtin, 7/9 → Policy, 4/6/8 → External, else Store. Name: `settings.manifest.name`, else highest version folder `Extensions\<id>\<ver>\manifest.json` `name`; `__MSG_<key>__` → `_locales\<default_locale>\messages.json` key (OrdinalIgnoreCase) `.message`; unresolved → id. Policies: values of `HKLM` and `HKCU` `SOFTWARE\Policies\Google\Chrome\ExtensionInstallForcelist` (Chrome) and `SOFTWARE\Policies\Microsoft\Edge\ExtensionInstallForcelist` (Edge), text before `;` = id → mark that browser's extension Policy, or add one with Profile „Richtlinie“, Name = id. Firefox `RoamingAppData\Mozilla\Firefox\Profiles\*\extensions.json` `addons[]` with `type == "extension"`: location `app-builtin`, `app-system-defaults`, `app-system-addons` → Builtin; `app-profile` with `sourceURI` host `addons.mozilla.org` → Store; else External; name `defaultLocale.name` ?? id. Files opened with `FileShare.ReadWrite | FileShare.Delete`, size ≤ `MaxFileBytes`; any IO/JSON error → skip that file.

- [ ] **Step 1: Write the failing tests** (`SecurityNetTests.cs`; temp `CleanerPaths`, `FakeRegistry`, `FakeSignatures`, `FakeProcessList`, fakes from Task 6 plus `FakeUpdateSearch`, `FakeListeners`, `FakeDefender`):
  - `Updates_rules`: reboot key → Yellow „Neustart nötig…“; `(3, t, false)` → Yellow starting „3 Updates ausstehend (Stand der letzten Windows-Suche: “; `(0, null, false)` → Green „Keine ausstehenden Updates“; `(null, null, true)` → Grey.
  - `Update_search_timeout_gives_grey` (Review Focus 4): `FakeUpdateSearch` returning `TimedOut = true` → `updates` row Grey; a fake that blocks 5 s with `sourceTimeout: 1 s` → row Grey with Finding containing „Zeitüberschreitung“, `RunAsync` finishes in < 3 s.
  - `Listener_rules`: ports `(10, "0.0.0.0", 445)`, `(4, "0.0.0.0", 139)`, `(20, "127.0.0.1", 5000)`, `(30, "::", 7000)`, `(30, "0.0.0.0", 7001)`; procs `(…, null, 10)`? no → pid 10 path `C:\P\svc.exe` signed „Foo GmbH“, pid 30 path `<profile>\AppData\Local\x\x.exe` signed → children: `svc.exe` Green „Signiert: Foo GmbH“, `PID 4` Grey, `x.exe` Yellow with Finding `"Port 7000, 7001"`, Action `DefenderScan`, `ScanPath` set; pid 20 absent (loopback); parent Yellow.
  - `Listener_unsigned_is_yellow`: unsigned `C:\P\a.exe` → Yellow „Nicht signiert“.
  - `Chromium_locations_and_names`: Edge `Default\Secure Preferences` with ids A (location 1), B (location 5), C (7), D (4) and `Extensions\D\1.0_0\manifest.json` `name "__MSG_appName__"`, `default_locale "de"`, `_locales\de\messages.json` `{"appName":{"message":"Dings"}}` → B absent, A Store, C Policy, D External with Name „Dings“.
  - `Chrome_forcelist_policy_marks_or_adds`: HKLM forcelist value `"1" = "E;https://x"`, extension E present as location 1 → Policy; id F only in HKCU policy → added with Profile „Richtlinie“.
  - `Opera_profile_is_the_stable_folder` and `Firefox_extensions_json_origins`: Opera `Opera Stable\Preferences` one extension → Browser „Opera“, Profile „Standard“; Firefox addons: AMO `app-profile` → Store, `app-profile` with `file:///` source → External, `app-builtin` → absent, `type: "theme"` → absent.
  - `Corrupt_or_locked_preferences_are_skipped_and_names_fall_back` (Review Focus 5): Chrome `Default\Preferences` with truncated JSON, Edge file held open with `FileShare.None` during `Scan()`, an extension with `__MSG_x__` and no `_locales` → no exception; the readable profiles still give their extensions; the unresolved one has `Name == Id`.
  - `Failing_or_hanging_source_gives_grey_row_only` (Review Focus 4): `FakeSecurityCenter.Throw`, `FakeVolumeEncryption.Throw`, `FakeDefender` throwing from `GetStatusAsync` → `defender` and `encryption` Grey „Nicht prüfbar“, firewall rows still computed (no takeover), all 11 ids present in `SecurityIds` order.
  - `TcpTable_listening_smoke`: `new TcpTableConnections().Listening()` does not throw (may be empty); every port in 1..65535.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~SecurityNetTests"` → FAIL.
- [ ] **Step 3: Implement** the types above; `HeuristicTests` fakes keep compiling (new interface is separate).
- [ ] **Step 4: Run** filter + full suite → PASS.
- [ ] **Step 5: Commit** `feat(security): update, listening ports and browser extension checks; check orchestrator`.

---

### Task 8: App – „Programme“ page

**Files:**
- Create: `src/WinWaechter.App/ViewModels/ProgramsViewModel.cs`, `Views/ProgramsView.xaml`, `Views/ProgramsView.xaml.cs`
- Modify: `Services/IDialogService.cs`, `Services/WpfDialogService.cs`, `Views/MessageDialog.xaml(.cs)` (cancel text), `ViewModels/ShellViewModel.cs`, `Views/ShellWindow.xaml` (DataTemplate), `Services/AppServices.cs`, `docs/design-notes.md` (nav glyph line), `tests/WinWaechter.Tests/ViewSmokeTests.cs`, `PrivacyViewModelTests.cs` (shell ctor + nav test), `ViewModelTests.cs` (shell ctor if used)
- Test: `tests/WinWaechter.Tests/ProgramsViewModelTests.cs`

**Interfaces:**
- Consumes: `IProgramActions`, `ProgramService`, `UninstallLeftoverFinder`, `ForcedRemovalPlanner`, `ForcedRemovalPlan` (Tasks 4–5); `UninstallPlan/Route`, `UninstallExit`, `JobUninstallLauncher`, `SystemLinks.AppsFeatures` (Task 3); `InstalledProgram`, `ProgramFlag`, `ProgramInventory` (Task 2); `OptimizerEngine.ApplyAsync`; `FindingItemViewModel`; `ChangeGate`; `Labels.Size`.
- Produces:
```csharp
// IDialogService (new, default implementation keeps existing fakes compiling)
bool Ask(string title, string message, string yesText, string noText) => Confirm(title, message, yesText);
// ISystemShell (new member; ExplorerShell: only SystemLinks.IsAllowed URIs, Process.Start(new ProcessStartInfo(uri){UseShellExecute=true}))
void OpenLink(string uri);

public sealed partial class ProgramItemViewModel : ObservableObject
{
    public ProgramItemViewModel(InstalledProgram p);
    public InstalledProgram Program { get; }
    public string Name { get; }  public string? Publisher { get; }  public string? Version { get; }
    public string DateText { get; }   // dd.MM.yyyy or ""
    public string SizeText { get; }   // Labels.Size or ""
    public string? FlagText { get; }  // Protected "Geschützt", Pua "Unerwünscht", Miner "Miner", None null
    public Tone FlagTone { get; }     // Protected Good, Pua Watch, Miner Alarm, None Neutral
    public bool CanUninstall { get; } // Flag != Protected
    public string? ProtectedHint { get; }  // "Geschützt – bei Bedarf über Windows-Einstellungen → Apps deinstallieren"
    [ObservableProperty] bool _canForceRemove;
}
public enum ProgramSort { Name, Size, Date }
public sealed partial class ProgramsViewModel : ObservableObject
{
    public ProgramsViewModel(IProgramActions actions, OptimizerEngine engine, IDialogService dialogs, ISystemShell shell,
        bool canChange, ChangeGate gate);
    public ObservableCollection<ProgramItemViewModel> Items { get; }          // filtered + sorted view of the last read
    [ObservableProperty] string _searchText = "";                             // Name or Publisher contains, OrdinalIgnoreCase
    [ObservableProperty] ProgramSort _sort = ProgramSort.Name;               // Name asc, Size desc (null last), Date desc (null last)
    public string CountText { get; }                                          // $"{shown} von {total} Programmen"
    public IAsyncRelayCommand RefreshCommand { get; }                         // „Neu laden“; first page show triggers it (ShellViewModel)
    public IAsyncRelayCommand<ProgramItemViewModel> UninstallCommand { get; }
    public IAsyncRelayCommand<ProgramItemViewModel> ForceRemoveCommand { get; }
    public InstalledProgram? Pending { get; }                                 // program uninstalled via Windows-Einstellungen / „Noch nicht“
    public string? PendingText { get; }                                       // $"Nach der Deinstallation von „{name}“ lassen sich Reste suchen."
    public IAsyncRelayCommand FindLeftoversCommand { get; }                   // for Pending
    public ObservableCollection<FindingItemViewModel> Leftovers { get; }
    public string? LeftoversTitle { get; }                                    // $"Reste von „{name}“"
    public bool ShowLeftovers { get; }
    public IAsyncRelayCommand ApplyLeftoversCommand { get; }                  // selected leftovers via engine, title $"Reste von {name}"
    public IRelayCommand CloseLeftoversCommand { get; }
    [ObservableProperty] bool _isBusy;  [ObservableProperty] string _progressText = "";  [ObservableProperty] string? _lastSummary;
    public const string RunningText = "Deinstallation läuft … Sie lässt sich hier nicht abbrechen. Bitte folgen Sie dem Fenster des Deinstallationsprogramms.";
    public event EventHandler? ChangesApplied;
}
// ShellViewModel(OptimizeViewModel, ProtectViewModel, PrivacyViewModel, ProgramsViewModel, HistoryViewModel, SettingsViewModel,
//                UpdateViewModel, bool)  – Task 9 inserts SecurityViewModel after ProtectViewModel
// PrimaryNav: Übersicht, Optimieren, Schutz, Datenschutz, Programme (glyph "\uE71D"), Verlauf; programs.ChangesApplied → history.ShowNewestAsync()
```
Uninstall flow (all system work inside `gate.RunAsync`; without `canChange` the commands are disabled and the admin notice shows):
1. `plan = actions.PlanUninstall(p)`. `OpenSettings` → `Confirm("Über Windows-Einstellungen deinstallieren", $"{plan.Reason} Bitte deinstallieren Sie „{name}“ über die Windows-Einstellungen.", "Einstellungen öffnen")` → `shell.OpenLink(SystemLinks.AppsFeatures)`, `Pending = p`; if `plan.UninstallerMissing` and `IsOffered(p, true, false)` → `CanForceRemove = true`.
2. `Launch` → `Confirm("Deinstallieren", $"„{name}“ von {publisher ?? "unbekanntem Herausgeber"}. Das Deinstallationsprogramm des Herstellers wird gestartet. Das lässt sich in WinWächter nicht rückgängig machen.", "Deinstallieren", [$"Programm: {exe} {args}", signature line as in 0.2 installer reasons], final: true)`.
3. `ProgressText = RunningText`; `code = await actions.RunUninstallerAsync(plan)`; `UninstallExit.Interpret` → Cancelled: `LastSummary = "Deinstallation abgebrochen."`, stop.
4. `Ask("Deinstallation", "Ist die Deinstallation abgeschlossen?", "Ja", "Noch nicht")`; „Noch nicht“ → `Pending = p`, stop. „Ja“ → Refresh; still listed → `CanForceRemove = IsOffered(p, false, true)` on the new row + `Inform` „Der Eintrag ist noch vorhanden. …“; gone → `FindLeftovers(p)` → panel, or `Inform("Reste", "Keine Reste gefunden.")`. `RestartRequired` adds „Ein Neustart ist nötig, um die Deinstallation abzuschließen.“ to the summary.
- `FindLeftoversCommand`: `actions.IsStillListed(Pending)` → `Inform` „Das Programm ist noch installiert. Reste lassen sich erst nach der Deinstallation suchen.“; else panel, `Pending = null`.
- `ForceRemoveCommand`: `plan = PlanForcedRemoval(p)`; no changes → `Inform` with `Notes`; else `Confirm("Erzwungen entfernen", "Alles wird gesichert und lässt sich im Verlauf rückgängig machen.", "Entfernen", plan.Changes.Select(c => c.Description).Concat(plan.Notes).ToList())` → `engine.ApplyAsync($"Erzwungen entfernt: {name}", findings, …)` (findings wrap each change: Id `forced|{Target}`, Area from Kind, preselected) → summary `Inform` → `ChangesApplied` → Refresh.
- View: page title „Programme“, search box + sort `ComboBox`, „Neu laden“, list rows (Name bold, Publisher · Version · Datum · Größe muted, flag chip, „Deinstallieren“ `Button.Final` or `ProtectedHint`, „Erzwungen entfernen“ when `CanForceRemove`), Pending banner with „Reste suchen“, leftover panel on top (same item template and checkboxes as `OptimizeView`, „Ausgewählte verschieben“ / „Schließen“). `ListBox` virtualization on.

- [ ] **Step 1: Write the failing tests** (`ProgramsViewModelTests.cs`; a `FakeProgramActions : IProgramActions` with settable lists/plans and call recording; recording dialogs with programmable `Confirm`/`Ask`; real `OptimizerEngine` over `SessionStore(_root)` and `ChangeFactory.CreateDefault(FakeRegistry…)`; recording `ISystemShell`):
  - `Search_and_sort`: programs „Alpha“ (10 MB, 2020), „beta“ (null size, 2024), „Gamma“ Publisher „Alpha Soft“ → `SearchText = "alpha"` → 2 items; Sort Size → order Alpha, Gamma?, beta last; Sort Date → beta first; `CountText == "2 von 3 Programmen"`.
  - `Protected_row_has_hint_and_no_uninstall`: `CanUninstall == false`, `ProtectedHint` exact text, `UninstallCommand.CanExecute(item) == false`.
  - `Settings_route_opens_appsfeatures_and_sets_pending`: plan OpenSettings → `OpenLink` called with `"ms-settings:appsfeatures"`, `Pending == p`, launcher not called.
  - `Missing_uninstaller_enables_force_remove`: OpenSettings + `UninstallerMissing` (not MSI, not protected) → `CanForceRemove`; MSI program → false.
  - `Launch_confirm_is_final_and_names_irreversibility`: recorded confirm `final == true`, message contains „lässt sich in WinWächter nicht rückgängig machen“; declined → no run.
  - `Cancelled_exit_offers_nothing`: exit 1602 → no `Ask`, no leftovers, `LastSummary == "Deinstallation abgebrochen."`.
  - `Not_yet_answer_keeps_pending`: `Ask` → false → `Pending == p`, no `FindLeftovers` call.
  - `Finished_and_gone_shows_leftovers_panel`: Ask true, `IsStillListed` false, leftovers = folder (not preselected) + orphan task (preselected) → `ShowLeftovers`, `Leftovers.Count == 2`, preselection kept; `ApplyLeftoversCommand` journals a session titled `"Reste von Foo"` with the selected change only.
  - `Finished_but_still_listed_offers_force_remove`: after refresh the program is still read → new row `CanForceRemove == true`.
  - `Restart_hint_for_3010`: `LastSummary` contains „Neustart“.
  - `Force_remove_previews_every_change_and_journals`: plan with `FakeChange`s „A“, „B“ + note → confirm items `["A","B", note]`, message „Alles wird gesichert und lässt sich im Verlauf rückgängig machen.“; newest session `Title == "Erzwungen entfernt: Foo"` with 2 records; `ChangesApplied` raised.
  - `Actions_disabled_without_admin`.
  - `Shell_nav_order_with_programme`: titles `["Übersicht","Optimieren","Schutz","Datenschutz","Programme","Verlauf"]` (replace the existing nav test).
  - `ViewSmokeTests.All_pages_render_in_light_and_dark` renders `ProgramsView` with two items, a pending banner and a leftover panel.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~ProgramsViewModelTests|FullyQualifiedName~ViewSmokeTests"` → FAIL.
- [ ] **Step 3: Implement** VM, view, dialog additions, shell/nav, wiring (`AppServices`: `traceScan = () => [..autostart.Scan(), ..services.Scan(), ..tasks.Scan()]`, `ProgramService(new ProgramInventory(reg, rules), reg, fo, sig, new JobUninstallLauncher(), new UninstallLeftoverFinder(...), new ForcedRemovalPlanner(...), paths)`; reads on `Task.Run`).
- [ ] **Step 4: Run** filter + full suite → PASS; `$DOTNET build WinWaechter.sln -c Debug` → 0 errors.
- [ ] **Step 5: Commit** `feat(app): Programme page with uninstall, forced removal and leftover search`.

---

### Task 9: App – „Sicherheit“ page and overview tile

**Files:**
- Create: `src/WinWaechter.App/ViewModels/SecurityViewModel.cs`, `Views/SecurityView.xaml`, `Views/SecurityView.xaml.cs`
- Modify: `ViewModels/ShellViewModel.cs`, `ViewModels/OverviewViewModel.cs`, `Views/OverviewView.xaml`, `Views/ShellWindow.xaml`, `Services/AppServices.cs`, `App.xaml.cs` (start check in background), `docs/design-notes.md`, `tests/WinWaechter.Tests/ViewSmokeTests.cs`, `ProgramsViewModelTests.cs` / `PrivacyViewModelTests.cs` (shell ctor)
- Test: `tests/WinWaechter.Tests/SecurityViewModelTests.cs`

**Interfaces:**
- Consumes: `SecurityItem`, `Light`, `SecurityActionKind`, `SecurityCheck`, `SecuritySources`, `FirewallChange.Register`, Win sources (Tasks 6–7); `IDefender.ScanPathAsync`; `ISystemShell.OpenLink` (Task 8); `OptimizerEngine.ApplyAsync`; `ChangeGate`.
- Produces:
```csharp
public sealed class SecurityItemViewModel
{
    public SecurityItemViewModel(SecurityItem item);
    public SecurityItem Item { get; }  public string Title { get; }  public string Finding { get; }  public string Explanation { get; }
    public Tone LightTone { get; }     // Green Good, Yellow Watch, Red Alarm, Grey Neutral
    public string LightText { get; }   // Green "In Ordnung", Yellow "Prüfen", Red "Handlungsbedarf", Grey "Nicht prüfbar"
    public string? ActionLabel { get; }  public bool HasAction { get; }
    public IReadOnlyList<SecurityItemViewModel> Children { get; }
}
public sealed partial class SecurityViewModel : ObservableObject
{
    public SecurityViewModel(Func<CancellationToken, Task<IReadOnlyList<SecurityItem>>> check, OptimizerEngine engine,
        IDefender defender, IDialogService dialogs, ISystemShell shell, bool canChange, ChangeGate gate);
    public ObservableCollection<SecurityItemViewModel> Items { get; }
    public bool IsLoaded { get; }  public int RedCount { get; }  public int YellowCount { get; }   // top-level rows only
    public string TileText { get; }    // "–" before the first check; else $"{RedCount} rot · {YellowCount} gelb", both 0 → "Alles in Ordnung"
    public Tone TileTone { get; }      // Red > 0 Alarm, Yellow > 0 Watch, else Good
    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand<SecurityItemViewModel> RunActionCommand { get; }
        // Link → shell.OpenLink(Link) only when SystemLinks.IsAllowed; Change → confirm + engine.ApplyAsync("Sicherheit", …) in gate,
        // then Refresh + ChangesApplied; DefenderScan → confirm + defender.ScanPathAsync(ScanPath) with progress text
    public event EventHandler? ChangesApplied;
    public event EventHandler? StateChanged;   // after every refresh
}
// ShellViewModel(OptimizeViewModel, ProtectViewModel, SecurityViewModel, PrivacyViewModel, ProgramsViewModel, HistoryViewModel,
//                SettingsViewModel, UpdateViewModel, bool)
// PrimaryNav: Übersicht, Optimieren, Schutz, Sicherheit (glyph "\uE72E"), Datenschutz, Programme, Verlauf
// OverviewViewModel(OptimizeViewModel, ProtectViewModel, SecurityViewModel, UpdateViewModel, Action showResults)
//   + SecurityText (= security.TileText), SecurityTone; raised on security.StateChanged
```
Firewall „Einschalten“: Confirm(„Firewall einschalten“, „Die Änderung steht danach im Verlauf und lässt sich dort rückgängig machen.“, „Einschalten“, [change.Description]); finding Id `security|{Target}`, Area `Einstellung`. View: page title „Sicherheit“, „Erneut prüfen“, rows with a filled 10 px `Ellipse` in the tone colour (Grey = `InkMuted`) + `LightText` chip, Title, Finding, Explanation (`Text.Small`, `InkMuted`, MaxWidth 620), action button (`Button` default style; never `Button.Final`), children indented below their parent. Overview: readout „Sicherheit“ with `SecurityText` in the Messleiste (same pattern as „Offene Vorschläge“). `AppServices`: `factory` gets `FirewallChange.Register(factory, firewall)`; `check = ct => Task.Run(() => new SecurityCheck(sources).RunAsync(ct), ct)`. Startup: `App.StartBackgroundWorkAsync` adds `shell.Security.RefreshCommand.ExecuteAsync(null)` to the start tasks; first show of the page refreshes only when `!IsLoaded && !RefreshCommand.IsRunning`.

- [ ] **Step 1: Write the failing tests** (`SecurityViewModelTests.cs`; canned item lists; recording shell/dialogs; `FakeFirewall`; real engine over temp `SessionStore` with `FirewallChange.Register`; `FakeDefender`):
  - `Counts_and_tile_text`: rows Red, Yellow, Yellow, Grey, Green (+ a Yellow child under the Green one) → `RedCount == 1`, `YellowCount == 2`, `TileText == "1 rot · 2 gelb"`, `TileTone == Alarm`; all green → `"Alles in Ordnung"`; before refresh `"–"`.
  - `Light_texts_and_tones`: the four mappings above.
  - `Link_action_opens_only_allowed_links`: `ms-settings:windowsupdate` → `OpenLink` called; item with Link `https://evil.com` → not called.
  - `Firewall_change_is_journalled_and_undoable`: Private off item from `SecurityChecks.Firewall(fw, [])` → RunAction with confirm yes → `fw.State[Private] == true`, newest session `Title == "Sicherheit"` with one `firewall` record; `engine.UndoSession` → `false` again.
  - `Defender_scan_action_calls_scan_path`: child with `ScanPath` → `FakeDefender` recorded path.
  - `Change_disabled_without_admin`: `canChange: false` → `RunActionCommand.CanExecute(firewallItem) == false`, link items still executable.
  - `Overview_shows_security_tile`: after refresh `Overview.SecurityText == "1 rot · 2 gelb"`.
  - `Shell_nav_full_order`: `["Übersicht","Optimieren","Schutz","Sicherheit","Datenschutz","Programme","Verlauf"]` (replaces Task 8's nav test).
  - `ViewSmokeTests.All_pages_render_in_light_and_dark` renders `SecurityView` with all four lights and one child row.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~SecurityViewModelTests|FullyQualifiedName~ViewSmokeTests"` → FAIL.
- [ ] **Step 3: Implement** VM, view, overview tile, shell/nav, wiring, startup refresh.
- [ ] **Step 4: Run** filter + full suite → PASS; `$DOTNET build WinWaechter.sln -c Debug` → 0 errors.
- [ ] **Step 5: Commit** `feat(app): Sicherheit page with traffic lights and overview tile`.

---

### Task 10: Polish – undo progress, Defender status text, sidebar icon, rules in the app, author/about, version 0.3.0

**Files:**
- Create: `README.md`
- Modify: `installer/winwaechter.iss`, `src/WinWaechter.Core/Sys/SystemLinks.cs`, `tests/WinWaechter.Tests/UninstallTests.cs` (seven links), `src/WinWaechter.Core/Optimizer/OptimizerEngine.cs`, `Optimizer/Storage/MoveToAblageChange.cs`, `Security/Defender/DefenderClient.cs`, `src/WinWaechter.App/ViewModels/HistoryViewModel.cs`, `Views/HistoryView.xaml`, `ViewModels/SettingsViewModel.cs`, `Views/SettingsView.xaml`, `Views/ShellWindow.xaml`, `WinWaechter.App.csproj`, `Services/AppServices.cs`, `Services/AppEnvironment.cs`, `App.xaml.cs`, `Directory.Build.props`, `CHANGELOG.md`, `docs/design-notes.md`, `tests/WinWaechter.Tests/DefenderParsingTests.cs`, `ViewSmokeTests.cs`
- Test: `tests/WinWaechter.Tests/PolishTests.cs`

**Interfaces:**
- Consumes: `RulesLoader`, `RulesStore`, `RulesInfo`, `RulesFetcher`, `RulesUpdater`, `RulesUpdateResult` (Task 1); `UpdateHttp.CreateHandler`; `FileOwner`.
- Produces:
```csharp
public sealed record UndoProgress(int Index, int Count, string Text);
public interface IProgressUndo { void Undo(ChangeRecord record, SessionContext ctx, Action<int> filesRestored); }  // MoveToAblageChange implements
// OptimizerEngine
public const int FileProgressStep = 500;
public UndoResult UndoSession(SessionJournal s, IProgress<UndoProgress>? p = null);
public UndoResult UndoOne(SessionJournal s, string recordId, IProgress<UndoProgress>? p = null);
//   per record: Text $"({i}/{m}) {Description}"; IProgressUndo changes additionally every FileProgressStep files:
//   $"({i}/{m}) {Description} – {n} Dateien zurückgeholt"
// DefenderClient
internal static string StatusText(int id, bool active);   // known ids unchanged; unknown: active → $"Unbekannter Status (Code {id})",
                                                          // else $"Abgeschlossen (Code {id})"
// HistoryViewModel: [ObservableProperty] string _progressText; [ObservableProperty] double _progressValue (0..1 = (Index-1)/Count)
// AppEnvironment: public string RulesDir => Path.Combine(DataRoot, "Regeln");
// SettingsViewModel(AppSettings, AppEnvironment, UpdateViewModel, ISystemShell, RulesInfo rules,
//                   Func<CancellationToken, Task<RulesUpdateResult>> updateRules)
//   public string RulesText { get; }               // rules.DisplayText
//   [ObservableProperty] string? _rulesStatus;      // last RulesUpdateResult.Message
//   public IAsyncRelayCommand UpdateRulesCommand { get; }   // „Regeln aktualisieren“
```
Wiring: `AppServices.Create` replaces `RuleSet.Default` by `RulesLoader.Load(RuleSet.Default, store, env.Version)` with `store = new RulesStore(env.RulesDir, UpdateKeys.PublicKeyPem, env.IsElevated ? FileOwner.IsAdminOrSystem : null, env.IsElevated ? FileOwner.SetAdmins : null)`; log `Info.DisplayText`. `updateRules = ct => new RulesUpdater(new RulesFetcher(UpdateHttp.CreateHandler()), store, info.Version, env.Version).RunAsync(ct)` (one updater instance kept). `App.StartBackgroundWorkAsync`: when `CheckUpdatesOnStart`, also `shell.Settings.UpdateRulesCommand.ExecuteAsync(null)`. History view: `ProgressBar` (Minimum 0, Maximum 1, `ProgressValue`) + `ProgressText` visible while `IsBusy`. Sidebar: `WinWaechter.App.csproj` `<Resource Include="..\..\assets\icon\winwaechter-1024.png" Link="Assets\winwaechter.png" />`; in `ShellWindow.xaml` the vector dial `Grid` becomes `<Image Width="22" Height="22" RenderOptions.BitmapScalingMode="HighQuality" AutomationProperties.Name="WinWächter">` with `BitmapImage UriSource="pack://application:,,,/Assets/winwaechter.png" DecodePixelWidth="44"`. `design-notes.md`: replace „Das App-Icon kommt später vom Nutzer.“ by „App-Icon: `assets/icon` (mit Gemini erstellt), auch in der Seitenleiste.“ and add the glyphs Sicherheit `E72E`, Programme `E71D`.
Author (user request; no e-mail address anywhere; no EN/TR translation – that is a later version):
- `SystemLinks` gets `public const string Repository = "https://github.com/yIrtIcIkurt/winwaechter";` and `Allowed` now holds these **seven** entries (update the Task 3 test `SystemLinks_allow_only_fixed_list` accordingly).
- `SettingsViewModel`: `public const string AuthorText = "Erstellt von Tahsin Yilmaz";`, `public IRelayCommand OpenRepositoryCommand { get; }` → `shell.OpenLink(SystemLinks.Repository)`. `SettingsView.xaml`: new last section „Über WinWächter“ (`Text.Section`) with `AuthorText`, `VersionText`, and a link-style button „Projekt auf GitHub“ bound to `OpenRepositoryCommand`.
- `Directory.Build.props`: `<Product>WinWächter</Product>`, `<Authors>Tahsin Yilmaz</Authors>`, `<Company>Tahsin Yilmaz</Company>` (replaces `WinWächter`), `<Copyright>© 2026 Tahsin Yilmaz</Copyright>`.
- `installer/winwaechter.iss`: `AppPublisher=Tahsin Yilmaz` (replaces `WinWächter`), new `AppCopyright=© 2026 Tahsin Yilmaz` below it; keep the file UTF-8 **with BOM** (check the first bytes `EF BB BF` after editing).
- `README.md` (new, German, short): what WinWächter is (Prüfgerät für den eigenen Windows-Rechner, erst zeigen, dann ändern, alles umkehrbar, keine Telemetrie), feature list (Optimieren, Schutz, Sicherheit, Datenschutz, Programme, Verlauf), installation from GitHub Releases (`WinWaechter-Setup-x.y.z.exe`, signed updates in the app), build (`dotnet test`, `build-release.ps1`), author „Tahsin Yilmaz“, link to the repo.

`Directory.Build.props` `<Version>0.3.0</Version>`; `CHANGELOG.md` new top section:
```
## 0.3.0
- Neue Seite „Programme“: installierte Programme mit Suche und Sortierung; offizielle Deinstallation (bei Bedarf über die Windows-Einstellungen), danach gezielte Suche nach Resten.
- Notfall „Erzwungen entfernen“ für Programme ohne funktionierendes Deinstallationsprogramm – alles gesichert und im Verlauf rückgängig zu machen.
- Neue Seite „Sicherheit“: Ampel für Firewall, Defender, SmartScreen, Benutzerkontensteuerung, Remotedesktop, Verschlüsselung, Windows-Updates, lauschende Programme und Browser-Erweiterungen; Übersicht zeigt die Anzahl.
- Regeln lassen sich signiert von GitHub aktualisieren; neue Regeln können den Schutz nur erweitern.
- Rückgängig zeigt den Fortschritt; Defender-Funde mit unbekanntem Status werden verständlich benannt; App-Icon in Fenster und Seitenleiste.
- Einstellungen: Abschnitt „Über WinWächter“ mit Autor und Link zum Projekt.
```

- [ ] **Step 1: Write the failing tests** (`PolishTests.cs` and edits):
  - `Undo_reports_per_record_and_every_500_files`: session with one `temp` change over 1 200 files (real temp dir, `WinFileOps`) plus one `FakeChange` → collected `UndoProgress` texts contain `"(1/2) "` + first description, `"– 500 Dateien zurückgeholt"`, `"– 1000 Dateien zurückgeholt"`, and `"(2/2) "`; `Count == 2` in every report (records undone in reverse order: index follows undo order).
  - `Undo_without_progress_still_works`: `UndoSession(s)` (no progress) → same `UndoResult` as before.
  - `History_shows_progress_while_undoing`: VM `ProgressText` non-empty during undo (fake change that waits on a gate), empty afterwards.
  - `DefenderParsingTests.Status_text_map`: `InlineData(42, …)` replaced by `StatusText(106, false) == "Abgeschlossen (Code 106)"`, `StatusText(106, true) == "Unbekannter Status (Code 106)"`, `StatusText(3, true) == "In Quarantäne"`; `JoinThreats` passes `t.IsActive`.
  - `Settings_shows_rules_and_updates_them`: `RulesInfo(2, true, utc)` → `RulesText` starts with `"Regeln: Version 2 vom "`; `UpdateRulesCommand` with a func returning `RulesUpdateResult(Accepted, "Neue Regeln …")` → `RulesStatus` equals that message.
  - `Settings_about_block_opens_repository`: `SettingsViewModel.AuthorText == "Erstellt von Tahsin Yilmaz"`; `OpenRepositoryCommand` → recording shell got exactly `"https://github.com/yIrtIcIkurt/winwaechter"`; `SystemLinks.IsAllowed(SystemLinks.Repository)`.
  - `Assembly_metadata_names_author`: `typeof(SettingsViewModel).Assembly` has `AssemblyCompanyAttribute.Company == "Tahsin Yilmaz"`, `AssemblyCopyrightAttribute.Copyright == "© 2026 Tahsin Yilmaz"`, `AssemblyProductAttribute.Product == "WinWächter"`.
  - `Sidebar_icon_resource_exists`: in `ViewSmokeTests` (STA, after `new App.App()`), `Application.GetResourceStream(new Uri("pack://application:,,,/WinWaechter.App;component/Assets/winwaechter.png"))` not null; rendering `ShellWindow` still passes.
  - `ScaffoldTests.Core_assembly_version_matches_props` passes with 0.3.0 (no edit needed, just run).
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~PolishTests|FullyQualifiedName~DefenderParsingTests|FullyQualifiedName~ViewSmokeTests"` → FAIL.
- [ ] **Step 3: Implement** engine progress, `MoveToAblageChange : IProgressUndo` (the existing `Undo` calls the new overload with a no-op), status text, history progress UI, rules wiring + settings row, icon, version, CHANGELOG, design notes.
- [ ] **Step 4: Run** full suite → PASS; `$DOTNET build WinWaechter.sln -c Release` → 0 errors.
- [ ] **Step 5: Commit** `feat: undo progress, Defender status text, sidebar icon, rules in settings, about block and author; version 0.3.0`.

---

### Task 11: Installer, on-machine test, release (controller only – needs the user's chat approval; not for subagents)

**Files:** none changed unless a defect is found (then fix in a new task with its own test). `rules/rules.json.sig` is written by the build and committed with approval.

- [ ] **Step 1:** Ask for approval, then `powershell -File build-release.ps1 -Version 0.3.0` → tests green, rules signed and verified (`rules.json.sig` updated), `dist\WinWaechter-Setup-0.3.0.exe`, `SHA256SUMS`, `SHA256SUMS.sig`, self-check OK; installer shows icon and wizard images. Commit `rules/rules.json.sig` (`build: sign rules for 0.3.0`).
- [ ] **Step 2:** With approval, on the user's machine (0.2.1 installed): install 0.3.0 (or update via the app once released). Check: Programme list ≈ 106 entries, flags plausible (Dell/Lexware/Microsoft „Geschützt“, no uninstall button); the user picks one harmless program → official uninstall (watch the vendor window), answer „Ja“, leftover panel plausible, move + undo one leftover; Settings route for an HKCU entry opens `ms-settings:appsfeatures`; Sicherheit page: firewall all green, UAC green, RDP green/grey, 11 Chrome extensions listed, listening programs listed, WU row within 30 s; Übersicht tile; Verlauf undo with progress text; sidebar icon light/dark; Einstellungen „Über WinWächter“ opens the repo; installer shows publisher „Tahsin Yilmaz“. No forced removal on a real program unless the user names one.
- [ ] **Step 3:** Rules update test, only after explicit approval for pushing to `main` (after the branch is merged by the user): on `main`, `powershell -File publish-rules.ps1` (bumps to `rulesVersion` 2, signs, asks, commits, pushes) → in the app „Regeln aktualisieren“ → „Neue Regeln (Version 2) geladen …“, restart → Settings shows „Regeln: Version 2 vom …“.
- [ ] **Step 4:** Only after explicit approval: `powershell -File build-release.ps1 -Version 0.3.0 -Publish`; verify the update 0.2.1 → 0.3.0 inside the app; merge/push decisions stay with the user.

---

## Self-Review

- **Spec coverage:** user addition (author, about block, README, metadata) → Task 10; §1.1 → Task 2 (+ search/sort Task 8); §1.2 + §7.1/§7.2 + parser correction → Tasks 3, 8; §1.3 + §7.4 + `regkey` correction → Tasks 4, 8; §1.4 → Task 5, panel in Task 8; §2 table + §7.5–7.8 + technical corrections (FwPolicy2, Home RDP, PID 4 grey, TCP only, PromptOnSecureDesktop) → Tasks 6, 7, page/tile Task 9, link allow-list Task 3; §3 + §7.3 → Task 1 (Core, tooling, scripts), app wiring Task 10; §4 + §7 icon note → Task 10 (ico/installer images already exist, sidebar icon added); §5 nav/design → Tasks 8, 9; §6 version/CHANGELOG/tests/manual test → Tasks 10, 11.
- **Types:** `InstalledProgram` (`Id`, `RegistryKey`, `IsMachineEntry`, `Flag`), `UninstallRoots.IsDirectChild`, `UninstallPlan`/`UninstallRoute`/`UninstallCommand`, `IUninstallLauncher.RunAsync(UninstallCommand)`, `RegKeyChange(reg, hive, key, displayName)`, `ForcedRemovalPlanner.IsOffered/Plan`, `IProgramActions`, `Finding.ExePath`, `RunningProcess.Pid`, `SecurityItem`/`Light`/`SecurityIds`, `FirewallChange.Register`, `SecurityCheck(SecuritySources)`, `RulesInfo.DisplayText`, `RulesUpdater.RunAsync`, `ISystemShell.OpenLink`, `IDialogService.Ask`, and the final `ShellViewModel` parameter order are used with the same names in every consuming task.
- **Review Focus:** each line has its named test: 1 → Task 1, 2 → Task 4 (two tests), 3 → Task 4, 4 → Task 7 (two tests), 5 → Task 7.
- **Decisions not stated in the spec** are listed in the hand-off report.
