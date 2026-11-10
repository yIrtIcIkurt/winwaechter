namespace WinWaechter.Core.Sys;

public enum SignatureState { Valid, Unsigned, Invalid, Unknown }

public sealed record SignatureInfo(SignatureState State, string? Publisher, bool IsMicrosoft);

public interface ISignatureVerifier
{
    SignatureInfo Verify(string path);
}

/// <summary>The German „Signatur: …“ line shown in reasons and dialogs.</summary>
public static class SignatureText
{
    public static string Line(SignatureInfo? s) => s?.State switch
    {
        SignatureState.Valid => string.IsNullOrWhiteSpace(s.Publisher) ? "Signatur: gültig" : $"Signatur: gültig, Herausgeber {s.Publisher}",
        SignatureState.Unsigned => "Signatur: nicht signiert",
        SignatureState.Invalid => "Signatur: ungültig",
        _ => "Signatur: nicht prüfbar",
    };
}
