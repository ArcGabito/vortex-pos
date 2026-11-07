using System.Text;
using VortexPos.Core.Domain;
using VortexPos.Core.Integration;
using VortexPos.Core.Printing;

namespace VortexPos.Core.Tests;

/// <summary>Fase 2 de la hoja de ruta: caja completa (ingresos y retiros, arqueo por denominación, reportes X y Z), tickets
/// en espera, configuración de tienda compartida, tiendas por usuario e impresión de tickets.</summary>
public sealed class CashRegisterTests : IAsyncLifetime
{
    private readonly TestWorkspace _t = new();

    public Task InitializeAsync() => _t.InitializeAsync();
    public Task DisposeAsync() => _t.DisposeAsync();

    private Task<SaleReceipt> SellCoffeeAsync(int quantity = 1, decimal cash = 50, decimal discount = 0) =>
        _t.Pos.Sales.CheckoutAsync(new SaleDraft([new CartLine(_t.Coffee, quantity, Discount: discount)],
            [new PaymentDraft(PaymentMethods.Cash, cash)]));

    // ---------- Ingresos y retiros ----------

    [Fact]
    public async Task Ingresos_y_retiros_cambian_el_efectivo_esperado()
    {
        var session = await _t.OpenCashAsync(100);
        await SellCoffeeAsync();                                                               // +18.50 en efectivo
        await _t.Pos.Cash.AddMovementAsync(CashMovementKinds.In, 50, "Sencillo del banco");
        await _t.Pos.Cash.AddMovementAsync(CashMovementKinds.Out, 30, "Pago al repartidor");

        var summary = await _t.Pos.Cash.GetSummaryAsync(session);
        Assert.Equal(50m, summary.CashIn);
        Assert.Equal(30m, summary.CashOut);
        Assert.Equal(100m + 18.50m + 50m - 30m, summary.ExpectedCash);

        var movements = await _t.Pos.Cash.GetMovementsAsync(session);
        Assert.Equal(["Sencillo del banco", "Pago al repartidor"], movements.Select(m => m.Reason));
        Assert.Contains((await _t.Pos.Audit.GetAsync(1, 20)).Items, a => a.Action == "cash.movement");
    }

    [Fact]
    public async Task No_se_retira_mas_efectivo_del_que_hay_ni_sin_motivo()
    {
        await _t.OpenCashAsync(100);
        var ex = await Assert.ThrowsAsync<PosValidationException>(() => _t.Pos.Cash.AddMovementAsync(CashMovementKinds.Out, 150, "Depósito"));
        Assert.Contains("No hay tanto efectivo", ex.Message);
        await Assert.ThrowsAsync<PosValidationException>(() => _t.Pos.Cash.AddMovementAsync(CashMovementKinds.In, 10, " "));
        await Assert.ThrowsAsync<PosValidationException>(() => _t.Pos.Cash.AddMovementAsync(CashMovementKinds.In, 0, "Cambio"));
    }

    [Fact]
    public async Task Un_cajero_no_registra_movimientos_ni_ve_el_reporte_x()
    {
        var session = await _t.OpenCashAsync(100);
        _t.Context.Role = PosPermissions.Cashier;
        await Assert.ThrowsAsync<PosValidationException>(() => _t.Pos.Cash.AddMovementAsync(CashMovementKinds.Out, 10, "Retiro"));
        await Assert.ThrowsAsync<PosValidationException>(() => _t.Pos.Cash.GetReportAsync(session));
    }

    // ---------- Cierre por denominación y reportes ----------

    [Fact]
    public async Task Cierre_contando_billetes_y_monedas_calcula_lo_contado_y_la_diferencia()
    {
        var session = await _t.OpenCashAsync(100);
        await SellCoffeeAsync();   // esperado 118.50

        var diff = await _t.Pos.Cash.CloseAsync(session,
            [new DenominationCount(100, 1), new DenominationCount(10, 1), new DenominationCount(5, 1), new DenominationCount(2, 1),
             new DenominationCount(1, 1), new DenominationCount(0.50m, 1), new DenominationCount(20, 0)], "Conteo de la noche");

        Assert.Equal(0m, diff);
        var report = await _t.Pos.Cash.GetReportAsync(session);
        Assert.True(report.IsFinal);
        Assert.Equal(118.50m, report.Session.CountedCash);
        Assert.Equal(6, report.Denominations.Count);                       // sin la de cantidad 0
        Assert.Equal(100m, report.Denominations[0].Value);                  // de mayor a menor
    }

