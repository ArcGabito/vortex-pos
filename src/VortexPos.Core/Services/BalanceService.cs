using System.Text.Json;
using Microsoft.Data.Sqlite;
using VortexApi.Client;
using VortexApi.Suite.Money;
using VortexPos.Core.Data;
using VortexPos.Core.Domain;
using VortexPos.Core.Integration;
using VortexPos.Core.Sync;

using static VortexPos.Core.Texts.CoreText;

namespace VortexPos.Core.Services;

public static class BalanceKinds
{
    public const string Points = "points";
    public const string GiftCard = "gift_card";
}

/// <summary>
/// Programa de puntos y gift cards de la organización. <see cref="PointsPerUnit"/>: puntos por cada unidad de moneda que se
/// paga; <see cref="PointValue"/>: cuánto vale un punto al canjearlo. <see cref="OfflineGiftCardLimit"/>: cuánto puede gastar
/// de una gift card una caja sin red (0: nada), para que dos cajas desconectadas no la gasten dos veces más allá de eso.
/// </summary>
public sealed record LoyaltySettings(bool Enabled = false, decimal PointsPerUnit = 1, decimal PointValue = 0.05m, int MinRedeem = 100,
    decimal OfflineGiftCardLimit = 0, int GiftCardMonths = 12)
{
    public static readonly LoyaltySettings Default = new();
}

public sealed record BalanceMovementRow(Guid Id, string Kind, Guid AccountId, string? Code, Guid? CustomerId, decimal Delta, Guid? SaleId, Guid SiteId,
    string Reason, DateOnly? ExpiresOn, DateTimeOffset CreatedAt);

public sealed record GiftCardRow(Guid Id, string Code, decimal Balance, decimal Issued, DateOnly? ExpiresOn, Guid? CustomerId);

/// <summary>Cuenta corriente del cliente: lo que debe de ventas al crédito (de todas las cajas) menos lo que cobró el ERP.</summary>
public sealed record CustomerAccount(Guid CustomerId, decimal Owed, decimal? Limit)
{
    public decimal? Available => Limit is { } l ? Math.Max(0, l - Owed) : null;
}

public static class LayawayStatuses
{
    public const string Open = "open";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";
}

public sealed record LayawayDeposit(Guid Id, decimal Amount, string Method, DateTimeOffset At, string? UserName);

/// <summary>Apartado (POS-55): lo que el cliente separa con un adelanto; se completa cuando paga el resto.</summary>
public sealed record Layaway(Guid Id, string Number, Guid SiteId, Guid CustomerId, IReadOnlyList<CartLine> Lines, decimal Total, string CurrencyCode,
    IReadOnlyList<LayawayDeposit> Deposits, string Status, DateTimeOffset CreatedAt, DateOnly? DueOn, Guid? SaleId, string? CustomerName = null)
{
    public decimal Paid => Deposits.Sum(d => d.Amount);
    public decimal Remaining => Math.Max(0, Total - Paid);
}

/// <summary>
/// Saldos del cliente en caja (fase 4b): puntos (POS-53), gift cards y vales (POS-25), apartados (POS-55) y cuenta corriente
/// con límite (POS-54). Puntos y gift cards son un libro de movimientos que se replica entre las cajas con
/// <c>balance.moved</c> (decisión 2): el saldo es la suma de los movimientos, igual en todas las cajas al sincronizar.
/// </summary>
public sealed class BalanceService(PosDatabase db, IPosContext context, TimeProvider clock) : PosService(db, context, clock)
{
    public const string LoyaltyRecordId = "org";
    public const string GiftCardPrefix = "GC";
    public const string LayawayPrefix = "APA";

    // ---------- Programa ----------

    public async Task<LoyaltySettings> GetSettingsAsync(CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await SettingsAsync(c, null);
    }

    internal static async Task<LoyaltySettings> SettingsAsync(SqliteConnection c, SqliteTransaction? tx) =>
        await c.ScalarAsync<string?>("SELECT data FROM pos_records WHERE kind = $k AND id = $id", tx, ("$k", PosRecordKinds.Loyalty), ("$id", LoyaltyRecordId))
            is { } json ? JsonSerializer.Deserialize<LoyaltySettings>(json, VortexJson.Options) ?? LoyaltySettings.Default : LoyaltySettings.Default;

