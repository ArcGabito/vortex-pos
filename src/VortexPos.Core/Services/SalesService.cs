using Microsoft.Data.Sqlite;
using VortexApi.Suite.Auditing;
using VortexApi.Suite.Money;
using VortexApi.Suite.Taxes;
using VortexPos.Core.Data;
using VortexPos.Core.Domain;
using VortexPos.Core.Integration;
using VortexPos.Core.Sync;

using static VortexPos.Core.Texts.CoreText;

namespace VortexPos.Core.Services;

/// <summary>
/// Ventas de caja. Cobrar, anular y devolver se guardan primero en este equipo y publican <c>sale.completed</c>,
/// <c>sale.voided</c> y <c>sale.returned</c> en la misma transacción: el WMS descuenta stock, el ERP registra el
/// ingreso y las demás cajas de la organización ven la venta.
/// </summary>
public sealed class SalesService(PosDatabase db, IPosContext context, TimeProvider clock) : PosService(db, context, clock)
{
    private sealed record PricedLine(ItemRow Item, decimal Quantity, decimal UnitPrice, decimal Discount, decimal Total,
        string? Presentation = null, decimal? ListPrice = null, Guid? PromotionId = null, decimal PromotionDiscount = 0,
        decimal ManualDiscount = 0, decimal TicketShare = 0, string? PromotionName = null);

    // ---------- Cobro ----------

    /// <summary>
    /// Totales del carrito tal como se cobrarían, con el impuesto del país desglosado por tasa: incluido en el precio
    /// (Perú, LATAM, Europa) o sumado al cobrar (EE. UU.).
    /// </summary>
    public static SaleTotals Totals(IEnumerable<(decimal Total, decimal Discount, string? TaxCode)> lines, TaxRegime regime, string currencyCode)
    {
        var list = lines.ToList();
        var b = TaxCalculator.Calculate(list.Select(l => new TaxableLine(l.Total, l.TaxCode)), regime, currencyCode);
        return new SaleTotals(b.Net, b.Tax, list.Sum(l => l.Discount), b.Total);
    }

    public static decimal LineTotal(decimal quantity, decimal unitPrice, decimal discount, string currencyCode) =>
        Currencies.Get(currencyCode).Round(quantity * unitPrice - discount);

    /// <summary>Régimen de impuesto con que se cobra (para mostrar el nombre del impuesto y calcular en pantalla).</summary>
    public async Task<TaxRegime> GetTaxRegimeAsync(CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await TaxRegimeAsync(c, null);
    }

    public Task<SaleReceipt> CheckoutAsync(SaleDraft draft, CancellationToken ct = default)
    {
        RequirePermission(PosPermissions.Sell);
        Require(draft.Lines.Count > 0, T("error.agrega_al_menos_un_articulo"));
        Require(draft.Lines.All(l => l.Quantity > 0), T("error.las_cantidades_deben_ser_mayores"));
        Require(draft.Lines.All(l => l.Discount >= 0), T("error.el_descuento_no_puede_ser"));
        Require(draft.Payments.Count > 0, T("error.elige_como_paga_el_cliente"));
        Require(draft.Payments.All(p => PaymentMethods.All.Contains(p.Method)), T("error.medio_de_pago_desconocido"));
        Require(draft.Payments.All(p => p.Amount > 0), T("error.los_montos_de_pago_deben"));
        Require(draft.Payments.Count(p => p.Method == PaymentMethods.Cash) <= 1, T("error.registra_el_efectivo_en_un"));

        return InTransactionAsync(async (c, tx, now) =>
        {
            var session = await CashService.OpenSessionAsync(c, tx);
            Require(session is not null, T("error.abre_la_caja_antes_de"));
            var siteId = draft.SiteId ?? session!.SiteId;
            if (draft.CustomerId is { } customer)
                Require(await c.ScalarAsync<long>("SELECT COUNT(*) FROM parties WHERE id = $id AND is_deleted = 0", tx, ("$id", customer)) > 0,
                    T("error.el_cliente_no_existe"));

            var regime = await TaxRegimeAsync(c, tx);
            var (lines, applied, priceChanges) = await PriceAsync(c, tx, now, draft, siteId);
            var currency = lines[0].Item.CurrencyCode;
            var totals = Totals(lines.Select(l => (l.Total, l.Discount, l.Item.TaxCode)), regime, currency);
            var payments = Settle(draft.Payments, totals.Total, draft.CustomerId, out var cashReceived, out var change);

            var saleId = Guid.NewGuid();
            var number = await DocumentNumbers.NextAsync(c, tx, DocumentNumbers.Sale, Context.DeviceTag);
            var completed = new SaleCompleted(saleId, number, siteId, draft.CustomerId, currency, totals.Subtotal, totals.Tax, totals.Total,
                lines.Select(l => new SaleLine(l.Item.Id, l.Item.Sku, l.Quantity, l.UnitPrice, l.Discount, l.Total, l.Item.Name,
                    regime.IsTaxed(l.Item.TaxCode), TaxCategories.Normalize(l.Item.TaxCode), l.Presentation, l.ListPrice, l.PromotionId)).ToList(),
                payments.Select(p => new SalePayment(p.Method, p.Amount)).ToList(), totals.Discount, Context.UserName,
                string.IsNullOrWhiteSpace(draft.Coupon) ? null : draft.Coupon.Trim().ToUpperInvariant());

            await InsertSaleAsync(c, tx, completed, "local", session!.Id, now, cashReceived, change, payments);
            await Outbox.EnqueueEventAsync(c, tx, now, SuiteEvents.SaleCompleted, completed, subjectType: "sale", subjectId: number, id: saleId);
            if (totals.Discount > 0)
                await AuditAsync(c, tx, now, AuditActions.Discount, "sale", saleId, number, $"Descuentos por {totals.Discount:0.##} {currency}");
            if (priceChanges.Count > 0)
                await AuditAsync(c, tx, now, AuditActions.PriceOverride, "sale", saleId, number, string.Join("; ", priceChanges));
            if (applied.Count > 0)
                await AuditAsync(c, tx, now, "promotion.applied", "sale", saleId, number,
                    string.Join("; ", applied.Select(a => $"{a.Name} −{a.Amount:0.00}")) + (completed.Coupon is { } cup ? $" · cupón {cup}" : ""));
            return new SaleReceipt(saleId, number, totals.Total, change);
        }, ct);
    }

