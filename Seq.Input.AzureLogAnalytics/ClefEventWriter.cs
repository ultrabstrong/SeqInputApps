using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.Monitor.Query.Models;

namespace Seq.Input.AzureLogAnalytics;

sealed class ClefEventWriter
{
    private readonly TextWriter _output;
    private readonly string? _sourceLabel;
    private readonly object _sync = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private static readonly Regex TemplateTokenRegex = new(
        @"(?<!\{)\{([^{}:,\s]+)(?:,[^}]*)?(?::[^}]*)?\}(?!\})",
        RegexOptions.Compiled
    );

    // Columns handled specially — not added as generic properties
    private static readonly HashSet<string> SpecialColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "timestamp",
        "TimeGenerated",
        "message",
        "Message",
        "severityLevel",
        "SeverityLevel",
        "customDimensions",
        "CustomDimensions",
        "Properties",
        "customMeasurements",
        "CustomMeasurements",
        "Measurements",
        "details",
        "Details",
        "itemId",
        "itemType",
        "itemCount",
        "_ItemId",
        "_BilledSize",
        "_IsBillable",
        "Type",
        "_ResourceId",
        "TenantId",
        "IKey",
        "SDKVersion",
        "ReferencedItemId",
        "ReferencedType",
        "SourceSystem",
        "SyntheticSource",
        "ResourceGUID",
        "__seq_event_id",
        "__seq_source_context"
    };

    public ClefEventWriter(TextWriter output, string? sourceLabel)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _sourceLabel = sourceLabel;
    }

    /// <summary>
    /// Writes all rows from a Log Analytics table as CLEF events.
    /// Returns the latest cursor position seen, for cursor tracking.
    /// </summary>
    public CursorPosition? WriteEvents(LogsTable table)
    {
        DateTimeOffset? latestTimestamp = null;
        string? latestItemId = null;
        var columnNames = new HashSet<string>(
            table.Columns.Select(c => c.Name),
            StringComparer.OrdinalIgnoreCase);

        foreach (var row in table.Rows)
        {
            var clef = new Dictionary<string, object?>();

            // @t — timestamp (App Insights: timestamp, Log Analytics: TimeGenerated)
            DateTimeOffset? ts = null;
            if (columnNames.Contains("TimeGenerated"))
            {
                try { ts = row.GetDateTimeOffset("TimeGenerated"); }
                catch { /* column type mismatch */ }
            }
            else if (columnNames.Contains("timestamp"))
            {
                try { ts = row.GetDateTimeOffset("timestamp"); }
                catch { /* column type mismatch */ }
            }

            clef["@t"] = (ts ?? DateTimeOffset.UtcNow).ToString("O");

            // @m / @mt — message and message template
            // Prefer MessageTemplate from Properties for Seq property highlighting
            var msg = TryGetPreferredMessage(row, columnNames);
            if (!string.IsNullOrWhiteSpace(msg))
                clef["@m"] = msg;

            // @l — level (App Insights: severityLevel, Log Analytics: SeverityLevel)
            var sevCol = columnNames.Contains("SeverityLevel") ? "SeverityLevel"
                       : columnNames.Contains("severityLevel") ? "severityLevel" : null;
            if (sevCol is not null)
            {
                try
                {
                    var sev = row.GetInt32(sevCol);
                    if (sev.HasValue)
                        clef["@l"] = SeverityMap.ToSeqLevel(sev.Value);
                }
                catch { /* column type mismatch */ }
            }

            // @x — exception details
            var detCol = columnNames.Contains("Details") ? "Details"
                       : columnNames.Contains("details") ? "details" : null;
            if (detCol is not null)
            {
                var details = TryGetString(row, detCol);
                if (!string.IsNullOrEmpty(details))
                    clef["@x"] = details;
            }

            // Flatten customDimensions / Properties
            var cdCol = columnNames.Contains("Properties") ? "Properties"
                      : columnNames.Contains("CustomDimensions") ? "CustomDimensions"
                      : columnNames.Contains("customDimensions") ? "customDimensions" : null;
            if (cdCol is not null)
                FlattenJsonColumn(row, cdCol, clef);

            // Flatten customMeasurements / Measurements
            var cmCol = columnNames.Contains("Measurements") ? "Measurements"
                      : columnNames.Contains("CustomMeasurements") ? "CustomMeasurements"
                      : columnNames.Contains("customMeasurements") ? "customMeasurements" : null;
            if (cmCol is not null)
                FlattenJsonColumn(row, cmCol, clef);

            // All other columns as properties (skip empty strings)
            foreach (var col in table.Columns)
            {
                if (SpecialColumns.Contains(col.Name))
                    continue;

                try
                {
                    var value = row[col.Name];
                    if (value is null)
                        continue;
                    if (value is string s && string.IsNullOrWhiteSpace(s))
                        continue;
                    clef.TryAdd(col.Name, value);
                }
                catch { /* skip inaccessible columns */ }
            }

            // Attach input source marker for easy filtering in Seq
            clef.TryAdd("Seq.Input", "AzureLogAnalytics");

            // Source label
            if (!string.IsNullOrWhiteSpace(_sourceLabel))
                clef.TryAdd("SourceLabel", _sourceLabel);

            var eventId = TryGetEventId(row, columnNames);
            if (
                ts.HasValue
                && (
                    latestTimestamp is null
                    || ts > latestTimestamp
                    || (
                        ts == latestTimestamp
                        && string.CompareOrdinal(eventId ?? string.Empty, latestItemId ?? string.Empty)
                            > 0
                    )
                )
            )
            {
                latestTimestamp = ts;
                latestItemId = eventId;
            }

            // Write CLEF line
            lock (_sync)
            {
                var json = JsonSerializer.Serialize(clef, JsonOptions);
                _output.WriteLine(json);
                _output.Flush();
            }
        }

        if (!latestTimestamp.HasValue)
            return null;

        return new CursorPosition(latestTimestamp.Value, latestItemId);
    }

    private static void FlattenJsonColumn(
        LogsTableRow row,
        string columnName,
        Dictionary<string, object?> target)
    {
        try
        {
            var raw = row[columnName]?.ToString();
            if (string.IsNullOrEmpty(raw))
                return;

            var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(raw);
            if (parsed is null)
                return;

            // Extract message template for Seq property highlighting
            string? messageTemplate = null;
            if (parsed.TryGetValue("MessageTemplate", out var mtEl) && mtEl.ValueKind == JsonValueKind.String)
                messageTemplate = mtEl.GetString();
            else if (parsed.TryGetValue("prop__{OriginalFormat}", out var ofEl) && ofEl.ValueKind == JsonValueKind.String)
                messageTemplate = ofEl.GetString();

            if (!string.IsNullOrWhiteSpace(messageTemplate))
                target["@mt"] = messageTemplate;

            foreach (var kvp in parsed)
            {
                // Skip internal/redundant properties
                if (kvp.Key is "MessageTemplate" or "prop__{OriginalFormat}")
                    continue;

                // Strip prop__ prefix so names match @mt placeholders
                var key = kvp.Key.StartsWith("prop__", StringComparison.Ordinal)
                    ? kvp.Key[6..]
                    : kvp.Key;

                var value = ConvertJsonElement(kvp.Value);
                if (value is string s && string.IsNullOrWhiteSpace(s))
                    continue;

                target.TryAdd(key, value);
            }

            if (!string.IsNullOrWhiteSpace(messageTemplate))
                EnsureTemplateProperties(messageTemplate, target);
        }
        catch { /* ignore parse errors */ }
    }

    private static string? TryGetString(LogsTableRow row, string column)
    {
        try { return row.GetString(column); }
        catch { return row[column]?.ToString(); }
    }

    private static string? TryGetPreferredMessage(
        LogsTableRow row,
        HashSet<string> columnNames
    )
    {
        var outerCol = columnNames.Contains("OuterMessage")
            ? "OuterMessage"
            : columnNames.Contains("outerMessage")
                ? "outerMessage"
                : null;
        if (outerCol is not null)
        {
            var outer = TryGetString(row, outerCol);
            if (!string.IsNullOrWhiteSpace(outer))
                return outer;
        }

        var innerCol = columnNames.Contains("InnermostMessage")
            ? "InnermostMessage"
            : columnNames.Contains("innermostMessage")
                ? "innermostMessage"
                : null;
        if (innerCol is not null)
        {
            var inner = TryGetString(row, innerCol);
            if (!string.IsNullOrWhiteSpace(inner))
                return inner;
        }

        var msgCol = columnNames.Contains("Message")
            ? "Message"
            : columnNames.Contains("message")
                ? "message"
                : null;
        return msgCol is null ? null : TryGetString(row, msgCol);
    }

    private static string? TryGetEventId(
        LogsTableRow row,
        HashSet<string> columnNames
    )
    {
        var idCol = columnNames.Contains("__seq_event_id")
            ? "__seq_event_id"
            : columnNames.Contains("ItemId")
                ? "ItemId"
                : columnNames.Contains("itemId")
                    ? "itemId"
                    : columnNames.Contains("_ItemId")
                        ? "_ItemId"
                        : columnNames.Contains("id")
                            ? "id"
                            : null;
        var id = idCol is null ? null : TryGetString(row, idCol);
        return string.IsNullOrWhiteSpace(id) ? null : id;
    }

    private static void EnsureTemplateProperties(
        string messageTemplate,
        Dictionary<string, object?> target
    )
    {
        var existingKeys = new HashSet<string>(target.Keys, StringComparer.OrdinalIgnoreCase);

        foreach (Match match in TemplateTokenRegex.Matches(messageTemplate))
        {
            if (!match.Success)
                continue;

            var token = match.Groups[1].Value.TrimStart('@', '$');
            if (string.IsNullOrWhiteSpace(token))
                continue;
            if (existingKeys.Contains(token))
                continue;

            target[token] = "(null)";
            existingKeys.Add(token);
        }
    }

    private static object? ConvertJsonElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => element.ToString()
    };
}
