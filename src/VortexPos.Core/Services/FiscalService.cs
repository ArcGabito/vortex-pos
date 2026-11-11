using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VortexApi.Client;
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
/// Comprobante electrónico en caja (POS-101, decisión 3: lo emite el POS). La boleta o factura se numera en la caja dentro de
/// la transacción de la venta (así sale en el ticket aunque no haya red) y se pide al hub, que la firma y la envía a la
/// autoridad; sin conexión queda en contingencia y se envía en la siguiente sincronización. Anular o devolver una venta con
/// comprobante emite su nota de crédito. Hoy solo Perú (boleta B###, factura F###).
/// </summary>
public sealed class FiscalService(PosDatabase db, IPosContext context, TimeProvider clock) : PosService(db, context, clock)
{
    /// <summary>Módulo del hub que habilita la emisión (<c>features</c> del acceso de la organización al POS).</summary>
    public const string Feature = "facturacion_electronica";

    public static readonly string[] Countries = ["PE"];

    /// <summary>Monto desde el que una boleta debe identificar al cliente (S/ 700).</summary>
    public const decimal ReceiptIdentifyFrom = 700m;

    /// <summary>Días de la cuota única de una factura al crédito (el ERP lleva el plazo real del cliente).</summary>
    public const int CreditDays = 30;

    public const string VoidReason = "01";
    public const string ReturnReason = "07";

    // ---------- Configuración ----------

    public async Task<FiscalSetup> GetSetupAsync(CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await SetupAsync(c, null);
    }

    internal async Task<FiscalSetup> SetupAsync(SqliteConnection c, SqliteTransaction? tx)
    {
        var series = await SeriesAsync(c, tx, Context.DeviceTag);
        if (!Context.Features.Contains(Feature, StringComparer.OrdinalIgnoreCase))
            return new FiscalSetup(false, series, T("fiscal.sin_modulo"));
        var company = await c.FirstOrDefaultAsync("SELECT country_code, tax_id FROM company WHERE id = 1",
            r => new { Country = r.Str("country_code"), TaxId = r.StrOrNull("tax_id") }, tx);
        if (company is null || !Countries.Contains(company.Country))
            return new FiscalSetup(false, series, T("fiscal.sin_pais"));
        if (!IsValidRuc(company.TaxId))
            return new FiscalSetup(false, series, T("fiscal.sin_ruc"));
        if (series is null)
            return new FiscalSetup(false, null, T("fiscal.sin_series"));
        return new FiscalSetup(true, series, null);
    }

    /// <summary>
    /// Guarda las series de esta caja. Viajan a las demás cajas (<c>pos.record.upserted</c>) para que ninguna otra use las
    /// mismas: dos cajas con la misma serie repetirían números.
    /// </summary>
    public Task SaveSeriesAsync(FiscalSeries series, CancellationToken ct = default)
    {
        RequirePermission(PosPermissions.StoreSettings);
        var clean = new FiscalSeries(series.ReceiptSeries.Trim().ToUpperInvariant(), series.InvoiceSeries.Trim().ToUpperInvariant());
        Require(IsSeries(clean.ReceiptSeries, 'B'), T("fiscal.serie_boleta_invalida"));
        Require(IsSeries(clean.InvoiceSeries, 'F'), T("fiscal.serie_factura_invalida"));
        return InTransactionAsync(async (c, tx, now) =>
        {
            foreach (var (device, other) in await AllSeriesAsync(c, tx))
            {
                if (device == Context.DeviceTag)
                    continue;
                Require(other.ReceiptSeries != clean.ReceiptSeries && other.InvoiceSeries != clean.InvoiceSeries,
                    T("fiscal.serie_en_uso", device));
            }
            var record = new PosRecordUpserted(PosRecordKinds.FiscalSeries, Context.DeviceTag,
                JsonSerializer.SerializeToElement(clean, VortexJson.Options), now, Context.UserName);
            await StoreSettingsService.UpsertAsync(c, tx, record, force: true);
            await Outbox.EnqueueEventAsync(c, tx, now, SuiteEvents.PosRecordUpserted, record, subjectType: "device", subjectId: Context.DeviceTag);
            await AuditAsync(c, tx, now, AuditActions.SettingsChanged, "device", null, Context.DeviceTag,
                $"Series {clean.ReceiptSeries} y {clean.InvoiceSeries}");
            return 0;
        }, ct);
    }

