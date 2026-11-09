using System.Text.Json;
using Microsoft.Data.Sqlite;
using VortexApi.Client;
using VortexApi.Suite.Auditing;
using VortexPos.Core.Data;
using VortexPos.Core.Domain;
using VortexPos.Core.Integration;
using VortexPos.Core.Sync;

using static VortexPos.Core.Texts.CoreText;

namespace VortexPos.Core.Services;

/// <summary>
/// Configuración de cada tienda (texto del ticket, ancho del papel, cierre ciego, cajón). La comparten todas las cajas
/// de la tienda: se guarda aquí y viaja como <c>pos.record.upserted</c>; si dos cajas la cambian sin conexión, gana el
/// cambio más reciente.
/// </summary>
public sealed class StoreSettingsService(PosDatabase db, IPosContext context, TimeProvider clock) : PosService(db, context, clock)
{
    public static readonly int[] PaperWidths = [32, 42, 48];

    public async Task<StoreSettings> GetAsync(Guid siteId, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await GetAsync(c, null, siteId);
    }

    /// <summary>Configuración de la tienda de la caja abierta (o de la tienda por defecto).</summary>
    public async Task<StoreSettings> GetCurrentAsync(CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        var site = (await CashService.OpenSessionAsync(c, null))?.SiteId ?? await DefaultSiteAsync(c, null);
        return site is { } id ? await GetAsync(c, null, id) : StoreSettings.Default;
    }

    public Task SaveAsync(Guid siteId, StoreSettings settings, CancellationToken ct = default)
    {
        RequirePermission(PosPermissions.StoreSettings);
        Require(PaperWidths.Contains(settings.PaperColumns), T("error.ancho_de_papel_invalido"));
        Require((settings.TicketHeader?.Length ?? 0) <= 500 && (settings.TicketFooter?.Length ?? 0) <= 500, T("error.texto_del_ticket_muy_largo"));
        Require(CanUseSite(siteId), T("error.tienda_no_asignada"));
        Require(ScaleLabel.Modes.Contains(settings.ScaleLabelMode) &&
            settings.ScaleItemDigits is >= ScaleLabel.MinItemDigits and <= ScaleLabel.MaxItemDigits, T("error.etiqueta_de_balanza_invalida"));
        var clean = settings with { TicketHeader = Clean(settings.TicketHeader), TicketFooter = Clean(settings.TicketFooter) };
        return InTransactionAsync(async (c, tx, now) =>
        {
            Require(await c.ScalarAsync<long>("SELECT COUNT(*) FROM sites WHERE id = $id AND is_deleted = 0", tx, ("$id", siteId)) > 0,
                T("error.la_tienda_elegida_no_existe"));
            var record = new PosRecordUpserted(PosRecordKinds.StoreSettings, siteId.ToString(),
                JsonSerializer.SerializeToElement(clean, VortexJson.Options), now, Context.UserName);
            await UpsertAsync(c, tx, record);
            await Outbox.EnqueueEventAsync(c, tx, now, SuiteEvents.PosRecordUpserted, record, subjectType: "store", subjectId: siteId.ToString());
            await AuditAsync(c, tx, now, AuditActions.SettingsChanged, "store", siteId, null, "Configuración de la tienda");
            return 0;
        }, ct);
    }

    internal static async Task<StoreSettings> GetAsync(SqliteConnection c, SqliteTransaction? tx, Guid siteId) =>
        await c.ScalarAsync<string?>("SELECT data FROM pos_records WHERE kind = $kind AND id = $id", tx,
            ("$kind", PosRecordKinds.StoreSettings), ("$id", siteId.ToString())) is { } json
            ? JsonSerializer.Deserialize<StoreSettings>(json, VortexJson.Options) ?? StoreSettings.Default
            : StoreSettings.Default;

    /// <summary>
    /// Guarda el registro si es más reciente que el que hay (a igual hora, desempata el nombre de quien lo cambió, para
    /// que todas las cajas lleguen al mismo resultado). Devuelve si lo aplicó.
    /// </summary>
    /// <param name="force">El cambio hecho en esta caja: gana siempre (aunque tenga la misma hora que el anterior).</param>
    internal static async Task<bool> UpsertAsync(SqliteConnection c, SqliteTransaction tx, PosRecordUpserted record, bool force = false)
    {
        var current = await c.FirstOrDefaultAsync("SELECT updated_at, updated_by FROM pos_records WHERE kind = $kind AND id = $id",
            r => new { At = r.Time("updated_at"), By = r.StrOrNull("updated_by") }, tx, ("$kind", record.Kind), ("$id", record.Id));
        if (!force && current is { } existing &&
            (existing.At > record.UpdatedAt ||
             (existing.At == record.UpdatedAt && string.CompareOrdinal(existing.By, record.UpdatedBy) >= 0)))
            return false;

        await c.ExecAsync("""
            INSERT INTO pos_records (kind, id, data, updated_at, updated_by) VALUES ($kind, $id, $data, $at, $by)
            ON CONFLICT (kind, id) DO UPDATE SET data = $data, updated_at = $at, updated_by = $by
            """, tx, ("$kind", record.Kind), ("$id", record.Id), ("$data", record.Data.GetRawText()), ("$at", record.UpdatedAt),
            ("$by", record.UpdatedBy));
        return true;
    }
}
