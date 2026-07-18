using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GalleryBrowser.Services;

internal sealed class JsonSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static readonly string[] ApplicationSettingsTables =
    [
        "external_app_rules",
        "new_tab_candidates",
        "winrar_settings",
        "ffmpeg_settings",
        "thumbnail_cache_targets",
        "thumbnail_cache_settings",
        "gallery_sections",
        "gallery_scan_targets",
        "gallery_scan_settings",
        "thumbnail_crop_adjustments",
        "gallery_database_settings",
        "search_engine_settings",
        "theme_settings",
        "language_settings",
        "gid_settings",
        "creator_tracking_settings"
    ];

    private static readonly string[] UiStateTables =
    [
        "explorer_bookmarks",
        "explorer_tabs",
        "ui_navigation_state"
    ];

    private readonly object _sync = new();
    private readonly string _applicationSettingsPath;
    private readonly string _uiStatePath;

    public JsonSettingsStore(string dataFolder)
    {
        _applicationSettingsPath = Path.Combine(dataFolder, "appsettings.json");
        _uiStatePath = Path.Combine(dataFolder, "ui-state.json");
    }

    public void HydrateOrCreate(SqliteConnection connection)
    {
        lock (_sync)
        {
            HydrateOrCreateFile(connection, _applicationSettingsPath, ApplicationSettingsTables);
            HydrateOrCreateFile(connection, _uiStatePath, UiStateTables);
        }
    }

    public void SaveApplicationSettings(SqliteConnection connection)
    {
        lock (_sync)
        {
            WriteSnapshot(connection, _applicationSettingsPath, ApplicationSettingsTables);
        }
    }

    public void SaveUiState(SqliteConnection connection)
    {
        lock (_sync)
        {
            WriteSnapshot(connection, _uiStatePath, UiStateTables);
        }
    }

    private static void HydrateOrCreateFile(
        SqliteConnection connection,
        string path,
        IReadOnlyList<string> tables)
    {
        if (!File.Exists(path))
        {
            WriteSnapshot(connection, path, tables);
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("tables", out var tableRoot) ||
                tableRoot.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"設定JSONのtablesを読み取れません: {path}");
            }

            using var transaction = connection.BeginTransaction();
            foreach (var table in tables)
            {
                if (!tableRoot.TryGetProperty(table, out var rows) || rows.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                ImportTable(connection, transaction, table, rows);
            }
            transaction.Commit();
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            var corruptPath = path + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            File.Copy(path, corruptPath, overwrite: false);
            WriteSnapshot(connection, path, tables);
        }
    }

    private static void ImportTable(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        JsonElement rows)
    {
        var columns = ReadColumnNames(connection, table);
        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = $"DELETE FROM {QuoteIdentifier(table)};";
            clear.ExecuteNonQuery();
        }

        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var values = columns
                .Where(column => row.TryGetProperty(column, out _))
                .Select(column => (Column: column, Value: row.GetProperty(column)))
                .ToArray();
            if (values.Length == 0)
            {
                continue;
            }

            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = $"INSERT INTO {QuoteIdentifier(table)} " +
                $"({string.Join(", ", values.Select(value => QuoteIdentifier(value.Column)))}) VALUES " +
                $"({string.Join(", ", values.Select((_, index) => "$value" + index))});";
            for (var index = 0; index < values.Length; index++)
            {
                insert.Parameters.AddWithValue("$value" + index, ConvertJsonValue(values[index].Value));
            }
            insert.ExecuteNonQuery();
        }
    }

    private static void WriteSnapshot(
        SqliteConnection connection,
        string path,
        IReadOnlyList<string> tables)
    {
        var tableRoot = new JsonObject();
        foreach (var table in tables)
        {
            tableRoot[table] = ExportTable(connection, table);
        }

        var root = new JsonObject
        {
            ["version"] = 1,
            ["updatedAt"] = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
            ["tables"] = tableRoot
        };
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, root.ToJsonString(JsonOptions));
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static JsonArray ExportTable(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {QuoteIdentifier(table)};";
        using var reader = command.ExecuteReader();
        var rows = new JsonArray();
        while (reader.Read())
        {
            var row = new JsonObject();
            for (var index = 0; index < reader.FieldCount; index++)
            {
                row[reader.GetName(index)] = reader.IsDBNull(index)
                    ? null
                    : ConvertDatabaseValue(reader.GetValue(index));
            }
            rows.Add(row);
        }
        return rows;
    }

    private static IReadOnlyList<string> ReadColumnNames(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({QuoteIdentifier(table)});";
        using var reader = command.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }
        return columns;
    }

    private static object ConvertJsonValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.True => 1,
        JsonValueKind.False => 0,
        JsonValueKind.Null => DBNull.Value,
        _ => value.GetRawText()
    };

    private static JsonNode? ConvertDatabaseValue(object value) => value switch
    {
        string text => JsonValue.Create(text),
        long integer => JsonValue.Create(integer),
        int integer => JsonValue.Create(integer),
        short integer => JsonValue.Create(integer),
        byte integer => JsonValue.Create(integer),
        double number => JsonValue.Create(number),
        float number => JsonValue.Create(number),
        decimal number => JsonValue.Create(number),
        bool boolean => JsonValue.Create(boolean),
        byte[] bytes => JsonValue.Create(Convert.ToBase64String(bytes)),
        _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture))
    };

    private static string QuoteIdentifier(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
