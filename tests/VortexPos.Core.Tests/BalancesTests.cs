using VortexPos.Core.Domain;
using VortexPos.Core.Integration;
using VortexPos.Core.Services;

namespace VortexPos.Core.Tests;

/// <summary>Fase 4b: puntos, gift cards, cuenta corriente con límite y apartados.</summary>
public sealed class BalancesTests : IAsyncLifetime
{
    private readonly TestWorkspace _a = new("CAJ1");
    private readonly TestWorkspace _b = new("CAJ2");

    public async Task InitializeAsync()
    {
        await _a.InitializeAsync();
        await _b.InitializeAsync();
        await _a.OpenCashAsync(100);
        await _b.OpenCashAsync(100);
        await _a.Pos.Balances.SaveSettingsAsync(new LoyaltySettings(true, PointsPerUnit: 1, PointValue: 0.10m, MinRedeem: 10, OfflineGiftCardLimit: 20));
        await ReplicateAsync(_a, _b);
    }

    public async Task DisposeAsync()
    {
        await _a.DisposeAsync();
        await _b.DisposeAsync();
    }

    /// <summary>Lo que una caja publicó llega a la otra (saldos y configuración).</summary>
    private static async Task ReplicateAsync(TestWorkspace from, TestWorkspace to)
    {
        foreach (var entry in await from.OutboxAsync())
        {
            var e = TestWorkspace.AsEvent(entry);
            if (entry.EventType == SuiteEvents.BalanceMoved)
                await to.Pos.SyncHandlers.HandleBalanceMovedAsync(e);
            else if (entry.EventType == SuiteEvents.PosRecordUpserted)
                await to.Pos.SyncHandlers.HandlePosRecordAsync(e);
            else if (entry.EventType == SuiteEvents.SaleCompleted)
                await to.Pos.SyncHandlers.HandleSaleCompletedAsync(e);
            await from.Pos.SyncStore.RemoveAsync(entry.Id);
        }
    }

    private static Task<SaleReceipt> SellAsync(TestWorkspace t, decimal coffee, params PaymentDraft[] payments) =>
        t.Pos.Sales.CheckoutAsync(new SaleDraft([new CartLine(t.Coffee, coffee)], payments, t.Customer));

    [Fact]
    public async Task Los_puntos_ganados_en_una_tienda_se_canjean_en_otra_y_la_anulacion_los_devuelve()
    {
        var sale = await SellAsync(_a, 2, new PaymentDraft(PaymentMethods.Cash, 37m));   // 37 puntos
        await ReplicateAsync(_a, _b);
        Assert.Equal(37m, await _b.Pos.Balances.GetPointsAsync(_b.Customer));

        // En la otra tienda paga 3.00 con 30 puntos (y el resto en efectivo: gana 15 por eso).
        await SellAsync(_b, 1, new PaymentDraft(PaymentMethods.Points, 3m), new PaymentDraft(PaymentMethods.Cash, 15.50m));
        Assert.Equal(22m, await _b.Pos.Balances.GetPointsAsync(_b.Customer));
        await Assert.ThrowsAsync<PosValidationException>(() => SellAsync(_b, 1, new PaymentDraft(PaymentMethods.Points, 0.50m), new PaymentDraft(PaymentMethods.Cash, 18m)));   // menos del mínimo
        await ReplicateAsync(_b, _a);
        Assert.Equal(22m, await _a.Pos.Balances.GetPointsAsync(_a.Customer));

        await _a.Pos.Sales.VoidAsync(sale.SaleId, "Error de caja");
        Assert.Equal(-15m, await _a.Pos.Balances.GetPointsAsync(_a.Customer));   // se quitan los 37 ganados
    }

