# Seq.Input.AzureLogAnalytics

A Seq input app that polls Azure Log Analytics / Application Insights telemetry and emits events into Seq as CLEF.

## What it does

- Queries Azure on a timer.
- Reads rows from either:
  - **Workspace schema** (`AppTraces`, `AppExceptions`, etc.), or
  - **Application Insights resource schema** (`traces`, `exceptions`, etc.).
- Converts each row into a Seq event.
- Tracks a cursor so only new rows are ingested on subsequent polls.

## Current behavior (important)

- **KQL Query** has a visible default value:
  - `union isfuzzy=true AppTraces, AppExceptions`
- If you use **Resource ID** and keep that default unchanged, the app maps it to:
  - `union isfuzzy=true traces, exceptions`
- The app automatically appends cursor filtering and ordering to your KQL.
- Every event gets a default property:
  - `Seq.Input = "AzureLogAnalytics"`

## Configuration

Configure this app in Seq input settings.

### Required target setting

Set **one** of these:

- **Workspace ID** (GUID) for Log Analytics workspace queries, or
- **Resource ID** (full ARM ID) for Application Insights resource queries.

Do not set both.

### Authentication

Two modes are supported:

1. **Service principal**
   - `Tenant ID`
   - `Client ID`
   - `Client Secret`
2. **DefaultAzureCredential** (if the above are not all provided)
   - Supports local `az login`, managed identity, and other DAC sources.

## Settings reference

| Setting | Default | Effect |
|---|---:|---|
| Workspace ID | _(empty)_ | Target workspace GUID for `QueryWorkspaceAsync`. |
| Resource ID | _(empty)_ | Target App Insights resource for `QueryResourceAsync`. Also switches default schema to `traces/exceptions`. |
| Tenant ID | _(empty)_ | Used only for service principal auth. |
| Client ID | _(empty)_ | Used only for service principal auth. |
| Client Secret | _(empty)_ | Used only for service principal auth. |
| KQL Query | `union isfuzzy=true AppTraces, AppExceptions` | Base query used for polling. If `Resource ID` is set and this value is unchanged, it maps to `union isfuzzy=true traces, exceptions`. Cursor filter/order is appended automatically. |
| Poll Interval (seconds) | `60` | How often Azure is polled for new rows. |
| Initial Lookback (minutes) | `5` | Used only when no cursor exists yet. |
| Source Label | _(empty)_ | Adds `SourceLabel` property to every ingested Seq event. |
| Diagnostic Log Level | `Warning` | Verbosity of this input app's internal diagnostics. `Off` disables diagnostic output. |

## Cursoring and incremental ingestion

Cursor state is persisted in the app storage path (`cursor.json`) as:

- last timestamp, and
- last event id.

The app appends a predicate equivalent to:

- `timestamp > lastTimestamp`, or
- if equal timestamp, only rows with lexicographically larger event id.

This avoids re-reading identical rows while still handling same-timestamp ties.

If you want a backfill replay, stop the input and delete/reset `cursor.json`.

## Event mapping into Seq

For each row, the app maps:

- `@t`: `TimeGenerated` (workspace) or `timestamp` (AI schema)
- `@m`: preferred message column (`OuterMessage`, then `InnermostMessage`, then `Message`)
- `@mt`: extracted from `Properties.MessageTemplate` or `prop__{OriginalFormat}`
- `@l`: mapped from severity integer (`0..4` => Verbose..Fatal)
- `@x`: exception `Details` column when present
- missing template tokens are backfilled as `"(null)"` so rendered messages don't show unresolved placeholders

It also flattens:

- `Properties` / `CustomDimensions` / `customDimensions`
- `Measurements` / `CustomMeasurements` / `customMeasurements`

Plus:

- `Seq.Input = AzureLogAnalytics` (always)
- `SourceLabel` (if configured)

## KQL guidance

- Do **not** add your own timestamp window/cursor filter in KQL; the app appends one.
- Your query should return rows compatible with either `TimeGenerated` or `timestamp`.
- Add any custom filtering (for example on `SourceContext`) directly in your KQL.

## Troubleshooting

- **`Either Workspace ID or Resource ID must be provided.`**
  - Set one target setting.
- **No events after startup**
  - Check cursor position and `Initial Lookback`.
  - Set `Diagnostic Log Level` to `Debug` temporarily.
- **KQL semantic errors**
  - Validate custom KQL in Azure Logs first.
  - Ensure required timestamp column exists in query output.

