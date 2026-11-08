namespace VortexPos.Core.Integration;

// Contratos de los eventos de la suite Vortex, versión 1 (docs/SISTEMAS.md del hub, sección 6).
// Se serializan con VortexJson.Options (camelCase). Se pueden agregar campos, nunca quitar ni cambiar su significado;
// al leer, los campos desconocidos se ignoran.

public static class SuiteEvents
{
    // Publica el POS (y los recibe de sus otras cajas)
    public const string SaleCompleted = "sale.completed";
    public const string SaleVoided = "sale.voided";
    public const string SaleReturned = "sale.returned";

    // Replicación interna entre las cajas de una organización
    public const string PosRecordUpserted = "pos.record.upserted";

    // Consume el POS
    public const string StockAdjusted = "stock.adjusted";
    public const string InvoiceIssued = "invoice.issued";
    public const string InvoiceVoided = "invoice.voided";
}

/// <summary>
/// <c>sale.completed</c>. Extiende v1 con <c>name</c> en las líneas, <c>taxable</c>, <c>discount</c> y
/// <c>createdBy</c>, y por línea <c>taxCode</c> (categoría de impuesto de la base común), para que las otras cajas y el
/// ERP muestren la venta completa.
/// </summary>
public sealed record SaleCompleted(
    Guid SaleId, string Number, Guid SiteId, Guid? CustomerId, string CurrencyCode, decimal Subtotal, decimal Tax,
    decimal Total, IReadOnlyList<SaleLine> Lines, IReadOnlyList<SalePayment> Payments, decimal Discount = 0,
    string? CreatedBy = null);

public sealed record SaleLine(
    Guid ItemId, string? Sku, decimal Quantity, decimal UnitPrice, decimal Discount, decimal Total, string? Name = null,
    bool Taxable = true, string? TaxCode = null, string? Presentation = null, decimal? ListPrice = null);

public sealed record SalePayment(string Method, decimal Amount);

public sealed record SaleVoided(Guid SaleId, string? Number, string? Reason);

/// <summary><c>sale.returned</c>. Extiende v1 con <c>number</c> y <c>reason</c>.</summary>
public sealed record SaleReturned(
    Guid SaleId, Guid ReturnId, Guid SiteId, IReadOnlyList<SaleReturnLine> Lines, decimal Total, string? Number = null,
    string? Reason = null);

public sealed record SaleReturnLine(Guid ItemId, decimal Quantity, decimal Total);

/// <summary><c>stock.adjusted</c> del WMS: <c>quantityOnHand</c> es el saldo absoluto en la sede.</summary>
public sealed record StockAdjusted(Guid SiteId, Guid ItemId, decimal QuantityDelta, decimal QuantityOnHand, string? Reason);

/// <summary><c>invoice.issued</c> del ERP: si viene de una venta de caja, trae <c>saleId</c>.</summary>
public sealed record InvoiceIssued(Guid InvoiceId, string? Number, Guid? CustomerId, Guid? SaleId, decimal Total, string? CurrencyCode);

/// <summary><c>invoice.voided</c> del ERP: si el comprobante era de una venta de caja, trae <c>saleId</c>.</summary>
public sealed record InvoiceVoided(Guid InvoiceId, string? Number, string? Reason, Guid? SaleId);

/// <summary>
/// <c>pos.record.upserted</c>: replicación interna del POS entre las cajas de una organización (por ahora, la
/// configuración de cada tienda). Gana el registro con <c>updatedAt</c> más reciente.
/// </summary>
public sealed record PosRecordUpserted(string Kind, string Id, System.Text.Json.JsonElement Data, DateTimeOffset UpdatedAt,
    string? UpdatedBy = null);

public static class PosRecordKinds
{
    public const string StoreSettings = "store_settings";
}
