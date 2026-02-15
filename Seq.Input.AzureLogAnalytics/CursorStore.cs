using System.Text.Json;

namespace Seq.Input.AzureLogAnalytics;

readonly record struct CursorPosition(DateTimeOffset Timestamp, string? ItemId);

sealed class CursorStore
{
    private readonly string _filePath;
    private readonly TimeSpan _initialLookback;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public CursorStore(string storagePath, TimeSpan initialLookback)
    {
        _initialLookback = initialLookback;
        Directory.CreateDirectory(storagePath);
        _filePath = Path.Combine(storagePath, "cursor.json");
    }

    public CursorPosition Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                var state = JsonSerializer.Deserialize<CursorState>(json);
                if (state?.LastTimestamp is not null)
                    return new CursorPosition(state.LastTimestamp.Value, state.LastItemId);
            }
        }
        catch
        {
            // Corrupt or inaccessible — fall back to lookback
        }

        return new CursorPosition(DateTimeOffset.UtcNow - _initialLookback, null);
    }

    public void Save(CursorPosition cursor)
    {
        try
        {
            var state = new CursorState
            {
                LastTimestamp = cursor.Timestamp,
                LastItemId = cursor.ItemId
            };
            var json = JsonSerializer.Serialize(state, JsonOptions);
            File.WriteAllText(_filePath, json);
        }
        catch
        {
            // Best-effort — don't crash the poller over persistence
        }
    }

    private sealed class CursorState
    {
        public DateTimeOffset? LastTimestamp { get; set; }
        public string? LastItemId { get; set; }
    }
}
