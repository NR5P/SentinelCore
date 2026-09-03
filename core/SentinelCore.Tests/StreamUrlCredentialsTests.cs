using SentinelCore.Web;
using Xunit;

namespace SentinelCore.Tests;

// The expectations below were measured against a Reolink RLC-510A at 192.168.50.27 on 2026-09-02,
// an account "admin" with a blank password (confirmed against the camera's own api.cgi Login, which
// issues a token for it). The two URL forms are not equivalent to that camera:
//
//   rtsp://admin@192.168.50.27:554/Preview_01_main    401 Unauthorized
//   rtsp://admin:@192.168.50.27:554/Preview_01_main   h264 2560x1920
//
// UriBuilder produces the first, which is why every ffmpeg consumer - the discovery probe, HLS,
// snapshots, trigger detection - failed on that camera while the go2rtc live path, whose parser
// accepts both, worked fine and hid the problem.
public sealed class StreamUrlCredentialsTests
{
    private const string Rtsp = "rtsp://192.168.50.27:554/Preview_01_main";

    // The bug. A blank password must still emit the ':' separator, or the camera is dialed
    // anonymously and answers 401.
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void BlankPassword_KeepsTheSeparator(string? password)
    {
        Assert.Equal(
            "rtsp://admin:@192.168.50.27:554/Preview_01_main",
            StreamUrlCredentials.Embed(Rtsp, "admin", password));
    }

    // The path every already-working camera takes. This must stay byte-for-byte what UriBuilder
    // produced before the fix, including its escaping - a regression here breaks every camera on
    // the network at once, which is a far worse failure than the one being fixed.
    [Fact]
    public void NormalPassword_IsUnchanged()
    {
        Assert.Equal(
            "rtsp://admin:s3cret@192.168.50.27:554/Preview_01_main",
            StreamUrlCredentials.Embed(Rtsp, "admin", "s3cret"));
    }

    [Fact]
    public void PasswordNeedingEscaping_IsUnchanged()
    {
        Assert.Equal(
            "rtsp://admin:p%40ss:w%2Frd@h/s",
            StreamUrlCredentials.Embed("rtsp://h/s", "admin", "p@ss:w/rd"));
    }

    // No username means the URL is used exactly as stored - which is how a camera needing no
    // authentication works, and also how someone who wrote credentials into the URL by hand gets
    // them left alone.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoUsername_LeavesTheUrlAlone(string? username)
    {
        Assert.Equal(Rtsp, StreamUrlCredentials.Embed(Rtsp, username, "ignored"));
    }

    // A username with a colon in it would otherwise split at the wrong place: "ad:min" with a blank
    // password reads back as user "ad", password "min". Escaping it is what keeps the userinfo
    // parseable.
    [Fact]
    public void UsernameContainingColon_IsEscaped()
    {
        Assert.Equal("rtsp://ad%3Amin:@h:554/a", StreamUrlCredentials.Embed("rtsp://h:554/a", "ad:min", ""));
    }

    // The same trap one step less obvious: a raw space makes a URL ffmpeg will not parse. This is
    // why the blank-password branch rebuilds from components instead of patching the string by
    // searching for the username in it - that search misses whenever the username needed escaping.
    [Fact]
    public void UsernameContainingSpace_IsEscaped()
    {
        Assert.Equal("rtsp://od%20d:@h/s", StreamUrlCredentials.Embed("rtsp://h/s", "od d", ""));
    }

    // Reolink's HTTP-FLV cameras keep a query string that must survive the rebuild intact - it
    // carries the stream selector, and on those cameras the credential too.
    [Fact]
    public void QueryString_SurvivesTheBlankPasswordRebuild()
    {
        Assert.Equal(
            "http://admin:@h/flv?port=1935&app=bcs&stream=channel0_main.bcs",
            StreamUrlCredentials.Embed("http://h/flv?port=1935&app=bcs&stream=channel0_main.bcs", "admin", ""));
    }

    // A Reolink serves its main stream from the bare root as well, and "Front of office" is stored
    // that way - so the rebuild has to keep the trailing slash rather than dropping to an empty path.
    [Fact]
    public void BareRootPath_IsPreserved()
    {
        Assert.Equal("rtsp://admin:@192.168.50.27:554/", StreamUrlCredentials.Embed("rtsp://192.168.50.27:554/", "admin", ""));
    }

    // The result has to be a URL, not just a string that looks like one - anything ffmpeg is handed
    // gets parsed, and the blank-password branch is the one that builds it by hand.
    [Fact]
    public void BlankPasswordResult_ParsesBackToTheSameCredentials()
    {
        var uri = new Uri(StreamUrlCredentials.Embed(Rtsp, "admin", ""));
        Assert.Equal("admin:", uri.UserInfo);
        Assert.Equal("192.168.50.27", uri.Host);
        Assert.Equal(554, uri.Port);
        Assert.Equal("/Preview_01_main", uri.AbsolutePath);
    }
}
