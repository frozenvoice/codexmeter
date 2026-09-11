using System.Text;

namespace CycleArc.Services;

public static class ExportService
{
    public static string ToJson(IEnumerable<UsageEvent> events)
    {
        var payload = events.Select(ToExportRow).ToList();
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    public static string ToCsv(IEnumerable<UsageEvent> events)
    {
        var sb = new StringBuilder();
        sb.AppendLine("timestamp,normalized_model,raw_model,reasoning_effort,usage_event_id,source,quota_family,conversation_id,request_id");
        foreach (var e in events)
        {
            sb.Append(Csv(e.CreatedAt.ToUniversalTime().ToString("O"))).Append(',')
                .Append(Csv(e.NormalizedModel)).Append(',')
                .Append(Csv(e.RawModel)).Append(',')
                .Append(Csv(ReasoningNormalizer.ToStorage(e.ReasoningEffort))).Append(',')
                .Append(Csv(e.Id)).Append(',')
                .Append(Csv(e.Source.ToString())).Append(',')
                .Append(Csv(e.QuotaFamily.ToString())).Append(',')
                .Append(Csv(e.ConversationId)).Append(',')
                .Append(Csv(e.RequestId))
                .AppendLine();
        }

        return sb.ToString();
    }

    private static object ToExportRow(UsageEvent e) => new
    {
        timestamp = e.CreatedAt.ToUniversalTime(),
        normalized_model = e.NormalizedModel,
        raw_model = e.RawModel,
        reasoning_effort = ReasoningNormalizer.ToStorage(e.ReasoningEffort),
        usage_event_id = e.Id,
        source = e.Source.ToString(),
        quota_family = e.QuotaFamily.ToString(),
        conversation_id = e.ConversationId,
        request_id = e.RequestId
    };

    private static string Csv(string? value)
    {
        var text = value ?? "";
        if (text.Contains(',') || text.Contains('"') || text.Contains('\n'))
        {
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }

        return text;
    }
}