    /// <summary>
    /// La venta tal como se cobraría (promociones, cupón, descuentos y totales), sin guardarla. La caja la usa para
    /// mostrar el total: es el mismo cálculo del cobro.
    /// </summary>
    public async Task<SaleQuote> QuoteAsync(SaleDraft draft, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        await using var tx = c.BeginTransaction();
        var session = await CashService.OpenSessionAsync(c, tx);
        var siteId = draft.SiteId ?? session?.SiteId ?? await DefaultSiteAsync(c, tx) ?? Guid.Empty;
        var regime = await TaxRegimeAsync(c, tx);
        var (lines, applied, _) = await PriceAsync(c, tx, Clock.GetUtcNow(), draft, siteId, checkPermissions: false);
        var currency = lines.FirstOrDefault()?.Item.CurrencyCode ?? "PEN";
        return new SaleQuote(
            lines.Select(l => new QuoteLine(l.Item.Id, l.Presentation, l.UnitPrice, l.Quantity, l.Quantity * l.UnitPrice, l.PromotionDiscount,
                l.PromotionName, l.ManualDiscount, l.TicketShare, l.Total)).ToList(),
            applied, lines.Sum(l => l.TicketShare), Totals(lines.Select(l => (l.Total, l.Discount, l.Item.TaxCode)), regime, currency));
    }

