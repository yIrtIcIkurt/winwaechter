using WinWaechter.Core.Sys;

namespace WinWaechter.Tests;

// Read-only checks against the real WinVerifyTrust APIs.
public sealed class SignatureSmokeTests : IDisposable
{
    static readonly string Sys32 = Environment.SystemDirectory;
    readonly string _dir = Path.Combine(Path.GetTempPath(), "ww-sig-" + Guid.NewGuid().ToString("N"));
    readonly WinTrustVerifier _v = new();

    public SignatureSmokeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Notepad_is_microsoft_signed()
    {
        var r = _v.Verify(Path.Combine(Sys32, "notepad.exe"));
        Assert.Equal(SignatureState.Valid, r.State);
        Assert.True(r.IsMicrosoft, $"Publisher: {r.Publisher}");
    }

    [Theory]
    [InlineData("ntoskrnl.exe")]
    [InlineData("cmd.exe")]
    public void Catalog_signed_system_file_is_valid(string file)
    {
        var r = _v.Verify(Path.Combine(Sys32, file));
        Assert.NotEqual(SignatureState.Unsigned, r.State);
        Assert.Equal(SignatureState.Valid, r.State);
        Assert.True(r.IsMicrosoft, $"Publisher: {r.Publisher}");
    }

    [Fact]
    public void Catalog_only_file_is_valid_microsoft()
    {
        // Find a System32 binary without an embedded signature so the catalog path is really exercised.
        var file = Directory.EnumerateFiles(Sys32, "*.exe")
            .FirstOrDefault(f => !HasEmbeddedSignature(f));
        Assert.NotNull(file);
        var r = _v.Verify(file!);
        Assert.Equal(SignatureState.Valid, r.State);
        Assert.True(r.IsMicrosoft, $"{file} Publisher: {r.Publisher}");
    }

    [Fact]
    public void Embedded_signed_file_is_valid_microsoft()
    {
        var file = EmbeddedSignedSystemFile();
        var r = _v.Verify(file);
        Assert.Equal(SignatureState.Valid, r.State);
        Assert.True(r.IsMicrosoft, $"{file} Publisher: {r.Publisher}");
    }

    [Fact]
    public void Tampered_signed_copy_is_invalid()
    {
        var src = EmbeddedSignedSystemFile();
        var f = Path.Combine(_dir, "tampered.exe");
        var bytes = File.ReadAllBytes(src);
        bytes[bytes.Length / 3] ^= 0xFF; // inside the signed image, before the signature blob
        File.WriteAllBytes(f, bytes);
        var r = _v.Verify(f);
        Assert.Equal(SignatureState.Invalid, r.State);
        Assert.False(r.IsMicrosoft);
    }

    [Fact]
    public void Temp_file_is_unsigned()
    {
        var f = Path.Combine(_dir, "stub.exe");
        File.WriteAllBytes(f, "MZ"u8.ToArray());
        var r = _v.Verify(f);
        Assert.NotEqual(SignatureState.Valid, r.State);
        Assert.Contains(r.State, new[] { SignatureState.Unsigned, SignatureState.Invalid });
        Assert.False(r.IsMicrosoft);
    }

    [Fact]
    public void Missing_file_is_unknown()
    {
        var r = _v.Verify(Path.Combine(_dir, "missing.exe"));
        Assert.Equal(SignatureState.Unknown, r.State);
        Assert.False(r.IsMicrosoft);
    }

    [Fact]
    public void Result_is_cached_until_file_changes()
    {
        var f = Path.Combine(_dir, "stub2.exe");
        File.WriteAllBytes(f, "MZ"u8.ToArray());
        var a = _v.Verify(f);
        Assert.Same(a, _v.Verify(f));
        File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddMinutes(-5));
        Assert.NotSame(a, _v.Verify(f));
    }

    static string EmbeddedSignedSystemFile() =>
        Directory.EnumerateFiles(Sys32, "*.exe").First(f => HasEmbeddedSignature(f) && new FileInfo(f).Length > 100_000);

    // PE security directory (index 4) non-empty = embedded Authenticode signature.
    static bool HasEmbeddedSignature(string path)
    {
        try
        {
            using var br = new BinaryReader(File.OpenRead(path));
            br.BaseStream.Seek(0x3C, SeekOrigin.Begin);
            var pe = br.ReadInt32();
            br.BaseStream.Seek(pe + 24, SeekOrigin.Begin);
            var magic = br.ReadUInt16();
            var dirs = pe + 24 + (magic == 0x20B ? 112 : 96);
            br.BaseStream.Seek(dirs + 4 * 8, SeekOrigin.Begin);
            br.ReadUInt32();
            return br.ReadUInt32() != 0;
        }
        catch { return true; }
    }
}
