using VortexApi.Client.Sync;
using VortexPos.Core.Domain;
using VortexPos.Core.Integration;
using VortexPos.Core.Services;

namespace VortexPos.Core.Tests;

public sealed class SalesTests : IAsyncLifetime
{
    private readonly TestWorkspace _t = new();

    public Task InitializeAsync() => _t.InitializeAsync();
    public Task DisposeAsync() => _t.DisposeAsync();

    [Fact]
    public async Task Sin_caja_abierta_no_se_vende()
    {
        var ex = await Assert.ThrowsAsync<PosValidationException>(() => _t.Pos.Sales.CheckoutAsync(
            new SaleDraft([new CartLine(_t.Coffee, 1)], [new PaymentDraft(PaymentMethods.Cash, 20)])));
        Assert.Contains("Abre la caja", ex.Message);
    }

    [Fact]
    public async Task Venta_en_efectivo_desglosa_el_igv_da_vuelto_y_publica_sale_completed()
    {
        await _t.OpenCashAsync();
        // 2 cafés (37.00 con IGV) + 4 panes exonerados (2.00), 1.00 de descuento en el café.
        var receipt = await _t.Pos.Sales.CheckoutAsync(new SaleDraft(
            [new CartLine(_t.Coffee, 2, Discount: 1), new CartLine(_t.Bread, 4)],
            [new PaymentDraft(PaymentMethods.Cash, 50)], _t.Customer));

        Assert.Equal(38.00m, receipt.Total);
        Assert.Equal(12.00m, receipt.Change);
        Assert.Equal("V-TEST-000001", receipt.Number);

        var sale = (await _t.Pos.Sales.GetSaleAsync(receipt.SaleId))!;
        Assert.Equal(30.51m + 2.00m, sale.Subtotal);   // 36.00 / 1.18 = 30.51 + exonerado
        Assert.Equal(5.49m, sale.Tax);
        Assert.Equal(1m, sale.Discount);
        Assert.Equal(50m, sale.CashReceived);
        Assert.Equal("Bodega Don Pepe", sale.CustomerName);

        var payments = await _t.Pos.Sales.GetPaymentsAsync(receipt.SaleId);
        Assert.Equal([new PaymentRow(PaymentMethods.Cash, 38.00m, null)], payments);

        var entry = Assert.Single(await _t.OutboxAsync());
        Assert.Equal(SuiteEvents.SaleCompleted, entry.EventType);
        Assert.Equal(receipt.SaleId, entry.Id);
        var data = entry.Payload!.Value;
        Assert.Equal(_t.Store, data.GetProperty("siteId").GetGuid());
        Assert.Equal(38.00m, data.GetProperty("total").GetDecimal());
        Assert.Equal(2, data.GetProperty("lines").GetArrayLength());
        Assert.Equal(38.00m, data.GetProperty("payments")[0].GetProperty("amount").GetDecimal());
    }

    [Fact]
    public async Task Pago_mixto_aplica_tarjeta_y_completa_con_efectivo()
    {
        await _t.OpenCashAsync();
        var receipt = await _t.Pos.Sales.CheckoutAsync(new SaleDraft([new CartLine(_t.Coffee, 3)],
            [new PaymentDraft(PaymentMethods.Card, 40, "VISA 1234"), new PaymentDraft(PaymentMethods.Cash, 20)]));

        Assert.Equal(55.50m, receipt.Total);
        Assert.Equal(4.50m, receipt.Change);
        var payments = await _t.Pos.Sales.GetPaymentsAsync(receipt.SaleId);
        Assert.Equal([new PaymentRow(PaymentMethods.Cash, 15.50m, null), new PaymentRow(PaymentMethods.Card, 40m, "VISA 1234")], payments);
    }

    [Theory]
    [InlineData(PaymentMethods.Cash, 10, "no alcanza")]
    [InlineData(PaymentMethods.Card, 25, "no pueden superar")]
    [InlineData(PaymentMethods.Card, 10, "Falta cobrar")]
    [InlineData(PaymentMethods.Credit, 18.50, "elige el cliente")]
    public async Task Pagos_invalidos_se_rechazan_con_un_mensaje_claro(string method, decimal amount, string message)
    {
        await _t.OpenCashAsync();
        var ex = await Assert.ThrowsAsync<PosValidationException>(() => _t.Pos.Sales.CheckoutAsync(
            new SaleDraft([new CartLine(_t.Coffee, 1)], [new PaymentDraft(method, amount)])));
        Assert.Contains(message, ex.Message);
        Assert.Empty(await _t.OutboxAsync());
    }