    [Fact]
    public async Task Reporte_x_resume_el_turno_abierto_y_el_z_queda_al_cerrar()
    {
        var session = await _t.OpenCashAsync(100);
        await SellCoffeeAsync(2, 50, discount: 1);                           // 36.00
        var voided = await SellCoffeeAsync();                                 // 18.50, luego anulada
        await _t.Pos.Sales.VoidAsync(voided.SaleId, "Error de digitación");
        await _t.Pos.Sales.CheckoutAsync(new SaleDraft([new CartLine(_t.Bread, 4)], [new PaymentDraft(PaymentMethods.Card, 2)]));
        await _t.Pos.Cash.AddMovementAsync(CashMovementKinds.Out, 20, "Caja fuerte");

        var x = await _t.Pos.Cash.GetReportAsync(session);
        Assert.False(x.IsFinal);
        Assert.Equal(2, x.Summary.Sales);
        Assert.Equal(38.00m, x.Summary.SalesTotal);
        Assert.Equal(1m, x.Discounts);
        Assert.Equal(1, x.Voided);
        Assert.Equal(18.50m, x.VoidedTotal);
        Assert.Equal(36m, x.Summary.ByMethod[PaymentMethods.Cash]);
        Assert.Equal(2m, x.Summary.ByMethod[PaymentMethods.Card]);
        Assert.Equal(100m + 36m - 20m, x.Summary.ExpectedCash);
        Assert.Equal("Luis", Assert.Single(x.ByCashier).UserName);
        Assert.Equal(x.Summary.SalesTotal, x.Subtotal + x.Tax);

        await _t.Pos.Cash.CloseAsync(session, 110, "Faltan 6");
        var z = await _t.Pos.Cash.GetReportAsync(session);
        Assert.True(z.IsFinal);
        Assert.Equal(-6m, z.Session.Difference);
    }

    // ---------- Tickets en espera ----------

    [Fact]
    public async Task Un_ticket_en_espera_se_retoma_una_sola_vez()
    {
        var id = await _t.Pos.Sales.HoldAsync([new CartLine(_t.Coffee, 2), new CartLine(_t.Bread, 3)], _t.Customer, "Señora de rojo");

        var held = Assert.Single(await _t.Pos.Sales.GetHeldAsync());
        Assert.Equal("Señora de rojo", held.Label);
        Assert.Equal("Bodega Don Pepe", held.CustomerName);
        Assert.Equal(2, held.Lines);
        Assert.Equal(38.50m, held.Total);

        var resumed = await _t.Pos.Sales.ResumeHeldAsync(id);
        Assert.Equal(_t.Customer, resumed.CustomerId);
        Assert.Equal([2m, 3m], resumed.Lines.Select(l => l.Quantity));
        Assert.Empty(await _t.Pos.Sales.GetHeldAsync());
        await Assert.ThrowsAsync<PosValidationException>(() => _t.Pos.Sales.ResumeHeldAsync(id));
        Assert.Empty(await _t.OutboxAsync());   // es de esta caja: no se publica
    }

    // ---------- Configuración de tienda compartida ----------

    [Fact]
    public async Task La_configuracion_de_la_tienda_llega_a_las_otras_cajas_y_gana_la_mas_reciente()
    {
        var settings = new StoreSettings("Av. Larco 123\nGracias por su compra", "Cambios hasta 7 días", 42, BlindClose: true);
        await _t.Pos.Stores.SaveAsync(_t.Store, settings);
        var entry = Assert.Single(await _t.OutboxAsync());
        Assert.Equal(SuiteEvents.PosRecordUpserted, entry.EventType);

        var other = new TestWorkspace("CAJA2");
        await other.InitializeAsync();
        try
        {
        await other.Pos.SyncHandlers.HandlePosRecordAsync(TestWorkspace.AsEvent(entry));
        Assert.Equal(settings, await other.Pos.Stores.GetAsync(_t.Store));

        // Un cambio más viejo que llega tarde (otra caja estuvo sin red) no pisa al más reciente.
        var older = TestWorkspace.Event(SuiteEvents.PosRecordUpserted, new PosRecordUpserted(PosRecordKinds.StoreSettings,
            _t.Store.ToString(), System.Text.Json.JsonSerializer.SerializeToElement(StoreSettings.Default, VortexApi.Client.VortexJson.Options),
            _t.Clock.GetUtcNow().AddHours(-1), "Ana"));
        await other.Pos.SyncHandlers.HandlePosRecordAsync(older);
        Assert.True((await other.Pos.Stores.GetAsync(_t.Store)).BlindClose);
        }
        finally
        {
            await other.DisposeAsync();
        }
    }

    [Fact]
    public async Task Solo_un_supervisor_cambia_la_configuracion_de_la_tienda()
    {
        _t.Context.Role = PosPermissions.Cashier;
        await Assert.ThrowsAsync<PosValidationException>(() => _t.Pos.Stores.SaveAsync(_t.Store, StoreSettings.Default));
        _t.Context.Role = PosPermissions.Supervisor;
        await Assert.ThrowsAsync<PosValidationException>(() => _t.Pos.Stores.SaveAsync(_t.Store, StoreSettings.Default with { PaperColumns = 60 }));
    }

    // ---------- Tiendas por usuario ----------

