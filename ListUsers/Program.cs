using System;
using Microsoft.Data.Sqlite;
using var conn = new SqliteConnection("Data Source=../MtgEngine.Api/mtgengine.db");
conn.Open();
var cmd = conn.CreateCommand();
cmd.CommandText = "DELETE FROM Users WHERE Username = 'testcheck';";
var deleted = cmd.ExecuteNonQuery();
Console.WriteLine($"Deleted {deleted} test user(s).");
