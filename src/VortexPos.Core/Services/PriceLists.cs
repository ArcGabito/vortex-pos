using Microsoft.Data.Sqlite;
using VortexPos.Core.Data;
using VortexPos.Core.Domain;

namespace VortexPos.Core.Services;

/// <summary>
/// Listas de precios del hub (fase 3c): la del cliente (<c>credit.priceListId</c>) si está vigente; si no, la de la tienda
/// (la primera por código); si no, el precio del artículo. Las escalas dependen de cuánto se lleva del artículo en toda la
/// venta. Se resuelve en el equipo, también sin conexión: dos cajas con la misma copia cobran igual.
/// </summary>
internal static class PriceLists
{
    internal static async Task<PriceListRef?> ChooseAsync(SqliteConnection c, SqliteTransaction? tx, DateTimeOffset now, Guid? customerId, Guid siteId)
    {
        var today = DateOnly.FromDateTime(now.ToOffset(PosClock.Offset).DateTime).ToString("yyyy-MM-dd");
        const string vigente = """
            l.is_active = 1 AND l.is_deleted = 0 AND (l.valid_from IS NULL OR l.valid_from <= $today) AND (l.valid_to IS NULL OR l.valid_to >= $today)
            """;
        if (customerId is { } customer && await c.FirstOrDefaultAsync($"""
                SELECT l.id, l.code, l.name, l.currency_code FROM price_lists l JOIN parties p ON p.price_list_id = l.id
                WHERE p.id = $customer AND {vigente}
                """, Read, tx, ("$customer", customer), ("$today", today)) is { } own)
            return own;
        return await c.FirstOrDefaultAsync($"""
            SELECT l.id, l.code, l.name, l.currency_code FROM price_lists l
            WHERE {vigente} AND EXISTS (SELECT 1 FROM json_each(l.site_ids) WHERE value = $site)
            ORDER BY l.code LIMIT 1
            """, Read, tx, ("$site", siteId.ToString()), ("$today", today));
    }

    /// <summary>Precio de la escala para esa cantidad (las fracciones toman la escala "desde 1"), o null si no está en la lista.</summary>
    internal static async Task<decimal?> PriceAsync(SqliteConnection c, SqliteTransaction? tx, Guid listId, Guid itemId, decimal quantity)
    {
        var prices = await c.QueryAsync("""
            SELECT price FROM price_list_lines WHERE list_id = $list AND item_id = $item AND min_qty <= $qty ORDER BY min_qty DESC LIMIT 1
            """, r => r.Dec("price"), tx, ("$list", listId), ("$item", itemId), ("$qty", Math.Max(quantity, 1)));
        return prices.Count == 0 ? null : prices[0];
    }

    private static PriceListRef Read(SqliteDataReader r) => new(r.Guid("id"), r.Str("code"), r.Str("name"), r.Str("currency_code"));
}
