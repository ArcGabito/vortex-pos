using System.Globalization;
using System.Text;
using VortexApi.Suite.Money;
using VortexApi.Suite.Regional;
using VortexPos.Core.Domain;

using static VortexPos.Core.Texts.CoreText;

namespace VortexPos.Core.Printing;

public enum TicketAlign { Left, Center, Right }

/// <summary>Una línea del ticket. <c>Large</c> es doble ancho y alto: ocupa el doble de columnas.</summary>
public sealed record TicketLine(string Text, TicketAlign Align = TicketAlign.Left, bool Bold = false, bool Large = false);

/// <summary>Ticket listo para imprimir: líneas ya cortadas al ancho del papel, más si se corta el papel y se abre el cajón.</summary>
/// <param name="Qr">Texto del código QR que va al final (representación impresa del comprobante electrónico).</param>
/// <param name="AfterQr">Líneas que van debajo del QR.</param>
public sealed record TicketDocument(IReadOnlyList<TicketLine> Lines, int Columns, bool OpenDrawer = false, bool Cut = true, string? Qr = null,
    IReadOnlyList<TicketLine>? AfterQr = null)
{
    /// <summary>El ticket como texto plano (vista previa y pruebas).</summary>
    public string ToText() => string.Join('\n', Lines.Select(l => l.Text)
        .Concat(Qr is null ? [] : [$"[QR] {Qr}"]).Concat((AfterQr ?? []).Select(l => l.Text)));
}

/// <summary>Comprobante electrónico de la venta para el ticket.</summary>
/// <param name="CreditNotes">Notas de crédito emitidas sobre el comprobante (anulación, devoluciones).</param>
public sealed record FiscalTicket(string Kind, string Number, string Status, string CustomerDocType, string? CustomerDocNumber, FiscalTotals Totals,
    string Qr, string? Hash, IReadOnlyList<string> CreditNotes);

/// <summary>Lo que lleva el ticket de una venta.</summary>
public sealed record SaleTicketData(
    SaleRow Sale, IReadOnlyList<SaleLineRow> Lines, IReadOnlyList<PaymentRow> Payments, CompanyInfo? Company, string? SiteName,
    string? SiteAddress, string TaxName, FiscalTicket? Fiscal = null);

