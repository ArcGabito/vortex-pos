using System.Text.Json;
using Microsoft.Data.Sqlite;
using VortexApi.Suite.Auditing;
using VortexPos.Core.Data;
using VortexPos.Core.Domain;

using static VortexPos.Core.Texts.CoreText;

namespace VortexPos.Core.Services;

/// <summary>
/// Turnos de caja de este equipo: apertura con fondo inicial, ventas y devoluciones del turno, ingresos y retiros de
/// efectivo, y cierre con arqueo (efectivo esperado contra contado, por total o por denominación). Es un dato local de
/// cada caja: no se publica a la suite.
/// </summary>
public sealed class CashService(PosDatabase db, IPosContext context, TimeProvider clock) : PosService(db, context, clock)
{
    /// <summary>Acción de auditoría de los ingresos y retiros de efectivo.</summary>
    public const string MovementAudit = "cash.movement";

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
            Require(CanUseSite(site!.Value), T("error.tienda_no_asignada"));

            var id = Guid.NewGuid();
            var number = await DocumentNumbers.NextAsync(c, tx, DocumentNumbers.CashSession, Context.DeviceTag);
            await c.ExecAsync("""
                INSERT INTO cash_sessions (id, number, site_id, status, opened_at, opened_by, opening_float)
                VALUES ($id, $number, $site, 'open', $now, $user, $float)
                """, tx, ("$id", id), ("$number", number), ("$site", site), ("$now", now), ("$user", Context.UserName),
                ("$float", openingFloat));
            await AuditAsync(c, tx, now, AuditActions.CashOpened, "cash_session", id, number, $"Fondo inicial {openingFloat:0.##}");
            return id;
        }, ct);
    }

    public async Task<CashSummary> GetSummaryAsync(Guid sessionId, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await SummaryAsync(c, null, sessionId);
    }

    // ---------- Ingresos y retiros ----------

    /// <summary>Registra efectivo que entra o sale del cajón durante el turno. El motivo es obligatorio.</summary>
    public Task<Guid> AddMovementAsync(string kind, decimal amount, string reason, CancellationToken ct = default)
    {
        RequirePermission(PosPermissions.CashMovement);
        Require(kind is CashMovementKinds.In or CashMovementKinds.Out, T("error.movimiento_desconocido"));
        Require(amount > 0, T("error.el_monto_debe_ser_mayor"));
        Require(!string.IsNullOrWhiteSpace(reason), T("error.escribe_el_motivo_del_movimiento"));
        return InTransactionAsync(async (c, tx, now) =>
        {
            var session = await OpenSessionAsync(c, tx);
            Require(session is not null, T("error.abre_la_caja_antes_de"));
            if (kind == CashMovementKinds.Out)
            {
                var available = (await SummaryAsync(c, tx, session!.Id)).ExpectedCash;
                Require(amount <= available, T("error.no_hay_tanto_efectivo", available));
            }

            var id = Guid.NewGuid();
            await c.ExecAsync("""
                INSERT INTO cash_movements (id, cash_session_id, kind, amount, reason, created_at, user_name)
                VALUES ($id, $session, $kind, $amount, $reason, $now, $user)
                """, tx, ("$id", id), ("$session", session!.Id), ("$kind", kind), ("$amount", amount), ("$reason", reason.Trim()),
                ("$now", now), ("$user", Context.UserName));
            await AuditAsync(c, tx, now, MovementAudit, "cash_session", session.Id, session.Number,
                $"{(kind == CashMovementKinds.In ? "Ingreso" : "Retiro")} de {amount:0.##}: {reason.Trim()}");
            return id;
        }, ct);
    }

    public async Task<IReadOnlyList<CashMovementRow>> GetMovementsAsync(Guid sessionId, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await MovementsAsync(c, null, sessionId);
    }

    // ---------- Cierre ----------

    /// <summary>Cierra el turno con el efectivo contado. Devuelve la diferencia (contado − esperado).</summary>
    public Task<decimal> CloseAsync(Guid sessionId, decimal countedCash, string? note, CancellationToken ct = default) =>
        CloseCoreAsync(sessionId, countedCash, null, note, ct);

    /// <summary>Cierra el turno contando billetes y monedas. El contado es la suma de las denominaciones.</summary>
    public Task<decimal> CloseAsync(Guid sessionId, IReadOnlyList<DenominationCount> counts, string? note, CancellationToken ct = default)
    {
        Require(counts.All(d => d.Value > 0 && d.Quantity >= 0), T("error.cantidades_de_billetes_invalidas"));
        var counted = counts.Where(d => d.Quantity > 0).OrderByDescending(d => d.Value).ToList();
        return CloseCoreAsync(sessionId, Denominations.Total(counted), counted, note, ct);
    }

    private Task<decimal> CloseCoreAsync(Guid sessionId, decimal countedCash, IReadOnlyList<DenominationCount>? counts, string? note,
        CancellationToken ct)
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
                                         counted_cash = $counted, note = $note, denominations = $denominations
                WHERE id = $id
                """, tx, ("$id", sessionId), ("$now", now), ("$user", Context.UserName), ("$expected", summary.ExpectedCash),
                ("$counted", countedCash), ("$note", Clean(note)),
                ("$denominations", counts is null ? null : JsonSerializer.Serialize(counts)));
            var number = await c.ScalarAsync<string>("SELECT number FROM cash_sessions WHERE id = $id", tx, ("$id", sessionId));
            await AuditAsync(c, tx, now, AuditActions.CashClosed, "cash_session", sessionId, number,
                $"Esperado {summary.ExpectedCash:0.##}, contado {countedCash:0.##}, diferencia {countedCash - summary.ExpectedCash:0.##}{(Clean(note) is { } n ? $" ({n})" : "")}");
            return countedCash - summary.ExpectedCash;
        }, ct);
    }

    // ---------- Reportes X y Z ----------

    /// <summary>
    /// Reporte del turno: X (parcial) si la caja sigue abierta, Z (cierre) si ya se cerró. Totales de venta, impuesto,
    /// descuentos, anulaciones, devoluciones, cobros por medio, ingresos y retiros, ventas por cajero y arqueo.
    /// </summary>
    public async Task<CashReport> GetReportAsync(Guid sessionId, CancellationToken ct = default)
    {
        RequirePermission(PosPermissions.CashReport);
        await using var c = await Db.OpenAsync(ct);
        var session = await c.FirstOrDefaultAsync($"{SelectSessions} WHERE cs.id = $id", ReadSession, null, ("$id", sessionId));
        Require(session is not null, T("error.el_turno_no_existe"));
        var summary = await SummaryAsync(c, null, sessionId);
        var totals = (await c.QueryAsync("""
            SELECT COALESCE(SUM(subtotal), 0) AS subtotal, COALESCE(SUM(tax), 0) AS tax, COALESCE(SUM(discount), 0) AS discount
            FROM sales WHERE cash_session_id = $id AND status = 'completed'
            """, r => (Subtotal: r.Dec("subtotal"), Tax: r.Dec("tax"), Discount: r.Dec("discount")), null, ("$id", sessionId)))[0];
        var voided = (await c.QueryAsync("""
            SELECT COUNT(*) AS n, COALESCE(SUM(total), 0) AS t FROM sales WHERE cash_session_id = $id AND status = 'voided'
            """, r => (Count: r.Int("n"), Total: r.Dec("t")), null, ("$id", sessionId)))[0];
        var returns = await c.ScalarAsync<int>("SELECT COUNT(*) FROM sale_returns WHERE cash_session_id = $id", null, ("$id", sessionId));
        var byCashier = await c.QueryAsync("""
            SELECT COALESCE(user_name, '') AS user_name, COUNT(*) AS n, SUM(total) AS t FROM sales
            WHERE cash_session_id = $id AND status = 'completed' GROUP BY user_name ORDER BY t DESC
            """, r => new CashierTotals(r.Str("user_name"), r.Int("n"), r.Dec("t")), null, ("$id", sessionId));
        var denominations = await c.ScalarAsync<string?>("SELECT denominations FROM cash_sessions WHERE id = $id", null, ("$id", sessionId))
            is { } json ? JsonSerializer.Deserialize<List<DenominationCount>>(json) ?? [] : [];
        var currency = await c.ScalarAsync<string?>("SELECT currency_code FROM company WHERE id = 1") ?? "PEN";

        return new CashReport(session!, summary, currency, totals.Subtotal, totals.Tax, totals.Discount, voided.Count, voided.Total,
            returns, await MovementsAsync(c, null, sessionId), byCashier, denominations, Clock.GetUtcNow());
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

    private static Task<List<CashMovementRow>> MovementsAsync(SqliteConnection c, SqliteTransaction? tx, Guid sessionId) =>
        c.QueryAsync("SELECT * FROM cash_movements WHERE cash_session_id = $id ORDER BY created_at", r => new CashMovementRow(
            r.Guid("id"), r.Str("kind"), r.Dec("amount"), r.Str("reason"), r.Time("created_at"), r.StrOrNull("user_name")),
            tx, ("$id", sessionId));

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
        var movements = (await c.QueryAsync(
            "SELECT kind, COALESCE(SUM(amount), 0) AS amount FROM cash_movements WHERE cash_session_id = $id GROUP BY kind",
            r => (Kind: r.Str("kind"), Amount: r.Dec("amount")), tx, ("$id", sessionId))).ToDictionary(x => x.Kind, x => x.Amount);
        var cashIn = movements.GetValueOrDefault(CashMovementKinds.In);
        var cashOut = movements.GetValueOrDefault(CashMovementKinds.Out);
        var cash = byMethod.GetValueOrDefault(PaymentMethods.Cash);
        return new CashSummary(opening, cash, refunds, opening + cash - refunds + cashIn - cashOut, byMethod, sales[0].Count,
            sales[0].Total, cashIn, cashOut);
    }
}
