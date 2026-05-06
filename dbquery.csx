#!/usr/bin/env dotnet-script
// Quick DB reader - run with: dotnet script dbquery.csx
using Microsoft.Data.Sqlite;

var db = @"C:\dev\MtgEngine\MtgEngine.Api\mtgengine.db";
using var conn = new SqliteConnection($"Data Source={db}");
conn.Open();

var cmd = conn.CreateCommand();
cmd.CommandText = @"
    SELECT u.Username, c.Id, c.Name, c.CommanderOracleId
    FROM Collections c
    JOIN Users u ON c.UserId = u.Id
    WHERE c.IsDeck=1 AND c.CommanderOracleId IS NOT NULL
    LIMIT 5";

using var r = cmd.ExecuteReader();
while (r.Read())
    Console.WriteLine($"user={r.GetString(0)}  deck={r.GetString(1)}  name={r.GetString(2)}  cmdr={r.GetString(3)}");
