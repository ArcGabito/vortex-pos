using VortexApi.Client;
using VortexApi.Client.Sync;
using VortexPos.Core.Domain;

namespace VortexPos.Core.Tests;

/// <summary>POS-56: consentimientos del cliente (Ley 29733, LGPD, GDPR) en el party del hub.</summary>
public sealed class ConsentsTests : IAsyncLifetime
{
    private readonly TestWorkspace _t = new();

    public Task InitializeAsync() => _t.InitializeAsync();
    public Task DisposeAsync() => _t.DisposeAsync();

    private async Task<System.Text.Json.JsonElement> LastPartyBodyAsync() =>
        (await _t.OutboxAsync()).Last(e => e.Kind == VortexOutboxKind.MasterUpsert && e.Resource == "parties").Payload!.Value;

    [Fact]
    public async Task El_alta_rapida_sube_los_consentimientos_dados_en_caja()
    {
        var id = await _t.Pos.Master.CreatePartyAsync(new PartyDraft("Rosa Quispe", "Person", true, false, "DNI", "45678912", null, "999111222", null,
            Consents: new ConsentDraft(true, true, ["WhatsApp", "fax"])));
        var c = (await LastPartyBodyAsync()).GetProperty("consents");
        Assert.Equal((true, "POS", "Luis"), (c.GetProperty("marketing").GetBoolean(), c.GetProperty("source").GetString(), c.GetProperty("capturedBy").GetString()));
        Assert.Equal(["whatsapp"], c.GetProperty("channels").EnumerateArray().Select(x => x.GetString()));
        Assert.True((await _t.Pos.Master.GetConsentsAsync(id))!.AllowsMarketing("whatsapp"));

        await Assert.ThrowsAsync<PosValidationException>(() => _t.Pos.Master.CreatePartyAsync(new PartyDraft("X", "Person", true, false, null, null, null, null, null,
            Consents: new ConsentDraft(true, true, []))));       // publicidad sin canal
        await Assert.ThrowsAsync<PosValidationException>(() => _t.Pos.Master.CreatePartyAsync(new PartyDraft("X", "Person", true, false, null, null, null, null, null,
            Consents: new ConsentDraft(false, true, ["email"]))));   // publicidad sin autorizar los datos

        // Un cliente creado en esta caja que aún no vuelve del hub no se puede actualizar (no se sabe qué más tiene).
        await Assert.ThrowsAsync<PosValidationException>(() => _t.Pos.Master.UpdateConsentsAsync(id, new ConsentDraft(true, false)));
    }

    [Fact]
    public async Task Cambiar_los_consentimientos_no_borra_lo_que_la_caja_no_guarda()
    {
        var party = TestWorkspace.Party(_t.Customer, "Bodega Don Pepe", "20123456789");
        await _t.Pos.Master.ApplyPartyAsync(party with
        {
            Version = 5,
            Data = party.Data with
            {
                Address = new VortexAddress("Av. Perú 123", "Breña", "Lima", null, null, "PE"), Notes = "Paga los viernes",
                Credit = new VortexPartyCredit(30, 5000m), Account = new VortexPartyAccount(Guid.NewGuid(), "Ana"),
            },
        });
        Assert.Null(await _t.Pos.Master.GetConsentsAsync(_t.Customer));

        await _t.Pos.Master.UpdateConsentsAsync(_t.Customer, new ConsentDraft(true, false, ["email"]));
        var body = await LastPartyBodyAsync();
        Assert.Equal(("Av. Perú 123", "Paga los viernes"), (body.GetProperty("address").GetProperty("line").GetString(), body.GetProperty("notes").GetString()));
        Assert.False(body.TryGetProperty("credit", out _));   // del ERP: no se toca
        Assert.False(body.TryGetProperty("account", out _));  // del CRM
        var consents = (await _t.Pos.Master.GetConsentsAsync(_t.Customer))!;
        Assert.Equal((true, false, 0), (consents.DataProcessing!.Value, consents.Marketing!.Value, consents.Channels!.Count));
    }
}
