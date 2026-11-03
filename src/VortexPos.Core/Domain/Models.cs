namespace VortexPos.Core.Domain;

/// <summary>Quién opera y en qué equipo. Lo provee la app a partir de la sesión del hub.</summary>
public interface IPosContext
{
    Guid? UserId { get; }
    string UserName { get; }

    /// <summary>Prefijo corto del equipo para numerar documentos sin choques entre cajas sin conexión.</summary>
    string DeviceTag { get; }

    /// <summary>Tienda donde está esta caja (puede ser null hasta que el usuario la elija).</summary>
    Guid? DefaultSiteId { get; }
}

/// <summary>Dato inválido o regla de negocio incumplida: se muestra tal cual al usuario.</summary>
public sealed class PosValidationException(string message) : Exception(message);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize)
{
    public int Pages => Math.Max(1, (Total + PageSize - 1) / PageSize);
}

// ---------- Datos maestros (copia local del hub) ----------

/// <summary><see cref="OnHand"/> es el stock que informa el WMS en la tienda consultada (null si el WMS no lo publicó).</summary>
public sealed record ItemRow(
    Guid Id, string Sku, string Name, string Kind, string? Description, string Unit, string? Barcode, string? Category,
    decimal Price, string CurrencyCode, string? TaxCode, bool TrackInventory, bool IsActive, int Version, bool Pending,
    decimal? OnHand)
{
    /// <summary>Con <c>taxCode = "igv"</c> el precio de lista incluye el IGV.</summary>
    public bool Taxable => string.Equals(TaxCode, Tax.IgvCode, StringComparison.OrdinalIgnoreCase);
}

public sealed record SiteRow(
    Guid Id, string Code, string Name, string Kind, string? Phone, string? AddressLine, string? City, string? CountryCode,
    bool IsActive, int Version, bool Pending, int SalesToday, decimal TotalToday);

public sealed record PartyRow(
    Guid Id, string Name, string? TradeName, string Kind, string? DocumentType, string? DocumentNumber, bool IsCustomer,
    bool IsSupplier, string? Email, string? Phone, string? City, bool IsActive, int Version, bool Pending);

public sealed record CompanyInfo(string LegalName, string? TradeName, string? TaxId, string CountryCode, string CurrencyCode);

public sealed record Option(Guid Id, string Label, string? Detail = null);

public sealed record ItemDraft(
    string Sku, string Name, string Unit, string? Category, string? Barcode, decimal Price, string CurrencyCode,
    string? Description = null, bool TrackInventory = true, string Kind = "Product", string? TaxCode = Tax.IgvCode, bool IsActive = true);

public sealed record SiteDraft(
    string Code, string Name, string Kind, string? Phone, string? AddressLine, string? City, string CountryCode, bool IsActive = true);

public sealed record PartyDraft(
    string Name, string Kind, bool IsCustomer, bool IsSupplier, string? DocumentType, string? DocumentNumber,
    string? Email, string? Phone, string? City, string CountryCode = "PE");

// ---------- Impuestos ----------

public static class Tax
{
    public const string IgvCode = "igv";

    /// <summary>IGV general de Perú: 16 % + 2 % de Impuesto de Promoción Municipal.</summary>
    public const decimal IgvRate = 0.18m;

    public static decimal Round(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Desglosa un total con IGV incluido: devuelve la base imponible (subtotal) y el impuesto. Lo no gravado no
    /// lleva IGV y suma directo al subtotal.
    /// </summary>
    public static (decimal Subtotal, decimal Tax) Split(decimal taxableTotal, decimal exemptTotal)
    {
        var baseAmount = Round(taxableTotal / (1 + IgvRate));
        return (baseAmount + exemptTotal, taxableTotal - baseAmount);
    }
}

// ---------- Ventas ----------

public static class PaymentMethods
{
    public const string Cash = "cash";
    public const string Card = "card";
    public const string Transfer = "transfer";

