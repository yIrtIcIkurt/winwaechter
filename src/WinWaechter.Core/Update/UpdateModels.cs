namespace WinWaechter.Core.Update;

public sealed record UpdateInfo(SemVer Version, string Notes, Uri SetupUrl, Uri SumsUrl, Uri SigUrl, string SetupName);

public sealed record UpdateCheckResult(UpdateInfo? Available, string Message);

public sealed record VerifiedUpdate(string SetupPath, SemVer Version);

// Message is German and safe to show to the user.
public sealed class UpdateVerificationException : Exception
{
    public UpdateVerificationException(string message) : base(message) { }
}

public sealed class UpdateDownloadException : Exception
{
    public UpdateDownloadException(string message, Exception? inner = null) : base(message, inner) { }
}
