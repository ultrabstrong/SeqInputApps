# SeqInputApps

Generic home for custom Seq apps and related tooling.

## Solution contents

- `Seq.Input.AzureLogAnalytics` — Seq input app that pulls telemetry from Azure Log Analytics / Application Insights into Seq.

## Project docs

- Azure Log Analytics input app README: [Seq.Input.AzureLogAnalytics/README.md](./Seq.Input.AzureLogAnalytics/README.md)

## Build

From the solution root:

```powershell
dotnet build .\SeqInputApps.slnx
```

## Package

To pack the Azure Log Analytics input app:

```powershell
dotnet pack .\Seq.Input.AzureLogAnalytics\Seq.Input.AzureLogAnalytics.csproj -c Release -o .\Seq.Input.AzureLogAnalytics\artifacts
```