    [Fact]
    public async Task Un_usuario_solo_abre_caja_y_ve_ventas_de_sus_tiendas()
    {
        await _t.OpenCashAsync();
        await SellCoffeeAsync();
        var cash = (await _t.Pos.Cash.GetOpenAsync())!;
        await _t.Pos.Cash.CloseAsync(cash.Id, 118.50m, null);

        _t.Context.SiteIds = [_t.Central];
        var ex = await Assert.ThrowsAsync<PosValidationException>(() => _t.Pos.Cash.OpenAsync(0, _t.Store));
        Assert.Contains("No tienes asignada", ex.Message);
        Assert.Empty((await _t.Pos.Sales.GetSalesAsync(new SaleQuery())).Items);
        Assert.Equal(0, (await _t.Pos.Sales.GetDashboardAsync()).TicketsToday);

        _t.Context.SiteIds = [_t.Store];
        Assert.Single((await _t.Pos.Sales.GetSalesAsync(new SaleQuery())).Items);
    }

    // ---------- Tickets impresos ----------

    [Fact]
    public async Task El_ticket_de_venta_entra_en_el_papel_y_abre_el_cajon_si_se_pago_en_efectivo()
    {
        await _t.OpenCashAsync();
        var receipt = await _t.Pos.Sales.CheckoutAsync(new SaleDraft(
            [new CartLine(_t.Coffee, 2, Discount: 1), new CartLine(_t.Bread, 4)], [new PaymentDraft(PaymentMethods.Cash, 50)], _t.Customer));
        var data = (await _t.Pos.Sales.GetTicketDataAsync(receipt.SaleId))!;
        var settings = new StoreSettings("Gracias por su compra", "Vuelva pronto", 32);
        var culture = System.Globalization.CultureInfo.GetCultureInfo("es-PE");

        var ticket = TicketBuilder.ForSale(data, settings, culture);
        var text = ticket.ToText();
        Assert.Contains("Bodega Demo SAC", text);
        Assert.Contains("Café tostado 250 g", text);
        Assert.Contains("IGV", text);
        Assert.Contains("S/ 38.00", text);
        Assert.Contains("Vuelto", text);
        Assert.Contains("Vuelva pronto", text);
        Assert.All(ticket.Lines, l => Assert.True(l.Text.Length <= (l.Large ? 16 : 32), $"'{l.Text}' no entra en el papel"));
        Assert.True(ticket.OpenDrawer);

        var bytes = EscPos.Encode(ticket);
        Assert.Equal([0x1B, 0x40], bytes[..2]);
        Assert.Equal(EscPos.DrawerPulse, bytes[^5..]);
        Assert.Contains((byte)0x82, bytes);   // "é" de "Café" en la página PC858

        var reprint = TicketBuilder.ForSale(data, settings, culture, reprint: true);
        Assert.Contains("REIMPRESIÓN", reprint.ToText());
        Assert.False(reprint.OpenDrawer);
    }

    [Fact]
    public async Task El_reporte_z_impreso_lleva_el_arqueo_y_el_conteo()
    {
        var session = await _t.OpenCashAsync(100);
        await SellCoffeeAsync();
        await _t.Pos.Cash.AddMovementAsync(CashMovementKinds.Out, 10, "Caja fuerte");
        await _t.Pos.Cash.CloseAsync(session, [new DenominationCount(100, 1), new DenominationCount(5, 1)], null);
        var report = await _t.Pos.Cash.GetReportAsync(session);

        var text = TicketBuilder.ForCashReport(report, await _t.Pos.Master.GetCompanyAsync(), StoreSettings.Default,
            System.Globalization.CultureInfo.GetCultureInfo("es-PE")).ToText();
        Assert.Contains("REPORTE Z", text);
        Assert.Contains("Caja fuerte", text);
        Assert.Contains("S/ 108.50", text);   // esperado
        Assert.Contains("S/ -3.50", text);    // diferencia
        Assert.Contains("1 x S/ 100.00", text);
    }

    [Fact]
    public void El_texto_largo_se_corta_por_palabras()
    {
        var doc = TicketBuilder.ForSale(new SaleTicketData(
            new SaleRow(Guid.NewGuid(), "V-1", "local", Guid.NewGuid(), null, null, null, SaleStatuses.Completed, "PEN", 10, 0, 0, 10, null,
                null, DateTimeOffset.UtcNow, null, null, null, null, 0, 1),
            [new SaleLineRow(1, Guid.NewGuid(), null, "Galletas de avena con chispas de chocolate y pasas edición especial", 1, 10, 0, 10, 0)],
            [new PaymentRow(PaymentMethods.Card, 10, null)], null, null, null, "IGV"),
            StoreSettings.Default with { PaperColumns = 32 }, System.Globalization.CultureInfo.InvariantCulture);
        Assert.All(doc.Lines, l => Assert.True(l.Text.Length <= 32));
        Assert.Contains(doc.Lines, l => l.Text.StartsWith("Galletas de avena con chispas"));
        Assert.False(doc.OpenDrawer);   // pago con tarjeta
        Assert.True(EscPos.Encode(doc).AsSpan().IndexOf(Encoding.ASCII.GetBytes("Galletas")) >= 0);
    }
}
