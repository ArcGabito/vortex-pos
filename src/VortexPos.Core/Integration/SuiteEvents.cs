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

    /// <summary>Efectivo de una caja depositado en el banco (lo concilia la tesorería del ERP).</summary>
    public const string CashDeposited = "cash.deposited";

    /// <summary>Movimiento de un saldo del cliente (puntos, gift card): libro replicado entre las cajas.</summary>
    public const string BalanceMoved = "balance.moved";

    /// <summary>Cobro del ERP: baja lo que el cliente debe en su cuenta corriente.</summary>
    public const string PaymentReceived = "payment.received";

    // Replicación interna entre las cajas de una organización
    public const string PosRecordUpserted = "pos.record.upserted";

    // Consume el POS
    public const string StockAdjusted = "stock.adjusted";
    public const string InvoiceIssued = "invoice.issued";
    public const string InvoiceVoided = "invoice.voided";

    // Del hub: respuesta de la autoridad tributaria a un comprobante (fase 5a)
    public const string FiscalAccepted = VortexApi.Client.VortexHubEvents.FiscalAccepted;
    public const string FiscalRejected = VortexApi.Client.VortexHubEvents.FiscalRejected;
}

/// <summary>
/// <c>sale.completed</c>. Extiende v1 con <c>name</c> en las líneas, <c>taxable</c>, <c>discount</c> y
/// <c>createdBy</c>, y por línea <c>taxCode</c> (categoría de impuesto de la base común), para que las otras cajas y el
/// ERP muestren la venta completa.
/// </summary>
public sealed record SaleCompleted(
    Guid SaleId, string Number, Guid SiteId, Guid? CustomerId, string CurrencyCode, decimal Subtotal, decimal Tax,
    decimal Total, IReadOnlyList<SaleLine> Lines, IReadOnlyList<SalePayment> Payments, decimal Discount = 0,
    string? CreatedBy = null, string? Coupon = null, Guid? PriceListId = null, SaleFiscal? Fiscal = null);

/// <summary>
/// Comprobante electrónico que emitió la caja (fase 5a): tipo (<c>receipt</c>, <c>invoice</c>, <c>credit_note</c>), serie y
/// número. Lo firma y lo envía el hub; las otras cajas y el ERP lo muestran (el ERP no vuelve a facturar la venta).
/// </summary>
public sealed record SaleFiscal(string Type, string Series, long Number);

public sealed record SaleLine(
    Guid ItemId, string? Sku, decimal Quantity, decimal UnitPrice, decimal Discount, decimal Total, string? Name = null,
    bool Taxable = true, string? TaxCode = null, string? Presentation = null, decimal? ListPrice = null,
    Guid? PromotionId = null);

public sealed record SalePayment(string Method, decimal Amount);

/// <param name="Fiscal">Nota de crédito (motivo 01, anulación) si la venta tenía comprobante electrónico.</param>
public sealed record SaleVoided(Guid SaleId, string? Number, string? Reason, SaleFiscal? Fiscal = null);

/// <summary><c>sale.returned</c>. Extiende v1 con <c>number</c> y <c>reason</c>.</summary>
/// <param name="SaleId">La venta devuelta; <see cref="Guid.Empty"/> en una devolución sin ticket.</param>
/// <param name="RefundMethod">Cómo se devolvió el dinero (<c>cash</c>, <c>card</c>…; <c>exchange</c> en un cambio).</param>
/// <param name="ExchangeSaleId">En un cambio, la venta de lo que se llevó.</param>
public sealed record SaleReturned(
    Guid SaleId, Guid ReturnId, Guid SiteId, IReadOnlyList<SaleReturnLine> Lines, decimal Total, string? Number = null,
    string? Reason = null, string? RefundMethod = null, Guid? ExchangeSaleId = null, SaleFiscal? Fiscal = null);

/// <summary><c>cash.deposited</c>: efectivo que sale de la caja para el banco.</summary>
public sealed record CashDeposited(Guid DepositId, Guid SiteId, decimal Amount, string CurrencyCode, string? Reference, string? Bank,
    DateOnly DepositedOn, Guid? SessionId, string? UserName);

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
    public const string Promotion = "promotion";

    /// <summary>Programa de puntos y gift cards de la organización. Id: <c>org</c>.</summary>
    public const string Loyalty = "loyalty";

    /// <summary>Apartado con sus abonos. Id: el del apartado.</summary>
    public const string Layaway = "layaway";

    /// <summary>Series de comprobantes de una caja (<see cref="Domain.FiscalSeries"/>). Id: la etiqueta del equipo.</summary>
    public const string FiscalSeries = "fiscal_series";
}

/// <summary><c>balance.moved</c>: un movimiento del libro de saldos (puntos o gift card). El saldo es la suma de los movimientos.</summary>
public sealed record BalanceMoved(Guid MovementId, string Kind, Guid AccountId, string? Code, Guid? CustomerId, decimal Delta, Guid? SaleId,
    Guid SiteId, string Reason, DateOnly? ExpiresOn);

/// <summary><c>payment.received</c> del ERP (solo lo que usa el POS: el cliente y el monto).</summary>
public sealed record PaymentReceived(Guid PaymentId, Guid? CustomerId, decimal Amount, string? CurrencyCode);