/// <summary>
/// Arma los tickets (venta y reporte X/Z) como líneas de texto al ancho del papel. No depende de la impresora: el mismo
/// documento se codifica en ESC/POS (<see cref="EscPos"/>) o se muestra en pantalla.
/// </summary>
public static class TicketBuilder
{
    public static TicketDocument ForSale(SaleTicketData data, StoreSettings settings, CultureInfo culture, bool reprint = false)
    {
        var b = new Builder(settings.PaperColumns);
        var sale = data.Sale;
        var currency = Currencies.Get(sale.CurrencyCode);
        string Money(decimal value) => currency.Format(value, culture);

        b.Header(data.Company, data.SiteName, data.SiteAddress, settings.TicketHeader);
        if (reprint)
            b.Center(T("ticket.reimpresion"), bold: true);
        var fiscal = data.Fiscal;
        if (fiscal is not null)
        {
            b.Separator();
            b.Center(T(fiscal.Kind == FiscalKinds.Invoice ? "ticket.factura_electronica" : "ticket.boleta_electronica"), bold: true);
            b.Center(fiscal.Number, bold: true, large: true);
        }
        b.Separator();
        b.Pair(T("ticket.venta"), sale.Number);
        b.Pair(T("ticket.fecha"), Local(sale.CreatedAt, data.Company).ToString("g", culture));
        if (sale.UserName is { Length: > 0 } cashier)
            b.Pair(T("ticket.cajero"), cashier);
        if (sale.CustomerName is { Length: > 0 } customer)
            b.Wrap($"{T("ticket.cliente")}: {customer}");
        if (fiscal?.CustomerDocNumber is { } doc)
            b.Pair(T(fiscal.CustomerDocType == "6" ? "ticket.ruc" : "ticket.documento"), doc);
        b.Separator();

        foreach (var line in data.Lines)
        {
            b.Wrap(line.Name);
            b.Pair($"  {line.Quantity.ToString("0.###", culture)} x {Money(line.UnitPrice)}", Money(line.Total));
            if (line.Discount > 0)
                b.Pair($"  {T("ticket.descuento")}", "-" + Money(line.Discount));
        }
        b.Separator();

        if (sale.Discount > 0)
            b.Pair(T("ticket.descuentos"), "-" + Money(sale.Discount));
        if (fiscal is not null)
        {
            // Los montos del comprobante (calculados por línea como en el XML): el total es lo cobrado.
            if (fiscal.Totals.Taxed > 0)
                b.Pair(T("ticket.op_gravada"), Money(fiscal.Totals.Taxed));
            if (fiscal.Totals.Exempt > 0)
                b.Pair(T("ticket.op_exonerada"), Money(fiscal.Totals.Exempt));
            if (fiscal.Totals.Unaffected > 0)
                b.Pair(T("ticket.op_inafecta"), Money(fiscal.Totals.Unaffected));
            b.Pair(data.TaxName, Money(fiscal.Totals.Tax));
        }
        else
        {
            b.Pair(T("ticket.subtotal"), Money(sale.Subtotal));
            b.Pair(data.TaxName, Money(sale.Tax));
        }
        b.Pair(T("ticket.total"), Money(sale.Total), bold: true, large: true);
        b.Blank();
        foreach (var payment in data.Payments)
            b.Pair(PaymentName(payment.Method) + (payment.Reference is { Length: > 0 } r ? $" ({r})" : ""), Money(payment.Amount));
        if (sale.CashReceived is { } received)
        {
            b.Pair(T("ticket.efectivo_recibido"), Money(received));
            b.Pair(T("ticket.vuelto"), Money(sale.ChangeGiven ?? 0));
        }
        if (sale.Status == SaleStatuses.Voided)
        {
            b.Blank();
            b.Center(T("ticket.anulada"), bold: true);
        }
        if (sale.InvoiceNumber is { Length: > 0 } invoice)
        {
            b.Blank();
            b.Pair(T("ticket.comprobante"), invoice);
        }

        foreach (var note in fiscal?.CreditNotes ?? [])
            b.Pair(T("ticket.nota_credito"), note);

        b.Footer(settings.TicketFooter);
        var paidCash = data.Payments.Any(p => p.Method == PaymentMethods.Cash);
        if (fiscal is null)
            return b.Build(openDrawer: !reprint && paidCash && settings.OpenDrawerOnCash);

        // Representación impresa: leyenda, QR y, si el hub ya firmó, el resumen de la firma.
        var after = new Builder(settings.PaperColumns);
        after.Center(T(fiscal.Kind == FiscalKinds.Invoice ? "ticket.representacion_factura" : "ticket.representacion_boleta"));
        if (fiscal.Hash is { Length: > 0 } hash)
            after.Center($"{T("ticket.resumen")}: {hash}");
        if (fiscal.Status == FiscalStatuses.Pending)
            after.Center(T("ticket.contingencia"));
        return b.Build(openDrawer: !reprint && paidCash && settings.OpenDrawerOnCash, qr: fiscal.Qr, afterQr: after.Lines);
    }

    /// <summary>Reporte X (caja abierta) o Z (caja cerrada) del turno.</summary>
    public static TicketDocument ForCashReport(CashReport report, CompanyInfo? company, StoreSettings settings, CultureInfo culture)
    {
        var b = new Builder(settings.PaperColumns);
        var currency = Currencies.Get(report.CurrencyCode);
        string Money(decimal value) => currency.Format(value, culture);
        var s = report.Session;
        var sum = report.Summary;

        b.Header(company, s.SiteName, null, null);
        b.Center(report.IsFinal ? T("ticket.reporte_z") : T("ticket.reporte_x"), bold: true, large: true);
        b.Separator();
        b.Pair(T("ticket.turno"), s.Number);
        b.Pair(T("ticket.apertura"), Local(s.OpenedAt, company).ToString("g", culture));
        if (s.OpenedBy is { Length: > 0 } openedBy)
            b.Pair("", openedBy);
        if (s.ClosedAt is { } closedAt)
        {
            b.Pair(T("ticket.cierre"), Local(closedAt, company).ToString("g", culture));
            if (s.ClosedBy is { Length: > 0 } closedBy)
                b.Pair("", closedBy);
        }
        else
        {
            b.Pair(T("ticket.emitido"), Local(report.GeneratedAt, company).ToString("g", culture));
        }

        b.Section(T("ticket.ventas"));
        b.Pair(T("ticket.tickets"), sum.Sales.ToString(culture));
        b.Pair(T("ticket.subtotal"), Money(report.Subtotal));
        b.Pair(T("ticket.impuestos"), Money(report.Tax));
        b.Pair(T("ticket.descuentos"), Money(report.Discounts));
        b.Pair(T("ticket.total_vendido"), Money(sum.SalesTotal), bold: true);
        b.Pair(T("ticket.anuladas", report.Voided), Money(report.VoidedTotal));
        b.Pair(T("ticket.devoluciones", report.Returns), Money(sum.CashRefunds));

        b.Section(T("ticket.por_medio_de_pago"));
        foreach (var (method, amount) in sum.ByMethod.OrderByDescending(m => m.Value))
            b.Pair(PaymentName(method), Money(amount));

        if (report.ByCashier.Count > 1)
        {
            b.Section(T("ticket.por_cajero"));
            foreach (var cashier in report.ByCashier)
                b.Pair($"{cashier.UserName} ({cashier.Sales})", Money(cashier.Total));
        }

        b.Section(T("ticket.efectivo"));
        b.Pair(T("ticket.fondo_inicial"), Money(sum.OpeningFloat));
        b.Pair(T("ticket.ventas_en_efectivo"), Money(sum.CashSales));
        b.Pair(T("ticket.devoluciones_en_efectivo"), "-" + Money(sum.CashRefunds));
        foreach (var m in report.Movements)
            b.Pair($"{(m.Kind == CashMovementKinds.In ? "+" : "-")} {m.Reason}", Money(m.Amount));
        b.Pair(T("ticket.efectivo_esperado"), Money(sum.ExpectedCash), bold: true);
        if (s.CountedCash is { } counted)
        {
            b.Pair(T("ticket.efectivo_contado"), Money(counted));
            b.Pair(T("ticket.diferencia"), (counted - sum.ExpectedCash > 0 ? "+" : "") + Money(counted - sum.ExpectedCash), bold: true);
            if (s.Note is { Length: > 0 } note)
                b.Wrap(note);
        }

        if (report.Denominations.Count > 0)
        {
            b.Section(T("ticket.conteo"));
            foreach (var d in report.Denominations)
                b.Pair($"{d.Quantity} x {Money(d.Value)}", Money(d.Total));
        }

        b.Blank();
        b.Blank();
        b.Center("______________________________");
        b.Center(T("ticket.firma"));
        return b.Build(openDrawer: false);
    }

    private static string PaymentName(string method) => method switch
    {
        PaymentMethods.Cash => T("fmt.efectivo"),
        PaymentMethods.Card => T("fmt.tarjeta"),
        PaymentMethods.Transfer => T("fmt.transferencia"),
        PaymentMethods.Wallet => T("fmt.yape_plin"),
        PaymentMethods.Credit => T("fmt.credito"),
        _ => method,
    };

    private static DateTime Local(DateTimeOffset instant, CompanyInfo? company) =>
        RegionalSettings.ToCompanyTime(instant, company?.TimeZone).DateTime;

    /// <summary>Nombre del documento de identidad fiscal de la empresa según el país.</summary>
    public static string TaxIdLabel(string? countryCode) => (countryCode ?? "").ToUpperInvariant() switch
    {
        "PE" or "EC" => "RUC",
        "MX" => "RFC",
        "CO" or "BO" or "GT" or "SV" or "HN" or "NI" => "NIT",
        "CL" => "RUT",
        "AR" => "CUIT",
        "BR" => "CNPJ",
        "UY" => "RUT",
        "PY" => "RUC",
        "CR" => "Céd. jurídica",
        "DO" => "RNC",
        "ES" or "PT" => "NIF",
        _ => "Tax ID",
    };

    private sealed class Builder(int columns)
    {
        private readonly List<TicketLine> _lines = [];

        public void Header(CompanyInfo? company, string? siteName, string? siteAddress, string? text)
        {
            if (company is not null)
            {
                Center(company.TradeName ?? company.LegalName, bold: true, large: true);
                if (company.TradeName is not null)
                    Center(company.LegalName);
                if (company.TaxId is { Length: > 0 } taxId)
                    Center($"{TaxIdLabel(company.CountryCode)} {taxId}");
            }
            if (siteName is { Length: > 0 })
                Center(siteName);
            if (siteAddress is { Length: > 0 })
                Center(siteAddress);
            foreach (var line in Split(text))
                Center(line);
        }

        public void Footer(string? text)
        {
            var lines = Split(text).ToList();
            if (lines.Count == 0)
                return;
            Separator();
            foreach (var line in lines)
                Center(line);
        }

        public void Section(string title)
        {
            Separator();
            _lines.Add(new TicketLine(Fit(title.ToUpper(CultureInfo.CurrentCulture), columns), Bold: true));
        }

        public void Separator() => _lines.Add(new TicketLine(new string('-', columns)));

        public void Blank() => _lines.Add(new TicketLine(""));

        public void Center(string text, bool bold = false, bool large = false)
        {
            var width = large ? columns / 2 : columns;
            foreach (var part in WrapText(text, width))
                _lines.Add(new TicketLine(part, TicketAlign.Center, bold, large));
        }

        public void Wrap(string text)
        {
            foreach (var part in WrapText(text, columns))
                _lines.Add(new TicketLine(part));
        }

        /// <summary>Texto a la izquierda y valor a la derecha; si no entran juntos, el texto va en su propia línea.</summary>
        public void Pair(string left, string right, bool bold = false, bool large = false)
        {
            var width = large ? columns / 2 : columns;
            if (left.Length + right.Length + 1 > width)
            {
                foreach (var part in WrapText(left, width))
                    _lines.Add(new TicketLine(part, Bold: bold));
                _lines.Add(new TicketLine(Fit(right, width).PadLeft(width), Bold: bold, Large: large));
                return;
            }
            _lines.Add(new TicketLine(left + right.PadLeft(width - left.Length), Bold: bold, Large: large));
        }

        public IReadOnlyList<TicketLine> Lines => _lines;

        public TicketDocument Build(bool openDrawer, string? qr = null, IReadOnlyList<TicketLine>? afterQr = null) =>
            new(_lines, columns, openDrawer, Qr: qr, AfterQr: afterQr);

        private static IEnumerable<string> Split(string? text) =>
            (text ?? "").Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);

        private static string Fit(string text, int width) => text.Length <= width ? text : text[..width];

        /// <summary>Corta por palabras; una palabra más larga que el ancho se parte.</summary>
        internal static IEnumerable<string> WrapText(string text, int width)
        {
            var line = new StringBuilder();
            foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var w = word;
                while (w.Length > width)
                {
                    if (line.Length > 0)
                    {
                        yield return line.ToString();
                        line.Clear();
                    }
                    yield return w[..width];
                    w = w[width..];
                }
                if (line.Length > 0 && line.Length + 1 + w.Length > width)
                {
                    yield return line.ToString();
                    line.Clear();
                }
                if (line.Length > 0)
                    line.Append(' ');
                line.Append(w);
            }
            if (line.Length > 0)
                yield return line.ToString();
        }
    }
}

