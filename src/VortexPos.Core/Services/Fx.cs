using Microsoft.Data.Sqlite;
using VortexApi.Suite.Regional;
using VortexPos.Core.Data;

namespace VortexPos.Core.Services;

/// <summary>
/// Tipos de cambio de la copia local del hub (<c>exchange_rate</c>, fase 5b). La caja <b>compra</b> la moneda extranjera
/// que entrega el cliente: usa el tipo de compra del día (o el último anterior) y, sin él, el de referencia. Un par sin
/// dato directo se calcula con el inverso del par contrario (su tipo de venta).
/// </summary>
public static class Fx
{
    /// <summary>Cuánto vale 1 <paramref name="from"/> en <paramref name="to"/> al cobrar; null si no hay dato.</summary>
    public static async Task<decimal?> BuyRateAsync(SqliteConnection c, SqliteTransaction? tx, string from, string to, DateOnly date)
    {
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
            return 1m;
        var day = date.ToString("yyyy-MM-dd");
        var direct = await c.ScalarAsync<decimal?>("""
            SELECT COALESCE(buy_rate, rate) FROM exchange_rates
            WHERE is_deleted = 0 AND from_currency = $f AND to_currency = $t AND date <= $d ORDER BY date DESC LIMIT 1
            """, tx, ("$f", from.ToUpperInvariant()), ("$t", to.ToUpperInvariant()), ("$d", day));
        if (direct is > 0)
            return direct;
        var inverse = await c.ScalarAsync<decimal?>("""
            SELECT COALESCE(sell_rate, rate) FROM exchange_rates
            WHERE is_deleted = 0 AND from_currency = $t AND to_currency = $f AND date <= $d ORDER BY date DESC LIMIT 1
            """, tx, ("$f", from.ToUpperInvariant()), ("$t", to.ToUpperInvariant()), ("$d", day));
        return inverse is > 0 ? Math.Round(1m / inverse.Value, 6) : null;
    }

    /// <summary>El día de hoy en la zona horaria de la empresa.</summary>
    public static async Task<DateOnly> TodayAsync(SqliteConnection c, SqliteTransaction? tx, DateTimeOffset now) =>
        DateOnly.FromDateTime(RegionalSettings.ToCompanyTime(now, await c.ScalarAsync<string?>("SELECT time_zone FROM company WHERE id = 1", tx)).DateTime);
}
