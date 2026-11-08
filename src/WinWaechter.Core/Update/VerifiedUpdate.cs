namespace WinWaechter.Core.Update;

// Proof that the setup file passed verification. Only ReleaseVerifier can create one.
// It keeps the setup open (read, no write/delete sharing) until disposed, so the verified bytes
// cannot be swapped before launch. Launch the installer while it is alive, then dispose.
public sealed class VerifiedUpdate : IDisposable
{
    readonly FileStream? _lock;

    public string SetupPath { get; }
    public SemVer Version { get; }

    internal VerifiedUpdate(string setupPath, SemVer version, FileStream? hold)
    {
        SetupPath = setupPath;
        Version = version;
        _lock = hold;
    }

    public void Dispose() => _lock?.Dispose();
}
