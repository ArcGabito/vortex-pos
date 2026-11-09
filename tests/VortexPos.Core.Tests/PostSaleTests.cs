using VortexPos.Core.Domain;
using VortexPos.Core.Integration;

namespace VortexPos.Core.Tests;

/// <summary>Fase 4a: cambios, reembolso al medio original, política de devoluciones, historial del cliente y depósitos.</summary>
public sealed class PostSaleTests : IAsyncLifetime
{
    private readonly TestWorkspace _t = new();
    private Guid _session;

    public async Task InitializeAsync()
    {
        await _t.InitializeAsync();
        _session = await _t.OpenCashAsync(100);
    }

    public Task DisposeAsync() => _t.DisposeAsync();

    private Task<SaleReceipt> SellAsync(decimal coffee, string method, decimal amount, Guid? customer = null) =>
        _t.Pos.Sales.CheckoutAsync(new SaleDraft([new CartLine(_t.Coffee, coffee)], [new PaymentDraft(method, amount)], customer));

    private async Task<decimal> ExpectedCashAsync() => (await _t.Pos.Cash.GetSummaryAsync(_session)).ExpectedCash;

    [Fact]
    public async Task La_devolucion_vuelve_al_medio_con_que_se_pago_y_solo_el_efectivo_sale_del_cajon()
    {
        var card = await SellAsync(2, PaymentMethods.Card, 37m);
        var cash = await SellAsync(1, PaymentMethods.Cash, 20m);
        Assert.Equal(118.50m, await ExpectedCashAsync());

        _t.Context.Role = PosPermissions.Cashier;
        await _t.Pos.Sales.ReturnAsync(card.SaleId, [new ReturnLine(1, 1)], "Talla");
        Assert.Equal(118.50m, await ExpectedCashAsync());   // a la tarjeta: el cajón no cambia
        await _t.Pos.Sales.ReturnAsync(cash.SaleId, [new ReturnLine(1, 1)], null);
        Assert.Equal(100m, await ExpectedCashAsync());

        var returns = (await _t.OutboxAsync()).Where(e => e.EventType == SuiteEvents.SaleReturned).Select(e => e.Payload!.Value).ToList();
        Assert.Equal(["card", "cash"], returns.Select(r => r.GetProperty("refundMethod").GetString()));
    }

    [Fact]
    public async Task Pasado_el_plazo_o_sin_ticket_solo_devuelve_un_supervisor()
    {
        var sale = await SellAsync(1, PaymentMethods.Cash, 18.50m);
        _t.Clock.Advance(TimeSpan.FromDays(31));
        _t.Context.Role = PosPermissions.Cashier;
        var ex = await Assert.ThrowsAsync<PosPermissionException>(() => _t.Pos.Sales.ReturnAsync(sale.SaleId, [new ReturnLine(1, 1)], null));
        Assert.Contains("30", ex.Message);
        await Assert.ThrowsAsync<PosPermissionException>(() => _t.Pos.Sales.ReturnWithoutReceiptAsync([new CartLine(_t.Coffee, 1)], "Regalo"));

        _t.Context.Role = PosPermissions.Supervisor;
        await _t.Pos.Sales.ReturnAsync(sale.SaleId, [new ReturnLine(1, 1)], null);
        var number = await _t.Pos.Sales.ReturnWithoutReceiptAsync([new CartLine(_t.Coffee, 2)], "Regalo sin ticket");
        var noReceipt = (await _t.OutboxAsync()).Last(e => e.EventType == SuiteEvents.SaleReturned).Payload!.Value;
        Assert.Equal((Guid.Empty, 37m, number), (noReceipt.GetProperty("saleId").GetGuid(), noReceipt.GetProperty("total").GetDecimal(),
            noReceipt.GetProperty("number").GetString()));
    }

    [Fact]
    public async Task Un_cambio_devuelve_y_vende_en_una_operacion_y_la_diferencia_se_paga_o_se_devuelve()
    {
        var sale = await SellAsync(2, PaymentMethods.Cash, 40m, _t.Customer);   // 37.00
        var before = await ExpectedCashAsync();

        // Cambia 1 café (18.50) por 2 cafés y 4 panes (39.00): paga 20.50 con tarjeta.
        var up = await _t.Pos.Sales.ExchangeAsync(sale.SaleId, [new ReturnLine(1, 1)],
            new SaleDraft([new CartLine(_t.Coffee, 2), new CartLine(_t.Bread, 4)], [new PaymentDraft(PaymentMethods.Card, 20.50m)]), "Pidió más");
        Assert.Equal((18.50m, 0m, 39m), (up.Credit, up.Refund, up.Sale.Total));
        Assert.Equal(before, await ExpectedCashAsync());

        // Cambia el otro café por un pan (0.50): se le devuelven 18.00 en efectivo (se pagó en efectivo).
        var down = await _t.Pos.Sales.ExchangeAsync(sale.SaleId, [new ReturnLine(1, 1)], new SaleDraft([new CartLine(_t.Bread, 1)], []), null);
        Assert.Equal((0.50m, 18m), (down.Credit, down.Refund));
        Assert.Equal(before - 18m, await ExpectedCashAsync());

        var outbox = await _t.OutboxAsync();
        var newSale = outbox.Single(e => e.Id == up.Sale.SaleId).Payload!.Value;
        Assert.Equal(_t.Customer, newSale.GetProperty("customerId").GetGuid());
        Assert.Equal(["exchange", "card"], newSale.GetProperty("payments").EnumerateArray().Select(p => p.GetProperty("method").GetString()));
        var ret = outbox.First(e => e.EventType == SuiteEvents.SaleReturned).Payload!.Value;
        Assert.Equal(("exchange", up.Sale.SaleId), (ret.GetProperty("refundMethod").GetString(), ret.GetProperty("exchangeSaleId").GetGuid()));
        await Assert.ThrowsAsync<PosValidationException>(() =>
            _t.Pos.Sales.ExchangeAsync(sale.SaleId, [new ReturnLine(1, 1)], new SaleDraft([new CartLine(_t.Bread, 1)], []), null));   // ya no queda
    }

    [Fact]
    public async Task El_historial_del_cliente_y_el_deposito_de_caja()
    {
        var first = await SellAsync(2, PaymentMethods.Cash, 37m, _t.Customer);
        _t.Clock.Advance(TimeSpan.FromDays(3));
        await SellAsync(1, PaymentMethods.Card, 18.50m, _t.Customer);
        await _t.Pos.Sales.ReturnAsync(first.SaleId, [new ReturnLine(1, 1)], null);
        var history = await _t.Pos.Sales.GetCustomerHistoryAsync(_t.Customer);
        Assert.Equal((2, 55.50m, 18.50m, 2), (history.Purchases, history.Total, history.Returned, history.Recent.Count));
        Assert.Equal((_t.Coffee, 2m), (history.TopItems.Single().ItemId, history.TopItems.Single().Quantity));

        var expected = await ExpectedCashAsync();
        await _t.Pos.Cash.AddMovementAsync(CashMovementKinds.Deposit, 100m, "Depósito del día", "OP-9912", "BCP");
        Assert.Equal(expected - 100m, await ExpectedCashAsync());
        var deposit = (await _t.OutboxAsync()).Single(e => e.EventType == SuiteEvents.CashDeposited).Payload!.Value;
        Assert.Equal((100m, "OP-9912", "PEN"), (deposit.GetProperty("amount").GetDecimal(), deposit.GetProperty("reference").GetString(),
            deposit.GetProperty("currencyCode").GetString()));
    }
}
