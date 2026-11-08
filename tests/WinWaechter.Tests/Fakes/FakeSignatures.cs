using WinWaechter.Core.Sys;

namespace WinWaechter.Tests.Fakes;

// Signature results per path; anything not set returns Default (Unknown).
public sealed class FakeSignatures : ISignatureVerifier
{
    readonly Dictionary<string, SignatureInfo> _byPath = new(StringComparer.OrdinalIgnoreCase);

    public SignatureInfo Default { get; set; } = new(SignatureState.Unknown, null, false);
    public List<string> VerifyCalls { get; } = [];

    public FakeSignatures Set(string path, SignatureInfo info)
    {
        _byPath[path] = info;
        return this;
    }

    public FakeSignatures Microsoft(string path) => Set(path, new(SignatureState.Valid, "Microsoft Windows", true));
    public FakeSignatures Signed(string path, string publisher) => Set(path, new(SignatureState.Valid, publisher, false));
    public FakeSignatures Unsigned(string path) => Set(path, new(SignatureState.Unsigned, null, false));

    public SignatureInfo Verify(string path)
    {
        VerifyCalls.Add(path);
        return _byPath.GetValueOrDefault(path, Default);
    }
}
