using Azure.Core;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;

namespace Seq.Input.AzureLogAnalytics;

sealed class LogAnalyticsQueryClient
{
    private readonly LogsQueryClient _client;
    private readonly string _resourceId;
    private readonly bool _useResourceQuery;

    public LogAnalyticsQueryClient(TokenCredential credential, string resourceId, bool useResourceQuery)
    {
        _client = new LogsQueryClient(credential);
        _resourceId = resourceId;
        _useResourceQuery = useResourceQuery;
    }

    public async Task<LogsTable?> QueryAsync(
        string kqlQuery,
        CursorPosition cursor,
        CancellationToken ct)
    {
        // Use an explicit where clause with strictly-greater-than to avoid re-fetching
        // the last seen event. The SDK's QueryTimeRange is inclusive (>=).
        var cursorUtc = cursor.Timestamp.UtcDateTime.ToString("O");
        var timestampExpr =
            "coalesce(" +
            "todatetime(column_ifexists(\"TimeGenerated\", datetime(null))), " +
            "todatetime(column_ifexists(\"timestamp\", datetime(null))))";
        var eventIdExpr =
            "coalesce(" +
            "tostring(column_ifexists(\"ItemId\", \"\")), " +
            "tostring(column_ifexists(\"itemId\", \"\")), " +
            "tostring(column_ifexists(\"_ItemId\", \"\")), " +
            "tostring(column_ifexists(\"id\", \"\")))";

        var cursorPredicate = string.IsNullOrWhiteSpace(cursor.ItemId)
            ? "__seq_ts > datetime('" + cursorUtc + "')"
            : "__seq_ts > datetime('" + cursorUtc + "') or (__seq_ts == datetime('" + cursorUtc + "') and strcmp(__seq_event_id, '" + EscapeKqlString(cursor.ItemId) + "') > 0)";

        var filteredQuery =
            $"{kqlQuery} | extend __seq_ts = {timestampExpr}, __seq_event_id = {eventIdExpr} | where isnotnull(__seq_ts) | where {cursorPredicate} | order by __seq_ts asc, __seq_event_id asc";

        var timeRange = new QueryTimeRange(cursor.Timestamp, DateTimeOffset.UtcNow);

        if (_useResourceQuery)
        {
            var response = await _client.QueryResourceAsync(
                new ResourceIdentifier(_resourceId),
                filteredQuery,
                timeRange,
                cancellationToken: ct);

            return response.Value.Table;
        }
        else
        {
            var response = await _client.QueryWorkspaceAsync(
                _resourceId,
                filteredQuery,
                timeRange,
                cancellationToken: ct);

            return response.Value.Table;
        }
    }

    private static string EscapeKqlString(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);
}
