namespace VortexPos.Core.Domain;

public static class PromotionKinds
{
    /// <summary>Porcentaje de descuento en los artículos que entran (todos, una categoría o un artículo).</summary>
    public const string Percent = "percent";

    /// <summary>Lleva N, paga M (2x1, 3x2) en los artículos que entran; salen gratis las unidades más baratas.</summary>
    public const string BuyPay = "buy_pay";

    /// <summary>Descuento al ticket (monto fijo o porcentaje) desde un monto mínimo de compra.</summary>
    public const string Ticket = "ticket";

    public static readonly string[] All = [Percent, BuyPay, Ticket];
}

/// <summary>
/// Promoción de la cadena. Es un registro del POS (se replica a todas las cajas con <c>pos.record.upserted</c>) y se
/// evalúa en Core con <see cref="PromotionEngine"/>, así dos cajas sin conexión cobran lo mismo. Con
/// <see cref="CouponCode"/> solo se aplica si se ingresa el cupón; <see cref="MaxUses"/> limita los usos del cupón.
/// </summary>
public sealed record Promotion(
    Guid Id, string Name, string Kind, string? Category = null, Guid? ItemId = null, decimal? Percent = null, int? Buy = null,
    int? Pay = null, decimal? MinAmount = null, decimal? Amount = null, DateOnly? StartsOn = null, DateOnly? EndsOn = null,
    IReadOnlyList<int>? Days = null, TimeOnly? FromTime = null, TimeOnly? ToTime = null, IReadOnlyList<Guid>? SiteIds = null,
    string? CouponCode = null, int? MaxUses = null, bool IsActive = true)
{
    public bool IsLinePromotion => Kind is PromotionKinds.Percent or PromotionKinds.BuyPay;

    /// <summary>Vigente en ese momento (hora local de la tienda) y en esa tienda.</summary>
    public bool AppliesAt(DateTime local, Guid siteId)
    {
        var day = DateOnly.FromDateTime(local);
        var time = TimeOnly.FromDateTime(local);
        return IsActive
               && (StartsOn is null || day >= StartsOn) && (EndsOn is null || day <= EndsOn)
               && (Days is not { Count: > 0 } || Days.Contains((int)local.DayOfWeek))
               && (FromTime is null || time >= FromTime) && (ToTime is null || time < ToTime)
               && (SiteIds is not { Count: > 0 } || SiteIds.Contains(siteId));
    }

    /// <summary>El artículo entra en la promoción (sin artículo ni categoría, entran todos).</summary>
    public bool Matches(Guid itemId, string? category) =>
        ItemId is { } item ? item == itemId
        : Category is { Length: > 0 } c ? string.Equals(c, category, StringComparison.OrdinalIgnoreCase)
        : true;
}

/// <summary>
/// Calcula los descuentos de las promociones sobre una venta. Reglas, iguales en todas las cajas:
/// <list type="number">
/// <item>Cada línea recibe a lo más una promoción de línea. Se aplica primero la que más descuenta en total (a igual
/// descuento, la de menor Id); se queda con sus líneas y las demás se recalculan con las que quedan libres.</item>
/// <item>Después, la mejor promoción de ticket que alcance su monto mínimo, sobre lo que queda; se reparte entre las
/// líneas en proporción a su importe (el redondeo va a la línea más grande).</item>
/// </list>
/// </summary>
public static class PromotionEngine
{
    public sealed record Line(Guid ItemId, string? Category, decimal Quantity, decimal UnitPrice)
    {
        public decimal Gross => Quantity * UnitPrice;
    }

    public sealed record Applied(Guid PromotionId, string Name, decimal Amount);

    public sealed record Result(decimal[] Discounts, Guid?[] LinePromotions, IReadOnlyList<Applied> Applied)
    {
        public decimal Total => Discounts.Sum();
    }

