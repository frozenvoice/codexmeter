using System.Globalization;
using System.Text;
using CycleArc.Providers.ChatGpt;

namespace CycleArc.Tests;

internal static class LargeProjectedConversationFactory
{
    public static string CreateCompleteMappingJson(string conversationId, int nodeCount, double time = 1_777_500_000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(nodeCount, 2);
        var builder = new StringBuilder(nodeCount * 340);
        builder.Append("{\"conversation_id\":\"").Append(conversationId);
        builder.Append("\",\"id\":\"").Append(conversationId);
        builder.Append("\",\"update_time\":").Append(time.ToString(CultureInfo.InvariantCulture));
        builder.Append(",\"current_node\":\"n").Append(nodeCount - 1);
        builder.Append("\",\"mapping\":{");
        for (var i = 0; i < nodeCount; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            var id = "n" + i;
            var role = i == 0 ? "system" : i % 2 == 1 ? "user" : "assistant";
            builder.Append('"').Append(id).Append("\":{\"id\":\"").Append(id).Append('"');
            if (i > 0)
            {
                builder.Append(",\"parent\":\"n").Append(i - 1).Append('"');
            }

            builder.Append(",\"children\":[");
            if (i + 1 < nodeCount)
            {
                builder.Append("\"n").Append(i + 1).Append('"');
            }

            builder.Append("],\"message\":{\"id\":\"").Append(id);
            builder.Append("\",\"author\":{\"role\":\"").Append(role);
            builder.Append("\"},\"create_time\":").Append((time - nodeCount + i).ToString(CultureInfo.InvariantCulture));
            builder.Append(",\"end_turn\":true,\"recipient\":\"all\",\"content\":{\"content_type\":\"text\"},\"metadata\":{");
            if (role == "assistant")
            {
                builder.Append("\"request_id\":\"r").Append(i).Append("\",\"model_slug\":\"gpt-5-6-pro\"");
            }

            builder.Append("}}}");
        }

        builder.Append("}}");
        return builder.ToString();
    }

    public static string CreatePaginatedMessagesJson(string conversationId, int messageCount, double time = 1_777_500_000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(messageCount, 2);
        var builder = new StringBuilder(messageCount * 340);
        builder.Append("{\"conversation_id\":\"").Append(conversationId);
        builder.Append("\",\"id\":\"").Append(conversationId);
        builder.Append("\",\"update_time\":").Append(time.ToString(CultureInfo.InvariantCulture));
        builder.Append(",\"current_node\":\"n").Append(messageCount - 1);
        builder.Append("\",\"page_info\":{\"has_previous_page\":false},\"messages\":[");
        for (var i = 0; i < messageCount; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            var id = "n" + i;
            var role = i == 0 ? "system" : i % 2 == 1 ? "user" : "assistant";
            builder.Append("{\"id\":\"").Append(id).Append('"');
            if (i > 0)
            {
                builder.Append(",\"parent\":\"n").Append(i - 1).Append('"');
            }

            builder.Append(",\"message\":{\"id\":\"").Append(id);
            builder.Append("\",\"author\":{\"role\":\"").Append(role);
            builder.Append("\"},\"create_time\":").Append((time - messageCount + i).ToString(CultureInfo.InvariantCulture));
            builder.Append(",\"end_turn\":true,\"recipient\":\"all\",\"content\":{\"content_type\":\"text\"},\"metadata\":{");
            if (role == "assistant")
            {
                builder.Append("\"request_id\":\"r").Append(i).Append("\",\"model_slug\":\"gpt-5-6-pro\"");
            }

            builder.Append("}}}");
        }

        builder.Append("]}");
        return builder.ToString();
    }

    public static string CreateAtLeastUtf8Bytes(string conversationId, int minBytes, bool paginated = false)
    {
        var count = Math.Max(8, minBytes / 280 + 8);
        string json;
        do
        {
            json = paginated
                ? CreatePaginatedMessagesJson(conversationId, count)
                : CreateCompleteMappingJson(conversationId, count);
            count += Math.Max(64, count / 5);
        } while (Encoding.UTF8.GetByteCount(json) < minBytes);

        return json;
    }

    public static JsonNode Parse(string json)
    {
        var node = JsonNode.Parse(json);
        Assert.NotNull(node);
        return node!;
    }
}
