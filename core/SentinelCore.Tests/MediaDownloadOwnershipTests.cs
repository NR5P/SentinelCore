using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using SentinelCore.Web;
using Xunit;

namespace SentinelCore.Tests;

// Cancel and retry are for the person who asked for the download and for admins - not for any
// account that can see the list.
public sealed class MediaDownloadOwnershipTests
{
    private static HttpContext As(string name, AccountRole role) => new DefaultHttpContext
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, name), new Claim(ClaimTypes.Role, role.ToString())], "test"))
    };

    [Fact]
    public void Only_the_requester_or_an_admin_may_manage_a_download()
    {
        var download = new MediaDownload { RequestedBy = "jordan" };

        Assert.True(MediaDownloadEndpoints.MayManage(As("jordan", AccountRole.Viewer), download));
        Assert.True(MediaDownloadEndpoints.MayManage(As("Jordan", AccountRole.Viewer), download));
        Assert.True(MediaDownloadEndpoints.MayManage(As("morgan", AccountRole.Admin), download));
        Assert.False(MediaDownloadEndpoints.MayManage(As("pat", AccountRole.Viewer), download));
    }
}
