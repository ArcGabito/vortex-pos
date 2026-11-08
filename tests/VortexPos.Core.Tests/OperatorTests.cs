using System.Security.Cryptography;
using Microsoft.Extensions.Time.Testing;
using VortexApi.Client;
using VortexPos.Core.Domain;
using VortexPos.Core.Services;

namespace VortexPos.Core.Tests;

/// <summary>Fase 2b: PIN de caja para cambiar de cajero, desbloquear y que un supervisor autorice.</summary>
public sealed class OperatorTests : IAsyncLifetime
{
    private readonly TestWorkspace _t = new(role: PosPermissions.Cashier);
    private static readonly Guid Rosa = Guid.NewGuid(), Marta = Guid.NewGuid();

    public Task InitializeAsync() => _t.InitializeAsync();
    public Task DisposeAsync() => _t.DisposeAsync();

    /// <summary>Verificador como lo genera el hub (PBKDF2-SHA256), con pocas iteraciones para la prueba.</summary>
    private static string Hash(string pin)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(pin, salt, 1000, HashAlgorithmName.SHA256, 32);
        return $"pin1$1000${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    private static OperatorDesk Desk(FakeTimeProvider? clock = null) => new(
    [
        new VortexPinVerifier(Rosa, "Rosa", null, "cajero", Hash("482915")),
        new VortexPinVerifier(Marta, "Marta", null, "supervisor", Hash("730164")),
    ], clock);

    [Fact]
    public void Un_PIN_identifica_al_cajero_y_los_errores_seguidos_bloquean_un_minuto()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var desk = Desk(clock);
        Assert.Equal(new PosOperator(Rosa, "Rosa", "cajero"), desk.Identify("482915"));

        for (var i = 0; i < OperatorDesk.MaxAttempts; i++)
            Assert.Contains("incorrecto", Assert.Throws<PosValidationException>(() => desk.Identify("000000")).Message);
        Assert.Contains("Espera 60", Assert.Throws<PosValidationException>(() => desk.Identify("482915")).Message);

        clock.Advance(OperatorDesk.Lockout);
        Assert.Equal("Rosa", desk.Identify("482915").Name);
    }

    [Fact]
    public void Solo_el_PIN_de_un_supervisor_autoriza()
    {
        var desk = Desk();
        Assert.Contains("Rosa no puede autorizar", Assert.Throws<PosValidationException>(() => desk.Authorize("482915", PosPermissions.Void)).Message);
        Assert.Equal(Marta, desk.Authorize("730164", PosPermissions.Void).UserId);
    }

    [Fact]
    public async Task Una_anulacion_sin_permiso_pide_autorizacion_y_queda_quien_la_autorizo()
    {
        _t.Context.Role = PosPermissions.Supervisor;
        await _t.OpenCashAsync();
        _t.Context.Role = PosPermissions.Cashier;
        var receipt = await _t.Pos.Sales.CheckoutAsync(new SaleDraft([new CartLine(_t.Coffee, 1)], [new PaymentDraft(PaymentMethods.Cash, 20)]));

        var denied = await Assert.ThrowsAsync<PosPermissionException>(() => _t.Pos.Sales.VoidAsync(receipt.SaleId, "Cliente se arrepintió"));
        Assert.Equal(PosPermissions.Void, denied.Permission);

        // La app pide el PIN de un supervisor y reintenta con su rol, dejando constancia.
        var supervisor = Desk().Authorize("730164", denied.Permission);
        (_t.Context.Role, _t.Context.AuthorizedBy) = (supervisor.Role, supervisor.Name);
        await _t.Pos.Sales.VoidAsync(receipt.SaleId, "Cliente se arrepintió");

        _t.Context.Role = PosPermissions.Supervisor;
        var audit = (await _t.Pos.Audit.GetAsync(1, 10)).Items.First(a => a.Action == VortexApi.Suite.Auditing.AuditActions.Voided);
        Assert.Equal("Cliente se arrepintió · autorizado por Marta", audit.Detail);
    }
}
