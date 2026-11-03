using VortexApi.Client;
using VortexApi.Client.Sessions;
using VortexApi.Client.Sync;
using VortexPos.Core.Domain;
using VortexPos.Core.Sync;

namespace VortexPos.Core.Tests;

/// <summary>Solo corre con un hub real: <c>VORTEX_HUB_URL=http://localhost:5299/ dotnet test</c> (organización demo).</summary>
public sealed class HubFactAttribute : FactAttribute
{
    public HubFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VORTEX_HUB_URL")))
            Skip = "Define VORTEX_HUB_URL para probar contra un hub real.";
    }
}

public sealed class HubIntegrationTests : IAsyncLifetime
{
    private readonly TestWorkspace _t = new();

    public Task InitializeAsync() => _t.InitializeAsync();
    public Task DisposeAsync() => _t.DisposeAsync();

    [HubFact]
    public async Task Cliente_y_venta_sin_conexion_llegan_al_hub()
    {
        var client = new VortexApiClient(
            new HttpClient { BaseAddress = new Uri(Environment.GetEnvironmentVariable("VORTEX_HUB_URL")!) },
            new VortexApiClientOptions
            {
                ProductCode = PosSyncHandlers.ProductCode,
                ProductKey = Environment.GetEnvironmentVariable("VORTEX_POS_KEY") ?? "vxp_pos_dev_key_change_me",
            });
        var sessions = new VortexSessionManager(client, new InMemorySessionStore(), "vortex-pos-integration-tests");
        await sessions.LoginAsync("luis@vortex.local", "Suite-Local-2026!", "Pruebas Vortex POS", "tests");
        var sync = _t.Pos.SyncHandlers.Register(new VortexSync(client, sessions, _t.Pos.SyncStore, PosSyncHandlers.SyncOptions));

        var first = await sync.SyncAsync();
        Assert.True(first.Completed, first.InterruptedBy);

        // Todo se registra sin red: un cliente nuevo (dato del hub) y una venta (dato del POS).
        var tag = Random.Shared.Next(10_000_000, 99_999_999).ToString();
        var customer = await _t.Pos.Master.CreatePartyAsync(new PartyDraft($"Cliente de pruebas {tag}", "Person", true, false, "DNI", tag,
            null, null, "Lima"));
        await _t.OpenCashAsync();
        await _t.Pos.Sales.CheckoutAsync(new SaleDraft([new CartLine(_t.Coffee, 1)], [new PaymentDraft(PaymentMethods.Cash, 20)], customer));

        var second = await sync.SyncAsync();
        Assert.True(second.Completed, second.InterruptedBy);
        Assert.Equal(0, second.Failed);
        Assert.Equal(0, second.PendingOutbox);
        Assert.Equal(1, second.PushedEvents);         // sale.completed
        Assert.Equal(1, second.PushedMasterWrites);   // cliente
    }
}
