using Microsoft.Data.Sqlite;
using VortexPos.Core.Data;
using VortexPos.Core.Domain;

using static VortexPos.Core.Texts.CoreText;

namespace VortexPos.Core.Services;

/// <summary>
/// Turnos de caja de este equipo: apertura con fondo inicial, ventas y devoluciones del turno, y cierre con arqueo
/// (efectivo esperado contra contado). Es un dato local de cada caja: no se publica a la suite.
/// </summary>
public sealed class CashService(PosDatabase db, IPosContext context, TimeProvider clock) : PosService(db, context, clock)
{
    public async Task<CashSessionRow?> GetOpenAsync(CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await OpenSessionAsync(c, null);
    }

    public Task<Guid> OpenAsync(decimal openingFloat, Guid? siteId = null, CancellationToken ct = default)
    {
        RequirePermission(PosPermissions.OpenCash);
        Require(openingFloat >= 0, T("error.el_fondo_inicial_no_puede"));
        return InTransactionAsync(async (c, tx, now) =>
        {
            Require(await OpenSessionAsync(c, tx) is null, T("error.ya_hay_una_caja_abierta"));
            var site = siteId ?? await DefaultSiteAsync(c, tx);
            Require(site is not null, T("error.elige_la_tienda_de_esta"));
            Require(await c.ScalarAsync<long>("SELECT COUNT(*) FROM sites WHERE id = $id AND is_deleted = 0", tx, ("$id", site)) > 0,
                T("error.la_tienda_elegida_no_existe"));

            var id = Guid.NewGuid();
            var number = await DocumentNumbers.NextAsync(c, tx, DocumentNumbers.CashSession, Context.DeviceTag);
            await c.ExecAsync("""
                INSERT INTO cash_sessions (id, number, site_id, status, opened_at, opened_by, opening_float)
                VALUES ($id, $number, $site, 'open', $now, $user, $float)
                """, tx, ("$id", id), ("$number", number), ("$site", site), ("$now", now), ("$user", Context.UserName),
                ("$float", openingFloat));
            await AuditAsync(c, tx, now, VortexApi.Suite.Auditing.AuditActions.CashOpened, "cash_session", id, number, $"Fondo inicial {openingFloat:0.##}");
            return id;
        }, ct);
    }

    public async Task<CashSummary> GetSummaryAsync(Guid sessionId, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await SummaryAsync(c, null, sessionId);
    }

    /// <summary>Cierra el turno con el efectivo contado. Devuelve la diferencia (contado − esperado).</summary>
    public Task<decimal> CloseAsync(Guid sessionId, decimal countedCash, string? note, CancellationToken ct = default)
    {
        RequirePermission(PosPermissions.CloseCash);
        Require(countedCash >= 0, T("error.el_efectivo_contado_no_puede"));
        return InTransactionAsync(async (c, tx, now) =>
        {
            var status = await c.ScalarAsync<string?>("SELECT status FROM cash_sessions WHERE id = $id", tx, ("$id", sessionId));
            Require(status == CashSessionStatuses.Open, T("error.la_caja_ya_esta_cerrada"));
            var summary = await SummaryAsync(c, tx, sessionId);
            await c.ExecAsync("""
                UPDATE cash_sessions SET status = 'closed', closed_at = $now, closed_by = $user, expected_cash = $expected,
                                         counted_cash = $counted, note = $note
                WHERE id = $id
                """, tx, ("$id", sessionId), ("$now", now), ("$user", Context.UserName), ("$expected", summary.ExpectedCash),
                ("$counted", countedCash), ("$note", Clean(note)));
            var number = await c.ScalarAsync<string>("SELECT number FROM cash_sessions WHERE id = $id", tx, ("$id", sessionId));
            await AuditAsync(c, tx, now, VortexApi.Suite.Auditing.AuditActions.CashClosed, "cash_session", sessionId, number,
                $"Esperado {summary.ExpectedCash:0.##}, contado {countedCash:0.##}, diferencia {countedCash - summary.ExpectedCash:0.##}{(Clean(note) is { } n ? $" ({n})" : "")}");
            return countedCash - summary.ExpectedCash;
        }, ct);
    }

    public async Task<PagedResult<CashSessionRow>> GetSessionsAsync(int page, int pageSize, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var total = await c.ScalarAsync<int>("SELECT COUNT(*) FROM cash_sessions");
        var rows = await c.QueryAsync($"{SelectSessions} ORDER BY cs.opened_at DESC LIMIT $limit OFFSET $offset", ReadSession, null,
            ("$limit", pageSize), ("$offset", (page - 1) * pageSize));
        return new PagedResult<CashSessionRow>(rows, total, page, pageSize);
    }

    internal static Task<CashSessionRow?> OpenSessionAsync(SqliteConnection c, SqliteTransaction? tx) =>
        c.FirstOrDefaultAsync($"{SelectSessions} WHERE cs.status = 'open' ORDER BY cs.opened_at DESC LIMIT 1", ReadSession, tx);

    private const string SelectSessions = """
        SELECT cs.*, t.name AS site_name,
               (SELECT COUNT(*) FROM sales s WHERE s.cash_session_id = cs.id AND s.status = 'completed') AS sales,
               (SELECT COALESCE(SUM(total), 0) FROM sales s WHERE s.cash_session_id = cs.id AND s.status = 'completed') AS sales_total
        FROM cash_sessions cs LEFT JOIN sites t ON t.id = cs.site_id
        """;

    private static CashSessionRow ReadSession(SqliteDataReader r) => new(
        r.Guid("id"), r.Str("number"), r.Guid("site_id"), r.StrOrNull("site_name"), r.Str("status"), r.Time("opened_at"),
        r.StrOrNull("opened_by"), r.Dec("opening_float"), r.TimeOrNull("closed_at"), r.StrOrNull("closed_by"),
        r.DecOrNull("expected_cash"), r.DecOrNull("counted_cash"), r.StrOrNull("note"), r.Int("sales"), r.Dec("sales_total"));

    private static async Task<CashSummary> SummaryAsync(SqliteConnection c, SqliteTransaction? tx, Guid sessionId)
    {
        var opening = await c.ScalarAsync<decimal>("SELECT opening_float FROM cash_sessions WHERE id = $id", tx, ("$id", sessionId));
        var byMethod = (await c.QueryAsync("""
            SELECT p.method, SUM(p.amount) AS amount FROM sale_payments p JOIN sales s ON s.id = p.sale_id
            WHERE s.cash_session_id = $id AND s.status = 'completed' GROUP BY p.method
            """, r => (Method: r.Str("method"), Amount: r.Dec("amount")), tx, ("$id", sessionId)))
            .ToDictionary(x => x.Method, x => x.Amount);
        var refunds = await c.ScalarAsync<decimal>(
            "SELECT COALESCE(SUM(total), 0) FROM sale_returns WHERE cash_session_id = $id", tx, ("$id", sessionId));
        var sales = await c.QueryAsync("SELECT COUNT(*) AS n, COALESCE(SUM(total), 0) AS t FROM sales WHERE cash_session_id = $id AND status = 'completed'",
            r => (Count: r.Int("n"), Total: r.Dec("t")), tx, ("$id", sessionId));
        var cash = byMethod.GetValueOrDefault(PaymentMethods.Cash);
        return new CashSummary(opening, cash, refunds, opening + cash - refunds, byMethod, sales[0].Count, sales[0].Total);
    }
}
