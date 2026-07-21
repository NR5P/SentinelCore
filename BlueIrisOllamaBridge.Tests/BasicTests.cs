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

    [Fact]
    public void ClosedGate_PersonInside_IsSuppressed()
    {
        var result = GateResult("inside_driveway");

        Assert.False(AlertNotificationPolicy.ShouldNotify(result));
    }

    [Fact]
    public void ClosedGate_PersonOutside_IsNotSuppressed()
    {
        var result = GateResult("outside_gate");

        Assert.True(AlertNotificationPolicy.ShouldNotify(result));
    }

    private static AlertResult GateResult(string personLocation) => new()
    {
        Profile = "gate",
        Analysis = new VisionAnalysis
        {
            NotificationRecommended = true,
            Gate = new GateAnalysis { State = "closed" },
            Person = new PersonAnalysis { Visible = true, Location = personLocation }
        }
    };
}