    /// <summary>Billeteras digitales (Yape, Plin…).</summary>
    public const string Wallet = "wallet";

    /// <summary>Venta al crédito: la cobra después el ERP. Requiere cliente.</summary>
    public const string Credit = "credit";

    public static readonly string[] All = [Cash, Card, Wallet, Transfer, Credit];
}

public static class SaleStatuses
{
    public const string Completed = "completed";
    public const string Voided = "voided";
}

public sealed record CartLine(Guid ItemId, decimal Quantity, decimal? UnitPrice = null, decimal Discount = 0);

public sealed record PaymentDraft(string Method, decimal Amount, string? Reference = null);

/// <summary>Lo que se cobra en caja. En efectivo, <c>Amount</c> es lo que entrega el cliente (puede haber vuelto).</summary>
public sealed record SaleDraft(IReadOnlyList<CartLine> Lines, IReadOnlyList<PaymentDraft> Payments, Guid? CustomerId = null, Guid? SiteId = null);

public sealed record SaleTotals(decimal Subtotal, decimal Tax, decimal Discount, decimal Total);

public sealed record SaleReceipt(Guid SaleId, string Number, decimal Total, decimal Change);

public sealed record SaleRow(
    Guid Id, string Number, string Origin, Guid SiteId, string? SiteName, Guid? CustomerId, string? CustomerName, string Status,
    string CurrencyCode, decimal Subtotal, decimal Tax, decimal Discount, decimal Total, decimal? CashReceived,
    decimal? ChangeGiven, DateTimeOffset CreatedAt, string? UserName, DateTimeOffset? VoidedAt, string? VoidReason,
    string? InvoiceNumber, decimal Returned, int Items)
{
    public bool CanVoid => Status == SaleStatuses.Completed && Returned == 0;
    public bool CanReturn => Status == SaleStatuses.Completed && Returned < Total;
}

public sealed record SaleLineRow(
    int LineNo, Guid ItemId, string? Sku, string Name, decimal Quantity, decimal UnitPrice, decimal Discount, decimal Total,
    decimal ReturnedQty)
{
    public decimal Returnable => Quantity - ReturnedQty;
}

public sealed record PaymentRow(string Method, decimal Amount, string? Reference);

public sealed record SaleQuery(
    string? Search = null, Guid? SiteId = null, string? Status = null, DateOnly? From = null, DateOnly? To = null, int Page = 1,
    int PageSize = 15);

public sealed record ReturnLine(int LineNo, decimal Quantity);

// ---------- Caja ----------

public static class CashSessionStatuses
{
    public const string Open = "open";
    public const string Closed = "closed";
}

public sealed record CashSessionRow(
    Guid Id, string Number, Guid SiteId, string? SiteName, string Status, DateTimeOffset OpenedAt, string? OpenedBy,
    decimal OpeningFloat, DateTimeOffset? ClosedAt, string? ClosedBy, decimal? ExpectedCash, decimal? CountedCash,
    string? Note, int Sales, decimal SalesTotal)
{
    public decimal? Difference => CountedCash - ExpectedCash;
}

/// <summary>Arqueo de un turno: lo que debería haber en el cajón y lo cobrado por cada medio.</summary>
public sealed record CashSummary(
    decimal OpeningFloat, decimal CashSales, decimal CashRefunds, decimal ExpectedCash, IReadOnlyDictionary<string, decimal> ByMethod,
    int Sales, decimal SalesTotal);

// ---------- Panel ----------

public sealed record TopItem(Guid ItemId, string Name, string? Sku, decimal Quantity, decimal Total);

public sealed record DashboardSummary(
    decimal SalesToday, int TicketsToday, decimal? SalesChangePct, decimal AverageTicket, decimal Sales7d, int Voided7d,
    IReadOnlyDictionary<string, decimal> ByMethodToday, string CurrencyCode);
