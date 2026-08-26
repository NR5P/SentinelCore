extern alias WebHost;

using Xunit;
using StreamUrlSecrets = WebHost::SentinelCore.Web.StreamUrlSecrets;

namespace SentinelCore.Tests;

// CameraPasswordTests covers the Password column. This covers the second copy of the same secret,
// the one inside StreamUrl - which is the copy that actually ships on this network, because no
// Reolink here can use RTSP and the HTTP-FLV workaround puts the admin password in the query
// string. Masking the column alone left that copy going out in full from GET /api/cameras to any
// signed-in caller, Viewer included.
//
// Every fixture in CameraPasswordTests uses a credential-free URL like rtsp://10.0.0.5/main with a
// separate Password field, which is why the gap survived: no stub ever looked like the real
// cameras. The first case below is the exact shape stored in this install's cameras.db.
public sealed class StreamUrlSecretsTests
{
    private const string Mask = "••••••••";
    private const string Secret = "hunter2-the-real-camera-password";

    [Fact]
    public void TheReolinkFlvShape_HasItsPasswordMaskedAndNothingElse()
    {
        var url = $"http://192.168.50.23/flv?port=1935&app=bcs&stream=channel0_main.bcs&user=admin&password={Secret}";

        var masked = StreamUrlSecrets.Mask(url);

        Assert.DoesNotContain(Secret, masked, StringComparison.Ordinal);
        Assert.Equal(
            $"http://192.168.50.23/flv?port=1935&app=bcs&stream=channel0_main.bcs&user=admin&password={Mask}",
            masked);
        // The host and path are what makes a stream URL recognisable in a list, and the username is
        // not masked in the Password column either - so neither is hidden here.
        Assert.Contains("192.168.50.23", masked, StringComparison.Ordinal);
        Assert.Contains("user=admin", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRtspUserinfoShape_HasItsPasswordMasked()
    {
        var masked = StreamUrlSecrets.Mask($"rtsp://admin:{Secret}@10.0.0.5:554/main");

        Assert.DoesNotContain(Secret, masked, StringComparison.Ordinal);
        Assert.Equal($"rtsp://admin:{Mask}@10.0.0.5:554/main", masked);
    }

    // The port colon sits after the '@', and a path can contain ':' too. Neither is a credential.
    [Theory]
    [InlineData("rtsp://10.0.0.5:554/main")]
    [InlineData("http://10.0.0.5/flv?port=1935&app=bcs&stream=channel0_main.bcs")]
    [InlineData("https://example.test/a:b/c")]
    public void AUrlWithNoCredential_IsLeftAlone(string url) =>
        Assert.Equal(url, StreamUrlSecrets.Mask(url));

    [Fact]
    public void AnEditThatChangedOnlyTheHost_KeepsTheStoredSecret()
    {
        var stored = $"http://192.168.50.23/flv?app=bcs&user=admin&password={Secret}";
        // What the form posts back after the user retyped the address and left the bullets alone.
        var incoming = $"http://192.168.50.99/flv?app=bcs&user=admin&password={Mask}";

        var restored = StreamUrlSecrets.Unmask(incoming, stored);

        Assert.Equal($"http://192.168.50.99/flv?app=bcs&user=admin&password={Secret}", restored);
    }

    [Fact]
    public void AGenuinelyNewSecret_IsTakenAsWritten()
    {
        var stored = $"rtsp://admin:{Secret}@10.0.0.5/main";

        var restored = StreamUrlSecrets.Unmask("rtsp://admin:a-brand-new-one@10.0.0.5/main", stored);

        Assert.Equal("rtsp://admin:a-brand-new-one@10.0.0.5/main", restored);
    }

    // The round trip that matters: whatever the client is handed, handing it straight back must
    // leave the stored URL byte-identical. This is what stops "change the name, hit save" from
    // writing eight bullet characters into the camera's password.
    [Theory]
    [InlineData("http://192.168.50.23/flv?port=1935&app=bcs&user=admin&password=SECRET")]
    [InlineData("rtsp://admin:SECRET@10.0.0.5:554/main")]
    [InlineData("rtsp://10.0.0.5/main")]
    public void MaskThenUnmask_ReturnsTheOriginal(string template)
    {
        var stored = template.Replace("SECRET", Secret, StringComparison.Ordinal);

        Assert.Equal(stored, StreamUrlSecrets.Unmask(StreamUrlSecrets.Mask(stored), stored));
    }

    // A mask with no stored counterpart means the stored URL has no credential in that position.
    // Writing the bullets into the database would be worse than leaving them on screen.
    [Fact]
    public void AMaskWithNothingStoredBehindIt_IsNotWrittenIn()
    {
        var restored = StreamUrlSecrets.Unmask($"http://host/flv?password={Mask}", "http://host/flv");

        Assert.Equal($"http://host/flv?password={Mask}", restored);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void EmptyInput_IsHandled(string? url)
    {
        Assert.Equal(string.Empty, StreamUrlSecrets.Mask(url));
        Assert.Equal(string.Empty, StreamUrlSecrets.Unmask(url, "rtsp://admin:x@h/main"));
    }
}