    /// <summary>
    /// Precio de cada línea (agrupada por artículo, presentación y precio): precio de lista o cambiado con permiso, las
    /// promociones vigentes (<see cref="PromotionEngine"/>), el descuento manual de la línea (más del 10 % de lo que queda
    /// necesita permiso) y el descuento manual del ticket, repartido en proporción.
    /// </summary>
    private async Task<(List<PricedLine> Lines, IReadOnlyList<PromotionEngine.Applied> Applied, List<string> PriceChanges)> PriceAsync(
        SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, SaleDraft draft, Guid siteId, bool checkPermissions = true)
    {
        var raw = new List<(ItemRow Item, decimal Quantity, decimal UnitPrice, decimal Manual, string? Presentation, decimal? ListPrice)>();
        var priceChanges = new List<string>();
        foreach (var cart in draft.Lines.GroupBy(l => (l.ItemId, Presentation: Clean(l.Presentation), Price: l.UnitPrice)))
        {
            var item = await c.FirstOrDefaultAsync("SELECT i.*, NULL AS on_hand FROM items i WHERE i.id = $id AND i.is_deleted = 0",
                MasterDataService.ReadItem, tx, ("$id", cart.Key.ItemId));
            Require(item is not null, T("error.uno_de_los_articulos_ya"));
            Require(item!.IsActive, T("error.esta_inactivo_no_se_puede", item.Name));
            if (cart.Key.Presentation is { } presentation)
                Require(await c.ScalarAsync<long>("SELECT COUNT(*) FROM item_presentations WHERE item_id = $id AND code = $code", tx,
                    ("$id", item.Id), ("$code", presentation)) > 0, T("error.la_presentacion_ya_no_existe", item.Name));
            var unitPrice = cart.Key.Price ?? item.Price;
            Require(unitPrice >= 0, T("error.el_precio_no_puede_ser"));
            // Cobrar a otro precio que el de lista es un cambio de precio: necesita permiso y queda en la auditoría.
            decimal? listPrice = null;
            if (unitPrice != item.Price)
            {
                if (checkPermissions)
                    RequirePermission(PosPermissions.ChangePrice);
                listPrice = item.Price;
                priceChanges.Add($"{item.Name}: {item.Price:0.00} → {unitPrice:0.00}");
            }
            raw.Add((item, cart.Sum(l => l.Quantity), unitPrice, cart.Sum(l => l.Discount), cart.Key.Presentation, listPrice));
        }
        if (raw.Count == 0)
            return ([], [], priceChanges);

        var currency = raw[0].Item.CurrencyCode;
        var money = Currencies.Get(currency);
        var promotions = await PromotionService.ApplicableAsync(c, tx, now, siteId, draft.Coupon);
        var result = PromotionEngine.Evaluate(raw.Select(r => new PromotionEngine.Line(r.Item.Id, r.Item.Category, r.Quantity, r.UnitPrice)).ToList(),
            promotions, money.Decimals);
        var names = result.Applied.ToDictionary(a => a.PromotionId, a => a.Name);

        // Descuento manual por línea, sobre lo que queda después de la promoción.
        var afterManual = new decimal[raw.Count];
        for (var i = 0; i < raw.Count; i++)
        {
            var r = raw[i];
            var left = r.Quantity * r.UnitPrice - result.Discounts[i];
            Require(r.Manual <= left, T("error.el_descuento_de_supera_su", r.Item.Name));
            if (checkPermissions && r.Manual > left * PosPermissions.CashierDiscountLimit)
                RequirePermission(PosPermissions.DiscountUnlimited);
            afterManual[i] = left - r.Manual;
        }

        // Descuento manual del ticket, repartido en proporción a lo que queda de cada línea.
        Require(draft.TicketDiscount >= 0, T("error.el_descuento_no_puede_ser"));
        var remaining = afterManual.Sum();
        Require(draft.TicketDiscount <= remaining, T("error.el_descuento_del_ticket_supera", money.Format(remaining, System.Globalization.CultureInfo.InvariantCulture)));
        if (checkPermissions && draft.TicketDiscount > remaining * PosPermissions.CashierDiscountLimit)
            RequirePermission(PosPermissions.DiscountUnlimited);
        var ticket = PromotionEngine.Allocate(draft.TicketDiscount, afterManual, money.Decimals);

        var lines = new List<PricedLine>();
        for (var i = 0; i < raw.Count; i++)
        {
            var r = raw[i];
            // Lo que la promoción de ticket le quitó a la línea va con su promoción (si la línea no tenía una de línea).
            var promoLine = result.LinePromotions[i];
            var discount = result.Discounts[i] + r.Manual + ticket[i];
            lines.Add(new PricedLine(r.Item, r.Quantity, r.UnitPrice, discount, LineTotal(r.Quantity, r.UnitPrice, discount, currency),
                r.Presentation, r.ListPrice, promoLine ?? (result.Discounts[i] > 0 ? result.Applied.LastOrDefault()?.PromotionId : null),
                result.Discounts[i], r.Manual, ticket[i], promoLine is { } id ? names.GetValueOrDefault(id) : null));
        }
        return (lines, result.Applied, priceChanges);
    }

    /// <summary>
    /// Reparte el pago: lo que no es efectivo se aplica tal cual (no puede pasarse del total) y el efectivo cubre el
    /// resto; si el cliente entrega más, hay vuelto.
    /// </summary>
    private static List<PaymentDraft> Settle(IReadOnlyList<PaymentDraft> drafts, decimal total, Guid? customerId,
        out decimal? cashReceived, out decimal change)
    {
        Require(!drafts.Any(p => p.Method == PaymentMethods.Credit) || customerId is not null, T("error.para_vender_al_credito_elige"));
        var nonCash = drafts.Where(p => p.Method != PaymentMethods.Cash).ToList();
        var nonCashTotal = nonCash.Sum(p => p.Amount);
        Require(nonCashTotal <= total, T("error.los_pagos_con_tarjeta_transferencia"));

        var cash = drafts.FirstOrDefault(p => p.Method == PaymentMethods.Cash);
        var cashDue = total - nonCashTotal;
        var result = nonCash.Select(p => p with { Reference = Clean(p.Reference) }).ToList();
        if (cash is null)
        {
            Require(cashDue == 0, T("error.falta_cobrar", cashDue));
            cashReceived = null;
            change = 0;
            return result;
        }

        Require(cash.Amount >= cashDue, T("error.el_efectivo_no_alcanza_faltan", (cashDue - cash.Amount)));
        cashReceived = cash.Amount;
        change = cash.Amount - cashDue;
        if (cashDue > 0)
            result.Insert(0, new PaymentDraft(PaymentMethods.Cash, cashDue));
        return result;
    }

    // ---------- Tickets en espera ----------

