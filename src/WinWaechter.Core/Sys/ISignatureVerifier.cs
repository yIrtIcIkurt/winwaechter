namespace WinWaechter.Core.Sys;

public enum SignatureState { Valid, Unsigned, Invalid, Unknown }

public sealed record SignatureInfo(SignatureState State, string? Publisher, bool IsMicrosoft);

public interface ISignatureVerifier
{
    SignatureInfo Verify(string path);
}
