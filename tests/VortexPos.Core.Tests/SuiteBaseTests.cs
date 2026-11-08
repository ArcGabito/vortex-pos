using VortexPos.Core.Domain;
using VortexPos.Core.Integration;

namespace VortexPos.Core.Tests;

/// <summary>Base común de la suite en el POS: impuestos por país, monedas, permisos y auditoría.</summary>
public sealed class SuiteBaseTests
{
    [Fact]
    public async Task En_EEUU_el_sales_tax_se_suma_al_cobrar()
    {
        var t = new TestWorkspace(country: "US", currency: "USD", taxRate: 8.875m);
        await t.InitializeAsync();
        try
        {
            await t.OpenCashAsync();
            var receipt = await t.Pos.Sales.CheckoutAsync(new SaleDraft([new CartLine(t.Coffee, 2)], [new PaymentDraft(PaymentMethods.Card, 40.28m)]));

            Assert.Equal(40.28m, receipt.Total);   // 37.00 + 8.875 % = 40.28
            var sale = (await t.Pos.Sales.GetSaleAsync(receipt.SaleId))!;
            Assert.Equal(37.00m, sale.Subtotal);
            Assert.Equal(3.28m, sale.Tax);
            Assert.Equal("USD", sale.CurrencyCode);
        }
        finally
        {
            await t.DisposeAsync();
        }
    }

    [Fact]
    public async Task En_Chile_se_cobra_sin_decimales_con_IVA_incluido()
    {
        var t = new TestWorkspace(country: "CL", currency: "CLP");
        await t.InitializeAsync();
        try
        {
            await t.OpenCashAsync();
            // 3 × 18.50 = 55.5 → en pesos chilenos se redondea a 56.
            var receipt = await t.Pos.Sales.CheckoutAsync(new SaleDraft([new CartLine(t.Coffee, 3)], [new PaymentDraft(PaymentMethods.Cash, 100)]));
            Assert.Equal(56m, receipt.Total);
            Assert.Equal(44m, receipt.Change);
            var sale = (await t.Pos.Sales.GetSaleAsync(receipt.SaleId))!;
            Assert.Equal(47m, sale.Subtotal);   // 56 / 1.19 = 47.06
            Assert.Equal(9m, sale.Tax);
            Assert.Equal("standard", (await t.OutboxAsync()).Single().Payload!.Value.GetProperty("lines")[0].GetProperty("taxCode").GetString());
        }
        finally
        {
            await t.DisposeAsync();
        }
    }

    [Fact]
    public async Task Un_cajero_vende_pero_no_anula_ni_devuelve_ni_cierra_la_caja()
    {
        var t = new TestWorkspace(role: PosPermissions.Cashier);
        await t.InitializeAsync();
        try
        {
            var session = await t.OpenCashAsync();
            var sale = await t.Pos.Sales.CheckoutAsync(new SaleDraft([new CartLine(t.Coffee, 1, Discount: 1.50m)], [new PaymentDraft(PaymentMethods.Cash, 20)]));

            var ex = await Assert.ThrowsAnyAsync<PosValidationException>(() => t.Pos.Sales.VoidAsync(sale.SaleId, "Error"));
            Assert.Contains("Tu rol (cajero) no permite anular ventas", ex.Message);
            await Assert.ThrowsAnyAsync<PosValidationException>(() => t.Pos.Sales.ReturnAsync(sale.SaleId, [new ReturnLine(1, 1)], null));
            await Assert.ThrowsAnyAsync<PosValidationException>(() => t.Pos.Cash.CloseAsync(session, 100, null));
            await Assert.ThrowsAnyAsync<PosValidationException>(() => t.Pos.Audit.GetAsync(1, 10));
            await Assert.ThrowsAnyAsync<PosValidationException>(() => t.Pos.Master.CreateItemAsync(
                new ItemDraft("X-1", "Nuevo", "unidad", null, null, 1, "PEN")));
            // Más del 10 % de descuento necesita supervisor.
            var big = await Assert.ThrowsAnyAsync<PosValidationException>(() => t.Pos.Sales.CheckoutAsync(
                new SaleDraft([new CartLine(t.Coffee, 1, Discount: 5m)], [new PaymentDraft(PaymentMethods.Cash, 20)])));
            Assert.Contains("descuentos mayores al 10 %", big.Message);

            t.Context.Role = PosPermissions.Supervisor;
            await t.Pos.Sales.VoidAsync(sale.SaleId, "Cliente se arrepintió");
            await t.Pos.Cash.CloseAsync(session, 100, null);
        }
        finally
        {
            await t.DisposeAsync();
        }
    }

    [Fact]
    public async Task Las_acciones_sensibles_quedan_en_la_auditoria()
    {
        var t = new TestWorkspace();
        await t.InitializeAsync();
        try
        {
            var session = await t.OpenCashAsync(50);
            var a = await t.Pos.Sales.CheckoutAsync(new SaleDraft([new CartLine(t.Coffee, 2, Discount: 2m)], [new PaymentDraft(PaymentMethods.Cash, 40)]));
            await t.Pos.Sales.ReturnAsync(a.SaleId, [new ReturnLine(1, 1)], "Dañado");
            var b = await t.Pos.Sales.CheckoutAsync(new SaleDraft([new CartLine(t.Bread, 4)], [new PaymentDraft(PaymentMethods.Cash, 2)]));
            await t.Pos.Sales.VoidAsync(b.SaleId, "Duplicada");
            await t.Pos.Cash.CloseAsync(session, 70, "Conteo de la noche");

            var log = (await t.Pos.Audit.GetAsync(1, 50)).Items;
            Assert.Equal(["cash.opened", "discount.applied", "document.returned", "document.voided", "cash.closed"],
                log.Select(e => e.Action).Reverse());
            Assert.All(log, e => Assert.Equal("Luis", e.UserName));
            Assert.Contains(log, e => e.Action == "document.voided" && e.Reference == b.Number && e.Detail == "Duplicada");
            Assert.Contains(log, e => e.Action == "cash.closed" && e.Detail!.Contains("Conteo de la noche"));
        }
        finally
        {
            await t.DisposeAsync();
        }
    }

    [Fact]
    public async Task La_venta_de_otra_caja_conserva_la_categoria_de_impuesto()
    {
        var t = new TestWorkspace();
        await t.InitializeAsync();
        try
        {
            await t.Pos.SyncHandlers.HandleSaleCompletedAsync(TestWorkspace.Event(SuiteEvents.SaleCompleted, new
            {
                saleId = Guid.NewGuid(), number = "V-9AFE-000001", siteId = t.Store, currencyCode = "PEN", subtotal = 2m, tax = 0m, total = 2m,
                lines = new[] { new { itemId = t.Bread, quantity = 4m, unitPrice = 0.5m, discount = 0m, total = 2m, taxable = false, taxCode = "exempt" } },
                payments = new[] { new { method = "cash", amount = 2m } },
            }));
            Assert.Single((await t.Pos.Sales.GetSalesAsync(new SaleQuery())).Items);
        }
        finally
        {
            await t.DisposeAsync();
        }
    }
}