    /// <summary>
    /// Guarda el carrito para retomarlo después y deja la caja libre para atender a otro cliente. Es de esta caja (no se
    /// replica); los precios se vuelven a tomar del catálogo al cobrar.
    /// </summary>
    public Task<Guid> HoldAsync(IReadOnlyList<CartLine> lines, Guid? customerId, string? label, CancellationToken ct = default)
    {
        RequirePermission(PosPermissions.Sell);
        Require(lines.Count > 0, T("error.agrega_al_menos_un_articulo"));
        Require(lines.All(l => l.Quantity > 0), T("error.las_cantidades_deben_ser_mayores"));
        return InTransactionAsync(async (c, tx, now) =>
        {
            var regime = await TaxRegimeAsync(c, tx);
            var priced = new List<(decimal Total, decimal Discount, string? TaxCode)>();
            string? currency = null;
            foreach (var line in lines)
            {
                var item = await c.FirstOrDefaultAsync("SELECT i.*, NULL AS on_hand FROM items i WHERE i.id = $id AND i.is_deleted = 0",
                    MasterDataService.ReadItem, tx, ("$id", line.ItemId));
                Require(item is not null, T("error.uno_de_los_articulos_ya"));
                currency ??= item!.CurrencyCode;
                priced.Add((LineTotal(line.Quantity, line.UnitPrice ?? item!.Price, line.Discount, item!.CurrencyCode), line.Discount, item.TaxCode));
            }

            var id = Guid.NewGuid();
            await c.ExecAsync("""
                INSERT INTO held_sales (id, label, customer_id, lines, total, created_at, user_name)
                VALUES ($id, $label, $customer, $lines, $total, $now, $user)
                """, tx, ("$id", id), ("$label", Clean(label)), ("$customer", customerId),
                ("$lines", System.Text.Json.JsonSerializer.Serialize(lines)), ("$total", Totals(priced, regime, currency!).Total),
                ("$now", now), ("$user", Context.UserName));
            return id;
        }, ct);
    }

    public async Task<IReadOnlyList<HeldSaleRow>> GetHeldAsync(CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await c.QueryAsync("""
            SELECT h.*, p.name AS customer_name FROM held_sales h LEFT JOIN parties p ON p.id = h.customer_id ORDER BY h.created_at
            """, r => new HeldSaleRow(r.Guid("id"), r.StrOrNull("label"), r.GuidOrNull("customer_id"), r.StrOrNull("customer_name"),
                System.Text.Json.JsonSerializer.Deserialize<List<CartLine>>(r.Str("lines"))?.Count ?? 0, r.Dec("total"),
                r.Time("created_at"), r.StrOrNull("user_name")));
    }

    /// <summary>Retoma un ticket en espera: lo devuelve y lo quita de la lista.</summary>
    public Task<HeldSale> ResumeHeldAsync(Guid id, CancellationToken ct = default) =>
        InTransactionAsync(async (c, tx, _) =>
        {
            var held = await c.FirstOrDefaultAsync("SELECT * FROM held_sales WHERE id = $id", r => new HeldSale(r.Guid("id"),
                r.StrOrNull("label"), r.GuidOrNull("customer_id"),
                System.Text.Json.JsonSerializer.Deserialize<List<CartLine>>(r.Str("lines")) ?? []), tx, ("$id", id));
            Require(held is not null, T("error.el_ticket_en_espera_ya"));
            await c.ExecAsync("DELETE FROM held_sales WHERE id = $id", tx, ("$id", id));
            return held!;
        }, ct);

    public async Task DiscardHeldAsync(Guid id, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        await c.ExecAsync("DELETE FROM held_sales WHERE id = $id", null, ("$id", id));
    }

    // ---------- Anulación y devolución ----------

    /// <summary>Anula una venta sin devoluciones. El evento tiene Id determinista: si dos cajas la anulan, cuenta una vez.</summary>
    public Task VoidAsync(Guid saleId, string reason, CancellationToken ct = default)
    {
        RequirePermission(PosPermissions.Void);
        Require(!string.IsNullOrWhiteSpace(reason), T("error.escribe_el_motivo_de_la"));
        return InTransactionAsync(async (c, tx, now) =>
        {
            var sale = await GetSaleAsync(c, tx, saleId);
            Require(sale is not null, T("error.la_venta_no_existe"));
            Require(sale!.Status == SaleStatuses.Completed, T("error.la_venta_ya_esta_anulada"));
            Require(sale.Returned == 0, T("error.la_venta_tiene_devoluciones_no"));
            await MarkVoidedAsync(c, tx, saleId, now, reason.Trim());
            await AuditAsync(c, tx, now, AuditActions.Voided, "sale", saleId, sale.Number, reason.Trim());
            await Outbox.EnqueueEventAsync(c, tx, now, SuiteEvents.SaleVoided, new SaleVoided(saleId, sale.Number, reason.Trim()),
                subjectType: "sale", subjectId: sale.Number, id: DeterministicId.From("sale_void", saleId));
            return 0;
        }, ct);
    }

