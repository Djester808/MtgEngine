#!/usr/bin/env dotnet-script
// Quick script to apply Format/CommanderOracleId columns to the SQLite DB

var dbPath = @"C:\dev\MtgEngine\MtgEngine.Api\mtgengine.db";
var connectionString = $"Data Source={dbPath}";

using var conn = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
conn.Open();

// Check if columns exist
bool hasFormat = false, hasCmdr = false;
using (var check = conn.CreateCommand()) {
    check.CommandText = "PRAGMA table_info(Collections);";
    using var reader = check.ExecuteReader();
    while (reader.Read()) {
        var col = reader.GetString(1);
        if (col == "Format") hasFormat = true;
        if (col == "CommanderOracleId") hasCmdr = true;
    }
}

Console.WriteLine($"Format column exists: {hasFormat}");
Console.WriteLine($"CommanderOracleId column exists: {hasCmdr}");

if (!hasFormat) {
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "ALTER TABLE Collections ADD COLUMN Format TEXT;";
    cmd.ExecuteNonQuery();
    Console.WriteLine("Added Format column.");
}

if (!hasCmdr) {
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "ALTER TABLE Collections ADD COLUMN CommanderOracleId TEXT;";
    cmd.ExecuteNonQuery();
    Console.WriteLine("Added CommanderOracleId column.");
}

// Record migration as applied
using (var ins = conn.CreateCommand()) {
    ins.CommandText = @"INSERT OR IGNORE INTO __EFMigrationsHistory (MigrationId, ProductVersion)
                        VALUES ('20260425200000_AddFormatToCollection', '9.0.0');";
    ins.ExecuteNonQuery();
    Console.WriteLine("Migration recorded in __EFMigrationsHistory.");
}

Console.WriteLine("Done.");
