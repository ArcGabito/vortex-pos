namespace VortexPos.Core.Domain;

/// <summary>
/// Billetes y monedas en circulación de cada moneda, de mayor a menor, para contar el cajón al cerrar. Una moneda que
/// no está aquí se cuenta solo por el total.
/// </summary>
public static class Denominations
{
    private static readonly Dictionary<string, decimal[]> ByCurrency = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PEN"] = [200, 100, 50, 20, 10, 5, 2, 1, 0.50m, 0.20m, 0.10m],
        ["USD"] = [100, 50, 20, 10, 5, 2, 1, 0.25m, 0.10m, 0.05m, 0.01m],
        ["EUR"] = [500, 200, 100, 50, 20, 10, 5, 2, 1, 0.50m, 0.20m, 0.10m, 0.05m, 0.02m, 0.01m],
        ["GBP"] = [50, 20, 10, 5, 2, 1, 0.50m, 0.20m, 0.10m, 0.05m, 0.02m, 0.01m],
        ["MXN"] = [1000, 500, 200, 100, 50, 20, 10, 5, 2, 1, 0.50m],
        ["COP"] = [100000, 50000, 20000, 10000, 5000, 2000, 1000, 500, 200, 100, 50],
        ["CLP"] = [20000, 10000, 5000, 2000, 1000, 500, 100, 50, 10],
        ["ARS"] = [20000, 10000, 2000, 1000, 500, 200, 100, 50, 20, 10],
        ["BRL"] = [200, 100, 50, 20, 10, 5, 2, 1, 0.50m, 0.25m, 0.10m, 0.05m],
        ["BOB"] = [200, 100, 50, 20, 10, 5, 2, 1, 0.50m, 0.20m, 0.10m],
        ["PYG"] = [100000, 50000, 20000, 10000, 5000, 2000, 1000, 500, 100, 50],
        ["UYU"] = [2000, 1000, 500, 200, 100, 50, 20, 10, 5, 2, 1],
        ["GTQ"] = [200, 100, 50, 20, 10, 5, 1, 0.50m, 0.25m, 0.10m, 0.05m],
        ["CRC"] = [50000, 20000, 10000, 5000, 2000, 1000, 500, 100, 50, 25, 10, 5],
        ["DOP"] = [2000, 1000, 500, 200, 100, 50, 25, 10, 5, 1],
    };

    public static IReadOnlyList<decimal> For(string currencyCode) => ByCurrency.GetValueOrDefault(currencyCode) ?? [];

    public static decimal Total(IEnumerable<DenominationCount> counts) => counts.Sum(c => c.Total);
}