    /// <summary>Devuelve parte o todo de una venta. El reembolso sale en efectivo de la caja abierta.</summary>
    public Task<string> ReturnAsync(Guid saleId, IReadOnlyList<ReturnLine> lines, string? reason, CancellationToken ct = default)
    {
        RequirePermission(PosPermissions.Return);
        var requested = lines.Where(l => l.Quantity > 0).ToList();
        Require(requested.Count > 0, T("error.indica_que_articulos_se_devuelven"));
        return InTransactionAsync(async (c, tx, now) =>
        {
            var session = await CashService.OpenSessionAsync(c, tx);
            Require(session is not null, T("error.abre_la_caja_para_devolver"));
            var sale = await GetSaleAsync(c, tx, saleId);
            Require(sale is not null, T("error.la_venta_no_existe"));
            Require(sale!.Status == SaleStatuses.Completed, T("error.la_venta_esta_anulada"));

            var saleLines = (await LinesAsync(c, tx, saleId)).ToDictionary(l => l.LineNo);
            var returned = new List<(SaleLineRow Line, decimal Quantity, decimal Total)>();
            foreach (var r in requested)
            {
                Require(saleLines.TryGetValue(r.LineNo, out var line), T("error.una_de_las_lineas_no"));
                Require(r.Quantity <= line!.Returnable, T("error.de_solo_se_pueden_devolver", line.Name, line.Returnable));
                returned.Add((line, r.Quantity, Currencies.Get(sale.CurrencyCode).Round(line.Total * r.Quantity / line.Quantity)));
            }

            var returnId = Guid.NewGuid();
            var number = await DocumentNumbers.NextAsync(c, tx, DocumentNumbers.Return, Context.DeviceTag);
            var total = returned.Sum(r => r.Total);
            var e = new SaleReturned(saleId, returnId, sale.SiteId,
                returned.Select(r => new SaleReturnLine(r.Line.ItemId, r.Quantity, r.Total)).ToList(), total, number, Clean(reason));
            await InsertReturnAsync(c, tx, e, session!.Id, now, Context.UserName,
                returned.Select(r => (r.Line.LineNo, r.Quantity)).ToList());
            await Outbox.EnqueueEventAsync(c, tx, now, SuiteEvents.SaleReturned, e, subjectType: "sale", subjectId: sale.Number, id: returnId);
            await AuditAsync(c, tx, now, AuditActions.Returned, "sale", saleId, sale.Number, $"{number} por {total:0.##} {sale.CurrencyCode}{(Clean(reason) is { } motivo ? $": {motivo}" : "")}");
            return number;
        }, ct);
    }

    // ---------- Consultas ----------

    public async Task<PagedResult<SaleRow>> GetSalesAsync(SaleQuery q, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var where = $"""
            WHERE ($status IS NULL OR s.status = $status){SiteFilter("s.site_id")}
              AND ($site IS NULL OR s.site_id = $site)
              AND ($from IS NULL OR s.created_at >= $from) AND ($to IS NULL OR s.created_at < $to)
              AND ($search IS NULL OR s.number LIKE $like OR p.name LIKE $like OR p.document_number LIKE $like
                   OR s.invoice_number LIKE $like)
            """;
        var args = new (string, object?)[]
        {
            ("$status", Clean(q.Status)), ("$site", q.SiteId), ("$from", Day(q.From)), ("$to", Day(q.To?.AddDays(1))),
            ("$search", Clean(q.Search)), ("$like", Like(q.Search)), ("$limit", q.PageSize), ("$offset", (q.Page - 1) * q.PageSize),
        };
        var total = await c.ScalarAsync<int>($"SELECT COUNT(*) FROM sales s LEFT JOIN parties p ON p.id = s.customer_id {where}", null, args);
        var rows = await c.QueryAsync($"{SelectSales} {where} ORDER BY s.created_at DESC LIMIT $limit OFFSET $offset", ReadSale, null, args);
        return new PagedResult<SaleRow>(rows, total, q.Page, q.PageSize);
    }

    public async Task<SaleRow?> GetSaleAsync(Guid id, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await GetSaleAsync(c, null, id);
    }

    public async Task<IReadOnlyList<SaleLineRow>> GetLinesAsync(Guid saleId, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await LinesAsync(c, null, saleId);
    }

    public async Task<IReadOnlyList<PaymentRow>> GetPaymentsAsync(Guid saleId, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await c.QueryAsync("SELECT * FROM sale_payments WHERE sale_id = $id ORDER BY line_no",
            r => new PaymentRow(r.Str("method"), r.Dec("amount"), r.StrOrNull("reference")), null, ("$id", saleId));
    }

