namespace SentinelCore.Web;

// The public half of the update signing key (ECDSA P-256). Every update manifest is signed with
// the private half by scripts/publish-update.sh on the maintainer's Mac, and an install refuses
// any manifest this key doesn't verify - so the relay that serves updates, or anything between it
// and the install, can't ship code. The private key lives at ~/sentinelcore-update-keys/ on that
// Mac and nowhere else; it is deliberately a different key from the license signing key, which
// lives on the internet-facing relay box.
//
// Replacing this key strands every existing install on its current build: they only trust the
// key compiled into them, so the first release with a new key has to be installed by hand.
public static class UpdateSigningPublicKey
{
    public const string Pem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEHDe+gYDesBUQGEAr/9Th8zRaeE9R
        ya8nFxUZpGLg/R8l6psBNLkfMMO7Do/FBBHevxiBAMtHzQKln1z1RNqx7w==
        -----END PUBLIC KEY-----
        """;
}
