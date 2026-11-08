using System.Text.Json;
using Microsoft.Data.Sqlite;
using VortexApi.Client;
using VortexApi.Client.Sync;
using VortexPos.Core.Data;
using VortexPos.Core.Integration;
using VortexPos.Core.Services;

namespace VortexPos.Core.Sync;

/// <summary>
/// Qué hace el POS con lo que llega del hub:
/// <list type="bullet">
/// <item>Actualiza la copia de datos maestros (artículos, tiendas, clientes, empresa).</item>
/// <item>Muestra el stock que publica el WMS (<c>stock.adjusted</c>, saldo absoluto por sede).</item>
/// <item>Anota el comprobante que emite el ERP para una venta (<c>invoice.issued</c> con <c>saleId</c>) y lo quita si se anula
/// (<c>invoice.voided</c>).</item>
/// <item>Replica las ventas, anulaciones y devoluciones de <b>las otras cajas</b> de la organización, que llegan como
/// sus propios eventos. Requiere <see cref="VortexSyncOptions.IncludeOwnEvents"/>.</item>
/// </list>
/// Cada evento se aplica una sola vez (tabla <c>processed_events</c>) y en una transacción junto con su efecto.
/// </summary>
public sealed class PosSyncHandlers(PosDatabase db, MasterDataService master, TimeProvider clock)
{
    public const string ProductCode = "POS";

    /// <summary>Opciones de <see cref="VortexSync"/> que necesita el POS (recibir las ventas de sus otras cajas).</summary>
    public static VortexSyncOptions SyncOptions => new() { IncludeOwnEvents = true };

    /// <summary>Registra todos los handlers. Llamar antes de la primera sincronización.</summary>
    public VortexSync Register(VortexSync sync) => sync
        .OnItems((c, ct) => master.ApplyItemAsync(c, ct))
        .OnSites((c, ct) => master.ApplySiteAsync(c, ct))
        .OnParties((c, ct) => master.ApplyPartyAsync(c, ct))
        .OnCompany((c, ct) => master.ApplyCompanyAsync(c, ct))
        .OnEvent(SuiteEvents.SaleCompleted, HandleSaleCompletedAsync)
        .OnEvent(SuiteEvents.SaleVoided, HandleSaleVoidedAsync)
        .OnEvent(SuiteEvents.SaleReturned, HandleSaleReturnedAsync)
        .OnEvent(SuiteEvents.PosRecordUpserted, HandlePosRecordAsync)
        .OnEvent(SuiteEvents.StockAdjusted, HandleStockAdjustedAsync)
        .OnEvent(SuiteEvents.InvoiceIssued, HandleInvoiceIssuedAsync)
        .OnEvent(SuiteEvents.InvoiceVoided, HandleInvoiceVoidedAsync);

    // ---------- Otras cajas del POS ----------

    public Task HandleSaleCompletedAsync(VortexEvent e, CancellationToken ct = default) =>
        ApplyOnceAsync<SaleCompleted>(e, async (sale, c, tx) =>
        {
            if (e.Source != ProductCode)
                return "ignorado: las ventas de caja solo las publica el POS";
            if (sale.SaleId == Guid.Empty || sale.SiteId == Guid.Empty)
                return "sin saleId o siteId";
            await SalesService.ApplyRemoteSaleAsync(c, tx, e.OccurredAt, sale);
            return null;
        }, ct);

    public Task HandleSaleVoidedAsync(VortexEvent e, CancellationToken ct = default) =>
        ApplyOnceAsync<SaleVoided>(e, async (voided, c, tx) =>
        {
            if (e.Source != ProductCode)
                return "ignorado: las anulaciones solo las publica el POS";
            await SalesService.ApplyRemoteVoidAsync(c, tx, e.OccurredAt, voided);
            return null;
        }, ct);

    public Task HandleSaleReturnedAsync(VortexEvent e, CancellationToken ct = default) =>
        ApplyOnceAsync<SaleReturned>(e, async (ret, c, tx) =>
        {
            if (e.Source != ProductCode)
                return "ignorado: las devoluciones solo las publica el POS";
            if (ret.ReturnId == Guid.Empty)
                return "sin returnId";
            await SalesService.ApplyRemoteReturnAsync(c, tx, e.OccurredAt, ret);
            return null;
        }, ct);

