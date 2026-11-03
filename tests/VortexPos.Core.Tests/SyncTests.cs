using VortexPos.Core.Domain;
using VortexPos.Core.Integration;

namespace VortexPos.Core.Tests;

public sealed class SyncTests : IAsyncLifetime
{
    private readonly TestWorkspace _t = new();

    public Task InitializeAsync() => _t.InitializeAsync();
    public Task DisposeAsync() => _t.DisposeAsync();

    [Fact]
    public async Task Stock_del_WMS_se_guarda_como_saldo_absoluto()
    {
        await _t.Pos.SyncHandlers.HandleStockAdjustedAsync(TestWorkspace.Event(SuiteEvents.StockAdjusted,
            new { siteId = _t.Store, itemId = _t.Coffee, quantityDelta = 10m, quantityOnHand = 10m, reason = "receipt" }, "WMS"));
        await _t.Pos.SyncHandlers.HandleStockAdjustedAsync(TestWorkspace.Event(SuiteEvents.StockAdjusted,
            new { siteId = _t.Store, itemId = _t.Coffee, quantityDelta = -2m, quantityOnHand = 8m, reason = "sale" }, "WMS"));

        Assert.Equal(8m, (await _t.Pos.Master.GetItemAsync(_t.Coffee, _t.Store))!.OnHand);
        Assert.Null((await _t.Pos.Master.GetItemAsync(_t.Bread, _t.Store))!.OnHand);   // sin dato del WMS
    }

    [Fact]
    public async Task Venta_de_otra_caja_se_replica_una_sola_vez_y_la_propia_no_se_duplica()
    {
        await _t.OpenCashAsync();
        var own = await _t.Pos.Sales.CheckoutAsync(new SaleDraft([new CartLine(_t.Coffee, 1)], [new PaymentDraft(PaymentMethods.Cash, 20)]));
        var ownEvent = TestWorkspace.AsEvent((await _t.OutboxAsync()).Single());
        await _t.Pos.SyncHandlers.HandleSaleCompletedAsync(ownEvent);   // vuelve por IncludeOwnEvents

        var remoteId = Guid.NewGuid();
        var remote = TestWorkspace.Event(SuiteEvents.SaleCompleted, new
        {
            saleId = remoteId, number = "V-9AFE-000007", siteId = _t.Store, customerId = (Guid?)null, currencyCode = "PEN",
            subtotal = 15.68m, tax = 2.82m, total = 18.50m,
            lines = new[] { new { itemId = _t.Coffee, sku = "CAFE-250", quantity = 1m, unitPrice = 18.50m, discount = 0m, total = 18.50m } },
            payments = new[] { new { method = "card", amount = 18.50m } },
        });
        await _t.Pos.SyncHandlers.HandleSaleCompletedAsync(remote);
        await _t.Pos.SyncHandlers.HandleSaleCompletedAsync(remote);

        var sales = (await _t.Pos.Sales.GetSalesAsync(new SaleQuery())).Items;
        Assert.Equal(2, sales.Count);
        var replica = sales.Single(s => s.Id == remoteId);
        Assert.Equal("remote", replica.Origin);
        Assert.Equal("Café tostado 250 g", (await _t.Pos.Sales.GetLinesAsync(remoteId)).Single().Name);   // nombre desde el catálogo
        Assert.Equal("local", sales.Single(s => s.Id == own.SaleId).Origin);

        // Las ventas de otras cajas no entran al arqueo de esta.
        var session = (await _t.Pos.Cash.GetOpenAsync())!;
        Assert.Equal(1, (await _t.Pos.Cash.GetSummaryAsync(session.Id)).Sales);
    }

    [Fact]
    public async Task Anulacion_y_devolucion_de_otra_caja_se_aplican()
    {
        // Lo que vende una caja llega a la otra con sus mismos eventos.
        var other = new TestWorkspace("CAJ2");
        await other.InitializeAsync();
        try
        {
            await other.OpenCashAsync();
            var sold = await other.Pos.Sales.CheckoutAsync(new SaleDraft([new CartLine(other.Coffee, 2), new CartLine(other.Bread, 2)],
                [new PaymentDraft(PaymentMethods.Cash, 40)]));
            await other.Pos.Sales.ReturnAsync(sold.SaleId, [new ReturnLine(1, 1)], "Cambio de opinión");
            var voidable = await other.Pos.Sales.CheckoutAsync(new SaleDraft([new CartLine(other.Bread, 1)], [new PaymentDraft(PaymentMethods.Cash, 1)]));
            await other.Pos.Sales.VoidAsync(voidable.SaleId, "Duplicada");

            foreach (var entry in await other.OutboxAsync())
            {
                var e = TestWorkspace.AsEvent(entry);
                await (entry.EventType switch
                {
                    SuiteEvents.SaleCompleted => _t.Pos.SyncHandlers.HandleSaleCompletedAsync(e),
                    SuiteEvents.SaleReturned => _t.Pos.SyncHandlers.HandleSaleReturnedAsync(e),
                    SuiteEvents.SaleVoided => _t.Pos.SyncHandlers.HandleSaleVoidedAsync(e),
                    _ => Task.CompletedTask,
                });
            }

            var replica = (await _t.Pos.Sales.GetSaleAsync(sold.SaleId))!;
            Assert.Equal(18.50m, replica.Returned);
            Assert.Equal(1m, (await _t.Pos.Sales.GetLinesAsync(sold.SaleId))[0].ReturnedQty);
            Assert.Equal(SaleStatuses.Voided, (await _t.Pos.Sales.GetSaleAsync(voidable.SaleId))!.Status);
        }
        finally
        {
            await other.DisposeAsync();
        }
    }

    [Fact]
    public async Task Comprobante_del_ERP_queda_en_la_venta()
    {
        await _t.OpenCashAsync();
        var sale = await _t.Pos.Sales.CheckoutAsync(new SaleDraft([new CartLine(_t.Coffee, 1)], [new PaymentDraft(PaymentMethods.Cash, 18.50m)]));
        await _t.Pos.SyncHandlers.HandleInvoiceIssuedAsync(TestWorkspace.Event(SuiteEvents.InvoiceIssued,
            new { invoiceId = Guid.NewGuid(), number = "B001-000045", saleId = sale.SaleId, total = 18.50m, currencyCode = "PEN" }, "ERP"));

        Assert.Equal("B001-000045", (await _t.Pos.Sales.GetSaleAsync(sale.SaleId))!.InvoiceNumber);
    }

    [Fact]
    public async Task Eventos_de_otra_version_o_de_otro_sistema_se_ignoran_sin_trabar_el_feed()
    {
        await _t.Pos.SyncHandlers.HandleSaleCompletedAsync(TestWorkspace.Event(SuiteEvents.SaleCompleted,
            new { saleId = Guid.NewGuid(), siteId = _t.Store }, schemaVersion: 2));
        await _t.Pos.SyncHandlers.HandleSaleCompletedAsync(TestWorkspace.Event(SuiteEvents.SaleCompleted,
            new { saleId = Guid.NewGuid(), siteId = _t.Store, total = 1m }, source: "ERP"));
        await _t.Pos.SyncHandlers.HandleStockAdjustedAsync(TestWorkspace.Event(SuiteEvents.StockAdjusted, new { reason = "x" }, "WMS"));

        Assert.Equal(0, (await _t.Pos.Sales.GetSalesAsync(new SaleQuery())).Total);
    }
}
