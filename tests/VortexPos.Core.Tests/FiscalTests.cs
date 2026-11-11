using System.Globalization;
using System.Text.Json;
using VortexApi.Client;
using VortexPos.Core.Domain;
using VortexPos.Core.Integration;
using VortexPos.Core.Printing;

namespace VortexPos.Core.Tests;

/// <summary>Fase 5a: boleta y factura electrónica en caja, contingencia sin red, notas de crédito y series por caja.</summary>
public sealed class FiscalTests : IAsyncLifetime
{
    private const string CompanyRuc = "20123456786";
    private const string CustomerRuc = "20111222330";

    private readonly TestWorkspace _t = new("CAJA1");
    private readonly FakeGateway _hub = new();
    private readonly Guid _company = Guid.Parse("33333333-0000-0000-0000-000000000010");
    private readonly Guid _person = Guid.Parse("33333333-0000-0000-0000-000000000011");

    public async Task InitializeAsync()
    {
        await _t.InitializeAsync();
        await _t.Pos.Master.ApplyCompanyAsync(TestWorkspace.Company("PE", "PEN", null, CompanyRuc, version: 2));
        await _t.Pos.Master.ApplyPartyAsync(TestWorkspace.Party(_company, "Restaurante El Sol SAC", CustomerRuc));
        await _t.Pos.Master.ApplyPartyAsync(TestWorkspace.Party(_person, "Ana Torres", "45678912", "DNI"));
        _t.Context.Features = ["facturacion_electronica"];
        await _t.Pos.Fiscal.SaveSeriesAsync(new FiscalSeries("b001", "F001"));
        await _t.OpenCashAsync();
    }

    public Task DisposeAsync() => _t.DisposeAsync();

    private Task<SaleReceipt> SellAsync(decimal coffee = 2, Guid? customer = null, string? type = null, decimal bread = 0) =>
        _t.Pos.Sales.CheckoutAsync(new SaleDraft(
            bread > 0 ? [new CartLine(_t.Coffee, coffee), new CartLine(_t.Bread, bread)] : [new CartLine(_t.Coffee, coffee)],
            [new PaymentDraft(PaymentMethods.Cash, 1000)], customer, FiscalType: type));

    private async Task<string> TicketAsync(Guid saleId) =>
        TicketBuilder.ForSale((await _t.Pos.Sales.GetTicketDataAsync(saleId))!, StoreSettings.Default, CultureInfo.GetCultureInfo("es-PE")).ToText();

    [Fact]
    public async Task La_boleta_se_numera_en_la_caja_sale_con_su_QR_y_viaja_en_la_venta()
    {
        var first = await SellAsync();
        var second = await SellAsync(1, _person, bread: 4);
        Assert.Equal(("B001-1", "B001-2"), (first.FiscalNumber, second.FiscalNumber));

        var events = (await _t.OutboxAsync()).Where(e => e.EventType == SuiteEvents.SaleCompleted).Select(e => e.Payload!.Value).ToList();
        var fiscal = events[1].GetProperty("fiscal");
        Assert.Equal(("receipt", "B001", 2L), (fiscal.GetProperty("type").GetString(), fiscal.GetProperty("series").GetString(), fiscal.GetProperty("number").GetInt64()));

        var ticket = await TicketAsync(second.SaleId);
        Assert.Contains("BOLETA DE VENTA ELECTRÓNICA", ticket);
        Assert.Contains("B001-2", ticket);
        Assert.Contains("45678912", ticket);
        // 18.50 con IGV → 15.68 + 2.82; el pan exonerado 2.00; total = lo cobrado.
        Assert.Contains("Op. gravada", ticket);
        Assert.Contains("Op. exonerada", ticket);
        Assert.Contains($"[QR] {CompanyRuc}|03|B001|2|2.82|20.50|2026-11-02|1|45678912||", ticket);
        Assert.Contains("contingencia", ticket);

        var row = (await _t.Pos.Sales.GetSaleAsync(second.SaleId))!;
        Assert.Equal(("receipt", "B001-2", FiscalStatuses.Pending), (row.FiscalKind, row.FiscalNumber, row.FiscalStatus));
        Assert.Single((await _t.Pos.Sales.GetSalesAsync(new SaleQuery(Search: "B001-2"))).Items);
    }

    [Fact]
    public async Task La_factura_pide_cliente_con_RUC_y_la_boleta_grande_pide_documento()
    {
        var invoice = await SellAsync(customer: _company, type: FiscalKinds.Invoice);
        Assert.Equal("F001-1", invoice.FiscalNumber);
        Assert.Contains("FACTURA ELECTRÓNICA", await TicketAsync(invoice.SaleId));

        await Assert.ThrowsAsync<PosValidationException>(() => SellAsync(customer: _person, type: FiscalKinds.Invoice));
        await Assert.ThrowsAsync<PosValidationException>(() => SellAsync(type: FiscalKinds.Invoice));
        // El cliente de prueba de siempre tiene un RUC con dígito verificador inválido.
        await Assert.ThrowsAsync<PosValidationException>(() => SellAsync(customer: _t.Customer));
        await Assert.ThrowsAsync<PosValidationException>(() => SellAsync(40));       // S/ 740 sin documento
        Assert.Equal("B001-1", (await SellAsync(40, _person)).FiscalNumber);
        // Nada de lo rechazado gastó número.
        Assert.Equal("B001-2", (await SellAsync()).FiscalNumber);
    }