    public Task SaveSettingsAsync(LoyaltySettings settings, CancellationToken ct = default)
    {
        RequirePermission(PosPermissions.StoreSettings);
        Require(settings.PointsPerUnit >= 0 && settings.PointValue > 0 && settings.MinRedeem >= 0 && settings.OfflineGiftCardLimit >= 0
                && settings.GiftCardMonths is >= 0 and <= 120, T("error.programa_invalido"));
        return InTransactionAsync(async (c, tx, now) =>
        {
            await PublishRecordAsync(c, tx, now, PosRecordKinds.Loyalty, LoyaltyRecordId, settings);
            await AuditAsync(c, tx, now, "loyalty.settings", "settings", null, null,
                $"{(settings.Enabled ? "activo" : "inactivo")}: {settings.PointsPerUnit} pts por unidad, punto a {settings.PointValue}");
            return 0;
        }, ct);
    }

    // ---------- Puntos ----------

    public async Task<decimal> GetPointsAsync(Guid customerId, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await BalanceAsync(c, null, BalanceKinds.Points, customerId);
    }

    /// <summary>Puntos que gana una venta (al pagar con puntos o con una gift card, eso no suma).</summary>
    internal static decimal Earned(LoyaltySettings s, decimal paid) => s.Enabled && paid > 0 ? Math.Floor(paid * s.PointsPerUnit) : 0;