/// <summary>
/// Comandos ESC/POS (Epson y compatibles: la mayoría de impresoras térmicas de 58 y 80 mm). El texto va en la página
/// de códigos PC858 (latín con €), que tienen casi todas.
/// </summary>
public static class EscPos
{
    private static readonly Encoding Text = CreateEncoding();

    public static byte[] Encode(TicketDocument doc)
    {
        using var buffer = new MemoryStream();
        void Write(params byte[] bytes) => buffer.Write(bytes);

        Write(0x1B, 0x40);             // ESC @: reinicia la impresora
        Write(0x1B, 0x74, 19);         // ESC t 19: página de códigos PC858
        foreach (var line in doc.Lines)
        {
            Write(0x1B, 0x61, line.Align switch { TicketAlign.Center => (byte)1, TicketAlign.Right => (byte)2, _ => (byte)0 });
            Write(0x1B, 0x45, line.Bold ? (byte)1 : (byte)0);
            Write(0x1D, 0x21, line.Large ? (byte)0x11 : (byte)0x00);
            Write(Text.GetBytes(line.Text));
            Write(0x0A);
        }
        if (doc.Qr is { Length: > 0 } qr)
        {
            Write(0x1B, 0x45, 0, 0x1D, 0x21, 0, 0x1B, 0x61, 1, 0x0A);
            Write(QrCode(qr));
            Write(0x0A);
            foreach (var line in doc.AfterQr ?? [])
            {
                Write(0x1B, 0x61, line.Align switch { TicketAlign.Center => (byte)1, TicketAlign.Right => (byte)2, _ => (byte)0 });
                Write(Text.GetBytes(line.Text));
                Write(0x0A);
            }
        }
        Write(0x1B, 0x45, 0, 0x1D, 0x21, 0, 0x1B, 0x61, 0);
        Write(0x1B, 0x64, 4);          // ESC d 4: avanza el papel para que el corte no se coma el final
        if (doc.Cut)
            Write(0x1D, 0x56, 66, 0);  // GS V B 0: corte parcial
        if (doc.OpenDrawer)
            Write(DrawerPulse);
        return buffer.ToArray();
    }