    [Fact]
    public async Task Sin_el_modulo_o_sin_series_la_caja_vende_como_antes()
    {
        _t.Context.Features = [];
        var sale = await SellAsync();
        Assert.Null(sale.FiscalNumber);
        Assert.DoesNotContain("ELECTRÓNICA", await TicketAsync(sale.SaleId));
        await Assert.ThrowsAsync<PosValidationException>(() => SellAsync(customer: _company, type: FiscalKinds.Invoice));
        Assert.False((await _t.Pos.Fiscal.GetSetupAsync()).Enabled);
    }

    [Fact]
    public async Task Sin_red_queda_en_contingencia_y_al_volver_se_envia_y_llega_la_respuesta()
    {
        var sale = await SellAsync(customer: _company, type: FiscalKinds.Invoice);
        _hub.Offline = true;
        Assert.Equal(0, await _t.Pos.Fiscal.SendPendingAsync(_hub));
        Assert.Equal(FiscalStatuses.Pending, (await _t.Pos.Sales.GetSaleAsync(sale.SaleId))!.FiscalStatus);

        _hub.Offline = false;
        Assert.Equal(1, await _t.Pos.Fiscal.SendPendingAsync(_hub));
        var request = Assert.Single(_hub.Issued);
        Assert.Equal((sale.SaleId, "invoice", "F001", 1L, "6", CustomerRuc), (request.SourceId, request.Type, request.Series, request.Number,
            request.Customer.DocType, request.Customer.DocNumber));
        Assert.Equal(37m, request.Lines.Sum(l => l.Total));
        Assert.Equal(("NIU", "taxed", 0.18m), (request.Lines[0].UnitCode, request.Lines[0].TaxCategory, request.Lines[0].TaxRate));
        Assert.Equal(0, await _t.Pos.Fiscal.SendPendingAsync(_hub));   // ya se envió

        await _t.Pos.SyncHandlers.HandleFiscalResponseAsync(TestWorkspace.Event(VortexHubEvents.FiscalAccepted,
            new VortexFiscalResponded(sale.SaleId, "POS", "invoice", "F001", 1, VortexFiscalStatus.Accepted, "aGFzaA==", null, "0", "Aceptada", null),
            source: "HUB"));
        Assert.Equal(FiscalStatuses.Accepted, (await _t.Pos.Sales.GetSaleAsync(sale.SaleId))!.FiscalStatus);
        var ticket = await TicketAsync(sale.SaleId);
        Assert.Contains("Resumen: aGFzaA==", ticket);
        Assert.DoesNotContain("contingencia", ticket);
    }

    [Fact]
    public async Task Anular_o_devolver_emite_la_nota_de_credito_de_su_comprobante()
    {
        var voided = await SellAsync();
        var returned = await SellAsync(3);

        await _t.Pos.Sales.VoidAsync(voided.SaleId, "Error de digitación");
        await _t.Pos.Sales.ReturnAsync(returned.SaleId, [new ReturnLine(1, 1)], "No le gustó");
        await _t.Pos.Fiscal.SendPendingAsync(_hub);

        var notes = _hub.Issued.Where(r => r.Type == "credit_note").ToList();
        Assert.Equal(2, notes.Count);
        Assert.Equal(("B001", 1L, "01", "B001", 1L, 37m), (notes[0].Series, notes[0].Number, notes[0].Reference!.ReasonCode,
            notes[0].Reference!.Series, notes[0].Reference!.Number, notes[0].Lines.Sum(l => l.Total)));
        Assert.Equal((2L, "07", 2L, 18.50m), (notes[1].Number, notes[1].Reference!.ReasonCode, notes[1].Reference!.Number, notes[1].Lines.Sum(l => l.Total)));

        var outbox = await _t.OutboxAsync();
        var voidEvent = outbox.Single(e => e.EventType == SuiteEvents.SaleVoided).Payload!.Value;
        Assert.Equal("credit_note", voidEvent.GetProperty("fiscal").GetProperty("type").GetString());
        var returnEvent = outbox.Single(e => e.EventType == SuiteEvents.SaleReturned).Payload!.Value;
        Assert.Equal(2, returnEvent.GetProperty("fiscal").GetProperty("number").GetInt64());
        Assert.Contains("Nota de crédito", await TicketAsync(returned.SaleId));
        Assert.Equal(["receipt", "credit_note"], (await _t.Pos.Fiscal.GetForSaleAsync(returned.SaleId)).Select(d => d.Kind));
    }

