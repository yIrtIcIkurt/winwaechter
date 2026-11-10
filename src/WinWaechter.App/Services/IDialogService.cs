namespace WinWaechter.App.Services;

/// <summary>Modal questions and messages, so view models stay free of WPF windows.</summary>
public interface IDialogService
{
    /// <summary>Returns true when the user confirms. items are listed below the message.</summary>
    bool Confirm(string title, string message, string confirmText, IReadOnlyList<string>? items = null, bool final = false);

    /// <summary>A yes/no question with own texts for both buttons; true = yes.</summary>
    bool Ask(string title, string message, string yesText, string noText) => Confirm(title, message, yesText);

    void Inform(string title, string message, IReadOnlyList<string>? items = null);
}

/// <summary>Opens folders in Explorer and the fixed system links (SystemLinks.Allowed).</summary>
public interface ISystemShell
{
    void OpenFolder(string path);

    /// <summary>Opens a URI only when it is on SystemLinks.Allowed; anything else is ignored.</summary>
    void OpenLink(string uri);
}
