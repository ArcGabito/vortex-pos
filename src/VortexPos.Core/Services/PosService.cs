using Microsoft.Data.Sqlite;
using VortexPos.Core.Data;
using VortexPos.Core.Domain;

namespace VortexPos.Core.Services;

/// <summary>Base de los servicios: base local, contexto del usuario y reloj.</summary>
public abstract class PosService(PosDatabase db, IPosContext context, TimeProvider clock)
{
    protected PosDatabase Db { get; } = db;
    protected IPosContext Context { get; } = context;
    protected TimeProvider Clock { get; } = clock;

    protected async Task<T> InTransactionAsync<T>(Func<SqliteConnection, SqliteTransaction, DateTimeOffset, Task<T>> work,
        CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        await using var tx = c.BeginTransaction();
        var result = await work(c, tx, Clock.GetUtcNow());
        await tx.CommitAsync(ct);
        return result;
    }

    /// <summary>Tienda de la caja: la elegida en este equipo o la primera tienda activa.</summary>
    protected async Task<Guid?> DefaultSiteAsync(SqliteConnection c, SqliteTransaction? tx)
    {
        if (Context.DefaultSiteId is { } chosen)
            return chosen;
        return await c.ScalarAsync<string?>("""
            SELECT id FROM sites WHERE is_deleted = 0 AND is_active = 1
            ORDER BY CASE kind WHEN 'Store' THEN 0 ELSE 1 END, code LIMIT 1
            """, tx) is { } id ? Guid.Parse(id) : null;
    }

    /// <summary>Inicio del día local (Perú, UTC-5) como instante UTC, para filtrar "hoy".</summary>
    protected DateTimeOffset StartOfDay(int daysAgo = 0)
    {
        var local = Clock.GetUtcNow().ToOffset(PosClock.Offset);
        return new DateTimeOffset(local.Date, PosClock.Offset).AddDays(-daysAgo).ToUniversalTime();
    }

    protected static string Like(string? term) => $"%{term?.Trim().Replace("%", "").Replace("_", "")}%";

    protected static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    protected static void Require(bool condition, string message)
    {
        if (!condition)
            throw new PosValidationException(message);
    }
}

/// <summary>Zona horaria de los reportes del día. Perú no usa horario de verano.</summary>
public static class PosClock
{
    public static readonly TimeSpan Offset = TimeSpan.FromHours(-5);
}

internal static class DocumentNumbers
{
    public const string Sale = "V";
    public const string Return = "DEV";
    public const string CashSession = "CAJA";

    /// <summary>Numeración por equipo (<c>V-3F0C-000012</c>): dos cajas sin conexión nunca generan el mismo número.</summary>
    public static async Task<string> NextAsync(SqliteConnection c, SqliteTransaction tx, string prefix, string deviceTag)
    {
        var value = await c.ScalarAsync<long>("""
            INSERT INTO document_sequences (prefix, value) VALUES ($p, 1)
            ON CONFLICT (prefix) DO UPDATE SET value = value + 1 RETURNING value
            """, tx, ("$p", prefix));
        return $"{prefix}-{deviceTag}-{value:D6}";
    }
}