    public static Result Evaluate(IReadOnlyList<Line> lines, IEnumerable<Promotion> promotions, int decimals)
    {
        var discounts = new decimal[lines.Count];
        var owner = new Guid?[lines.Count];
        var applied = new List<Applied>();
        var candidates = promotions.Where(p => p.IsLinePromotion).ToList();

        while (true)
        {
            var best = candidates
                .Select(p => (Promo: p, Shares: LineDiscounts(p, lines, owner, decimals)))
                .Select(x => (x.Promo, x.Shares, Total: x.Shares.Values.Sum()))
                .Where(x => x.Total > 0)
                .OrderByDescending(x => x.Total).ThenBy(x => x.Promo.Id)
                .FirstOrDefault();
            if (best.Promo is null)
                break;
            foreach (var (index, amount) in best.Shares)
            {
                discounts[index] = amount;
                owner[index] = best.Promo.Id;
            }
            applied.Add(new Applied(best.Promo.Id, best.Promo.Name, best.Total));
            candidates.Remove(best.Promo);
        }

        // Promoción de ticket sobre lo que queda.
        var remaining = lines.Select((l, i) => l.Gross - discounts[i]).ToArray();
        var subtotal = remaining.Sum();
        var ticket = promotions.Where(p => p.Kind == PromotionKinds.Ticket && subtotal >= (p.MinAmount ?? 0))
            .Select(p => (Promo: p, Amount: Math.Min(subtotal, Round(p.Amount ?? subtotal * (p.Percent ?? 0) / 100m, decimals))))
            .Where(x => x.Amount > 0)
            .OrderByDescending(x => x.Amount).ThenBy(x => x.Promo.Id)
            .FirstOrDefault();
        if (ticket.Promo is not null)
        {
            var shares = Allocate(ticket.Amount, remaining, decimals);
            for (var i = 0; i < lines.Count; i++)
                discounts[i] += shares[i];
            applied.Add(new Applied(ticket.Promo.Id, ticket.Promo.Name, ticket.Amount));
        }
        return new Result(discounts, owner, applied);
    }

    /// <summary>Reparte <paramref name="amount"/> en proporción a <paramref name="weights"/>; el redondeo va al mayor.</summary>
    public static decimal[] Allocate(decimal amount, IReadOnlyList<decimal> weights, int decimals)
    {
        var result = new decimal[weights.Count];
        var total = weights.Sum();
        if (total <= 0 || amount <= 0)
            return result;
        for (var i = 0; i < weights.Count; i++)
            result[i] = Round(amount * weights[i] / total, decimals);
        var largest = Enumerable.Range(0, weights.Count).OrderByDescending(i => weights[i]).ThenBy(i => i).First();
        result[largest] += amount - result.Sum();
        return result;
    }

    /// <summary>Descuento por línea libre que daría la promoción.</summary>
    private static Dictionary<int, decimal> LineDiscounts(Promotion p, IReadOnlyList<Line> lines, Guid?[] owner, int decimals)
    {
        var free = Enumerable.Range(0, lines.Count).Where(i => owner[i] is null && lines[i].Quantity > 0 && p.Matches(lines[i].ItemId, lines[i].Category)).ToList();
        var shares = new Dictionary<int, decimal>();
        if (free.Count == 0)
            return shares;
        if (p.Kind == PromotionKinds.Percent)
        {
            foreach (var i in free)
                shares[i] = Round(lines[i].Gross * (p.Percent ?? 0) / 100m, decimals);
            return shares;
        }

        // Lleva N, paga M: por cada N unidades (de todas las líneas que entran) salen gratis N − M, las más baratas.
        if (p.Buy is not { } buy || p.Pay is not { } pay || buy <= 0 || pay < 0 || pay >= buy)
            return shares;
        var units = free.Sum(i => Math.Floor(lines[i].Quantity));
        var gratis = Math.Floor(units / buy) * (buy - pay);
        foreach (var i in free.OrderBy(i => lines[i].UnitPrice).ThenBy(i => i))
        {
            if (gratis <= 0)
                break;
            var take = Math.Min(gratis, Math.Floor(lines[i].Quantity));
            if (take > 0)
                shares[i] = Round(take * lines[i].UnitPrice, decimals);
            gratis -= take;
        }
        return shares;
    }

    private static decimal Round(decimal value, int decimals) => Math.Round(value, decimals, MidpointRounding.AwayFromZero);
}