    /// <summary>Todo lo que lleva el ticket de una venta (para imprimirlo o reimprimirlo).</summary>
    public async Task<Printing.SaleTicketData?> GetTicketDataAsync(Guid saleId, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        if (await GetSaleAsync(c, null, saleId) is not { } sale)
            return null;
        var payments = await c.QueryAsync("SELECT * FROM sale_payments WHERE sale_id = $id ORDER BY line_no",
            r => new PaymentRow(r.Str("method"), r.Dec("amount"), r.StrOrNull("reference")), null, ("$id", saleId));
        var company = await c.FirstOrDefaultAsync("SELECT * FROM company WHERE id = 1", r => new CompanyInfo(
            r.Str("legal_name"), r.StrOrNull("trade_name"), r.StrOrNull("tax_id"), r.Str("country_code"), r.Str("currency_code"),
            r.DecOrNull("default_tax_rate"), r.StrOrNull("time_zone")));
        var address = await c.FirstOrDefaultAsync("SELECT address_line, city FROM sites WHERE id = $id",
            r => string.Join(", ", new[] { r.StrOrNull("address_line"), r.StrOrNull("city") }.Where(x => !string.IsNullOrWhiteSpace(x))),
            null, ("$id", sale.SiteId));
        var regime = await TaxRegimeAsync(c, null);
        return new Printing.SaleTicketData(sale, await LinesAsync(c, null, saleId), payments, company, sale.SiteName, address, regime.TaxName);
    }

    public async Task<DashboardSummary> GetDashboardAsync(CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var today = StartOfDay();
        var yesterday = StartOfDay(1);
        var now = Clock.GetUtcNow();
        async Task<(int Count, decimal Total)> Window(DateTimeOffset from, DateTimeOffset to) =>
            (await c.QueryAsync($"""
                SELECT COUNT(*) AS n, COALESCE(SUM(total), 0) AS t FROM sales
                WHERE status = 'completed' AND created_at >= $from AND created_at < $to{SiteFilter("site_id")}
                """, r => (r.Int("n"), r.Dec("t")), null, ("$from", from), ("$to", to)))[0];

        var todayTotals = await Window(today, now.AddMinutes(1));
        // Comparación justa: ayer hasta la misma hora.
        var yesterdayTotals = await Window(yesterday, now - (today - yesterday));
        var week = await Window(StartOfDay(6), now.AddMinutes(1));
        var voided = await c.ScalarAsync<int>($"SELECT COUNT(*) FROM sales WHERE status = 'voided' AND created_at >= $from{SiteFilter("site_id")}", null,
            ("$from", StartOfDay(6)));
        var byMethod = (await c.QueryAsync($"""
            SELECT p.method, SUM(p.amount) AS amount FROM sale_payments p JOIN sales s ON s.id = p.sale_id
            WHERE s.status = 'completed' AND s.created_at >= $from{SiteFilter("s.site_id")} GROUP BY p.method ORDER BY amount DESC
            """, r => (r.Str("method"), r.Dec("amount")), null, ("$from", today))).ToDictionary(x => x.Item1, x => x.Item2);
        var currency = await c.ScalarAsync<string?>("SELECT currency_code FROM company WHERE id = 1") ?? "PEN";

        return new DashboardSummary(todayTotals.Total, todayTotals.Count,
            yesterdayTotals.Total > 0 ? Math.Round((todayTotals.Total - yesterdayTotals.Total) / yesterdayTotals.Total * 100, 1) : null,
            todayTotals.Count == 0 ? 0 : Math.Round(todayTotals.Total / todayTotals.Count, 2, MidpointRounding.AwayFromZero), week.Total, voided, byMethod, currency);
    }

    /// <summary>Lo más vendido en los últimos <paramref name="days"/> días (sin contar lo devuelto).</summary>
    public async Task<IReadOnlyList<TopItem>> GetTopItemsAsync(int days = 7, int limit = 6, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await c.QueryAsync($"""
            SELECT l.item_id, MAX(l.name) AS name, MAX(l.sku) AS sku, SUM(l.quantity - l.returned_qty) AS qty,
                   SUM(l.total * (l.quantity - l.returned_qty) / l.quantity) AS total
            FROM sale_lines l JOIN sales s ON s.id = l.sale_id
            WHERE s.status = 'completed' AND s.created_at >= $from{SiteFilter("s.site_id")}
            GROUP BY l.item_id HAVING qty > 0 ORDER BY total DESC LIMIT $limit
            """, r => new TopItem(r.Guid("item_id"), r.Str("name"), r.StrOrNull("sku"), r.Dec("qty"), Math.Round(r.Dec("total"), 2, MidpointRounding.AwayFromZero)), null,
            ("$from", StartOfDay(days - 1)), ("$limit", limit));
    }

    // ---------- Lo que llega de otras cajas y del ERP (lo usan los handlers de sincronización) ----------

