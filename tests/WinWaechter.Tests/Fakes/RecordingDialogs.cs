using WinWaechter.App.Services;

namespace WinWaechter.Tests.Fakes;

/// <summary>One dialog the view model showed. Button is the confirm/yes text (null for Inform).</summary>
public sealed record DialogCall(string Title, string Message, IReadOnlyList<string> Items, bool Final, string? Button);

// Records every Confirm/Ask/Inform; ConfirmAnswer and AskAnswer decide what the "user" clicks.
public sealed class RecordingDialogs : IDialogService
{
    public bool ConfirmAnswer { get; set; } = true;
    public bool AskAnswer { get; set; } = true;

    public List<DialogCall> Confirms { get; } = [];
    public List<DialogCall> Asks { get; } = [];
    public List<DialogCall> Informs { get; } = [];

    public bool Confirm(string title, string message, string confirmText, IReadOnlyList<string>? items = null, bool final = false)
    {
        Confirms.Add(new(title, message, items ?? [], final, confirmText));
        return ConfirmAnswer;
    }

    public bool Ask(string title, string message, string yesText, string noText)
    {
        Asks.Add(new(title, message, [], false, yesText));
        return AskAnswer;
    }

    public void Inform(string title, string message, IReadOnlyList<string>? items = null) =>
        Informs.Add(new(title, message, items ?? [], false, null));
}
