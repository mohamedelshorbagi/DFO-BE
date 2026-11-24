using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Dfo.Infrastructure;

public static class SequenceExtensions
{
    private static readonly HashSet<string> Known = ["ListingCodeSeq", "LegalRefSeq"];

    /// <summary>
    /// Next value of a SQL Server sequence. Uses a plain command because EF wraps raw
    /// queries in a subquery, where NEXT VALUE FOR is not allowed.
    /// </summary>
    public static async Task<int> NextValueAsync(this DfoDbContext db, string sequence)
    {
        if (!Known.Contains(sequence)) throw new ArgumentException("Unknown sequence.", nameof(sequence)); // name is concatenated: whitelist only
        var conn = db.Database.GetDbConnection();
        var wasClosed = conn.State != ConnectionState.Open;
        if (wasClosed) await db.Database.OpenConnectionAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT NEXT VALUE FOR [{sequence}]";
            var tx = db.Database.CurrentTransaction?.GetDbTransaction();
            if (tx is not null) cmd.Transaction = tx;
            return Convert.ToInt32(await cmd.ExecuteScalarAsync());
        }
        finally
        {
            if (wasClosed) await db.Database.CloseConnectionAsync();
        }
    }
}
