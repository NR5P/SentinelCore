using System.Text.Json;
using SentinelCore.Web;
using Xunit;

namespace SentinelCore.Tests;

// Viewers can read automation rules but not edit them, so the secrets a webhook rule carries
// (a Home Assistant bearer token, a token in the query string) are hidden from them.
public sealed class AutomationRuleRedactionTests
{
    private const string Rules = """
        [
          { "id": "1", "name": "Porch light", "webhookUrl": "http://user:pw@homeassistant.local:8123/api/services/light/turn_on?token=abc",
            "webhookHeaders": "Authorization: Bearer eyJhbGciOi", "webhookBody": "{\"secret\":\"s\"}", "mqttTopic": "home/porch" },
          { "id": "2", "name": "MQTT only", "webhookUrl": "", "webhookHeaders": "", "webhookBody": "" }
        ]
        """;

    [Fact]
    public void Secrets_are_hidden_and_the_destination_is_kept()
    {
        using var redacted = JsonDocument.Parse(AutomationRuleProxyEndpoints.RedactForViewer(Rules));
        var first = redacted.RootElement[0];

        Assert.Equal("http://homeassistant.local:8123/api/services/light/turn_on?" + AutomationRuleProxyEndpoints.Hidden,
            first.GetProperty("webhookUrl").GetString());
        Assert.Equal(AutomationRuleProxyEndpoints.Hidden, first.GetProperty("webhookHeaders").GetString());
        Assert.Equal(AutomationRuleProxyEndpoints.Hidden, first.GetProperty("webhookBody").GetString());
        Assert.Equal("Porch light", first.GetProperty("name").GetString());
        Assert.Equal("home/porch", first.GetProperty("mqttTopic").GetString());

        var raw = redacted.RootElement.GetRawText();
        foreach (var secret in new[] { "pw@", "token=abc", "eyJhbGciOi", "\"s\"" })
            Assert.DoesNotContain(secret, raw);
    }

    [Fact]
    public void Empty_fields_stay_empty_and_a_single_rule_is_handled()
    {
        using var list = JsonDocument.Parse(AutomationRuleProxyEndpoints.RedactForViewer(Rules));
        Assert.Equal("", list.RootElement[1].GetProperty("webhookHeaders").GetString());
        Assert.Equal("", list.RootElement[1].GetProperty("webhookUrl").GetString());

        using var single = JsonDocument.Parse(AutomationRuleProxyEndpoints.RedactForViewer(
            """{ "id": "1", "webhookUrl": "https://example.com/hook@v2", "webhookHeaders": "X-Key: k" }"""));
        Assert.Equal("https://example.com/hook@v2", single.RootElement.GetProperty("webhookUrl").GetString());
        Assert.Equal(AutomationRuleProxyEndpoints.Hidden, single.RootElement.GetProperty("webhookHeaders").GetString());
    }
}
