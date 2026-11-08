using VortexApi.Client;
using VortexApi.Client.Sync;
using VortexApi.Suite.Auditing;
using VortexPos.Core.Domain;
using VortexPos.Core.Integration;

namespace VortexPos.Core.Tests;

/// <summary>Fase 3a de la hoja de ruta: venta por presentación, etiquetas de balanza y cambio de precio con permiso.</summary>
public sealed class CatalogTests : IAsyncLifetime
{
    private readonly TestWorkspace _t = new();
    private static readonly Guid Soda = Guid.Parse("22222222-0000-0000-0000-000000000010");
    private static readonly Guid Cheese = Guid.Parse("22222222-0000-0000-0000-000000000011");

    public async Task InitializeAsync()
    {
        await _t.InitializeAsync();
        // Gaseosa: la unidad tiene su código; la caja de 12 y el pack de 6 traen otros (vienen del hub).
        await _t.Pos.Master.ApplyItemAsync(Item(Soda, "GAS-500", "Gaseosa 500 ml", 2.50m, "7750000000017",
            [new VortexItemPresentation("CJ12", "Caja x 12", 12, "17750000000014"), new VortexItemPresentation("PK6", "Pack x 6", 6, "27750000000011")]));
        // Queso por kilo: su SKU numérico es el PLU de la balanza.
        await _t.Pos.Master.ApplyItemAsync(Item(Cheese, "00123", "Queso fresco (kg)", 24.00m, null, null));
    }

    public Task DisposeAsync() => _t.DisposeAsync();

    private static VortexMasterChange<VortexItem> Item(Guid id, string sku, string name, decimal price, string? barcode,
        IReadOnlyList<VortexItemPresentation>? presentations, int version = 1) =>
        new(version, id, VortexChangeOperation.Created, version, DateTimeOffset.UtcNow, "HUB",
            new VortexItem(id, sku, name, "Product", null, "unidad", barcode, "Bebidas", price, "PEN", "igv", true, true,
                new VortexMasterMeta(version, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null), Presentations: presentations));

    [Fact]
    public async Task Escanear_la_caja_vende_12_unidades_y_la_venta_dice_la_presentacion()
    {
        var unidad = (await _t.Pos.Master.ResolveScanAsync("7750000000017"))!;
        Assert.Equal((Soda, 1m, (ItemPresentationRow?)null), (unidad.Item.Id, unidad.Quantity, unidad.Presentation));

        var caja = (await _t.Pos.Master.ResolveScanAsync(" 17750000000014 "))!;
        Assert.Equal((Soda, 12m, "CJ12"), (caja.Item.Id, caja.Quantity, caja.Presentation!.Code));
        Assert.Null(await _t.Pos.Master.ResolveScanAsync("0000000000000"));

        await _t.OpenCashAsync();
        var receipt = await _t.Pos.Sales.CheckoutAsync(new SaleDraft(
            [new CartLine(Soda, 12, Presentation: "CJ12"), new CartLine(Soda, 1)], [new PaymentDraft(PaymentMethods.Cash, 40)]));
        Assert.Equal(32.50m, receipt.Total);
        var lines = await _t.Pos.Sales.GetLinesAsync(receipt.SaleId);
        Assert.Equal([12m, 1m], lines.Select(l => l.Quantity));

        var sale = (await _t.OutboxAsync()).Single(e => e.EventType == SuiteEvents.SaleCompleted);
        Assert.Contains("\"presentation\":\"CJ12\"", sale.Payload!.Value.GetRawText());
    }