    [Fact]
    public async Task Una_gift_card_no_se_gasta_dos_veces_aunque_dos_cajas_esten_sin_red()
    {
        var code = await _a.Pos.Balances.IssueGiftCardAsync(50m, PaymentMethods.Cash);
        Assert.Equal(150m, (await _a.Pos.Cash.GetSummaryAsync((await _a.Pos.Cash.GetOpenAsync())!.Id)).ExpectedCash);
        await ReplicateAsync(_a, _b);

        // Las dos cajas sin red: cada una gasta como mucho 20 (el límite sin conexión), nunca más que el saldo entre las dos.
        _a.Context.IsOnline = false;
        _b.Context.IsOnline = false;
        await Assert.ThrowsAsync<PosValidationException>(() => SellAsync(_a, 2, new PaymentDraft(PaymentMethods.GiftCard, 37m, code)));
        await SellAsync(_a, 1, new PaymentDraft(PaymentMethods.GiftCard, 18.50m, code));
        await SellAsync(_b, 1, new PaymentDraft(PaymentMethods.GiftCard, 18.50m, code));

        // Al volver la red, el libro suma lo de las dos: quedan 13.
        await ReplicateAsync(_a, _b);
        await ReplicateAsync(_b, _a);
        Assert.Equal(13m, (await _a.Pos.Balances.GetGiftCardAsync(code))!.Balance);
        Assert.Equal(13m, (await _b.Pos.Balances.GetGiftCardAsync(code))!.Balance);
        _a.Context.IsOnline = true;
        await Assert.ThrowsAsync<PosValidationException>(() => SellAsync(_a, 1, new PaymentDraft(PaymentMethods.GiftCard, 18.50m, code)));
        await Assert.ThrowsAsync<PosValidationException>(() => SellAsync(_a, 1, new PaymentDraft(PaymentMethods.GiftCard, 18.50m, "NO-EXISTE")));
    }

    [Fact]
    public async Task La_venta_al_credito_respeta_el_limite_y_los_cobros_del_erp_lo_liberan()
    {
        var party = TestWorkspace.Party(_a.Customer, "Bodega Don Pepe", "20123456789");
        await _a.Pos.Master.ApplyPartyAsync(party with { Version = 5, Data = party.Data with { Credit = new VortexApi.Client.VortexPartyCredit(30, 100m) } });
        await SellAsync(_a, 5, new PaymentDraft(PaymentMethods.Credit, 92.50m));
        _a.Context.Role = PosPermissions.Cashier;
        await Assert.ThrowsAsync<PosPermissionException>(() => SellAsync(_a, 1, new PaymentDraft(PaymentMethods.Credit, 18.50m)));

        await _a.Pos.SyncHandlers.HandlePaymentReceivedAsync(TestWorkspace.Event(SuiteEvents.PaymentReceived,
            new PaymentReceived(Guid.NewGuid(), _a.Customer, 50m, "PEN"), source: "ERP"));
        Assert.Equal(new CustomerAccount(_a.Customer, 42.50m, 100m), await _a.Pos.Balances.GetAccountAsync(_a.Customer));
        await SellAsync(_a, 1, new PaymentDraft(PaymentMethods.Credit, 18.50m));
    }

    [Fact]
    public async Task El_apartado_se_completa_al_pagar_el_resto_y_se_cancela_devolviendo_lo_abonado()
    {
        var session = (await _a.Pos.Cash.GetOpenAsync())!.Id;
        var number = await _a.Pos.Balances.CreateLayawayAsync(_a.Customer, [new CartLine(_a.Coffee, 2)], 10m, PaymentMethods.Cash);
        var layaway = Assert.Single(await _a.Pos.Balances.GetLayawaysAsync());
        Assert.Equal((number, 37m, 27m), (layaway.Number, layaway.Total, layaway.Remaining));
        Assert.Null(await _a.Pos.Balances.PayLayawayAsync(layaway.Id, 7m, PaymentMethods.Card));
        var receipt = await _a.Pos.Balances.PayLayawayAsync(layaway.Id, 20m, PaymentMethods.Cash);
        Assert.Equal(37m, receipt!.Total);
        Assert.Equal([PaymentMethods.Layaway], (await _a.Pos.Sales.GetPaymentsAsync(receipt.SaleId)).Select(p => p.Method));
        Assert.Equal(130m, (await _a.Pos.Cash.GetSummaryAsync(session)).ExpectedCash);   // 100 + 10 + 20 en efectivo
        Assert.Empty(await _a.Pos.Balances.GetLayawaysAsync());

        var other = await _a.Pos.Balances.CreateLayawayAsync(_a.Customer, [new CartLine(_a.Coffee, 1)], 5m, PaymentMethods.Cash);
        var open = Assert.Single(await _a.Pos.Balances.GetLayawaysAsync());
        _a.Context.Role = PosPermissions.Cashier;
        await Assert.ThrowsAnyAsync<PosValidationException>(() => _a.Pos.Balances.CancelLayawayAsync(open.Id));
        _a.Context.Role = PosPermissions.Supervisor;
        await _a.Pos.Balances.CancelLayawayAsync(open.Id);
        Assert.Equal(130m, (await _a.Pos.Cash.GetSummaryAsync(session)).ExpectedCash);
        Assert.NotNull(other);
    }
}