    [Fact]
    public async Task Cada_caja_usa_sus_series_y_una_caja_reinstalada_sigue_la_numeracion()
    {
        var other = new PosRecordUpserted(PosRecordKinds.FiscalSeries, "CAJA2",
            JsonSerializer.SerializeToElement(new FiscalSeries("B002", "F002"), VortexJson.Options), _t.Clock.GetUtcNow(), "Rosa");
        await _t.Pos.SyncHandlers.HandlePosRecordAsync(TestWorkspace.Event(SuiteEvents.PosRecordUpserted, other));
        var ex = await Assert.ThrowsAsync<PosValidationException>(() => _t.Pos.Fiscal.SaveSeriesAsync(new FiscalSeries("B002", "F001")));
        Assert.Contains("CAJA2", ex.Message);
        await Assert.ThrowsAsync<PosValidationException>(() => _t.Pos.Fiscal.SaveSeriesAsync(new FiscalSeries("F003", "F001")));

        // La venta de otra caja llega con su comprobante.
        var remoteSale = Guid.NewGuid();
        await _t.Pos.SyncHandlers.HandleSaleCompletedAsync(TestWorkspace.Event(SuiteEvents.SaleCompleted, new SaleCompleted(
            remoteSale, "V-CAJA2-000001", _t.Store, null, "PEN", 15.68m, 2.82m, 18.50m,
            [new SaleLine(_t.Coffee, "CAFE-250", 1, 18.50m, 0, 18.50m)], [new SalePayment("cash", 18.50m)], Fiscal: new SaleFiscal("receipt", "B002", 7))));
        Assert.Equal("B002-7", (await _t.Pos.Sales.GetSaleAsync(remoteSale))!.FiscalNumber);

        // Esta caja ya había emitido hasta la B001-41 antes de reinstalarse: su venta vuelve por la sincronización.
        await _t.Pos.SyncHandlers.HandleSaleCompletedAsync(TestWorkspace.Event(SuiteEvents.SaleCompleted, new SaleCompleted(
            Guid.NewGuid(), "V-CAJA1-000041", _t.Store, null, "PEN", 15.68m, 2.82m, 18.50m,
            [new SaleLine(_t.Coffee, "CAFE-250", 1, 18.50m, 0, 18.50m)], [new SalePayment("cash", 18.50m)], Fiscal: new SaleFiscal("receipt", "B001", 41))));
        Assert.Equal("B001-42", (await SellAsync()).FiscalNumber);
    }

    [Fact]
    public async Task Un_comprobante_que_el_hub_no_acepta_queda_con_error_y_se_reintenta()
    {
        var sale = await SellAsync();
        _hub.Reject = new VortexApiException(409, "fiscal_number_in_use", "El número B001-1 ya se usó.");
        await _t.Pos.Fiscal.SendPendingAsync(_hub);
        var doc = Assert.Single(await _t.Pos.Fiscal.GetOutstandingAsync());
        Assert.Equal((FiscalStatuses.Error, "El número B001-1 ya se usó."), (doc.Status, doc.LastError));

        _hub.Reject = null;
        await _t.Pos.Fiscal.RetryAsync(_hub, doc.Id);
        Assert.Single(_hub.Issued);
        Assert.Equal(FiscalStatuses.Pending, (await _t.Pos.Sales.GetSaleAsync(sale.SaleId))!.FiscalStatus);
        Assert.DoesNotContain(await _t.Pos.Fiscal.GetOutstandingAsync(), d => d.Status != FiscalStatuses.Pending);
    }

    [Fact]
    public void El_QR_se_imprime_con_los_comandos_ESC_POS()
    {
        var doc = new TicketDocument([new TicketLine("x")], 48, Qr: "20123456786|03|B001|1|", AfterQr: [new TicketLine("Representación")]);
        var bytes = EscPos.Encode(doc);
        var qr = EscPos.QrCode("20123456786|03|B001|1|");
        Assert.True(bytes.AsSpan().IndexOf(qr) > 0);
        Assert.Equal([0x1D, 0x28, 0x6B, 25, 0, 0x31, 0x50, 0x30], qr[25..33]);
        Assert.EndsWith("Representación", doc.ToText());
    }

    private sealed class FakeGateway : IFiscalGateway
    {
        public List<VortexFiscalRequest> Issued { get; } = [];
        public bool Offline { get; set; }
        public VortexApiException? Reject { get; set; }

        public Task<VortexFiscalDocument> IssueAsync(VortexFiscalRequest request, CancellationToken ct = default)
        {
            if (Offline)
                throw new HttpRequestException("Sin conexión");
            if (Reject is { } ex)
                throw ex;
            Issued.Add(request);
            return Task.FromResult(new VortexFiscalDocument(Guid.NewGuid(), request.SourceId, "POS", request.Type, request.Series, request.Number,
                request.IssueDate, request.Currency, request.Lines.Sum(l => l.Total ?? 0), VortexFiscalStatus.Pending, 0, null, null, null, null, null,
                null, DateTimeOffset.UtcNow, null));
        }

        public Task<VortexFiscalDocument> RetryAsync(Guid hubId, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
