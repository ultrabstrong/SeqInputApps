using System.IO;
using Azure.Identity;
using Azure.Core;
using Seq.Apps;

namespace Seq.Input.AzureLogAnalytics;

[SeqApp(
    "Azure Log Analytics Input",
    Description = "Polls an Azure Log Analytics workspace and publishes structured events to Seq.")]
public class LogAnalyticsInput : SeqApp, IPublishJson, IDisposable
{
    private const string DefaultWorkspaceKqlQuery = "union isfuzzy=true AppTraces, AppExceptions";
    private const string DefaultResourceKqlQuery = "union isfuzzy=true traces, exceptions";

    LogAnalyticsPoller? _poller;

    [SeqAppSetting(
        DisplayName = "Workspace ID",
        IsOptional = true,
        HelpText = "The Log Analytics workspace ID (GUID). Use this OR Resource ID, not both.")]
    public string? WorkspaceId { get; set; }

    [SeqAppSetting(
        DisplayName = "Resource ID",
        IsOptional = true,
        HelpText = "The full Azure resource ID for App Insights (e.g. /subscriptions/.../microsoft.insights/components/...). " +
                   "When set, queries use the App Insights schema (traces, requests, etc.) instead of Log Analytics schema (AppTraces, AppRequests).")]
    public string? ResourceId { get; set; }

    [SeqAppSetting(
        DisplayName = "Tenant ID",
        IsOptional = true,
        HelpText = "Azure AD tenant ID. Required when using Client ID / Secret authentication. " +
                   "If omitted, DefaultAzureCredential is used (supports az login, managed identity, etc.).")]
    public string? TenantId { get; set; }

    [SeqAppSetting(
        DisplayName = "Client ID",
        IsOptional = true,
        HelpText = "Azure AD app registration client ID for service principal authentication.")]
    public string? ClientId { get; set; }

    [SeqAppSetting(
        DisplayName = "Client Secret",
        IsOptional = true,
        InputType = SettingInputType.Password,
        HelpText = "Azure AD app registration client secret.")]
    public string? ClientSecret { get; set; }

    [SeqAppSetting(
        DisplayName = "KQL Query",
        IsOptional = true,
        InputType = SettingInputType.LongText,
        HelpText = "The KQL query to execute against the workspace. " +
                   "Default: 'union isfuzzy=true AppTraces, AppExceptions'. " +
                   "When Resource ID is set and this default is unchanged, it automatically maps to 'union isfuzzy=true traces, exceptions'. " +
                   "The time range is applied automatically; do not add a timestamp filter. " +
                   "The query must return either 'timestamp' (App Insights schema) or 'TimeGenerated' (Log Analytics schema).")]
    public string KqlQuery { get; set; } = DefaultWorkspaceKqlQuery;

    [SeqAppSetting(
        DisplayName = "Poll Interval (seconds)",
        IsOptional = true,
        HelpText = "How often to poll Log Analytics for new events. Default: 60.")]
    public int IntervalSeconds { get; set; } = 60;

    [SeqAppSetting(
        DisplayName = "Initial Lookback (minutes)",
        IsOptional = true,
        HelpText = "How far back to look on the first poll after startup. Default: 5.")]
    public int InitialLookbackMinutes { get; set; } = 5;

    [SeqAppSetting(
        DisplayName = "Source Label",
        IsOptional = true,
        HelpText = "An optional label added as a 'SourceLabel' property on every ingested event, " +
                   "useful for filtering in Seq (e.g. 'Production', 'AzureLogAnalytics').")]
    public string? SourceLabel { get; set; }

    [SeqAppSetting(
        DisplayName = "Diagnostic Log Level",
        IsOptional = true,
        HelpText = "Minimum level for internal diagnostic messages. Default: Warning (only warnings and errors). " +
                   "Set to Debug or Verbose for troubleshooting. Valid values: Verbose, Debug, Information, Warning, Error, Fatal, Off.")]
    public string DiagnosticLogLevel { get; set; } = "Warning";

    public void Start(TextWriter inputWriter)
    {
        var credential = CreateCredential();

        var useResource = !string.IsNullOrWhiteSpace(ResourceId);
        var targetId = useResource ? ResourceId! : WorkspaceId!;

        if (string.IsNullOrWhiteSpace(targetId))
            throw new InvalidOperationException("Either Workspace ID or Resource ID must be provided.");

        Log.Information(
            "Starting Azure Log Analytics input. Target={TargetId}, Mode={Mode}, HasTenant={HasTenant}, HasClientId={HasClientId}, HasSecret={HasSecret}",
            targetId,
            useResource ? "Resource" : "Workspace",
            !string.IsNullOrWhiteSpace(TenantId),
            !string.IsNullOrWhiteSpace(ClientId),
            !string.IsNullOrWhiteSpace(ClientSecret));

        var queryClient = new LogAnalyticsQueryClient(credential, targetId, useResource);
        var writer = new ClefEventWriter(inputWriter, SourceLabel);
        var query = ResolveKqlQuery(KqlQuery, useResource);

        var cursorStore = new CursorStore(App.StoragePath, TimeSpan.FromMinutes(InitialLookbackMinutes));

        var minLevel = SeverityMap.ParseDiagnosticLogLevel(DiagnosticLogLevel);

        _poller = new LogAnalyticsPoller(
            queryClient,
            writer,
            query,
            TimeSpan.FromSeconds(IntervalSeconds),
            cursorStore,
            Log,
            minLevel);
    }

    public void Stop()
    {
        _poller?.Stop();
    }

    public void Dispose()
    {
        _poller?.Dispose();
    }

    private TokenCredential CreateCredential()
    {
        if (!string.IsNullOrWhiteSpace(TenantId)
            && !string.IsNullOrWhiteSpace(ClientId)
            && !string.IsNullOrWhiteSpace(ClientSecret))
        {
            return new ClientSecretCredential(TenantId, ClientId, ClientSecret);
        }

        return new DefaultAzureCredential();
    }

    private static string ResolveKqlQuery(string? configuredQuery, bool useResourceQuery)
    {
        var query = configuredQuery?.Trim();
        if (string.IsNullOrWhiteSpace(query))
            return useResourceQuery ? DefaultResourceKqlQuery : DefaultWorkspaceKqlQuery;

        if (
            useResourceQuery
            && string.Equals(query, DefaultWorkspaceKqlQuery, StringComparison.OrdinalIgnoreCase)
        )
            return DefaultResourceKqlQuery;

        return query;
    }
}