    internal static async Task<FiscalSeries?> SeriesAsync(SqliteConnection c, SqliteTransaction? tx, string deviceTag) =>
        await c.ScalarAsync<string?>("SELECT data FROM pos_records WHERE kind = $kind AND id = $id", tx,
            ("$kind", PosRecordKinds.FiscalSeries), ("$id", deviceTag)) is { } json
            ? JsonSerializer.Deserialize<FiscalSeries>(json, VortexJson.Options)
            : null;

    private static async Task<List<(string Device, FiscalSeries Series)>> AllSeriesAsync(SqliteConnection c, SqliteTransaction tx) =>
        (await c.QueryAsync("SELECT id, data FROM pos_records WHERE kind = $kind",
            r => (r.Str("id"), JsonSerializer.Deserialize<FiscalSeries>(r.Str("data"), VortexJson.Options)!), tx,
            ("$kind", PosRecordKinds.FiscalSeries))).ToList();

    private static bool IsSeries(string series, char letter) =>
        series.Length == 4 && series[0] == letter && series.Skip(1).All(ch => char.IsAsciiDigit(ch) || char.IsAsciiLetterUpper(ch));

    // ---------- Emisión (en la transacción de la venta, la anulación o la devolución) ----------

    /// <summary>
    /// Numera el comprobante de la venta y lo deja en cola para el hub. Sin emisión configurada devuelve null (la caja vende
    /// como antes), salvo que se pida factura.
    /// </summary>
    internal async Task<SaleFiscal?> IssueForSaleAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, SaleCompleted sale,
        string? requestedType)
    {
        Require(requestedType is null or FiscalKinds.Receipt or FiscalKinds.Invoice, T("fiscal.tipo_desconocido"));
        var setup = await SetupAsync(c, tx);
        if (!setup.Enabled)
        {
            Require(requestedType != FiscalKinds.Invoice, T("fiscal.no_emite", setup.Reason ?? ""));
            return null;
        }
        var type = requestedType ?? FiscalKinds.Receipt;
        var customer = await CustomerAsync(c, tx, sale.CustomerId);
        if (type == FiscalKinds.Invoice)
            Require(customer.DocType == "6" && IsValidRuc(customer.DocNumber), T("fiscal.factura_sin_ruc"));
        else if (customer.DocType is "6" && !IsValidRuc(customer.DocNumber))
            throw new PosValidationException(T("fiscal.ruc_invalido"));
        if (type == FiscalKinds.Receipt && customer.DocNumber is null && sale.CurrencyCode == "PEN" && sale.Total >= ReceiptIdentifyFrom)
            throw new PosValidationException(T("fiscal.boleta_sin_documento", ReceiptIdentifyFrom));

        var series = type == FiscalKinds.Invoice ? setup.Series!.InvoiceSeries : setup.Series!.ReceiptSeries;
        var number = await NextNumberAsync(c, tx, type, series);
        var regime = await TaxRegimeAsync(c, tx);
        var lines = new List<VortexFiscalLine>();
        foreach (var l in sale.Lines)
            lines.Add(await LineAsync(c, tx, regime, l.ItemId, l.Name ?? l.Sku ?? "Artículo", l.Sku, l.Quantity, l.Total, l.TaxCode));

        var local = now.ToOffset(PosClock.Offset);
        var issueDate = DateOnly.FromDateTime(local.DateTime);
        VortexFiscalPayment? payment = null;
        DateOnly? due = null;
        var credit = sale.Payments.Where(p => p.Method == PaymentMethods.Credit).Sum(p => p.Amount);
        if (type == FiscalKinds.Invoice && credit > 0)
        {
            due = issueDate.AddDays(CreditDays);
            payment = new VortexFiscalPayment(true, [new VortexFiscalInstallment(credit, due.Value)]);
        }
        var request = new VortexFiscalRequest(sale.SaleId, type, series, number, issueDate, sale.CurrencyCode, customer, lines,
            TimeOnly.FromDateTime(local.DateTime), due, payment);
        await InsertLocalAsync(c, tx, now, request, sale.SaleId, null);
        return new SaleFiscal(type, series, number);
    }

    /// <summary>
    /// Nota de crédito de una venta con comprobante (anulación: motivo 01, todas sus líneas; devolución: motivo 07, lo
    /// devuelto). Si la venta no tenía comprobante, devuelve null. La serie es la de esta caja para el tipo del comprobante.
    /// </summary>
    internal async Task<SaleFiscal?> IssueCreditNoteAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, Guid sourceId, Guid saleId,
        Guid? returnId, IReadOnlyList<(Guid ItemId, decimal Quantity, decimal Total)> lines, string reasonCode, string description)
    {
        var original = await c.FirstOrDefaultAsync("""
            SELECT kind, series, number FROM fiscal_documents WHERE sale_id = $sale AND kind IN ('receipt', 'invoice')
            """, r => new { Kind = r.Str("kind"), Series = r.Str("series"), Number = r.Long("number") }, tx, ("$sale", saleId));
        if (original is null)
            return null;
        var setup = await SetupAsync(c, tx);
        Require(setup.Enabled, T("fiscal.nota_sin_configuracion", setup.Reason ?? ""));

        var sale = await c.FirstOrDefaultAsync("SELECT customer_id, currency_code FROM sales WHERE id = $id",
            r => new { Customer = r.GuidOrNull("customer_id"), Currency = r.Str("currency_code") }, tx, ("$id", saleId));
        var customer = await CustomerAsync(c, tx, sale?.Customer);
        var series = original.Kind == FiscalKinds.Invoice ? setup.Series!.InvoiceSeries : setup.Series!.ReceiptSeries;
        var number = await NextNumberAsync(c, tx, FiscalKinds.CreditNote, series);
        var regime = await TaxRegimeAsync(c, tx);
        var saleLines = await c.QueryAsync("SELECT item_id, sku, name, tax_code FROM sale_lines WHERE sale_id = $id",
            r => new { Item = r.Guid("item_id"), Sku = r.StrOrNull("sku"), Name = r.Str("name"), TaxCode = r.StrOrNull("tax_code") }, tx, ("$id", saleId));
        var fiscalLines = new List<VortexFiscalLine>();
        foreach (var l in lines.Where(l => l.Total > 0))
        {
            var source = saleLines.FirstOrDefault(s => s.Item == l.ItemId);
            fiscalLines.Add(await LineAsync(c, tx, regime, l.ItemId, source?.Name ?? "Artículo", source?.Sku, l.Quantity, l.Total, source?.TaxCode));
        }
        if (fiscalLines.Count == 0)
            return null;

        var local = now.ToOffset(PosClock.Offset);
        var request = new VortexFiscalRequest(sourceId, FiscalKinds.CreditNote, series, number, DateOnly.FromDateTime(local.DateTime),
            sale?.Currency ?? "PEN", customer, fiscalLines, TimeOnly.FromDateTime(local.DateTime),
            Reference: new VortexFiscalReference(original.Kind, original.Series, original.Number, reasonCode, Truncate(description, 250)));
        await InsertLocalAsync(c, tx, now, request, saleId, returnId);
        return new SaleFiscal(FiscalKinds.CreditNote, series, number);
    }

    private static async Task<VortexFiscalLine> LineAsync(SqliteConnection c, SqliteTransaction tx, TaxRegime regime, Guid itemId, string name,
        string? sku, decimal quantity, decimal total, string? taxCode)
    {
        var item = await c.FirstOrDefaultAsync("SELECT kind, unit_code FROM items WHERE id = $id",
            r => new { Kind = r.Str("kind"), UnitCode = r.StrOrNull("unit_code") }, tx, ("$id", itemId));
        var unit = item?.UnitCode is { Length: >= 2 and <= 3 } code ? code.ToUpperInvariant() : item?.Kind == "Service" ? "ZZ" : "NIU";
        var (category, rate) = Category(regime, taxCode);
        return new VortexFiscalLine(Truncate(name, 500), quantity, unit, 0, category, rate, sku is { Length: <= 30 } ? sku : null, total);
    }

    /// <summary>Categoría fiscal de la categoría de impuesto de la base común: con tasa, gravado; exonerado; lo demás, inafecto.</summary>
    public static (string Category, decimal Rate) Category(TaxRegime regime, string? taxCode)
    {
        var rate = regime.RateOf(taxCode);
        if (rate > 0)
            return (VortexFiscalTaxCategories.Taxed, rate);
        return TaxCategories.Normalize(taxCode) == TaxCategories.Exempt
            ? (VortexFiscalTaxCategories.Exempt, 0)
            : (VortexFiscalTaxCategories.Unaffected, 0);
    }

    /// <summary>Cliente del comprobante (catálogo 6): sin cliente, "clientes varios" sin documento.</summary>
    private static async Task<VortexFiscalParty> CustomerAsync(SqliteConnection c, SqliteTransaction tx, Guid? customerId)
    {
        var party = customerId is { } id
            ? await c.FirstOrDefaultAsync("SELECT name, document_type, document_number FROM parties WHERE id = $id",
                r => new { Name = r.Str("name"), Type = r.StrOrNull("document_type"), Number = r.StrOrNull("document_number") }, tx, ("$id", id))
            : null;
        if (party is null || party.Number is null)
            return new VortexFiscalParty("-", null, party?.Name ?? T("fiscal.clientes_varios"));
        var docType = party.Type?.ToUpperInvariant() switch
        {
            "RUC" => "6",
            "DNI" => "1",
            "CE" => "4",
            "PASAPORTE" or "PASSPORT" => "7",
            _ => "0",
        };
        return new VortexFiscalParty(docType, party.Number, Truncate(party.Name, 300));
    }

    /// <summary>
    /// Siguiente número de la serie: el mayor que se conoce más uno. Las ventas propias vuelven por la sincronización, así
    /// una caja reinstalada sigue desde donde iba.
    /// </summary>
    private static async Task<long> NextNumberAsync(SqliteConnection c, SqliteTransaction tx, string kind, string series) =>
        await c.ScalarAsync<long>("SELECT COALESCE(MAX(number), 0) FROM fiscal_documents WHERE kind = $kind AND series = $series", tx,
            ("$kind", kind), ("$series", series)) + 1;

    private static Task InsertLocalAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset now, VortexFiscalRequest request, Guid saleId,
        Guid? returnId) =>
        c.ExecAsync("""
            INSERT INTO fiscal_documents (id, kind, series, number, sale_id, return_id, request, status, created_at, updated_at)
            VALUES ($id, $kind, $series, $number, $sale, $return, $request, 'pending', $now, $now)
            """, tx, ("$id", request.SourceId), ("$kind", request.Type), ("$series", request.Series), ("$number", request.Number),
            ("$sale", saleId), ("$return", returnId), ("$request", JsonSerializer.Serialize(request, VortexJson.Options)), ("$now", now));

    /// <summary>Comprobante de otra caja (o el propio al volver del hub): se anota una vez.</summary>
    internal static Task ApplyRemoteAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset at, Guid id, SaleFiscal fiscal, Guid saleId,
        Guid? returnId) =>
        c.ExecAsync("""
            INSERT OR IGNORE INTO fiscal_documents (id, kind, series, number, sale_id, return_id, status, created_at, updated_at)
            VALUES ($id, $kind, $series, $number, $sale, $return, 'pending', $at, $at)
            """, tx, ("$id", id), ("$kind", fiscal.Type), ("$series", fiscal.Series.ToUpperInvariant()), ("$number", fiscal.Number),
            ("$sale", saleId), ("$return", returnId), ("$at", at));

    /// <summary>Respuesta de la autoridad (<c>hub.fiscal.*</c>): la reciben todas las cajas.</summary>
    internal static Task<int> ApplyResponseAsync(SqliteConnection c, SqliteTransaction tx, DateTimeOffset at, VortexFiscalResponded r) =>
        c.ExecAsync("""
            UPDATE fiscal_documents SET status = $status, hash = COALESCE($hash, hash), response_code = $code, response_description = $desc,
                notes = $notes, sent = 1, last_error = NULL, updated_at = $at
            WHERE id = $id
            """, tx, ("$id", r.SourceId), ("$status", FiscalStatuses.From(r.Status)), ("$hash", r.Hash), ("$code", r.ResponseCode),
            ("$desc", r.ResponseDescription), ("$notes", r.Notes), ("$at", at));

    // ---------- Envío al hub ----------

    /// <summary>
    /// Pide al hub los comprobantes de esta caja que aún no recibió (los de contingencia incluidos). Se corta al primer error de
    /// conexión; un comprobante que el hub no acepta (datos, número repetido) queda en <see cref="FiscalStatuses.Error"/>.
    /// Devuelve cuántos se enviaron.
    /// </summary>
    public async Task<int> SendPendingAsync(IFiscalGateway gateway, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var pending = await c.QueryAsync("""
            SELECT id, request FROM fiscal_documents WHERE request IS NOT NULL AND sent = 0 AND status = 'pending' ORDER BY created_at, number
            """, r => (Id: r.Guid("id"), Request: r.Str("request")));
        var sent = 0;
        foreach (var (id, json) in pending)
        {
            var request = JsonSerializer.Deserialize<VortexFiscalRequest>(json, VortexJson.Options)!;
            var now = Clock.GetUtcNow();
            try
            {
                var document = await gateway.IssueAsync(request, ct);
                await c.ExecAsync("""
                    UPDATE fiscal_documents SET sent = 1, hub_id = $hub, status = $status, hash = $hash, response_code = $code,
                        response_description = $desc, notes = $notes, last_error = NULL, updated_at = $now
                    WHERE id = $id
                    """, null, ("$id", id), ("$hub", document.Id), ("$status", FiscalStatuses.From(document.Status)), ("$hash", document.Hash),
                    ("$code", document.ResponseCode), ("$desc", document.ResponseDescription), ("$notes", document.Notes), ("$now", now));
                sent++;
            }
            catch (VortexApiException ex) when (ex.StatusCode is 400 or 422 || ex.Code == "fiscal_number_in_use")
            {
                await c.ExecAsync("UPDATE fiscal_documents SET status = 'error', last_error = $error, updated_at = $now WHERE id = $id", null,
                    ("$id", id), ("$error", Truncate(ex.Message, 500)), ("$now", now));
            }
            catch (VortexApiException ex) when (ex.Code is "fiscal_not_enabled" or "fiscal_not_configured" or "fiscal_country_unsupported")
            {
                // El hub aún no está listo: todos fallarían igual, se reintenta en la próxima sincronización.
                await c.ExecAsync("UPDATE fiscal_documents SET last_error = $error, updated_at = $now WHERE id = $id", null,
                    ("$id", id), ("$error", Truncate(ex.Message, 500)), ("$now", now));
                break;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or VortexApiException && !ct.IsCancellationRequested)
            {
                break;
            }
        }
        return sent;
    }

    /// <summary>Vuelve a enviar un comprobante que falló en el hub o que el hub no aceptó.</summary>
    public async Task RetryAsync(IFiscalGateway gateway, Guid documentId, CancellationToken ct = default)
    {
        RequirePermission(PosPermissions.Void);
        await using var c = await Db.OpenAsync(ct);
        var doc = await c.FirstOrDefaultAsync("SELECT status, hub_id, request FROM fiscal_documents WHERE id = $id",
            r => new { Status = r.Str("status"), Hub = r.GuidOrNull("hub_id"), Local = r.StrOrNull("request") is not null }, null, ("$id", documentId));
        Require(doc is not null, T("fiscal.no_existe"));
        Require(doc!.Status is FiscalStatuses.Failed or FiscalStatuses.Error, T("fiscal.no_reintentable"));
        var now = Clock.GetUtcNow();
        if (doc.Status == FiscalStatuses.Failed && doc.Hub is { } hubId)
        {
            var document = await gateway.RetryAsync(hubId, ct);
            await c.ExecAsync("UPDATE fiscal_documents SET status = $status, last_error = NULL, updated_at = $now WHERE id = $id", null,
                ("$id", documentId), ("$status", FiscalStatuses.From(document.Status)), ("$now", now));
        }
        else
        {
            Require(doc.Local, T("fiscal.de_otra_caja"));
            await c.ExecAsync("UPDATE fiscal_documents SET status = 'pending', sent = 0, last_error = NULL, updated_at = $now WHERE id = $id", null,
                ("$id", documentId), ("$now", now));
            await SendPendingAsync(gateway, ct);
        }
        await using var tx = c.BeginTransaction();
        await AuditAsync(c, tx, now, "fiscal.retried", "fiscal", documentId, null, null);
        await tx.CommitAsync(ct);
    }

    // ---------- Consultas ----------

    /// <summary>Comprobante y notas de una venta.</summary>
    public async Task<IReadOnlyList<FiscalDocumentRow>> GetForSaleAsync(Guid saleId, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await ForSaleAsync(c, null, saleId);
    }

    internal static async Task<IReadOnlyList<FiscalDocumentRow>> ForSaleAsync(SqliteConnection c, SqliteTransaction? tx, Guid saleId) =>
        await c.QueryAsync("SELECT * FROM fiscal_documents WHERE sale_id = $id ORDER BY created_at, kind DESC", Read, tx, ("$id", saleId));

    /// <summary>Comprobantes de esta caja sin respuesta todavía o con problemas (para el aviso de la caja).</summary>
    public async Task<IReadOnlyList<FiscalDocumentRow>> GetOutstandingAsync(CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await c.QueryAsync("""
            SELECT * FROM fiscal_documents WHERE request IS NOT NULL AND status IN ('pending', 'failed', 'error', 'rejected') ORDER BY created_at
            """, Read);
    }

    private static FiscalDocumentRow Read(SqliteDataReader r) => new(
        r.Guid("id"), r.Str("kind"), r.Str("series"), r.Long("number"), r.GuidOrNull("sale_id"), r.GuidOrNull("return_id"), r.Str("status"),
        r.StrOrNull("hash"), r.StrOrNull("response_code"), r.StrOrNull("response_description"), r.StrOrNull("notes"), r.StrOrNull("last_error"),
        r.StrOrNull("request") is not null, r.Long("sent") != 0, r.Time("created_at"));

    // ---------- Representación impresa ----------

    /// <summary>
    /// Montos como los calcula el hub desde lo cobrado: por línea, valor = total / (1 + tasa) redondeado y el IGV la
    /// diferencia; el total es exactamente lo cobrado.
    /// </summary>
    public static FiscalTotals Totals(IEnumerable<(decimal Total, string? TaxCode)> lines, TaxRegime regime)
    {
        decimal taxed = 0, exempt = 0, unaffected = 0, tax = 0, total = 0;
        foreach (var (lineTotal, taxCode) in lines)
        {
            var (category, rate) = Category(regime, taxCode);
            var value = Math.Round(lineTotal / (1 + rate), 2, MidpointRounding.AwayFromZero);
            if (category == VortexFiscalTaxCategories.Taxed)
            {
                taxed += value;
                tax += lineTotal - value;
            }
            else if (category == VortexFiscalTaxCategories.Exempt)
                exempt += lineTotal;
            else
                unaffected += lineTotal;
            total += lineTotal;
        }
        return new FiscalTotals(taxed, exempt, unaffected, tax, total);
    }

    public static string TypeCode(string kind) => kind switch
    {
        FiscalKinds.Invoice => "01",
        FiscalKinds.Receipt => "03",
        FiscalKinds.CreditNote => "07",
        _ => "00",
    };

    /// <summary>
    /// Texto del QR (RUC | tipo | serie | número | IGV | total | fecha | tipo doc. | n.º doc. | resumen |). El resumen de la
    /// firma es opcional: el ticket sale en el momento, antes de que el hub firme.
    /// </summary>
    public static string QrText(string ruc, string kind, string series, long number, decimal tax, decimal total, DateOnly issued, string customerDocType,
        string? customerDocNumber, string? hash) =>
        string.Join('|', ruc, TypeCode(kind), series, number, tax.ToString("0.00", CultureInfo.InvariantCulture),
            total.ToString("0.00", CultureInfo.InvariantCulture), issued.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), customerDocType,
            customerDocNumber ?? "-", hash ?? "") + "|";

    /// <summary>RUC: 11 dígitos, empieza en 10, 15, 16, 17 o 20, y dígito verificador módulo 11.</summary>
    public static bool IsValidRuc(string? ruc)
    {
        if (ruc is not { Length: 11 } || !ruc.All(char.IsAsciiDigit) || ruc[..2] is not ("10" or "15" or "16" or "17" or "20"))
            return false;
        int[] weights = [5, 4, 3, 2, 7, 6, 5, 4, 3, 2];
        var check = 11 - weights.Select((w, i) => w * (ruc[i] - '0')).Sum() % 11;
        return ruc[10] - '0' == check switch { 10 => 0, 11 => 1, _ => check };
    }

    /// <summary>Tipo de documento del cliente (catálogo 6) para el ticket y el QR.</summary>
    public static string CustomerDocType(string? documentType, string? documentNumber) => documentNumber is null
        ? "-"
        : documentType?.ToUpperInvariant() switch { "RUC" => "6", "DNI" => "1", "CE" => "4", "PASAPORTE" or "PASSPORT" => "7", _ => "0" };

    private static string Truncate(string value, int max) => value.Length > max ? value[..max] : value;
}
