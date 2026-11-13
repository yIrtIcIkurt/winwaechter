namespace WinWaechter.Core.Actions;

/// <summary>Result of a one-off action. Link: an allowed system link to offer afterwards.</summary>
public sealed record ActionOutcome(bool Success, string Message, string? Link = null);

/// <summary>
/// A one-off action without undo. ConfirmText == null means harmless (click only);
/// a final action must always carry a confirm text.
/// </summary>
public sealed record SystemAction
{
    public SystemAction(
        string Id,
        string Title,
        string ButtonText,
        string? ConfirmText,
        bool Final,
        Func<IProgress<string>?, CancellationToken, Task<ActionOutcome>> RunAsync,
        IReadOnlyList<string>? Items = null)
    {
        if (Final && ConfirmText is null)
            throw new ArgumentException("Eine endgültige Aktion braucht einen Bestätigungstext.", nameof(ConfirmText));
        this.Id = Id;
        this.Title = Title;
        this.ButtonText = ButtonText;
        this.ConfirmText = ConfirmText;
        this.Final = Final;
        this.RunAsync = RunAsync;
        this.Items = Items;
    }

    public string Id { get; init; }
    public string Title { get; init; }
    public string ButtonText { get; init; }
    public string? ConfirmText { get; init; }
    public bool Final { get; init; }
    public Func<IProgress<string>?, CancellationToken, Task<ActionOutcome>> RunAsync { get; init; }
    public IReadOnlyList<string>? Items { get; init; }
}
