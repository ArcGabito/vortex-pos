using VortexApi.Client;

namespace VortexPos.Core.Domain;

/// <summary>Comprobantes electrónicos que emite la caja (los firma y los envía el hub).</summary>
public static class FiscalKinds
{
    public const string Receipt = VortexFiscalTypes.Receipt;
    public const string Invoice = VortexFiscalTypes.Invoice;
    public const string CreditNote = VortexFiscalTypes.CreditNote;
}

/// <summary>Estado del comprobante ante la autoridad tributaria.</summary>
public static class FiscalStatuses
{
    /// <summary>Emitido en la caja, todavía sin respuesta (sin red queda así: contingencia, se envía al volver la conexión).</summary>
    public const string Pending = "pending";
    public const string Accepted = "accepted";

    /// <summary>Aceptado con observaciones: es válido.</summary>
    public const string Observed = "observed";
    public const string Rejected = "rejected";

    /// <summary>No se pudo enviar (clave SOL, certificado…): se reintenta desde Ventas.</summary>
    public const string Failed = "failed";

    /// <summary>El hub no lo aceptó (datos inválidos o número repetido): hay que revisarlo.</summary>
    public const string Error = "error";

    public static string From(VortexFiscalStatus status) => status switch
    {
        VortexFiscalStatus.Accepted => Accepted,
        VortexFiscalStatus.Observed => Observed,
        VortexFiscalStatus.Rejected => Rejected,
        VortexFiscalStatus.Failed => Failed,
        _ => Pending,
    };

    public static bool IsValid(string? status) => status is Accepted or Observed;
}

/// <summary>Series de esta caja (en Perú, B### para boletas y F### para facturas). Cada caja usa las suyas.</summary>
public sealed record FiscalSeries(string ReceiptSeries, string InvoiceSeries);

/// <summary>Si la caja emite comprobantes y con qué series.</summary>
/// <param name="Reason">Por qué no emite (sin el módulo, otro país, sin series), para mostrarlo en Configuración.</param>
public sealed record FiscalSetup(bool Enabled, FiscalSeries? Series, string? Reason);

/// <summary>Comprobante de una venta, una devolución o una anulación (de esta caja o de otra).</summary>
public sealed record FiscalDocumentRow(
    Guid Id, string Kind, string Series, long Number, Guid? SaleId, Guid? ReturnId, string Status, string? Hash, string? ResponseCode,
    string? ResponseDescription, string? Notes, string? LastError, bool Local, bool Sent, DateTimeOffset CreatedAt)
{
    public string FullNumber => $"{Series}-{Number}";
}

/// <summary>Cómo habla la caja con el hub para los comprobantes (en las pruebas, uno falso).</summary>
public interface IFiscalGateway
{
    Task<VortexFiscalDocument> IssueAsync(VortexFiscalRequest request, CancellationToken ct = default);
    Task<VortexFiscalDocument> RetryAsync(Guid hubId, CancellationToken ct = default);
}

/// <summary>Montos del comprobante como los calcula el hub (por línea desde lo cobrado): van en el ticket.</summary>
public sealed record FiscalTotals(decimal Taxed, decimal Exempt, decimal Unaffected, decimal Tax, decimal Total);