    [Fact]
    public async Task Una_version_nueva_del_articulo_reemplaza_sus_presentaciones()
    {
        await _t.Pos.Master.ApplyItemAsync(Item(Soda, "GAS-500", "Gaseosa 500 ml", 2.50m, "7750000000017",
            [new VortexItemPresentation("CJ24", "Caja x 24", 24, "37750000000018")], version: 2));
        Assert.Equal(["CJ24"], (await _t.Pos.Master.GetPresentationsAsync(Soda)).Select(p => p.Code));
        Assert.Null(await _t.Pos.Master.ResolveScanAsync("17750000000014"));

        // Una versión vieja que llega tarde no las toca.
        await _t.Pos.Master.ApplyItemAsync(Item(Soda, "GAS-500", "Gaseosa 500 ml", 2.50m, "7750000000017", null));
        Assert.Single(await _t.Pos.Master.GetPresentationsAsync(Soda));

        await _t.OpenCashAsync();
        var ex = await Assert.ThrowsAnyAsync<PosValidationException>(() => _t.Pos.Sales.CheckoutAsync(new SaleDraft(
            [new CartLine(Soda, 12, Presentation: "CJ12")], [new PaymentDraft(PaymentMethods.Cash, 40)])));
        Assert.Contains("ya no existe", ex.Message);
    }

    [Fact]
    public async Task La_etiqueta_de_balanza_agrega_el_peso_o_lo_que_equivale_al_precio()
    {
        var porPeso = ScaleLabel.Build("20", "00123", 1250);   // 1.250 kg
        Assert.Null(await _t.Pos.Master.ResolveScanAsync(porPeso));   // la tienda aún no usa etiquetas de balanza

        await _t.Pos.Stores.SaveAsync(_t.Store, new StoreSettings(ScaleLabels: true));
        var peso = (await _t.Pos.Master.ResolveScanAsync(porPeso))!;
        Assert.Equal((Cheese, 1.25m, true), (peso.Item.Id, peso.Quantity, peso.Weighed));

        // Dígito de control equivocado: no es una etiqueta.
        Assert.Null(await _t.Pos.Master.ResolveScanAsync(porPeso[..12] + ((porPeso[12] - '0' + 1) % 10)));

        // Modo precio: S/ 12.00 a S/ 24.00 el kilo son 0.5 kg.
        _t.Clock.Advance(TimeSpan.FromMinutes(1));
        await _t.Pos.Stores.SaveAsync(_t.Store, new StoreSettings(ScaleLabels: true, ScaleLabelMode: ScaleLabel.Price));
        var precio = (await _t.Pos.Master.ResolveScanAsync(ScaleLabel.Build("21", "123", 1200)))!;
        Assert.Equal(0.5m, precio.Quantity);

        await Assert.ThrowsAnyAsync<PosValidationException>(() =>
            _t.Pos.Stores.SaveAsync(_t.Store, new StoreSettings(ScaleLabels: true, ScaleItemDigits: 8)));
    }

    [Fact]
    public async Task Cambiar_el_precio_necesita_permiso_y_queda_auditado()
    {
        await _t.OpenCashAsync();
        _t.Context.Role = PosPermissions.Cashier;
        var draft = new SaleDraft([new CartLine(_t.Coffee, 1, UnitPrice: 15.00m)], [new PaymentDraft(PaymentMethods.Cash, 20)]);
        await Assert.ThrowsAsync<PosPermissionException>(() => _t.Pos.Sales.CheckoutAsync(draft));

        // Al precio de lista el cajero vende sin pedir nada.
        await _t.Pos.Sales.CheckoutAsync(new SaleDraft([new CartLine(_t.Coffee, 1, UnitPrice: 18.50m)], [new PaymentDraft(PaymentMethods.Cash, 20)]));

        _t.Context.Role = PosPermissions.Supervisor;
        var receipt = await _t.Pos.Sales.CheckoutAsync(draft);
        Assert.Equal(15.00m, receipt.Total);
        var audit = (await _t.Pos.Audit.GetAsync(1, 10)).Items.Single(a => a.Action == AuditActions.PriceOverride);
        Assert.Contains("18.50 → 15.00", audit.Detail);
        var evento = (await _t.OutboxAsync()).Last(e => e.EventType == SuiteEvents.SaleCompleted);
        Assert.Contains("\"listPrice\":18.5", evento.Payload!.Value.GetRawText());
    }
}
