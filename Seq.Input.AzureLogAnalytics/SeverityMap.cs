using Serilog.Events;

namespace Seq.Input.AzureLogAnalytics;

static class SeverityMap
{
    public static string ToSeqLevel(int severityLevel) => severityLevel switch
    {
        0 => "Verbose",
        1 => "Information",
        2 => "Warning",
        3 => "Error",
        4 => "Fatal",
        _ => "Information"
    };

    public static LogEventLevel? ParseDiagnosticLogLevel(string? level) =>
        level?.Trim().ToLowerInvariant() switch
        {
            "verbose" or "trace" => LogEventLevel.Verbose,
            "debug" => LogEventLevel.Debug,
            "information" or "info" => LogEventLevel.Information,
            "warning" or "warn" => LogEventLevel.Warning,
            "error" => LogEventLevel.Error,
            "fatal" or "critical" => LogEventLevel.Fatal,
            "off" or "none" => null,
            _ => LogEventLevel.Warning
        };
}
