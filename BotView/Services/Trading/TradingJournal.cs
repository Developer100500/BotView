using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using BotView.Models;

namespace BotView.Services.Trading;

/// <summary>Crash recovery journal. Only application models are serialized; keys and raw API data never enter this database.</summary>
public sealed class TradingJournal
{
    private readonly string _path;
    private readonly object _sync = new();

    /// <summary>Creates a private journal independent of market-data databases.</summary>
    public TradingJournal(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BotView", "trading-journal.db");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS journal (account TEXT NOT NULL, kind TEXT NOT NULL, id TEXT NOT NULL, payload TEXT NOT NULL, PRIMARY KEY(account,kind,id));";
        command.ExecuteNonQuery();
    }

    /// <summary>Commits an operation before the network boundary; every write is its own durable transaction.</summary>
    public void Save(TradingOperation operation) => Write(operation.AccountKey, "intent", operation.Id, operation);

    /// <summary>Persists authoritative order state, including partially filled terminal orders.</summary>
    internal void Save(string account, OrderSnapshot order) => Write(account, "order", Identity(order.Reference), order);

    /// <summary>Upserts executions by exchange identity so repeated stream/history events cannot duplicate a fill.</summary>
    internal void Save(string account, ExecutionSnapshot execution) => Write(account, "fill", Identity(execution.Order) + ":" + execution.ExecutionId, execution);

    /// <summary>Loads account-scoped durable intents after account identity verification.</summary>
    public IReadOnlyList<TradingOperation> Operations(string account) => Read<TradingOperation>(account, "intent");

    /// <summary>Restores order state without claiming that a saved snapshot is current.</summary>
    internal IReadOnlyList<OrderSnapshot> Orders(string account) => Read<OrderSnapshot>(account, "order");

    /// <summary>Restores execution identities for deduplication and history watermarks.</summary>
    internal IReadOnlyList<ExecutionSnapshot> Executions(string account) => Read<ExecutionSnapshot>(account, "fill");

    /// <summary>Scopes order identities by the complete instrument listing.</summary>
    private static string Identity(OrderReference reference) => JsonSerializer.Serialize(reference);

    /// <summary>Uses parameterized writes and never interpolates application data into SQL.</summary>
    private void Write<T>(string account, string kind, string id, T value)
    {
        lock (_sync)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = "INSERT INTO journal(account,kind,id,payload) VALUES($account,$kind,$id,$payload) ON CONFLICT(account,kind,id) DO UPDATE SET payload=excluded.payload";
            command.Parameters.AddWithValue("$account", account);
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(value));
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Reads only the selected verified account and record category.</summary>
    private IReadOnlyList<T> Read<T>(string account, string kind)
    {
        lock (_sync)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT payload FROM journal WHERE account=$account AND kind=$kind";
            command.Parameters.AddWithValue("$account", account);
            command.Parameters.AddWithValue("$kind", kind);
            using var reader = command.ExecuteReader();
            var result = new List<T>();
            while (reader.Read()) result.Add(JsonSerializer.Deserialize<T>(reader.GetString(0))!);
            return result;
        }
    }

    /// <summary>Opens a short-lived connection with durable synchronous commits.</summary>
    private SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
        return db;
    }
}
