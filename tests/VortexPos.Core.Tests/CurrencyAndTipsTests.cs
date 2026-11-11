using VortexApi.Client;
using VortexPos.Core.Domain;
using VortexPos.Core.Integration;

namespace VortexPos.Core.Tests;

/// <summary>Fase 5b: cobro en otra moneda con el tipo de cambio del hub y propinas.</summary>
public sealed class CurrencyAndTipsTests : IAsyncLifetime
{
    private readonly TestWorkspace _t = new();
    private Guid _session;

    public async Task InitializeAsync()
    {
        await _t.InitializeAsync();
        // Dólar: referencia 3.75, compra 3.70, venta 3.80 (del 1 de noviembre; la caja vende el 2).
        var id = Guid.NewGuid();
        await _t.Pos.Master.ApplyExchangeRateAsync(new VortexMasterChange<VortexExchangeRate>(1, id, VortexChangeOperation.Created, 1,
            DateTimeOffset.UtcNow, "HUB", new VortexExchangeRate(id, new DateOnly(2026, 11, 1), "USD", "PEN", 3.75m, 3.70m, 3.80m, "SBS",
                new VortexMasterMeta(1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null))));
        _session = await _t.OpenCashAsync(100);
    }

    public Task DisposeAsync() => _t.DisposeAsync();

    private async Task<System.Text.Json.JsonElement> LastSaleAsync() =>
        (await _t.OutboxAsync()).Last(e => e.EventType == SuiteEvents.SaleCompleted).Payload!.Value;

    [Fact]
    public async Task El_efectivo_en_dolares_se_convierte_al_tipo_de_compra_y_el_vuelto_sale_en_soles()
    {
        Assert.Equal([("USD", 3.70m)], await _t.Pos.Master.GetForeignCurrenciesAsync("PEN"));

        // 3 cafés = S/ 55.50; el cliente da USD 20 = S/ 74.00: vuelto S/ 18.50.
        var receipt = await _t.Pos.Sales.CheckoutAsync(new SaleDraft([new CartLine(_t.Coffee, 3)],
            [new PaymentDraft(PaymentMethods.Cash, 20, CurrencyCode: "usd")]));
        Assert.Equal((55.50m, 18.50m), (receipt.Total, receipt.Change));

        var payment = Assert.Single(await _t.Pos.Sales.GetPaymentsAsync(receipt.SaleId));
        Assert.Equal((55.50m, "USD", 20m, 3.70m), (payment.Amount, payment.CurrencyCode, payment.ForeignAmount!.Value, payment.ExchangeRate!.Value));
        var e = (await LastSaleAsync()).GetProperty("payments")[0];
        Assert.Equal(("USD", 20m), (e.GetProperty("currencyCode").GetString(), e.GetProperty("foreignAmount").GetDecimal()));

        // Mixto: USD 10 (S/ 37) + S/ 20 en efectivo para 3 cafés: vuelto S/ 1.50.
        var mixed = await _t.Pos.Sales.CheckoutAsync(new SaleDraft([new CartLine(_t.Coffee, 3)],
            [new PaymentDraft(PaymentMethods.Cash, 10, CurrencyCode: "USD"), new PaymentDraft(PaymentMethods.Cash, 20)]));
        Assert.Equal(1.50m, mixed.Change);

        // El cajón en soles: 100 + 18.50 (mixto) − 18.50 (vuelto de la primera); en dólares, 30 aparte.
        var summary = await _t.Pos.Cash.GetSummaryAsync(_session);
        Assert.Equal(100m, summary.ExpectedCash);
        Assert.Equal(30m, summary.ForeignCash!["USD"]);
        Assert.Equal(111m, summary.SalesTotal);
    }

    [Fact]
    public async Task Sin_tipo_de_cambio_o_con_un_medio_que_no_lo_admite_no_se_cobra()
    {
        await Assert.ThrowsAsync<PosValidationException>(() => _t.Pos.Sales.CheckoutAsync(new SaleDraft([new CartLine(_t.Coffee, 1)],
            [new PaymentDraft(PaymentMethods.Cash, 10, CurrencyCode: "EUR")])));
        await Assert.ThrowsAsync<PosValidationException>(() => _t.Pos.Sales.CheckoutAsync(new SaleDraft([new CartLine(_t.Coffee, 1)],
            [new PaymentDraft(PaymentMethods.Wallet, 10, CurrencyCode: "USD")])));
        // Con tarjeta en dólares se cobra lo justo convertido: USD 5 = S/ 18.50.
        var card = await _t.Pos.Sales.CheckoutAsync(new SaleDraft([new CartLine(_t.Coffee, 1)],
            [new PaymentDraft(PaymentMethods.Card, 5, "1234", CurrencyCode: "USD")]));
        Assert.Equal(0m, card.Change);
        await Assert.ThrowsAsync<PosValidationException>(() => _t.Pos.Sales.CheckoutAsync(new SaleDraft([new CartLine(_t.Coffee, 1)],
            [new PaymentDraft(PaymentMethods.Card, 6, CurrencyCode: "USD")])));   // pasa del total
    }

    [Fact]
    public async Task La_propina_se_cobra_aparte_del_total_y_del_comprobante()
    {
        // 2 cafés = S/ 37.00 + S/ 4 de propina para Ana; paga S/ 50: vuelto S/ 9.
        var receipt = await _t.Pos.Sales.CheckoutAsync(new SaleDraft([new CartLine(_t.Coffee, 2)],
            [new PaymentDraft(PaymentMethods.Cash, 50)], Tip: 4, TipTo: "Ana"));
        Assert.Equal((37m, 9m), (receipt.Total, receipt.Change));
        var sale = (await _t.Pos.Sales.GetSaleAsync(receipt.SaleId))!;
        Assert.Equal((4m, "Ana"), (sale.Tip, sale.TipTo));

        var e = await LastSaleAsync();
        Assert.Equal((37m, 4m, "Ana"), (e.GetProperty("total").GetDecimal(), e.GetProperty("tip").GetDecimal(), e.GetProperty("tipTo").GetString()));
        Assert.Equal(37m, e.GetProperty("payments").EnumerateArray().Sum(p => p.GetProperty("amount").GetDecimal()));   // el evento cuadra con el total

        // Con tarjeta y sin decir para quién: es del cajero.
        await _t.Pos.Sales.CheckoutAsync(new SaleDraft([new CartLine(_t.Coffee, 1)], [new PaymentDraft(PaymentMethods.Card, 20.50m)], Tip: 2));
        var summary = await _t.Pos.Cash.GetSummaryAsync(_session);
        Assert.Equal(6m, summary.Tips);
        Assert.Equal(4m, summary.TipsByPerson!["Ana"]);
        Assert.Equal(2m, summary.TipsByPerson!["Luis"]);
        Assert.Equal(141m, summary.ExpectedCash);   // 100 + 41 cobrados en efectivo (la propina está en el cajón)

        await Assert.ThrowsAsync<PosValidationException>(() => _t.Pos.Sales.CheckoutAsync(new SaleDraft([new CartLine(_t.Coffee, 1)],
            [new PaymentDraft(PaymentMethods.Card, 18.50m)], Tip: 1)));   // la propina también hay que cobrarla
    }
}
