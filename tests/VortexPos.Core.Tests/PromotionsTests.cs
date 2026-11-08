using VortexApi.Client.Sync;
using VortexApi.Suite.Auditing;
using VortexPos.Core.Domain;
using VortexPos.Core.Integration;

namespace VortexPos.Core.Tests;

/// <summary>Fase 3b de la hoja de ruta: promociones, cupones y descuento por ticket, iguales en todas las cajas.</summary>
public sealed class PromotionsTests : IAsyncLifetime
{
    // El reloj de prueba es el lunes 2 de noviembre de 2026, 10:00 en Lima. Café S/ 18.50 y pan S/ 0.50, ambos de "Abarrotes".
    private readonly TestWorkspace _a = new("AAAA");
    private readonly TestWorkspace _b = new("BBBB");

    public async Task InitializeAsync()
    {
        await _a.InitializeAsync();
        await _b.InitializeAsync();
        await _a.OpenCashAsync();
        await _b.OpenCashAsync();
    }

    public async Task DisposeAsync()
    {
        await _a.DisposeAsync();
        await _b.DisposeAsync();
    }

    private static SaleDraft Draft(IReadOnlyList<CartLine> lines, decimal cash = 200, string? coupon = null, decimal ticket = 0) =>
        new(lines, [new PaymentDraft(PaymentMethods.Cash, cash)], Coupon: coupon, TicketDiscount: ticket);

    /// <summary>Pasa a la otra caja lo que esta publicó (promociones y ventas).</summary>
    private async Task ReplicateAsync(TestWorkspace from, TestWorkspace to)
    {
        foreach (var e in (await from.OutboxAsync()).Where(e => e.Kind == VortexOutboxKind.Event).Select(e => TestWorkspace.AsEvent(e)))
        {
            if (e.Type == SuiteEvents.PosRecordUpserted)
                await to.Pos.SyncHandlers.HandlePosRecordAsync(e);
            else if (e.Type == SuiteEvents.SaleCompleted)
                await to.Pos.SyncHandlers.HandleSaleCompletedAsync(e);
        }
    }

    [Fact]
    public async Task Un_2x1_por_categoria_da_el_mismo_total_en_dos_cajas_sin_conexion()
    {
        await _a.Pos.Promotions.SaveAsync(new Promotion(Guid.Empty, "2x1 abarrotes", PromotionKinds.BuyPay, Category: "Abarrotes", Buy: 2, Pay: 1));
        await ReplicateAsync(_a, _b);

        var lines = new List<CartLine> { new(_a.Coffee, 3), new(_a.Bread, 1) };   // 4 unidades: salen gratis las 2 más baratas
        var qa = await _a.Pos.Sales.QuoteAsync(Draft(lines));
        var qb = await _b.Pos.Sales.QuoteAsync(Draft(lines));
        Assert.Equal(qa.Totals, qb.Totals);
        Assert.Equal(56.00m - 19.00m, qa.Totals.Total);   // pan 0.50 + un café 18.50 gratis
        Assert.Equal("2x1 abarrotes", Assert.Single(qa.Promotions).Name);

        var receipt = await _a.Pos.Sales.CheckoutAsync(Draft(lines));
        Assert.Equal(qa.Totals.Total, receipt.Total);
        var sale = (await _a.OutboxAsync()).Last(e => e.EventType == SuiteEvents.SaleCompleted).Payload!.Value;
        Assert.All(sale.GetProperty("lines").EnumerateArray(), l => Assert.True(l.TryGetProperty("promotionId", out _)));
        Assert.Contains((await _a.Pos.Audit.GetAsync(1, 10)).Items, a => a.Action == "promotion.applied");
    }

    [Fact]
    public async Task Cada_linea_toma_la_promocion_que_mas_descuenta_y_despues_la_de_ticket()
    {
        await _a.Pos.Promotions.SaveAsync(new Promotion(Guid.Empty, "10 % abarrotes", PromotionKinds.Percent, Category: "Abarrotes", Percent: 10));
        await _a.Pos.Promotions.SaveAsync(new Promotion(Guid.Empty, "Café a mitad", PromotionKinds.Percent, ItemId: _a.Coffee, Percent: 50));
        await _a.Pos.Promotions.SaveAsync(new Promotion(Guid.Empty, "S/ 5 desde S/ 20", PromotionKinds.Ticket, Amount: 5, MinAmount: 20));

        // Café 2 × 18.50 = 37 → mitad (18.50); pan 4 × 0.50 = 2 → 10 % (0.20). Quedan 20.30 ≥ 20: −5 al ticket.
        var q = await _a.Pos.Sales.QuoteAsync(Draft([new CartLine(_a.Coffee, 2), new CartLine(_a.Bread, 4)]));
        Assert.Equal(["Café a mitad", "10 % abarrotes", "S/ 5 desde S/ 20"], q.Promotions.Select(p => p.Name));
        Assert.Equal(39.00m - 18.50m - 0.20m - 5m, q.Totals.Total);
        Assert.Equal(q.Totals.Total, q.Lines.Sum(l => l.Total));

        // Fuera de horario no aplica: una promoción solo de sábado.
        await _a.Pos.Promotions.SaveAsync(new Promotion(Guid.Empty, "Sábado", PromotionKinds.Percent, ItemId: _a.Bread, Percent: 90, Days: [6]));
        q = await _a.Pos.Sales.QuoteAsync(Draft([new CartLine(_a.Bread, 1)]));
        Assert.DoesNotContain(q.Promotions, p => p.Name == "Sábado");
    }

