using Microsoft.Data.Sqlite;
using VortexApi.Suite.Auditing;
using VortexApi.Suite.Money;
using VortexApi.Suite.Taxes;
using VortexPos.Core.Data;
using VortexPos.Core.Domain;

using static VortexPos.Core.Texts.CoreText;

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

    /// <summary>Tienda de la caja: la elegida en este equipo (si el usuario puede usarla) o la primera tienda activa permitida.</summary>
    protected async Task<Guid?> DefaultSiteAsync(SqliteConnection c, SqliteTransaction? tx)
    {
        if (Context.DefaultSiteId is { } chosen && CanUseSite(chosen))
            return chosen;
        return await c.ScalarAsync<string?>($"""
            SELECT id FROM sites WHERE is_deleted = 0 AND is_active = 1{SiteFilter("id")}
            ORDER BY CASE kind WHEN 'Store' THEN 0 ELSE 1 END, code LIMIT 1
            """, tx) is { } id ? Guid.Parse(id) : null;
    }

    /// <summary>Si el usuario puede trabajar en la tienda (sin tiendas asignadas en el hub, en todas).</summary>
    protected bool CanUseSite(Guid siteId) => Context.SiteIds.Count == 0 || Context.SiteIds.Contains(siteId);

    /// <summary>Condición SQL que limita a las tiendas del usuario (vacía si puede usar todas). Los Ids son Guid: no hay inyección.</summary>
    protected string SiteFilter(string column) =>
        Context.SiteIds.Count == 0 ? "" : $" AND {column} IN ({string.Join(", ", Context.SiteIds.Select(s => $"'{s}'"))})";

    /// <summary>Inicio del día local (Perú, UTC-5) como instante UTC, para filtrar "hoy".</summary>
    protected DateTimeOffset StartOfDay(int daysAgo = 0)
    {
        var local = Clock.GetUtcNow().ToOffset(PosClock.Offset);
        return new DateTimeOffset(local.Date, PosClock.Offset).AddDays(-daysAgo).ToUniversalTime();
    }

    protected static string Like(string? term) => $"%{term?.Trim().Replace("%", "").Replace("_", "")}%";

    protected static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Corta la operación si el rol del usuario no la permite (el mensaje se muestra tal cual).</summary>
    protected void RequirePermission(string permission)
    {
        if (!Context.Can(permission))
            throw new PosValidationException(T("error.permiso_supervisor", Context.Role, PosPermissions.Describe(permission)));
    }

    /// <summary>Deja la acción en la bitácora, en la misma transacción que la acción.</summary>
    protected Task AuditAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, string action, string entity, Guid? entityId,
        string? reference, string? detail) =>
        Audit.WriteAsync(c, tx, new AuditEntry(Guid.NewGuid(), now, Context.UserId, Context.UserName, Context.DeviceTag, action, entity,
            entityId?.ToString(), reference, detail));

    /// <summary>Régimen de impuesto de la empresa (país y tasa general del hub). Sin datos de la empresa, Perú.</summary>
    internal static async Task<TaxRegime> TaxRegimeAsync(SqliteConnection c, SqliteTransaction? tx)
    {
        var company = await c.FirstOrDefaultAsync("SELECT country_code, default_tax_rate FROM company WHERE id = 1",
            r => new { Country = r.Str("country_code"), Rate = r.DecOrNull("default_tax_rate") }, tx);
        return TaxCatalog.For(company?.Country ?? "PE", company?.Rate);
    }

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

/// <summary>Bitácora de acciones sensibles (no se edita ni se borra).</summary>
internal static class Audit
{
    public static Task WriteAsync(SqliteConnection c, SqliteTransaction tx, AuditEntry e) =>
        c.ExecAsync("""
            INSERT INTO audit_log (id, at, user_id, user_name, device, action, entity, entity_id, reference, detail)
            VALUES ($id, $at, $user, $name, $device, $action, $entity, $entityId, $ref, $detail)
            """, tx, ("$id", e.Id), ("$at", e.At), ("$user", e.UserId), ("$name", e.UserName), ("$device", e.Device),
            ("$action", e.Action), ("$entity", e.Entity), ("$entityId", e.EntityId), ("$ref", e.Reference), ("$detail", e.Detail));
}

/// <summary>Consulta de la bitácora.</summary>
public sealed class AuditService(PosDatabase db, IPosContext context, TimeProvider clock) : PosService(db, context, clock)
{
    public async Task<PagedResult<AuditRow>> GetAsync(int page, int pageSize, CancellationToken ct = default)
    {
        RequirePermission(PosPermissions.ViewAudit);
        await using var c = await Db.OpenAsync(ct);
        var total = await c.ScalarAsync<int>("SELECT COUNT(*) FROM audit_log");
        var rows = await c.QueryAsync("SELECT * FROM audit_log ORDER BY at DESC LIMIT $limit OFFSET $offset", r => new AuditRow(
            r.Guid("id"), r.Time("at"), r.StrOrNull("user_name"), r.StrOrNull("device"), r.Str("action"), r.Str("entity"),
            r.StrOrNull("reference"), r.StrOrNull("detail")), null, ("$limit", pageSize), ("$offset", (page - 1) * pageSize));
        return new PagedResult<AuditRow>(rows, total, page, pageSize);
    }
}
