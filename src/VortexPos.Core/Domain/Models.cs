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

    /// <summary>Tiendas asignadas al usuario en el hub (<c>Access.SiteIds</c>). Vacío: trabaja en todas.</summary>
    IReadOnlyList<Guid> SiteIds => [];

    /// <summary>Rol efectivo del usuario en el POS (<see cref="PosPermissions"/>).</summary>
    string Role { get; }

    /// <summary>Si el usuario puede hacer la acción (<see cref="PosPermissions"/>).</summary>
    bool Can(string permission);

    /// <summary>Supervisor que autorizó con su PIN la acción en curso (null si no hubo autorización).</summary>
    string? AuthorizedBy => null;
}

/// <summary>Dato inválido o regla de negocio incumplida: se muestra tal cual al usuario.</summary>
public class PosValidationException(string message) : Exception(message);

/// <summary>El rol del usuario no permite la acción. La app puede pedir el PIN de un supervisor y reintentarla autorizada.</summary>
public sealed class PosPermissionException(string message, string permission) : PosValidationException(message)
{
    public string Permission { get; } = permission;
}

/// <summary>Quien opera la caja: el usuario de la sesión o el que entró con su PIN.</summary>
public sealed record PosOperator(Guid UserId, string Name, string Role);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize)
{
    public int Pages => Math.Max(1, (Total + PageSize - 1) / PageSize);
}

// ---------- Datos maestros (copia local del hub) ----------

/// <summary><see cref="OnHand"/> es el stock que informa el WMS en la tienda consultada (null si el WMS no lo publicó).</summary>
public sealed record ItemRow(
    Guid Id, string Sku, string Name, string Kind, string? Description, string Unit, string? Barcode, string? Category,
    decimal Price, string CurrencyCode, string? TaxCode, bool TrackInventory, bool IsActive, int Version, bool Pending,
    decimal? OnHand);

public sealed record SiteRow(
    Guid Id, string Code, string Name, string Kind, string? Phone, string? AddressLine, string? City, string? CountryCode,
    bool IsActive, int Version, bool Pending, int SalesToday, decimal TotalToday);

public sealed record PartyRow(
    Guid Id, string Name, string? TradeName, string Kind, string? DocumentType, string? DocumentNumber, bool IsCustomer,
    bool IsSupplier, string? Email, string? Phone, string? City, bool IsActive, int Version, bool Pending);

/// <summary>Empresa del hub. <c>DefaultTaxRate</c> en porcentaje (18 = 18 %) y <c>TimeZone</c> IANA.</summary>
public sealed record CompanyInfo(
    string LegalName, string? TradeName, string? TaxId, string CountryCode, string CurrencyCode, decimal? DefaultTaxRate = null,
    string? TimeZone = null);

public sealed record Option(Guid Id, string Label, string? Detail = null);

public sealed record ItemDraft(
    string Sku, string Name, string Unit, string? Category, string? Barcode, decimal Price, string CurrencyCode,
    string? Description = null, bool TrackInventory = true, string Kind = "Product", string? TaxCode = VortexApi.Suite.Taxes.TaxCategories.Standard,
    bool IsActive = true);

public sealed record SiteDraft(
    string Code, string Name, string Kind, string? Phone, string? AddressLine, string? City, string CountryCode, bool IsActive = true);

public sealed record PartyDraft(
    string Name, string Kind, bool IsCustomer, bool IsSupplier, string? DocumentType, string? DocumentNumber,
    string? Email, string? Phone, string? City, string CountryCode = "PE");

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

/// <summary>
/// Línea del carrito. <paramref name="Quantity"/> va siempre en la unidad del artículo (una caja de 12 son 12); la
/// <paramref name="Presentation"/> solo dice cómo se vendió. Un <paramref name="UnitPrice"/> distinto del de lista
/// necesita permiso.
/// </summary>
public sealed record CartLine(Guid ItemId, decimal Quantity, decimal? UnitPrice = null, decimal Discount = 0, string? Presentation = null);

/// <summary>Presentación de un artículo (caja, pack…) con cuántas unidades trae y su código de barras.</summary>
public sealed record ItemPresentationRow(Guid ItemId, string Code, string Name, decimal Factor, string? Barcode);

/// <summary>Qué se escaneó: el artículo, cuántas unidades agrega y si vino por presentación o etiqueta de balanza.</summary>
public sealed record ScanMatch(ItemRow Item, decimal Quantity, ItemPresentationRow? Presentation = null, bool Weighed = false);

public sealed record PaymentDraft(string Method, decimal Amount, string? Reference = null);

