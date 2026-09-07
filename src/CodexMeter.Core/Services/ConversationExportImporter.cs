namespace CodexMeter.Services;

public sealed class ConversationExportImporter
{
    private readonly ConversationParser _parser;
    private readonly ModelNormalizer _models;

    public ConversationExportImporter(ConversationParser parser, ModelNormalizer models)
    {
        _parser = parser;
        _models = models;
    }

    public ImportResult Import(string json, bool includeHistorical, DateTimeOffset periodStart)
    {
        var result = new ImportResult();
        var node = ChatGptJson.ParseNode(json);
        if (node is null)
        {
            result.Error = "The file is not valid JSON.";
            return result;
        }

        var conversations = new List<JsonNode>();
        if (node is JsonArray array)
        {
            conversations.AddRange(ChatGptJson.Enumerate(array));
        }
        else if (node["conversations"] is JsonArray named)
        {
            conversations.AddRange(ChatGptJson.Enumerate(named));
        }
        else if (node["mapping"] is not null)
        {
            conversations.Add(node);
        }

        if (conversations.Count == 0)
        {
            result.Error = "No conversations were found in the export.";
            return result;
        }

        foreach (var conversation in conversations)
        {
            var context = new ConversationParseContext
            {
                ConversationId = ChatGptJson.GetString(conversation, "conversation_id", "id") ?? "",
                Source = UsageSource.OfficialExport,
                Archived = ChatGptJson.GetBool(conversation, "is_archived", "archived") == true,
                ProjectId = ChatGptJson.GetString(conversation, "gizmo_id", "project_id"),
                UpdateTime = ChatGptJson.GetDouble(conversation, "update_time") ?? 0
            };
            var parsed = _parser.Parse(conversation, context);
            if (parsed.SchemaMismatch)
            {
                result.Skipped++;
                continue;
            }

            foreach (var usage in parsed.Events)
            {
                if (!includeHistorical && usage.CreatedAt < periodStart)
                {
                    result.Skipped++;
                    continue;
                }

                result.Events.Add(usage);
            }
        }

        _models.ObserveCatalog(result.Events.Select(e => new ModelCatalogEntry
        {
            Slug = e.RawModel,
            Title = e.NormalizedModel
        }));

        return result;
    }
}

public sealed class ImportResult
{
    public List<UsageEvent> Events { get; } = [];
    public int Skipped { get; set; }
    public string? Error { get; set; }
}
