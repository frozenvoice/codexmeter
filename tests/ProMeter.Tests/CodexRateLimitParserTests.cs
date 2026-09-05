using ProMeter.Codex;

namespace ProMeter.Tests;

public class CodexRateLimitParserTests
{
    [Fact]
    public void RateLimitsOnly_ParsesPrimaryAndSecondaryByDuration()
    {
        var parsed = CodexRateLimitParser.Parse(null, JsonNode.Parse("""
            {
              "rateLimits": {
                "primary": { "usedPercent": 42, "windowDurationMins": 300, "resetsAt": 1893456000, "limitId": "codex" },
                "secondary": { "usedPercent": 31, "windowDurationMins": 10080, "resetsAt": 1894051200 },
                "ordinaryUsageAllowed": true,
                "rateLimitReachedType": "none",
                "rateLimitResetCredits": { "availableCount": 1 }
              }
            }
            """));
        Assert.Equal(CodexQuotaStatus.Available, parsed.Status);
        Assert.Equal(2, parsed.Windows.Count);
        Assert.Equal(CodexWindowKind.FiveHour, parsed.Windows[0].Kind);
        Assert.Equal(42, parsed.Windows[0].UsedPercent);
        Assert.Equal(58, parsed.Windows[0].RemainingPercent);
        Assert.Equal(CodexWindowKind.Weekly, parsed.Windows[1].Kind);
        Assert.True(parsed.OrdinaryUsageAllowed);
        Assert.Equal(1, parsed.ResetCreditsAvailable);
        Assert.Equal("none", parsed.RateLimitReachedType);
    }

    [Fact]
    public void RateLimitsByLimitId_PrefersCodexBucket()
    {
        var parsed = CodexRateLimitParser.Parse(null, JsonNode.Parse("""
            {
              "rateLimits": {
                "primary": { "usedPercent": 9, "windowDurationMins": 300 }
              },
              "rateLimitsByLimitId": {
                "chatgpt": { "primary": { "usedPercent": 80, "windowDurationMins": 300 } },
                "codex": { "primary": { "usedPercent": 12, "windowDurationMins": 300 } }
              }
            }
            """));
        Assert.Single(parsed.Windows);
        Assert.Equal(12, parsed.Windows[0].UsedPercent);
    }

    [Fact]
    public void PrimaryWeeklyWithNullSecondary_IsNotFailure()
    {
        var parsed = CodexRateLimitParser.Parse(null, JsonNode.Parse("""
            {
              "rateLimits": {
                "primary": { "usedPercent": 20, "windowDurationMins": 10080 },
                "secondary": null
              }
            }
            """));
        Assert.Equal(CodexQuotaStatus.Available, parsed.Status);
        Assert.Single(parsed.Windows);
        Assert.Equal(CodexWindowKind.Weekly, parsed.Windows[0].Kind);
    }

    [Fact]
    public void ReversedSlots_StillClassifyByDuration()
    {
        var parsed = CodexRateLimitParser.Parse(null, JsonNode.Parse("""
            {
              "rateLimits": {
                "primary": { "usedPercent": 10, "windowDurationMins": 10080 },
                "secondary": { "usedPercent": 40, "windowDurationMins": 300 }
              }
            }
            """));
        Assert.Equal(CodexWindowKind.Weekly, parsed.Windows[0].Kind);
        Assert.Equal(CodexWindowKind.FiveHour, parsed.Windows[1].Kind);
    }

    [Fact]
    public void UnknownDuration_RemainsOther()
    {
        var parsed = CodexRateLimitParser.Parse(null, JsonNode.Parse("""
            { "rateLimits": { "primary": { "usedPercent": 5, "windowDurationMins": 1440 } } }
            """));
        Assert.Equal(CodexWindowKind.Other, parsed.Windows[0].Kind);
        Assert.Equal(1440, parsed.Windows[0].WindowDurationMinutes);
    }

    [Fact]
    public void InvalidPercent_BecomesUnavailableNotZero()
    {
        var parsed = CodexRateLimitParser.Parse(null, JsonNode.Parse("""
            { "rateLimits": { "primary": { "usedPercent": "NaN", "windowDurationMins": 300 } } }
            """));
        Assert.Null(parsed.Windows[0].UsedPercent);
        Assert.Null(parsed.Windows[0].RemainingPercent);
    }

    [Fact]
    public void OutOfRangePercent_IsClamped()
    {
        var parsed = CodexRateLimitParser.Parse(null, JsonNode.Parse("""
            { "rateLimits": { "primary": { "usedPercent": 150, "windowDurationMins": 300 } } }
            """));
        Assert.Equal(100, parsed.Windows[0].UsedPercent);
        Assert.Equal(0, parsed.Windows[0].RemainingPercent);
    }

    [Fact]
    public void InvalidResetTimestamp_IsUnavailable()
    {
        var parsed = CodexRateLimitParser.Parse(null, JsonNode.Parse("""
            { "rateLimits": { "primary": { "usedPercent": 10, "windowDurationMins": 300, "resetsAt": -5 } } }
            """));
        Assert.Null(parsed.Windows[0].ResetsAt);
        Assert.Equal(10, parsed.Windows[0].UsedPercent);
    }

    [Fact]
    public void UnknownProperties_AreIgnored()
    {
        var parsed = CodexRateLimitParser.Parse(null, JsonNode.Parse("""
            {
              "rateLimits": {
                "primary": { "usedPercent": 3, "windowDurationMins": 300, "secretToken": "nope", "email": "a@b.c" },
                "mystery": { "hello": true }
              }
            }
            """));
        Assert.Equal(CodexQuotaStatus.Available, parsed.Status);
        Assert.Single(parsed.Windows);
        Assert.Equal(3, parsed.Windows[0].UsedPercent);
    }

    [Fact]
    public void SignedOutAccount_DoesNotInventPercents()
    {
        var parsed = CodexRateLimitParser.Parse(JsonNode.Parse("""{"loggedIn":false}"""), JsonNode.Parse("""{"rateLimits":null}"""));
        Assert.Equal(CodexQuotaStatus.SignedOut, parsed.Status);
        Assert.Empty(parsed.Windows);
    }
}