    /// <summary>Configuración compartida que cambió otra caja. Gana el cambio más reciente.</summary>
    public Task HandlePosRecordAsync(VortexEvent e, CancellationToken ct = default) =>
        ApplyOnceAsync<PosRecordUpserted>(e, async (record, c, tx) =>
        {
            if (e.Source != ProductCode)
                return "ignorado: los registros del POS solo los publica el POS";
            if (record.Kind != PosRecordKinds.StoreSettings || string.IsNullOrEmpty(record.Id))
                return $"ignorado: tipo de registro {record.Kind}";
            return await StoreSettingsService.UpsertAsync(c, tx, record) ? null : "ignorado: hay un cambio más reciente";
        }, ct);

    // ---------- Otros sistemas ----------

    /// <summary>
    /// Stock del WMS: se guarda el saldo absoluto (<c>quantityOnHand</c>), no se suma el delta, así no cuenta dos veces
    /// lo que el WMS también informa por otros eventos. El feed llega en orden: el último gana.
    /// </summary>
    public Task HandleStockAdjustedAsync(VortexEvent e, CancellationToken ct = default) =>
        ApplyOnceAsync<StockAdjusted>(e, async (s, c, tx) =>
        {
            if (s.SiteId == Guid.Empty || s.ItemId == Guid.Empty)
                return "sin siteId o itemId";
            await c.ExecAsync("""
                INSERT INTO stock (site_id, item_id, on_hand, updated_at) VALUES ($site, $item, $qty, $at)
                ON CONFLICT (site_id, item_id) DO UPDATE SET on_hand = $qty, updated_at = $at
                """, tx, ("$site", s.SiteId), ("$item", s.ItemId), ("$qty", s.QuantityOnHand), ("$at", e.OccurredAt));
            return null;
        }, ct);

    public Task HandleInvoiceIssuedAsync(VortexEvent e, CancellationToken ct = default) =>
        ApplyOnceAsync<InvoiceIssued>(e, async (invoice, c, tx) =>
        {
            if (invoice.SaleId is null)
                return "comprobante sin venta de caja";
            await SalesService.ApplyInvoiceAsync(c, tx, invoice);
            return null;
        }, ct);

    /// <summary>Comprobante anulado en el ERP: la venta de caja vuelve a quedar sin comprobante (el ERP puede emitirle otro).</summary>
    public Task HandleInvoiceVoidedAsync(VortexEvent e, CancellationToken ct = default) =>
        ApplyOnceAsync<InvoiceVoided>(e, async (voided, c, tx) =>
        {
            if (voided.SaleId is null)
                return "comprobante sin venta de caja";
            await SalesService.ApplyInvoiceVoidedAsync(c, tx, voided);
            return null;
        }, ct);

    // ---------- Internos ----------

    /// <summary>
    /// Aplica un evento una sola vez. Un esquema futuro o un <c>data</c> ilegible se registran como ignorados (nunca
    /// se van a poder aplicar y no deben trabar el feed); un error de la base sí se propaga para reintentar.
    /// </summary>
    private async Task ApplyOnceAsync<T>(VortexEvent e, Func<T, SqliteConnection, SqliteTransaction, Task<string?>> apply,
        CancellationToken ct) where T : class
    {
        await using var c = await db.OpenAsync(ct);
        await using var tx = c.BeginTransaction();
        if (await c.ScalarAsync<long>("SELECT COUNT(*) FROM processed_events WHERE event_id = $id", tx, ("$id", e.Id)) > 0)
            return;

        string? note;
        if (e.SchemaVersion != 1)
        {
            note = $"ignorado: schemaVersion {e.SchemaVersion} no soportada";
        }
        else
        {
            T? data = null;
            try
            {
                data = e.DataAs<T>();
                note = data is null ? "ignorado: data vacía" : null;
            }
            catch (JsonException ex)
            {
                note = $"ignorado: data inválida ({ex.Message})";
            }
            if (data is not null)
                note = await apply(data, c, tx);
        }

        await c.ExecAsync("INSERT INTO processed_events (event_id, type, processed_at, note) VALUES ($id, $type, $now, $note)", tx,
            ("$id", e.Id), ("$type", e.Type), ("$now", clock.GetUtcNow()), ("$note", note));
        await tx.CommitAsync(ct);
    }
}
