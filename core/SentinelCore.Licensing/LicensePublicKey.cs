namespace SentinelCore.Licensing;

// The public half of the license signing key, compiled into everything that verifies a license:
// the main app (LicenseStatus), and SentinelCore.PushRelay, which accepts a license as its
// caller's credential. Generated once via LicenseGenerator's `generate-keys` command; the
// matching private key never touches this repo - it stays only wherever it was generated.
public static class LicensePublicKey
{
    public const string Pem = """
        -----BEGIN PUBLIC KEY-----
        MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAoP5AaxG6j2hLHQvG7seg
        WGg4zFznO6LSdcz5oWRHFzuNF8LigifsJ/RLPeJnwNBgnt0CodfkZ/E0CSc1Hpx9
        kYMZLZQ48jaSIlxmLL4Zt2teGYTGHkDac/6VLyPVQESKjZPZ7V5GMsFf5mupz6zC
        pAAcVslomeBASvrW+23gxuMnXcJ7aFClkJlnfmRq2H1zpwBU+opHYdplgq+Vr7Xg
        nIvGxbFn8v7zj3f9aATmtWwbuPWNjCeZHFLrv6nnQoVgWcc9i5HAWT/1Wv1UYqfW
        obrXx6U+YFiVoRuAW+b/eVpJEoQPw4zU1D6jNldJTqcoNOrV5jMcte/zcao0AKuj
        gQRZ2gb+qXdao2rpAfczGFZlCEvucT/ZtCPb+jQVlv7qgrCPA7DYXG87fE/Yqzlt
        G52WeZZ0G/EHp2kJljcNaCg//4P9nASAnqhLuj0xDLJMuAN700FC4b/RZvaQ2CB2
        s97CzJ/AlJvqfymwsgjQQUqpH5lLUq5zOuSXYbyPxdXNAgMBAAE=
        -----END PUBLIC KEY-----
        """;
}
