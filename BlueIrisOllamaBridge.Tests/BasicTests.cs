using Xunit;

namespace BlueIrisOllamaBridge.Tests;

public sealed class BasicTests
{
    [Fact]
    public void VisionAnalysis_Defaults_AreSafe()
    {
        var analysis = new VisionAnalysis();
        Assert.False(analysis.NotificationRecommended);
        Assert.False(analysis.Person.Visible);
        Assert.False(analysis.Vehicle.Visible);
        Assert.False(analysis.Package.Visible);
        Assert.Equal("unknown", analysis.EventType);
    }
}