    /// <summary>
    /// Código QR modelo 2 con los comandos <c>GS ( k</c>: modelo, tamaño del módulo (6 puntos), corrección M, guardar los
    /// datos e imprimir.
    /// </summary>
    public static byte[] QrCode(string data)
    {
        var bytes = Encoding.ASCII.GetBytes(data);
        var length = bytes.Length + 3;
        byte pL = (byte)(length % 256), pH = (byte)(length / 256);
        return
        [
            0x1D, 0x28, 0x6B, 4, 0, 0x31, 0x41, 0x32, 0x00,   // modelo 2
            0x1D, 0x28, 0x6B, 3, 0, 0x31, 0x43, 0x06,         // módulo de 6 puntos
            0x1D, 0x28, 0x6B, 3, 0, 0x31, 0x45, 0x31,         // corrección de errores M
            0x1D, 0x28, 0x6B, pL, pH, 0x31, 0x50, 0x30, .. bytes,
            0x1D, 0x28, 0x6B, 3, 0, 0x31, 0x51, 0x30,         // imprimir
        ];
    }

    /// <summary>ESC p 0 25 250: pulso al pin 2 del conector del cajón.</summary>
    public static byte[] DrawerPulse => [0x1B, 0x70, 0x00, 0x19, 0xFA];

    private static Encoding CreateEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            return Encoding.GetEncoding(858, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Encoding.ASCII;
        }
    }
}
