using VortexApi.Client;
using VortexApi.Client.Sync;
using VortexPos.Core.Domain;
using VortexPos.Core.Integration;

namespace VortexPos.Core.Tests;

/// <summary>Fase 3c de la hoja de ruta: listas de precios del hub por cliente y por tienda, con escalas.</summary>
public sealed class PriceListsTests : IAsyncLifetime
{
    // Café S/ 18.50 y pan S/ 0.50; la caja es de la tienda (Store). El reloj: 2 de noviembre de 2026.
    private readonly TestWorkspace _t = new();
    private static readonly Guid Wholesale = Guid.Parse("44444444-0000-0000-0000-000000000001");
    private static readonly Guid StoreList = Guid.Parse("44444444-0000-0000-0000-000000000002");

    public async Task InitializeAsync()
    {
        await _t.InitializeAsync();
        await _t.OpenCashAsync();
    }

    public Task DisposeAsync() => _t.DisposeAsync();

    private static VortexMasterChange<VortexPriceList> List(Guid id, string code, IReadOnlyList<Guid> sites, int version = 1,
        DateOnly? from = null, params VortexPriceListLine[] lines) =>
        new(version, id, VortexChangeOperation.Created, version, DateTimeOffset.UtcNow, "HUB",
            new VortexPriceList(id, code, code, "PEN", from, null, sites, true, lines,
                new VortexMasterMeta(version, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null)));

    private SaleDraft Draft(Guid? customer, params CartLine[] lines) =>
        new(lines, [new PaymentDraft(PaymentMethods.Cash, 500)], customer);

    [Fact]
    public async Task La_lista_del_cliente_gana_a_la_de_la_tienda_y_tiene_escalas()
    {
        await _t.Pos.Master.ApplyPriceListAsync(List(Wholesale, "MAYORISTA", [], 1, null,
            new VortexPriceListLine(_t.Coffee, 17m), new VortexPriceListLine(_t.Coffee, 15.5m, 12)));
        await _t.Pos.Master.ApplyPriceListAsync(List(StoreList, "TIENDA", [_t.Store], 1, null, new VortexPriceListLine(_t.Coffee, 18m)));
        var party = TestWorkspace.Party(_t.Customer, "Bodega Sur", "20111222333");
        await _t.Pos.Master.ApplyPartyAsync(party with { Version = 2, Data = party.Data with { Credit = new VortexPartyCredit(PriceListId: Wholesale) } });

        // Sin cliente rige la de la tienda; el pan no está en la lista: su precio de siempre.
        var walkIn = await _t.Pos.Sales.QuoteAsync(Draft(null, new CartLine(_t.Coffee, 2), new CartLine(_t.Bread, 1)));
        Assert.Equal("TIENDA", walkIn.PriceList!.Code);
        Assert.Equal([18m, 0.50m], walkIn.Lines.Select(l => l.UnitPrice));

        // Con el cliente, su lista; desde 12 unidades (sumando las líneas del café) la escala.
        Assert.Equal(17m, (await _t.Pos.Sales.QuoteAsync(Draft(_t.Customer, new CartLine(_t.Coffee, 2)))).Lines.Single().UnitPrice);
        var bulk = await _t.Pos.Sales.QuoteAsync(Draft(_t.Customer, new CartLine(_t.Coffee, 10), new CartLine(_t.Coffee, 2, Presentation: null, Discount: 1)));
        Assert.All(bulk.Lines, l => Assert.Equal(15.5m, l.UnitPrice));

        // Cobrar al precio de la lista no es un cambio de precio; la venta lleva la lista.
        _t.Context.Role = PosPermissions.Cashier;
        var receipt = await _t.Pos.Sales.CheckoutAsync(Draft(_t.Customer, new CartLine(_t.Coffee, 2)));
        Assert.Equal(34m, receipt.Total);
        var sale = (await _t.OutboxAsync()).Last(e => e.EventType == SuiteEvents.SaleCompleted).Payload!.Value;
        Assert.Equal(Wholesale, sale.GetProperty("priceListId").GetGuid());
        Assert.Equal(17m, sale.GetProperty("lines")[0].GetProperty("listPrice").GetDecimal());

        // Otro precio que el de la lista sí lo es (el cajero no puede).
        await Assert.ThrowsAsync<PosPermissionException>(() => _t.Pos.Sales.CheckoutAsync(Draft(_t.Customer, new CartLine(_t.Coffee, 1, 18.5m))));
    }

    [Fact]
    public async Task Una_lista_fuera_de_vigencia_o_borrada_no_rige()
    {
        await _t.Pos.Master.ApplyPriceListAsync(List(StoreList, "TIENDA", [_t.Store], 1, new DateOnly(2026, 12, 1), new VortexPriceListLine(_t.Coffee, 16m)));
        var quote = await _t.Pos.Sales.QuoteAsync(Draft(null, new CartLine(_t.Coffee, 1)));
        Assert.Null(quote.PriceList);
        Assert.Equal(18.50m, quote.Lines.Single().UnitPrice);

        await _t.Pos.Master.ApplyPriceListAsync(List(StoreList, "TIENDA", [_t.Store], 2, null, new VortexPriceListLine(_t.Coffee, 16m)));
        Assert.Equal(16m, (await _t.Pos.Sales.QuoteAsync(Draft(null, new CartLine(_t.Coffee, 1)))).Lines.Single().UnitPrice);
        var deleted = List(StoreList, "TIENDA", [_t.Store], 3);
        await _t.Pos.Master.ApplyPriceListAsync(deleted with { Operation = VortexChangeOperation.Deleted });
        Assert.Equal(18.50m, (await _t.Pos.Sales.QuoteAsync(Draft(null, new CartLine(_t.Coffee, 1)))).Lines.Single().UnitPrice);
    }
}
