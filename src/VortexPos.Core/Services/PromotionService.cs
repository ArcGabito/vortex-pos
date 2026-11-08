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
/// Promociones y cupones de la cadena (fase 3b). Se guardan como registros del POS (<c>pos.record.upserted</c>, kind
/// <c>promotion</c>): las ve cada caja, también sin conexión; si dos cajas cambian la misma, gana el cambio más
/// reciente. No se borran: se desactivan (las ventas que la usaron la siguen nombrando).
/// </summary>
public sealed class PromotionService(PosDatabase db, IPosContext context, TimeProvider clock) : PosService(db, context, clock)
{
    public async Task<IReadOnlyList<Promotion>> ListAsync(bool activeOnly = false, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return (await AllAsync(c, null)).Where(p => !activeOnly || p.IsActive).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Crea o cambia una promoción (con Id vacío se crea). Devuelve su Id.</summary>
    public Task<Guid> SaveAsync(Promotion promotion, CancellationToken ct = default)
    {
        RequirePermission(PosPermissions.ManagePromotions);
        var p = Normalize(promotion);
        Validate(p);
        return InTransactionAsync(async (c, tx, now) =>
        {
            if (p.CouponCode is { } code)
                Require(!(await AllAsync(c, tx)).Any(x => x.Id != p.Id && x.IsActive && string.Equals(x.CouponCode, code, StringComparison.OrdinalIgnoreCase)),
                    T("error.el_cupon_ya_existe", code));
            var record = new PosRecordUpserted(PosRecordKinds.Promotion, p.Id.ToString(), JsonSerializer.SerializeToElement(p, VortexJson.Options),
                now, Context.UserName);
            await StoreSettingsService.UpsertAsync(c, tx, record);
            await Outbox.EnqueueEventAsync(c, tx, now, SuiteEvents.PosRecordUpserted, record, subjectType: "promotion", subjectId: p.Id.ToString());
            await AuditAsync(c, tx, now, AuditActions.SettingsChanged, "promotion", p.Id, p.CouponCode, $"Promoción {p.Name} ({(p.IsActive ? "activa" : "inactiva")})");
            return p.Id;
        }, ct);
    }

    /// <summary>Usos de un cupón (en esta caja y en las ventas que llegaron de las demás).</summary>
    public async Task<int> CouponUsesAsync(string code, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await UsesAsync(c, null, code);
    }

    // ---------- Para el cobro ----------

    /// <summary>
    /// Promociones que aplican a la venta: activas, vigentes en la hora local de la tienda y en la tienda; las de cupón,
    /// solo si se ingresó ese cupón. Un cupón que no existe, no está vigente o se agotó corta el cobro.
    /// </summary>
    internal static async Task<List<Promotion>> ApplicableAsync(SqliteConnection c, SqliteTransaction? tx, DateTimeOffset now, Guid siteId,
        string? coupon)
    {
        var local = now.ToOffset(PosClock.Offset).DateTime;
        var all = (await AllAsync(c, tx)).Where(p => p.AppliesAt(local, siteId)).ToList();
        var code = coupon?.Trim();
        if (!string.IsNullOrEmpty(code))
        {
            var match = all.Where(p => string.Equals(p.CouponCode, code, StringComparison.OrdinalIgnoreCase)).ToList();
            Require(match.Count > 0, T("error.el_cupon_no_es_valido", code));
            foreach (var p in match.Where(p => p.MaxUses is not null))
                Require(await UsesAsync(c, tx, code) < p.MaxUses, T("error.el_cupon_ya_se_uso", code));
        }
        return all.Where(p => p.CouponCode is null || string.Equals(p.CouponCode, code, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static Task<int> UsesAsync(SqliteConnection c, SqliteTransaction? tx, string code) =>
        c.ScalarAsync<int>("SELECT COUNT(*) FROM sales WHERE coupon = $code COLLATE NOCASE AND status <> 'voided'", tx, ("$code", code.Trim()));

    private static async Task<List<Promotion>> AllAsync(SqliteConnection c, SqliteTransaction? tx) =>
        (await c.QueryAsync("SELECT data FROM pos_records WHERE kind = $kind", r => r.Str("data"), tx, ("$kind", PosRecordKinds.Promotion)))
            .Select(json => JsonSerializer.Deserialize<Promotion>(json, VortexJson.Options)).OfType<Promotion>().ToList();

    private static Promotion Normalize(Promotion p) => p with
    {
        Id = p.Id == Guid.Empty ? Guid.NewGuid() : p.Id,
        Name = p.Name.Trim(),
        Category = string.IsNullOrWhiteSpace(p.Category) ? null : p.Category.Trim(),
        CouponCode = string.IsNullOrWhiteSpace(p.CouponCode) ? null : p.CouponCode.Trim().ToUpperInvariant(),
        Days = p.Days is { Count: > 0 } d ? d.Distinct().Order().ToList() : null,
        SiteIds = p.SiteIds is { Count: > 0 } s ? s.Distinct().ToList() : null,
    };

    private static void Validate(Promotion p)
    {
        Require(p.Name.Length > 0, T("error.el_nombre_es_obligatorio"));
        Require(PromotionKinds.All.Contains(p.Kind), T("error.tipo_de_promocion_invalido"));
        switch (p.Kind)
        {
            case PromotionKinds.Percent:
                Require(p.Percent is > 0 and <= 100, T("error.el_porcentaje_va_de_1_a_100"));
                break;
            case PromotionKinds.BuyPay:
                Require(p.Buy is >= 2 && p.Pay is >= 0 && p.Pay < p.Buy, T("error.lleva_y_paga_invalidos"));
                break;
            case PromotionKinds.Ticket:
                Require((p.Amount is > 0) != (p.Percent is > 0 and <= 100), T("error.monto_o_porcentaje_del_ticket"));
                Require(p.MinAmount is null or >= 0, T("error.el_monto_minimo_no_puede_ser_negativo"));
                break;
        }
        Require(p.StartsOn is null || p.EndsOn is null || p.StartsOn <= p.EndsOn, T("error.la_vigencia_termina_antes"));
        Require(p.MaxUses is null or > 0, T("error.los_usos_deben_ser_mayores"));
        Require(p.MaxUses is null || p.CouponCode is not null, T("error.los_usos_son_del_cupon"));
    }
}
