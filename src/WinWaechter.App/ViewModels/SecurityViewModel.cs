using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinWaechter.App.Services;
using WinWaechter.Core.Actions;
using WinWaechter.Core.Changes;
using WinWaechter.Core.Logging;
using WinWaechter.Core.Model;
using WinWaechter.Core.Optimizer;
using WinWaechter.Core.Security.Checks;
using WinWaechter.Core.Security.Defender;
using WinWaechter.Core.Sys;

namespace WinWaechter.App.ViewModels;

/// <summary>One traffic-light row: light, title, finding, explanation, optional action, child rows.</summary>
public sealed class SecurityItemViewModel
{
    public SecurityItemViewModel(SecurityItem item)
    {
        Item = item;
        (LightTone, LightText) = item.Light switch
        {
            Light.Green => (Tone.Good, "In Ordnung"),
            Light.Yellow => (Tone.Watch, "Prüfen"),
            Light.Red => (Tone.Alarm, "Handlungsbedarf"),
            _ => (Tone.Neutral, "Nicht prüfbar"),
        };
        // a row may name its state itself („Ansehen“, „Deaktiviert“); grey with a name is a known, calm state
        if (!string.IsNullOrWhiteSpace(item.Chip))
        {
            LightText = item.Chip;
            if (item.Light == Light.Grey) LightTone = Tone.Calm;
        }
        Children = (item.Children ?? []).Select(c => new SecurityItemViewModel(c)).ToList();
    }

    public SecurityItem Item { get; }
    public string Title => Item.Title;
    public string Finding => Item.Finding;
    public string Explanation => Item.Explanation;
    public Tone LightTone { get; }
    public string LightText { get; }
    public string? ActionLabel => HasAction ? Item.ActionLabel : null;
    public bool HasAction => Item.Action != SecurityActionKind.None && !string.IsNullOrWhiteSpace(Item.ActionLabel);

    /// <summary>A second button next to the action (a listener row: „Eingehende Verbindungen sperren“).</summary>
    public bool HasChangeButton => Item.Change is not null && !string.IsNullOrWhiteSpace(Item.ChangeLabel);
    public string? ChangeLabel => HasChangeButton ? Item.ChangeLabel : null;
    public IReadOnlyList<SecurityItemViewModel> Children { get; }
}

/// <summary>„Sicherheit“: traffic lights for the protection of the system; fixes (journalled changes), one-off actions, links and a Defender scan.</summary>
public sealed partial class SecurityViewModel : ObservableObject
{
    public const string ReadingText = "Sicherheit wird geprüft …";
    public const string ConfirmNote = "Die Änderung steht danach im Verlauf und lässt sich dort rückgängig machen.";
    public const string SysprotectNote = "Den Computerschutz schaltet WinWächter auch beim Rückgängigmachen nicht wieder ab, " +
        "weil Windows dabei alle Wiederherstellungspunkte löscht.";
    public const string OpenSecurityText = "Windows-Sicherheit öffnen";

    readonly Func<CancellationToken, Task<IReadOnlyList<SecurityItem>>> _check;
    readonly OptimizerEngine _engine;
    readonly IDefender _defender;
    readonly IDialogService _dialogs;
    readonly ISystemShell _shell;
    readonly ChangeGate _gate;
    readonly Func<bool> _defenderBusy;
    readonly IExtensionPages? _extensionPages;
    readonly Action<string> _copyText;
    readonly ActionRunner? _actions;

