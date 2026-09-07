using System.Text.Json.Nodes;
using CodexMeter.Providers.ChatGpt;

namespace CodexMeter.Tests;

public class TimestampParserTests
{
    [Fact]
    public void AcceptsUnixSecondsMillisecondsNumericStringsIsoAndNulls()
    {
        var seconds = TimestampParser.Parse(1_777_500_000d);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_777_500_000), seconds);

        var millis = TimestampParser.Parse(1_777_500_000_000d);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_777_500_000_000), millis);

        var numeric = TimestampParser.Parse("1777500000");
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_777_500_000), numeric);

        var iso = TimestampParser.Parse("2026-09-01T12:00:00Z");
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero), iso);

        Assert.Null(TimestampParser.Parse((string?)null));
        Assert.Null(TimestampParser.Parse("not-a-date"));
        Assert.Null(TimestampParser.Parse((JsonNode?)null));
        Assert.Null(TimestampParser.Parse(new JsonObject { ["create_time"] = "bogus" }["create_time"]));
    }

    [Fact]
    public void ReadsNamedFieldsFromIndexAndMessageNodes()
    {
        var index = new JsonObject { ["update_time"] = "2026-09-02T08:15:00Z" };
        Assert.Equal(
            new DateTimeOffset(2026, 9, 2, 8, 15, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
            TimestampParser.ToUnixSeconds(index, "update_time", "updateTime"));

        var message = new JsonObject { ["create_time"] = 1_777_500_123_000d };
        Assert.Equal(
            DateTimeOffset.FromUnixTimeMilliseconds(1_777_500_123_000),
            TimestampParser.ToDateTimeOffset(message, "create_time"));
    }
}