    /// <summary>Venta de otra caja de la organización. Idempotente por <c>saleId</c> (las propias ya existen).</summary>
    internal static async Task ApplyRemoteSaleAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset occurredAt, SaleCompleted e)
    {
        if (await c.ScalarAsync<long>("SELECT COUNT(*) FROM sales WHERE id = $id", tx, ("$id", e.SaleId)) > 0)
            return;
        await InsertSaleAsync(c, tx, e, "remote", null, occurredAt, null, null,
            (e.Payments ?? []).Select(p => new PaymentDraft(p.Method, p.Amount)).ToList());
    }

    internal static async Task ApplyRemoteVoidAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset occurredAt, SaleVoided e) =>
        await MarkVoidedAsync(c, tx, e.SaleId, occurredAt, e.Reason);

    internal static async Task ApplyRemoteReturnAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset occurredAt, SaleReturned e)
    {
        if (await c.ScalarAsync<long>("SELECT COUNT(*) FROM sale_returns WHERE id = $id", tx, ("$id", e.ReturnId)) > 0)
            return;
        var saleLines = await LinesAsync(c, tx, e.SaleId);
        if (saleLines.Count == 0)
            return;

        // La devolución viaja por artículo: se reparte entre las líneas de la venta con ese artículo.
        var matched = new List<(int LineNo, decimal Quantity)>();
        foreach (var r in e.Lines ?? [])
        {
            var left = Math.Abs(r.Quantity);
            foreach (var line in saleLines.Where(l => l.ItemId == r.ItemId && l.Returnable > 0))
            {
                var take = Math.Min(left, line.Returnable);
                if (take <= 0)
                    break;
                matched.Add((line.LineNo, take));
                left -= take;
            }
        }
        await InsertReturnAsync(c, tx, e, null, occurredAt, null, matched);
    }

    internal static Task ApplyInvoiceAsync(SqliteConnection c, SqliteTransaction tx, InvoiceIssued e) =>
        c.ExecAsync("UPDATE sales SET invoice_number = $number WHERE id = $id", tx, ("$id", e.SaleId), ("$number", e.Number));

    /// <summary>Quita el número solo si es el del comprobante anulado (si ya llegó otro, se queda).</summary>
    internal static Task ApplyInvoiceVoidedAsync(SqliteConnection c, SqliteTransaction tx, InvoiceVoided e) =>
        c.ExecAsync("UPDATE sales SET invoice_number = NULL WHERE id = $id AND ($number IS NULL OR invoice_number = $number)", tx,
            ("$id", e.SaleId), ("$number", e.Number));

    // ---------- Internos ----------

    private const string SelectSales = """
        SELECT s.*, t.name AS site_name, p.name AS customer_name,
               (SELECT COALESCE(SUM(total), 0) FROM sale_returns r WHERE r.sale_id = s.id) AS returned,
               (SELECT COALESCE(SUM(quantity), 0) FROM sale_lines l WHERE l.sale_id = s.id) AS items
        FROM sales s LEFT JOIN sites t ON t.id = s.site_id LEFT JOIN parties p ON p.id = s.customer_id
        """;

    private static Task<SaleRow?> GetSaleAsync(SqliteConnection c, SqliteTransaction? tx, Guid id) =>
        c.FirstOrDefaultAsync($"{SelectSales} WHERE s.id = $id", ReadSale, tx, ("$id", id));

    private static SaleRow ReadSale(SqliteDataReader r) => new(
        r.Guid("id"), r.Str("number"), r.Str("origin"), r.Guid("site_id"), r.StrOrNull("site_name"), r.GuidOrNull("customer_id"),
        r.StrOrNull("customer_name"), r.Str("status"), r.Str("currency_code"), r.Dec("subtotal"), r.Dec("tax"), r.Dec("discount"),
        r.Dec("total"), r.DecOrNull("cash_received"), r.DecOrNull("change_given"), r.Time("created_at"), r.StrOrNull("user_name"),
        r.TimeOrNull("voided_at"), r.StrOrNull("void_reason"), r.StrOrNull("invoice_number"), r.Dec("returned"), (int)r.Dec("items"));

    private static async Task<List<SaleLineRow>> LinesAsync(SqliteConnection c, SqliteTransaction? tx, Guid saleId) =>
        await c.QueryAsync("SELECT * FROM sale_lines WHERE sale_id = $id ORDER BY line_no", r => new SaleLineRow(
            r.Int("line_no"), r.Guid("item_id"), r.StrOrNull("sku"), r.Str("name"), r.Dec("quantity"), r.Dec("unit_price"),
            r.Dec("discount"), r.Dec("total"), r.Dec("returned_qty")), tx, ("$id", saleId));

    private static async Task InsertSaleAsync(SqliteConnection c, SqliteTransaction tx, SaleCompleted e, string origin, Guid? sessionId,
        DateTimeOffset at, decimal? cashReceived, decimal? change, IReadOnlyList<PaymentDraft> payments)
    {
        var lines = e.Lines ?? [];
        await c.ExecAsync("""
            INSERT INTO sales (id, number, origin, site_id, customer_id, cash_session_id, status, currency_code, subtotal, tax,
                               discount, total, cash_received, change_given, created_at, user_name, coupon)
            VALUES ($id, $number, $origin, $site, $customer, $session, 'completed', $currency, $subtotal, $tax, $discount, $total,
                    $received, $change, $at, $user, $coupon)
            """, tx, ("$id", e.SaleId), ("$number", e.Number ?? e.SaleId.ToString()[..8].ToUpperInvariant()), ("$origin", origin),
            ("$site", e.SiteId), ("$customer", e.CustomerId), ("$session", sessionId), ("$currency", e.CurrencyCode ?? "PEN"),
            ("$subtotal", e.Subtotal), ("$tax", e.Tax), ("$discount", e.Discount != 0 ? e.Discount : lines.Sum(l => l.Discount)),
            ("$total", e.Total), ("$received", cashReceived), ("$change", change), ("$at", at), ("$user", e.CreatedBy), ("$coupon", e.Coupon));

        var lineNo = 0;
        foreach (var l in lines.Where(l => l.ItemId != Guid.Empty))
        {
            var name = l.Name ?? await c.ScalarAsync<string?>("SELECT name FROM items WHERE id = $id", tx, ("$id", l.ItemId)) ?? l.Sku ?? "Artículo";
            await c.ExecAsync("""
                INSERT INTO sale_lines (sale_id, line_no, item_id, sku, name, quantity, unit_price, discount, total, taxable, tax_code,
                                        presentation, list_price, promotion_id)
                VALUES ($sale, $no, $item, $sku, $name, $qty, $price, $discount, $total, $taxable, $taxCode, $presentation, $listPrice, $promo)
                """, tx, ("$sale", e.SaleId), ("$no", ++lineNo), ("$item", l.ItemId), ("$sku", l.Sku), ("$name", name),
                ("$qty", l.Quantity), ("$price", l.UnitPrice), ("$discount", l.Discount), ("$total", l.Total), ("$taxable", l.Taxable),
                ("$taxCode", l.TaxCode ?? (l.Taxable ? TaxCategories.Standard : TaxCategories.Exempt)),
                ("$presentation", l.Presentation), ("$listPrice", l.ListPrice), ("$promo", l.PromotionId));
        }

        var paymentNo = 0;
        foreach (var p in payments)
        {
            await c.ExecAsync("""
                INSERT INTO sale_payments (sale_id, line_no, method, amount, reference) VALUES ($sale, $no, $method, $amount, $ref)
                """, tx, ("$sale", e.SaleId), ("$no", ++paymentNo), ("$method", p.Method), ("$amount", p.Amount), ("$ref", p.Reference));
        }
    }

    private static Task MarkVoidedAsync(SqliteConnection c, SqliteTransaction tx, Guid saleId, DateTimeOffset at, string? reason) =>
        c.ExecAsync("""
            UPDATE sales SET status = 'voided', voided_at = $at, void_reason = $reason WHERE id = $id AND status = 'completed'
            """, tx, ("$id", saleId), ("$at", at), ("$reason", reason));

    private static async Task InsertReturnAsync(SqliteConnection c, SqliteTransaction tx, SaleReturned e, Guid? sessionId,
        DateTimeOffset at, string? userName, IReadOnlyList<(int LineNo, decimal Quantity)> lines)
    {
        await c.ExecAsync("""
            INSERT INTO sale_returns (id, number, sale_id, site_id, cash_session_id, total, reason, created_at, user_name)
            VALUES ($id, $number, $sale, $site, $session, $total, $reason, $at, $user)
            """, tx, ("$id", e.ReturnId), ("$number", e.Number ?? e.ReturnId.ToString()[..8].ToUpperInvariant()), ("$sale", e.SaleId),
            ("$site", e.SiteId), ("$session", sessionId), ("$total", e.Total), ("$reason", e.Reason), ("$at", at), ("$user", userName));
        var lineNo = 0;
        foreach (var l in e.Lines ?? [])
        {
            await c.ExecAsync("""
                INSERT INTO sale_return_lines (return_id, line_no, item_id, quantity, total) VALUES ($id, $no, $item, $qty, $total)
                """, tx, ("$id", e.ReturnId), ("$no", ++lineNo), ("$item", l.ItemId), ("$qty", Math.Abs(l.Quantity)), ("$total", l.Total));
        }
        foreach (var (no, quantity) in lines)
        {
            await c.ExecAsync("UPDATE sale_lines SET returned_qty = returned_qty + $qty WHERE sale_id = $sale AND line_no = $no", tx,
                ("$sale", e.SaleId), ("$no", no), ("$qty", quantity));
        }
    }

    private DateTimeOffset? Day(DateOnly? day) =>
        day is { } d ? new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), PosClock.Offset).ToUniversalTime() : null;
}
