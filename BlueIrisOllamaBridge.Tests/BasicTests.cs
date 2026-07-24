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

    [Fact]
    public void ClosedGate_VehicleSettledInUsualSpot_NoPerson_IsSuppressed()
    {
        var result = GateResult(
            vehicle: new VehicleAnalysis { Visible = true, Location = "parked_inside_property", Activity = "parked" });

        Assert.False(AlertNotificationPolicy.ShouldNotify(result));
    }

    [Fact]
    public void ClosedGate_VehicleFreshlyInDriveway_IsNotSuppressed()
    {
        // A vehicle that just arrived looks the same, in a single still frame, as one that has
        // been sitting there a while: the model can't confirm motion, so it reports "unknown"
        // activity rather than "entering". Location alone (inside_driveway, not the more settled
        // parked_inside_property) must be enough to avoid dropping real arrivals.
        var result = GateResult(
            vehicle: new VehicleAnalysis { Visible = true, Location = "inside_driveway", Activity = "unknown" });

        Assert.True(AlertNotificationPolicy.ShouldNotify(result));
    }

    [Fact]
    public void ClosedGate_VehicleAtOutsideEntrance_IsNotSuppressed()
    {
        var result = GateResult(
            vehicle: new VehicleAnalysis { Visible = true, Location = "outside_gate_entrance", Activity = "stopped" });

        Assert.True(AlertNotificationPolicy.ShouldNotify(result));
    }

    private static AlertResult GateResult(string? personLocation = null, VehicleAnalysis? vehicle = null) => new()
    {
        Profile = "gate",
        Analysis = new VisionAnalysis
        {
            NotificationRecommended = true,
            Gate = new GateAnalysis { State = "closed" },
            Person = personLocation is null
                ? new PersonAnalysis { Visible = false }
                : new PersonAnalysis { Visible = true, Location = personLocation },
            Vehicle = vehicle ?? new VehicleAnalysis()
        }
    };
}