/// <summary>Lo que se cobra en caja. En efectivo, <c>Amount</c> es lo que entrega el cliente (puede haber vuelto).</summary>
/// <param name="Coupon">Cupón ingresado (activa las promociones de ese cupón).</param>
/// <param name="TicketDiscount">Descuento manual a todo el ticket (se reparte entre las líneas); más del 10 % necesita permiso.</param>
public sealed record SaleDraft(IReadOnlyList<CartLine> Lines, IReadOnlyList<PaymentDraft> Payments, Guid? CustomerId = null, Guid? SiteId = null,
    string? Coupon = null, decimal TicketDiscount = 0);

/// <summary>Cómo queda una línea agrupada (artículo, presentación y precio) con sus descuentos.</summary>
public sealed record QuoteLine(Guid ItemId, string? Presentation, decimal UnitPrice, decimal Quantity, decimal Gross, decimal PromotionDiscount,
    string? PromotionName, decimal ManualDiscount, decimal TicketShare, decimal Total);

/// <summary>La venta tal como se cobraría: líneas, promociones aplicadas y totales (lo mismo que calcula el cobro).</summary>
public sealed record SaleQuote(IReadOnlyList<QuoteLine> Lines, IReadOnlyList<PromotionEngine.Applied> Promotions, decimal TicketDiscount,
    SaleTotals Totals);

/// <summary>Totales de la venta. <c>Subtotal</c> es la base sin impuesto; con precios sin impuesto (EE. UU.) el impuesto se suma.</summary>
public sealed record SaleTotals(decimal Subtotal, decimal Tax, decimal Discount, decimal Total);

// ---------- Auditoría ----------

public sealed record AuditRow(
    Guid Id, DateTimeOffset At, string? UserName, string? Device, string Action, string Entity, string? Reference, string? Detail);

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
    int Sales, decimal SalesTotal, decimal CashIn = 0, decimal CashOut = 0);

public static class CashMovementKinds
{
    /// <summary>Entra efectivo al cajón (cambio, sencillo traído del banco).</summary>
    public const string In = "in";

    /// <summary>Sale efectivo del cajón (pago a un proveedor, retiro a la caja fuerte, depósito).</summary>
    public const string Out = "out";
}

public sealed record CashMovementRow(Guid Id, string Kind, decimal Amount, string Reason, DateTimeOffset CreatedAt, string? UserName);

/// <summary>Cantidad contada de un billete o moneda.</summary>
public sealed record DenominationCount(decimal Value, int Quantity)
{
    public decimal Total => Value * Quantity;
}

/// <summary>Ventas de un cajero dentro del turno (reporte X/Z).</summary>
public sealed record CashierTotals(string UserName, int Sales, decimal Total);

/// <summary>
/// Reporte de un turno. Mientras la caja está abierta es el reporte X (parcial); al cerrarla, el Z (cierre), con lo
/// contado y la diferencia.
/// </summary>
public sealed record CashReport(
    CashSessionRow Session, CashSummary Summary, string CurrencyCode, decimal Subtotal, decimal Tax, decimal Discounts,
    int Voided, decimal VoidedTotal, int Returns, IReadOnlyList<CashMovementRow> Movements, IReadOnlyList<CashierTotals> ByCashier,
    IReadOnlyList<DenominationCount> Denominations, DateTimeOffset GeneratedAt)
{
    public bool IsFinal => Session.Status == CashSessionStatuses.Closed;
}

// ---------- Tickets en espera ----------

/// <summary>Venta guardada para retomarla después (el cliente fue a buscar algo). Es de esta caja: no se replica.</summary>
public sealed record HeldSaleRow(Guid Id, string? Label, Guid? CustomerId, string? CustomerName, int Lines, decimal Total,
    DateTimeOffset CreatedAt, string? UserName);

public sealed record HeldSale(Guid Id, string? Label, Guid? CustomerId, IReadOnlyList<CartLine> Lines);

// ---------- Configuración de tienda ----------

/// <summary>
/// Configuración de una tienda, compartida por todas sus cajas (<c>pos.record.upserted</c>, gana el cambio más reciente).
/// La impresora es de cada caja y vive en el equipo.
/// </summary>
public sealed record StoreSettings(
    string? TicketHeader = null, string? TicketFooter = null, int PaperColumns = 48, bool BlindClose = false,
    bool PrintAutomatically = true, bool OpenDrawerOnCash = true, bool ScaleLabels = false,
    string ScaleLabelMode = ScaleLabel.Weight, int ScaleItemDigits = 5)
{
    public static StoreSettings Default { get; } = new();
}

// ---------- Panel ----------

public sealed record TopItem(Guid ItemId, string Name, string? Sku, decimal Quantity, decimal Total);

public sealed record DashboardSummary(
    decimal SalesToday, int TicketsToday, decimal? SalesChangePct, decimal AverageTicket, decimal Sales7d, int Voided7d,
    IReadOnlyDictionary<string, decimal> ByMethodToday, string CurrencyCode);
