using SentinelCore;
using Xunit;

namespace SentinelCore.Tests;

// The email subject for an animal used to read "Animal detected near your gate" on every camera,
// because it was written when the gate was the only camera that had one. The chicken coop's first
// real notification - a raccoon on the ladder at 05:40 - arrived titled with a location it is
// nowhere near.
//
// The subject now says what was seen instead of where, which at a coop is the entire question: the
// chickens and the thing eating them are both "animal", and the subject line is all you get on a
// phone at five in the morning.
public sealed class AnimalSubjectTests
{
    private static AnimalAnalysis Animal(string description) =>
        new() { Visible = true, Description = description };

    [Fact]
    public void ItNamesTheAnimalRatherThanAPlace()
    {
        var subject = EmailAlertAction.AnimalSubject(Animal("unidentified animal (likely raccoon) on ladder"));

        Assert.Equal("Animal detected: unidentified animal (likely raccoon) on ladder", subject);
        Assert.DoesNotContain("gate", subject, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("chickens", "Animal detected: chickens")]
    [InlineData("Two chickens", "Animal detected: Two chickens")]
    // The model supplies a trailing full stop about half the time, which reads wrong in a subject.
    [InlineData("Several chickens are present in the coop.", "Animal detected: Several chickens are present in the coop")]
    public void ItUsesTheModelsOwnWording(string description, string expected) =>
        Assert.Equal(expected, EmailAlertAction.AnimalSubject(Animal(description)));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void WithNoDescriptionItStillSaysSomethingTrue(string? description)
    {
        // Never a dangling "Animal detected: " - and still no invented location.
        var subject = EmailAlertAction.AnimalSubject(Animal(description!));

        Assert.Equal("Animal detected", subject);
    }

    [Fact]
    public void ARamblingDescriptionIsCutRatherThanLeftToTheMailClient()
    {
        // Free text from a model. A subject that runs to a paragraph gets truncated anyway, just
        // in whatever place the client feels like.
        var subject = EmailAlertAction.AnimalSubject(Animal(new string('a', 200)));

        Assert.StartsWith("Animal detected: ", subject);
        Assert.EndsWith("...", subject);
        Assert.True(subject.Length < 120, $"subject was {subject.Length} chars: {subject}");
    }
}