    /// <param name="actions">The app's shared runner for one-off actions; without it action rows offer no button.</param>
    public SecurityViewModel(Func<CancellationToken, Task<IReadOnlyList<SecurityItem>>> check, OptimizerEngine engine,
        IDefender defender, IDialogService dialogs, ISystemShell shell, bool canChange, ChangeGate gate, Func<bool>? defenderBusy = null,
        IExtensionPages? extensionPages = null, Action<string>? copyText = null, ActionRunner? actions = null)
    {
        _actions = actions;
        _extensionPages = extensionPages;
        _copyText = copyText ?? System.Windows.Clipboard.SetText;
        _defenderBusy = defenderBusy ?? (() => false);
        _check = check;
        _engine = engine;
        _defender = defender;
        _dialogs = dialogs;
        _shell = shell;
        CanChange = canChange;
        _gate = gate;
        gate.PropertyChanged += (_, _) =>
        {
            RunActionCommand.NotifyCanExecuteChanged();
            RunChangeCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(GateHint));
        };
    }

    /// <summary>Raised after changes were applied (the history should reload).</summary>
    public event EventHandler? ChangesApplied;

    /// <summary>Raised after every refresh (the overview tile reads the counts).</summary>
    public event EventHandler? StateChanged;

    public bool CanChange { get; }
    public bool ShowAdminNotice => !CanChange;
    public string AdminNotice => AppEnvironment.AdminNotice;
    public string? GateHint => _gate.IsBusyFor(this) ? ChangeGate.BusyHint : null;

    public ObservableCollection<SecurityItemViewModel> Items { get; } = [];

    public bool IsLoaded { get; private set; }

    // top-level rows only; child rows explain their parent
    public int RedCount => Items.Count(i => i.Item.Light == Light.Red);
    public int YellowCount => Items.Count(i => i.Item.Light == Light.Yellow);

    public string TileText => !IsLoaded ? "–"
        : RedCount == 0 && YellowCount == 0 ? "Alles in Ordnung"
        : $"{RedCount} rot · {YellowCount} gelb";

    public Tone TileTone => RedCount > 0 ? Tone.Alarm : YellowCount > 0 ? Tone.Watch : Tone.Good;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunActionCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunChangeCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _progressText = "";

    [ObservableProperty]
    private string? _lastSummary;

    // ---------- check ----------

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync(CancellationToken ct)
    {
        IsBusy = true;
        ProgressText = ReadingText;
        try
        {
            await LoadAsync(ct);
        }
        finally
        {
            ProgressText = "";
            IsBusy = false;
        }
    }

    bool CanRefresh() => !IsBusy;

    // never throws; a failed or cancelled check keeps the previous rows
    async Task LoadAsync(CancellationToken ct)
    {
        try
        {
            var rows = await _check(ct);
            Items.Clear();
            foreach (var r in rows) Items.Add(new SecurityItemViewModel(r));
            IsLoaded = true;
            LastSummary = null;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            FileLog.Error("Sicherheitsprüfung fehlgeschlagen", ex);
            LastSummary = $"Prüfen fehlgeschlagen: {ex.Message}";
        }
        OnPropertyChanged(nameof(IsLoaded));
        OnPropertyChanged(nameof(RedCount));
        OnPropertyChanged(nameof(YellowCount));
        OnPropertyChanged(nameof(TileText));
        OnPropertyChanged(nameof(TileTone));
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---------- actions ----------

    [RelayCommand(CanExecute = nameof(CanRunAction))]
    private async Task RunActionAsync(SecurityItemViewModel? row)
    {
        if (row is null || !CanRunAction(row)) return;
        var item = row.Item;
        switch (item.Action)
        {
            case SecurityActionKind.Link:
                // only the fixed list of system links is ever opened
                if (SystemLinks.IsAllowed(item.Link)) _shell.OpenLink(item.Link!);
                else FileLog.Warn($"Link nicht erlaubt: {item.Link}");
                break;
            case SecurityActionKind.Change:
                await ApplyChangeAsync(item, item.ActionLabel ?? "Anwenden");
                break;
            case SecurityActionKind.DefenderScan:
                await ScanAsync(item);
                break;
            case SecurityActionKind.ExtensionPage:
                await OpenExtensionPageAsync(item.Extension!);
                break;
            case SecurityActionKind.Action:
                await RunSystemActionAsync(item.Act!);
                break;
        }
    }

    /// <summary>The second button of a row: applies <see cref="SecurityItem.Change"/> like a fix.</summary>
    [RelayCommand(CanExecute = nameof(CanRunChange))]
    private async Task RunChangeAsync(SecurityItemViewModel? row)
    {
        if (row is null || !CanRunChange(row)) return;
        await ApplyChangeAsync(row.Item, row.ChangeLabel!);
    }

    bool CanRunChange(SecurityItemViewModel? row) => row is { HasChangeButton: true } && CanChange && !IsBusy && !_gate.IsBusy;

    bool CanRunAction(SecurityItemViewModel? row) => row is { HasAction: true } && row.Item.Action switch
    {
        SecurityActionKind.Link => true,
        SecurityActionKind.Change => row.Item.Change is not null && CanChange && !IsBusy && !_gate.IsBusy,
        // like the Schutz page: needs admin rights and no other Defender operation
        SecurityActionKind.DefenderScan => !string.IsNullOrWhiteSpace(row.Item.ScanPath) && CanChange && !IsBusy && !_defenderBusy(),
        // opening the browser's page changes nothing itself; the user removes the extension there
        SecurityActionKind.ExtensionPage => row.Item.Extension is not null && _extensionPages is not null && !IsBusy,
        // one-off actions change the system: admin rights, the shared runner and a free gate
        SecurityActionKind.Action => row.Item.Act is not null && _actions is not null && CanChange && !IsBusy && !_gate.IsBusy,
        _ => false,
    };

    public const string PasteKeys = "Strg+L, Strg+V und Enter";

    public static string HowToText(string browser) =>
        $"WinWächter kopiert die Adresse der Erweiterung und öffnet {browser}. Drücken Sie dort {PasteKeys} und klicken Sie " +
        $"bei der Erweiterung auf „Entfernen“. ({browser} lässt andere Programme diese Seite nicht direkt öffnen.)";

    public static string OpenedText(string browser) =>
        $"{browser} ist geöffnet, die Adresse ist kopiert: {PasteKeys}, dann „Entfernen“. Danach „Erneut prüfen“.";

    public static string OpenedNoCopyText(string browser, string address) =>
        $"{browser} ist geöffnet. Geben Sie dort in die Adresszeile ein: {address} – dann „Entfernen“. Danach „Erneut prüfen“.";

    async Task OpenExtensionPageAsync(ExtensionTarget target)
    {
        // busy while the browser starts: a second click cannot start it twice (CanRunAction checks IsBusy)
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            // the opener validates browser, exe and id again; the row is only a hint
            var address = _extensionPages!.Address(target.Browser, target.Id);
            if (!_dialogs.Confirm($"In {target.Browser} entfernen", HowToText(target.Browser), $"{target.Browser} öffnen", [address]))
                return;
            var copied = true;
            try { _copyText(address); }
            catch (Exception ex)
            {
                // the clipboard can be held by another program; the address is then shown to type in
                FileLog.Warn("Adresse der Erweiterung nicht kopiert", ex);
                copied = false;
            }
            ProgressText = $"{target.Browser} wird geöffnet …";
            await Task.Run(() => _extensionPages.Open(target.Browser));
            LastSummary = copied ? OpenedText(target.Browser) : OpenedNoCopyText(target.Browser, address);
        }
        catch (Exception ex)
        {
            FileLog.Warn($"Erweiterungsseite nicht geöffnet: {target.Browser}", ex);
            _dialogs.Inform($"{target.Browser} ließ sich nicht öffnen",
                ex is ExtensionPageException ? ex.Message : $"{target.Browser} ließ sich nicht starten.");
        }
        finally
        {
            ProgressText = "";
            IsBusy = false;
        }
    }

    // The runner confirms (and says „endgültig“), takes the gate and logs; a blocked result can point to Windows-Sicherheit.
    async Task RunSystemActionAsync(SystemAction act)
    {
        if (IsBusy) return;
        // busy only once the action really runs, i.e. after the confirmation and inside the gate
        var run = act with
        {
            RunAsync = (p, ct) =>
            {
                IsBusy = true;
                ProgressText = $"{act.Title} …";
                return act.RunAsync(p, ct);
            },
        };
        try
        {
            var outcome = await _actions!.RunAsync(this, run, new Progress<string>(s => ProgressText = s));
            if (outcome is null) return;
            IsBusy = true;
            if (outcome.Link is { } link)
            {
                if (!SystemLinks.IsAllowed(link)) FileLog.Warn($"Link nicht erlaubt: {link}");
                else if (_dialogs.Confirm(act.Title, outcome.Message, OpenSecurityText)) _shell.OpenLink(link);
            }
            ProgressText = ReadingText;
            await LoadAsync(CancellationToken.None);
            LastSummary = outcome.Message;
        }
        finally
        {
            ProgressText = "";
            IsBusy = false;
        }
    }

    async Task ApplyChangeAsync(SecurityItem item, string label)
    {
        var change = item.Change!;
        var title = change.Kind == "firewall" ? "Firewall einschalten" : label;
        // Computerschutz: undo puts only the services back (the confirm line says „Nur teilweise rückgängig“)
        var note = change.Kind == "sysprotect" ? $"{ConfirmNote} {SysprotectNote}" : ConfirmNote;
        if (!_dialogs.Confirm(title, note, label, [ChangeText.ConfirmLine(change)])) return;

        var finding = new Finding($"security|{change.Target}", FindingArea.Einstellung, change.Description, change.Target, [],
            Rating.Info, true, change);
        var applied = false;
        var ran = await _gate.RunAsync(this, async () =>
        {
            IsBusy = true;
            ProgressText = "Sicherung wird vorbereitet …";
            try
            {
                var progress = new Progress<string>(s => ProgressText = s);
                var result = await _engine.ApplyAsync("Sicherheit", [finding], progress, CancellationToken.None);
                applied = true;
                _dialogs.Inform("Zusammenfassung",
                    result.Applied == 1 ? "Die Änderung ist angewendet und steht im Verlauf."
                        : "Die Änderung ist fehlgeschlagen. Details stehen im Verlauf.",
                    [result.RestorePoint.Message]);
                LastSummary = result.Applied == 1 ? "Zuletzt: 1 angewendet." : "Zuletzt: 1 fehlgeschlagen.";
            }
            catch (Exception ex)
            {
                FileLog.Error($"Sicherheitsänderung fehlgeschlagen: {change.Target}", ex);
                _dialogs.Inform("Anwenden fehlgeschlagen", ex.Message);
            }
            ProgressText = ReadingText;
            try
            {
                var summary = LastSummary;
                await LoadAsync(CancellationToken.None);
                if (applied && LastSummary is null) LastSummary = summary;
            }
            finally
            {
                ProgressText = "";
                IsBusy = false;
            }
            if (applied) ChangesApplied?.Invoke(this, EventArgs.Empty);
        });
        if (!ran) LastSummary = ChangeGate.BusyHint;
    }

    async Task ScanAsync(SecurityItem item)
    {
        var path = item.ScanPath!;
        if (!_dialogs.Confirm("Von Defender prüfen lassen", "Defender prüft diese Datei. Das kann einen Moment dauern.", "Prüfen", [path]))
            return;
        IsBusy = true;
        ProgressText = $"Defender prüft {path} …";
        try
        {
            var exit = await _defender.ScanPathAsync(path, CancellationToken.None);
            LastSummary = exit == 0 ? $"Defender hat {path} geprüft." : $"Defender meldet Code {exit} für {path}.";
        }
        catch (Exception ex)
        {
            FileLog.Warn($"Defender-Prüfung fehlgeschlagen: {path}", ex);
            LastSummary = $"Defender-Prüfung fehlgeschlagen: {ex.Message}";
        }
        finally
        {
            ProgressText = "";
            IsBusy = false;
        }
    }
}
