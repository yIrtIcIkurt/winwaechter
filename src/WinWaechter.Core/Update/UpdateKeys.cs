namespace WinWaechter.Core.Update;

public static class UpdateKeys
{
    // ECDSA P-256 public key (SPKI PEM). The private key lives only in C:\Users\tahsi\dev\winwaechter-keys.
    public const string PublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAERAt4zlYr1HW1jPIuJ3zY1AETq2hK
        vuKmi1XgFdc5DNKl8GZk69H+2eop0b8h1Zh4lO9ss4IRRqAmnL9e8IfDgQ==
        -----END PUBLIC KEY-----
        """;
}
