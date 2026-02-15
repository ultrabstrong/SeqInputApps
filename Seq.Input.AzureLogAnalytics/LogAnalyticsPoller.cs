using Serilog;
using Serilog.Events;

namespace Seq.Input.AzureLogAnalytics;

sealed class LogAnalyticsPoller : IDisposable
{
    private readonly CancellationTokenSource _cancel = new();
    private readonly Task _pollingTask;

    public LogAnalyticsPoller(
        LogAnalyticsQueryClient queryClient,
        ClefEventWriter writer,
        string kqlQuery,
        TimeSpan interval,
        CursorStore cursorStore,
        ILogger diagnosticLog,
        LogEventLevel? minLevel)
    {
        _pollingTask = Task.Run(
            () => RunAsync(queryClient, writer, kqlQuery, interval, cursorStore, diagnosticLog, minLevel, _cancel.Token),
            _cancel.Token);
    }

    private static async Task RunAsync(
        LogAnalyticsQueryClient queryClient,
        ClefEventWriter writer,
        string kqlQuery,
        TimeSpan interval,
        CursorStore cursorStore,
        ILogger diagnosticLog,
        LogEventLevel? minLevel,
        CancellationToken ct)
    {
        var cursor = cursorStore.Load();

        if (minLevel is not null && minLevel <= LogEventLevel.Information)
            diagnosticLog.Information(
                "Azure Log Analytics poller started. Polling every {IntervalSeconds}s, cursor starting at {CursorTimestamp} ({CursorItemId})",
                interval.TotalSeconds,
                cursor.Timestamp,
                cursor.ItemId ?? "<none>");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var table = await queryClient.QueryAsync(kqlQuery, cursor, ct);

                if (table is not null && table.Rows.Count > 0)
                {
                    if (minLevel is not null && minLevel <= LogEventLevel.Debug)
                        diagnosticLog.Debug(
                            "Retrieved {RowCount} events from Log Analytics",
                            table.Rows.Count);

                    var latestCursor = writer.WriteEvents(table);

                    if (latestCursor.HasValue)
                    {
                        cursor = latestCursor.Value;
                        cursorStore.Save(cursor);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                diagnosticLog.Error(ex, "Error polling Azure Log Analytics");
            }

            try
            {
                await Task.Delay(interval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        if (minLevel is not null && minLevel <= LogEventLevel.Information)
            diagnosticLog.Information("Azure Log Analytics poller stopped");
    }

    public void Stop()
    {
        _cancel.Cancel();

        try { _pollingTask.Wait(TimeSpan.FromSeconds(10)); }
        catch (AggregateException) { /* expected on cancellation */ }
    }

    public void Dispose()
    {
        _cancel.Dispose();
        _pollingTask.Dispose();
    }
}