    /// <summary>Canje: cuántos puntos cuesta pagar <paramref name="amount"/> y si el cliente los tiene.</summary>
    internal async Task RedeemPointsAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, Guid customerId, decimal amount, Guid saleId,
        Guid siteId)
    {
        var settings = await SettingsAsync(c, tx);
        Require(settings.Enabled, T("error.programa_inactivo"));
        var points = Math.Ceiling(amount / settings.PointValue);
        var balance = await BalanceAsync(c, tx, BalanceKinds.Points, customerId);
        Require(points >= settings.MinRedeem, T("error.puntos_minimo", settings.MinRedeem));
        Require(points <= balance, T("error.puntos_insuficientes", balance, points));
        await MoveAsync(c, tx, now, BalanceKinds.Points, customerId, null, customerId, -points, saleId, siteId, T("saldo.canje"), null);
    }

    internal async Task EarnPointsAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, Guid customerId, decimal paid, Guid saleId, Guid siteId)
    {
        var points = Earned(await SettingsAsync(c, tx), paid);
        if (points > 0)
            await MoveAsync(c, tx, now, BalanceKinds.Points, customerId, null, customerId, points, saleId, siteId, T("saldo.compra"), null);
    }

    // ---------- Gift cards ----------

    public static Guid GiftCardId(string code) => DeterministicId.From("gift_card|" + Normalize(code));

    private static string Normalize(string code) => code.Trim().ToUpperInvariant().Replace(" ", "").Replace("-", "");

    /// <summary>
    /// Vende una gift card o un vale por <paramref name="amount"/> (sin código, se genera). El dinero entra a la caja como
    /// ingreso (en efectivo, al cajón). Devuelve el código.
    /// </summary>
    public Task<string> IssueGiftCardAsync(decimal amount, string method, string? code = null, Guid? customerId = null, CancellationToken ct = default)
    {
        RequirePermission(PosPermissions.Sell);
        Require(amount > 0, T("error.el_monto_debe_ser_mayor"));
        Require(method is PaymentMethods.Cash or PaymentMethods.Card or PaymentMethods.Wallet or PaymentMethods.Transfer, T("error.medio_de_pago_desconocido"));
        return InTransactionAsync(async (c, tx, now) =>
        {
            var session = await CashService.OpenSessionAsync(c, tx);
            Require(session is not null, T("error.abre_la_caja_antes_de"));
            var number = code is { Length: > 0 } given ? Normalize(given)
                : $"{GiftCardPrefix}{Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6))}";
            Require(number.Length is >= 6 and <= 32, T("error.gift_card_codigo"));
            var id = GiftCardId(number);
            Require(await c.ScalarAsync<long>("SELECT COUNT(*) FROM balance_movements WHERE account_id = $id", tx, ("$id", id)) == 0,
                T("error.gift_card_existe", number));
            var months = (await SettingsAsync(c, tx)).GiftCardMonths;
            DateOnly? expires = months > 0 ? DateOnly.FromDateTime(now.ToOffset(PosClock.Offset).DateTime).AddMonths(months) : null;
            await MoveAsync(c, tx, now, BalanceKinds.GiftCard, id, number, customerId, amount, null, session!.SiteId, T("saldo.emision", method), expires);
            if (method == PaymentMethods.Cash)
                await CashService.InsertMovementAsync(c, tx, now, session.Id, CashMovementKinds.In, amount, T("saldo.venta_gift_card", number), Context.UserName);
            await AuditAsync(c, tx, now, "gift_card.issued", "gift_card", id, number, $"{amount:0.##} ({method})");
            return number;
        }, ct);
    }

    public async Task<GiftCardRow?> GetGiftCardAsync(string code, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await GiftCardAsync(c, null, code);
    }

    private static async Task<GiftCardRow?> GiftCardAsync(SqliteConnection c, SqliteTransaction? tx, string code)
    {
        var id = GiftCardId(code);
        var rows = await c.QueryAsync("SELECT delta, expires_on, customer_id FROM balance_movements WHERE account_id = $id AND kind = 'gift_card'",
            r => (Delta: r.Dec("delta"), Expires: r.StrOrNull("expires_on"), Customer: r.GuidOrNull("customer_id")), tx, ("$id", id));
        if (rows.Count == 0)
            return null;
        var expires = rows.Select(r => r.Expires).FirstOrDefault(e => e is not null) is { } e ? DateOnly.Parse(e, System.Globalization.CultureInfo.InvariantCulture) : (DateOnly?)null;
        return new GiftCardRow(id, Normalize(code), rows.Sum(r => r.Delta), rows.Where(r => r.Delta > 0).Sum(r => r.Delta), expires,
            rows.Select(r => r.Customer).FirstOrDefault(x => x is not null));
    }

    /// <summary>
    /// Gasta una gift card en una venta. Con red, hasta su saldo; sin red, hasta el límite de la caja sin conexión (para que
    /// dos cajas desconectadas no la gasten dos veces más allá de ese monto).
    /// </summary>
    internal async Task SpendGiftCardAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, string? code, decimal amount, Guid saleId, Guid siteId)
    {
        Require(!string.IsNullOrWhiteSpace(code), T("error.gift_card_sin_codigo"));
        var card = await GiftCardAsync(c, tx, code!);
        Require(card is not null, T("error.gift_card_no_existe", Normalize(code!)));
        Require(card!.ExpiresOn is null || card.ExpiresOn >= DateOnly.FromDateTime(now.ToOffset(PosClock.Offset).DateTime), T("error.gift_card_vencida", card.Code));
        var allowed = card.Balance;
        if (!Context.IsOnline)
            allowed = Math.Min(allowed, (await SettingsAsync(c, tx)).OfflineGiftCardLimit);
        Require(amount <= allowed, Context.IsOnline ? T("error.gift_card_saldo", card.Code, card.Balance)
            : T("error.gift_card_sin_red", card.Code, allowed));
        await MoveAsync(c, tx, now, BalanceKinds.GiftCard, card.Id, card.Code, card.CustomerId, -amount, saleId, siteId, T("saldo.consumo"), null);
    }

    // ---------- Cuenta corriente ----------

    public async Task<CustomerAccount> GetAccountAsync(Guid customerId, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await AccountAsync(c, null, customerId);
    }

    internal static async Task<CustomerAccount> AccountAsync(SqliteConnection c, SqliteTransaction? tx, Guid customerId)
    {
        var credit = await c.ScalarAsync<decimal>("""
            SELECT COALESCE(SUM(p.amount), 0) FROM sale_payments p JOIN sales s ON s.id = p.sale_id
            WHERE s.customer_id = $id AND s.status = 'completed' AND p.method = 'credit'
            """, tx, ("$id", customerId));
        var paid = await c.ScalarAsync<decimal>("SELECT COALESCE(SUM(amount), 0) FROM customer_payments WHERE customer_id = $id", tx, ("$id", customerId));
        var limit = await c.ScalarAsync<decimal?>("SELECT credit_limit FROM parties WHERE id = $id", tx, ("$id", customerId));
        return new CustomerAccount(customerId, Math.Max(0, credit - paid), limit);
    }

    /// <summary>Una venta al crédito no pasa el límite del cliente (el del ERP, en el hub) sin un supervisor.</summary>
    internal async Task RequireCreditAsync(SqliteConnection c, SqliteTransaction tx, Guid customerId, decimal amount)
    {
        var account = await AccountAsync(c, tx, customerId);
        if (account.Limit is { } limit && account.Owed + amount > limit && !Context.Can(PosPermissions.CreditOverride))
            throw new PosPermissionException(T("error.credito_excede_limite", account.Owed, limit), PosPermissions.CreditOverride);
    }

    /// <summary>Cobro del ERP a un cliente: baja lo que debe en la cuenta corriente.</summary>
    internal static Task ApplyCustomerPaymentAsync(SqliteConnection c, SqliteTransaction tx, Guid paymentId, Guid customerId, decimal amount, DateTimeOffset at) =>
        c.ExecAsync("INSERT OR IGNORE INTO customer_payments (id, customer_id, amount, received_at) VALUES ($id, $c, $a, $at)", tx,
            ("$id", paymentId), ("$c", customerId), ("$a", amount), ("$at", at));

    // ---------- Apartados ----------

    /// <summary>Separa los artículos para un cliente con un adelanto (en efectivo, al cajón). Los precios quedan los de hoy.</summary>
    public Task<string> CreateLayawayAsync(Guid customerId, IReadOnlyList<CartLine> lines, decimal deposit, string method, DateOnly? dueOn = null,
        CancellationToken ct = default)
    {
        RequirePermission(PosPermissions.Sell);
        Require(lines.Count > 0 && lines.All(l => l.Quantity > 0), T("error.agrega_al_menos_un_articulo"));
        Require(deposit > 0, T("error.apartado_adelanto"));
        return InTransactionAsync(async (c, tx, now) =>
        {
            var session = await CashService.OpenSessionAsync(c, tx);
            Require(session is not null, T("error.abre_la_caja_antes_de"));
            var quote = await new SalesService(Db, Context, Clock).QuoteAsync(c, tx, now, new SaleDraft(lines, [], customerId), session!.SiteId);
            Require(deposit < quote.Totals.Total, T("error.apartado_adelanto_total"));
            var id = Guid.NewGuid();
            var number = await DocumentNumbers.NextAsync(c, tx, LayawayPrefix, Context.DeviceTag);
            var depositRow = new LayawayDeposit(Guid.NewGuid(), deposit, method, now, Context.UserName);
            var currency = await c.ScalarAsync<string?>("SELECT currency_code FROM company WHERE id = 1", tx) ?? "PEN";
            var layaway = new Layaway(id, number, session.SiteId, customerId, lines, quote.Totals.Total, currency, [depositRow],
                LayawayStatuses.Open, now, dueOn, null);
            await RecordDepositAsync(c, tx, now, session, layaway, depositRow);
            await PublishRecordAsync(c, tx, now, PosRecordKinds.Layaway, id.ToString(), layaway);
            await AuditAsync(c, tx, now, "layaway.created", "layaway", id, number, $"{quote.Totals.Total:0.##}, adelanto {deposit:0.##}");
            return number;
        }, ct);
    }

    /// <summary>Abona al apartado; si con eso se completa, se convierte en la venta (el pago <c>layaway</c> es lo abonado).</summary>
    public Task<SaleReceipt?> PayLayawayAsync(Guid layawayId, decimal amount, string method, CancellationToken ct = default)
    {
        RequirePermission(PosPermissions.Sell);
        Require(amount > 0, T("error.el_monto_debe_ser_mayor"));
        return InTransactionAsync<SaleReceipt?>(async (c, tx, now) =>
        {
            var session = await CashService.OpenSessionAsync(c, tx);
            Require(session is not null, T("error.abre_la_caja_antes_de"));
            var layaway = await LayawayAsync(c, tx, layawayId);
            Require(layaway.Status == LayawayStatuses.Open, T("error.apartado_cerrado"));
            Require(amount <= layaway.Remaining, T("error.apartado_saldo", layaway.Remaining));
            var deposit = new LayawayDeposit(Guid.NewGuid(), amount, method, now, Context.UserName);
            await RecordDepositAsync(c, tx, now, session!, layaway, deposit);
            layaway = layaway with { Deposits = [.. layaway.Deposits, deposit] };
            SaleReceipt? receipt = null;
            if (layaway.Remaining == 0)
            {
                receipt = await new SalesService(Db, Context, Clock).CheckoutLayawayAsync(c, tx, now, session!, layaway);
                layaway = layaway with { Status = LayawayStatuses.Completed, SaleId = receipt.SaleId };
            }
            await PublishRecordAsync(c, tx, now, PosRecordKinds.Layaway, layaway.Id.ToString(), layaway);
            await AuditAsync(c, tx, now, receipt is null ? "layaway.payment" : "layaway.completed", "layaway", layaway.Id, layaway.Number,
                $"{amount:0.##} ({method}){(receipt is null ? "" : $" → {receipt.Number}")}");
            return receipt;
        }, ct);
    }

    /// <summary>Anula el apartado y devuelve lo abonado en efectivo (supervisor).</summary>
    public Task CancelLayawayAsync(Guid layawayId, CancellationToken ct = default)
    {
        RequirePermission(PosPermissions.Void);
        return InTransactionAsync(async (c, tx, now) =>
        {
            var session = await CashService.OpenSessionAsync(c, tx);
            Require(session is not null, T("error.abre_la_caja_antes_de"));
            var layaway = await LayawayAsync(c, tx, layawayId);
            Require(layaway.Status == LayawayStatuses.Open, T("error.apartado_cerrado"));
            var expected = (await CashService.SummaryForAsync(c, tx, session!.Id)).ExpectedCash;
            Require(layaway.Paid <= expected, T("error.no_hay_tanto_efectivo", expected));
            await CashService.InsertMovementAsync(c, tx, now, session.Id, CashMovementKinds.Out, layaway.Paid,
                T("saldo.devolucion_apartado", layaway.Number), Context.UserName);
            await PublishRecordAsync(c, tx, now, PosRecordKinds.Layaway, layaway.Id.ToString(), layaway with { Status = LayawayStatuses.Cancelled });
            await AuditAsync(c, tx, now, "layaway.cancelled", "layaway", layaway.Id, layaway.Number, $"devuelto {layaway.Paid:0.##}");
            return 0;
        }, ct);
    }

    public async Task<IReadOnlyList<Layaway>> GetLayawaysAsync(bool openOnly = true, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var list = await c.QueryAsync("SELECT r.data, p.name FROM pos_records r LEFT JOIN parties p ON p.id = json_extract(r.data, '$.customerId') WHERE r.kind = $k",
            r => JsonSerializer.Deserialize<Layaway>(r.Str("data"), VortexJson.Options)! with { CustomerName = r.StrOrNull("name") }, null,
            ("$k", PosRecordKinds.Layaway));
        return list.Where(l => !openOnly || l.Status == LayawayStatuses.Open).OrderByDescending(l => l.CreatedAt).ToList();
    }

    private static async Task<Layaway> LayawayAsync(SqliteConnection c, SqliteTransaction tx, Guid id) =>
        await c.ScalarAsync<string?>("SELECT data FROM pos_records WHERE kind = $k AND id = $id", tx, ("$k", PosRecordKinds.Layaway), ("$id", id.ToString()))
            is { } json ? JsonSerializer.Deserialize<Layaway>(json, VortexJson.Options)! : throw new PosValidationException(T("error.apartado_no_existe"));

    /// <summary>Un abono en efectivo entra al cajón; los demás medios, no.</summary>
    private async Task RecordDepositAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, CashSessionRow session, Layaway layaway,
        LayawayDeposit deposit)
    {
        Require(deposit.Method is PaymentMethods.Cash or PaymentMethods.Card or PaymentMethods.Wallet or PaymentMethods.Transfer,
            T("error.medio_de_pago_desconocido"));
        if (deposit.Method == PaymentMethods.Cash)
            await CashService.InsertMovementAsync(c, tx, now, session.Id, CashMovementKinds.In, deposit.Amount,
                T("saldo.abono_apartado", layaway.Number), Context.UserName);
    }

    // ---------- Libro ----------

    private static Task<decimal> BalanceAsync(SqliteConnection c, SqliteTransaction? tx, string kind, Guid account) =>
        c.ScalarAsync<decimal>("SELECT COALESCE(SUM(delta), 0) FROM balance_movements WHERE kind = $k AND account_id = $a", tx, ("$k", kind), ("$a", account));

    private async Task MoveAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, string kind, Guid account, string? code, Guid? customer,
        decimal delta, Guid? saleId, Guid siteId, string reason, DateOnly? expires)
    {
        var e = new BalanceMoved(Guid.NewGuid(), kind, account, code, customer, delta, saleId, siteId, reason, expires);
        await ApplyMovementAsync(c, tx, now, e);
        await Outbox.EnqueueEventAsync(c, tx, now, SuiteEvents.BalanceMoved, e, subjectType: kind, subjectId: code ?? account.ToString(), id: e.MovementId);
    }

    /// <summary>Guarda el movimiento una sola vez (el de esta caja y los de las demás llegan con el mismo Id).</summary>
    internal static Task ApplyMovementAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset at, BalanceMoved e) =>
        c.ExecAsync("""
            INSERT OR IGNORE INTO balance_movements (id, kind, account_id, code, customer_id, delta, sale_id, site_id, reason, expires_on, created_at)
            VALUES ($id, $kind, $account, $code, $customer, $delta, $sale, $site, $reason, $expires, $at)
            """, tx, ("$id", e.MovementId), ("$kind", e.Kind), ("$account", e.AccountId), ("$code", e.Code), ("$customer", e.CustomerId),
            ("$delta", e.Delta), ("$sale", e.SaleId), ("$site", e.SiteId), ("$reason", e.Reason), ("$expires", e.ExpiresOn?.ToString("yyyy-MM-dd")),
            ("$at", at));

    /// <summary>Al anular una venta, se devuelven los puntos y la gift card que usó y se quitan los que ganó.</summary>
    internal async Task ReverseSaleAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, Guid saleId)
    {
        var moves = await c.QueryAsync("SELECT * FROM balance_movements WHERE sale_id = $s", r => new BalanceMovementRow(r.Guid("id"), r.Str("kind"),
            r.Guid("account_id"), r.StrOrNull("code"), r.GuidOrNull("customer_id"), r.Dec("delta"), r.GuidOrNull("sale_id"), r.Guid("site_id"),
            r.Str("reason"), null, r.Time("created_at")), tx, ("$s", saleId));
        foreach (var m in moves.GroupBy(m => (m.Kind, m.AccountId, m.Code, m.CustomerId, m.SiteId)))
        {
            var total = m.Sum(x => x.Delta);
            if (total != 0)
                await MoveAsync(c, tx, now, m.Key.Kind, m.Key.AccountId, m.Key.Code, m.Key.CustomerId, -total, saleId, m.Key.SiteId, T("saldo.anulacion"), null);
        }
    }

    private async Task PublishRecordAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, string kind, string id, object data)
    {
        var record = new PosRecordUpserted(kind, id, JsonSerializer.SerializeToElement(data, VortexJson.Options), now, Context.UserName);
        await StoreSettingsService.UpsertAsync(c, tx, record, force: true);
        await Outbox.EnqueueEventAsync(c, tx, now, SuiteEvents.PosRecordUpserted, record, subjectType: kind, subjectId: id);
    }
}
