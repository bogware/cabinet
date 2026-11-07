using Dapper;
using Npgsql;

namespace Cabinet.Database.Providers;

/// <summary>
/// Serializes schema creation between API instances. <c>CREATE ... IF NOT EXISTS</c> isn't safe
/// to run concurrently in PostgreSQL: instances starting together on a new database can fail with
/// a unique violation on the system catalogs.
/// </summary>
internal static class PostgresSchemaLock
{
    private const long LockKey = 0x4341_4249_4E45_54; // "CABINET"

    /// <summary>
    /// Begins a transaction holding the schema lock. Commit it after creating the schema, which
    /// also releases the lock.
    /// </summary>
    public static async Task<NpgsqlTransaction> BeginAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken = default)
    {
        var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(
            new CommandDefinition(
                "SELECT pg_advisory_xact_lock(@LockKey)",
                new { LockKey },
                transaction,
                cancellationToken: cancellationToken));
        return transaction;
    }
}