    [Fact]
    public async Task Un_cupon_de_un_uso_vale_una_vez_en_todas_las_cajas()
    {
        await Assert.ThrowsAnyAsync<PosValidationException>(() => _a.Pos.Promotions.SaveAsync(
            new Promotion(Guid.Empty, "Mal", PromotionKinds.Ticket, Percent: 10, MaxUses: 1)));   // usos sin cupón
        await _a.Pos.Promotions.SaveAsync(new Promotion(Guid.Empty, "Bienvenida", PromotionKinds.Ticket, Percent: 10, CouponCode: " hola10 ", MaxUses: 1));
        await ReplicateAsync(_a, _b);

        var sin = await _a.Pos.Sales.QuoteAsync(Draft([new CartLine(_a.Coffee, 2)]));
        Assert.Empty(sin.Promotions);   // sin el cupón no aplica
        var ex = await Assert.ThrowsAnyAsync<PosValidationException>(() => _a.Pos.Sales.QuoteAsync(Draft([new CartLine(_a.Coffee, 2)], coupon: "NOEXISTE")));
        Assert.Contains("NOEXISTE", ex.Message);

        var receipt = await _a.Pos.Sales.CheckoutAsync(Draft([new CartLine(_a.Coffee, 2)], coupon: "hola10"));
        Assert.Equal(33.30m, receipt.Total);
        await Assert.ThrowsAnyAsync<PosValidationException>(() => _a.Pos.Sales.CheckoutAsync(Draft([new CartLine(_a.Coffee, 1)], coupon: "HOLA10")));

        // La otra caja se entera del uso al recibir la venta.
        await ReplicateAsync(_a, _b);
        Assert.Equal(1, await _b.Pos.Promotions.CouponUsesAsync("hola10"));
        await Assert.ThrowsAnyAsync<PosValidationException>(() => _b.Pos.Sales.CheckoutAsync(Draft([new CartLine(_b.Coffee, 1)], coupon: "HOLA10")));
    }

    [Fact]
    public async Task El_descuento_al_ticket_se_reparte_y_mas_del_10_por_ciento_necesita_permiso()
    {
        _a.Context.Role = PosPermissions.Cashier;
        var lines = new List<CartLine> { new(_a.Coffee, 2), new(_a.Bread, 4) };   // 39.00
        await Assert.ThrowsAsync<PosPermissionException>(() => _a.Pos.Sales.CheckoutAsync(Draft(lines, ticket: 5)));
        var q = await _a.Pos.Sales.QuoteAsync(Draft(lines, ticket: 3.90m));
        Assert.Equal(3.90m, q.TicketDiscount);
        Assert.Equal(3.90m, q.Lines.Sum(l => l.TicketShare));
        var receipt = await _a.Pos.Sales.CheckoutAsync(Draft(lines, ticket: 3.90m));
        Assert.Equal(35.10m, receipt.Total);

        _a.Context.Role = PosPermissions.Supervisor;
        Assert.Equal(34.00m, (await _a.Pos.Sales.CheckoutAsync(Draft(lines, ticket: 5))).Total);
        await Assert.ThrowsAnyAsync<PosValidationException>(() => _a.Pos.Sales.CheckoutAsync(Draft(lines, ticket: 40)));
        Assert.Contains((await _a.Pos.Audit.GetAsync(1, 20)).Items, a => a.Action == AuditActions.Discount);
    }

    [Fact]
    public void El_motor_reparte_sin_perder_centimos()
    {
        var shares = PromotionEngine.Allocate(10m, [3m, 3m, 3m], 2);
        Assert.Equal(10m, shares.Sum());
        Assert.Equal([3.34m, 3.33m, 3.33m], shares);
    }
}