    [Fact]
    public async Task Anular_publica_un_evento_determinista_y_no_se_anula_dos_veces()
    {
        await _t.OpenCashAsync();
        var receipt = await _t.Pos.Sales.CheckoutAsync(new SaleDraft([new CartLine(_t.Coffee, 1)], [new PaymentDraft(PaymentMethods.Cash, 18.50m)]));

        await _t.Pos.Sales.VoidAsync(receipt.SaleId, "Error de digitación");
        await Assert.ThrowsAsync<PosValidationException>(() => _t.Pos.Sales.VoidAsync(receipt.SaleId, "Otra vez"));

        var voided = (await _t.OutboxAsync()).Single(e => e.EventType == SuiteEvents.SaleVoided);
        Assert.Equal(DeterministicId.From("sale_void", receipt.SaleId), voided.Id);
        Assert.Equal(SaleStatuses.Voided, (await _t.Pos.Sales.GetSaleAsync(receipt.SaleId))!.Status);

        var summary = await _t.Pos.Sales.GetDashboardAsync();
        Assert.Equal(0, summary.TicketsToday);
        Assert.Equal(1, summary.Voided7d);
    }

    [Fact]
    public async Task Devolucion_parcial_reembolsa_en_efectivo_y_ajusta_el_arqueo()
    {
        var session = await _t.OpenCashAsync(100);
        var receipt = await _t.Pos.Sales.CheckoutAsync(new SaleDraft([new CartLine(_t.Coffee, 3)], [new PaymentDraft(PaymentMethods.Cash, 60)]));

        var number = await _t.Pos.Sales.ReturnAsync(receipt.SaleId, [new ReturnLine(1, 1)], "Producto dañado");
        Assert.Equal("DEV-TEST-000001", number);
        await Assert.ThrowsAsync<PosValidationException>(() => _t.Pos.Sales.ReturnAsync(receipt.SaleId, [new ReturnLine(1, 3)], null));
        await Assert.ThrowsAsync<PosValidationException>(() => _t.Pos.Sales.VoidAsync(receipt.SaleId, "Con devolución"));

        var returned = (await _t.OutboxAsync()).Single(e => e.EventType == SuiteEvents.SaleReturned).Payload!.Value;
        Assert.Equal(18.50m, returned.GetProperty("total").GetDecimal());
        Assert.Equal(1m, returned.GetProperty("lines")[0].GetProperty("quantity").GetDecimal());

        var cash = await _t.Pos.Cash.GetSummaryAsync(session);
        Assert.Equal(100m + 55.50m - 18.50m, cash.ExpectedCash);

        var difference = await _t.Pos.Cash.CloseAsync(session, 135m, "Faltaron 2 soles");
        Assert.Equal(-2m, difference);
        Assert.Null(await _t.Pos.Cash.GetOpenAsync());
    }

    [Fact]
    public async Task Panel_y_lo_mas_vendido_descuentan_lo_devuelto()
    {
        await _t.OpenCashAsync();
        var a = await _t.Pos.Sales.CheckoutAsync(new SaleDraft([new CartLine(_t.Coffee, 2)], [new PaymentDraft(PaymentMethods.Wallet, 37)]));
        await _t.Pos.Sales.CheckoutAsync(new SaleDraft([new CartLine(_t.Bread, 10)], [new PaymentDraft(PaymentMethods.Cash, 5)]));
        await _t.Pos.Sales.ReturnAsync(a.SaleId, [new ReturnLine(1, 1)], null);

        var summary = await _t.Pos.Sales.GetDashboardAsync();
        Assert.Equal(42m, summary.SalesToday);
        Assert.Equal(2, summary.TicketsToday);
        Assert.Equal(21m, summary.AverageTicket);
        Assert.Equal(37m, summary.ByMethodToday[PaymentMethods.Wallet]);

        var top = await _t.Pos.Sales.GetTopItemsAsync();
        Assert.Equal([(_t.Coffee, 1m, 18.50m), (_t.Bread, 10m, 5m)], top.Select(i => (i.ItemId, i.Quantity, i.Total)));
    }

    [Fact]
    public void Totales_del_carrito_coinciden_con_los_de_la_venta()
    {
        var totals = SalesService.Totals([(SalesService.LineTotal(2, 18.50m, 1, "PEN"), 1m, "igv"), (2m, 0m, "exonerado")],
            VortexApi.Suite.Taxes.TaxCatalog.For("PE"), "PEN");
        Assert.Equal(new SaleTotals(32.51m, 5.49m, 1m, 38m), totals);
    }
}
