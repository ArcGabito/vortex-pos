using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using VortexApi.Client;
using VortexApi.Client.Sync;
using VortexPos.Core.Domain;

namespace VortexPos.Core.Tests;

/// <summary>Workspace sobre una base SQLite temporal, con una tienda, un cliente y artículos ya sincronizados desde el "hub".</summary>
public sealed class TestWorkspace(string deviceTag = "TEST") : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"vortex-pos-test-{Guid.NewGuid():N}.db");

    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 2, 15, 0, 0, TimeSpan.Zero));
    public PosWorkspace Pos { get; private set; } = null!;

    public Guid Store { get; } = Guid.Parse("11111111-0000-0000-0000-000000000002");
    public Guid Central { get; } = Guid.Parse("11111111-0000-0000-0000-000000000001");
    public Guid Coffee { get; } = Guid.Parse("22222222-0000-0000-0000-000000000001");   // S/ 18.50 con IGV
    public Guid Bread { get; } = Guid.Parse("22222222-0000-0000-0000-000000000004");    // S/ 0.50, exonerado
    public Guid Customer { get; } = Guid.Parse("33333333-0000-0000-0000-000000000001");

    public async Task InitializeAsync()
    {
        Pos = await PosWorkspace.OpenAsync(_path, new Context(deviceTag, Store), Clock);
        await Pos.Master.ApplySiteAsync(Site(Central, "ALM-01", "Almacén central", "Warehouse"));
        await Pos.Master.ApplySiteAsync(Site(Store, "TDA-01", "Tienda Miraflores", "Store"));
        await Pos.Master.ApplyItemAsync(Item(Coffee, "CAFE-250", "Café tostado 250 g", 18.50m));
        await Pos.Master.ApplyItemAsync(Item(Bread, "PAN-01", "Pan francés", 0.50m, taxCode: null));
        await Pos.Master.ApplyPartyAsync(Party(Customer, "Bodega Don Pepe", "20123456789"));
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
            File.Delete(file);
        return Task.CompletedTask;
    }

    public async Task<List<VortexOutboxEntry>> OutboxAsync() => (await Pos.SyncStore.PeekAsync(1000)).ToList();

    public async Task<Guid> OpenCashAsync(decimal openingFloat = 100) => await Pos.Cash.OpenAsync(openingFloat);

    public static VortexMasterChange<VortexItem> Item(Guid id, string sku, string name, decimal price, int version = 1,
        string? taxCode = "igv") =>
        new(version, id, VortexChangeOperation.Created, version, DateTimeOffset.UtcNow, "HUB",
            new VortexItem(id, sku, name, "Product", null, "unidad", null, "Abarrotes", price, "PEN", taxCode, true, true,
                new VortexMasterMeta(version, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null)));

    public static VortexMasterChange<VortexSite> Site(Guid id, string code, string name, string kind) =>
        new(1, id, VortexChangeOperation.Created, 1, DateTimeOffset.UtcNow, "HUB",
            new VortexSite(id, code, name, kind, null, new VortexAddress(null, null, "Lima", null, null, "PE"), true,
                new VortexMasterMeta(1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null)));

    public static VortexMasterChange<VortexParty> Party(Guid id, string name, string ruc) =>
        new(1, id, VortexChangeOperation.Created, 1, DateTimeOffset.UtcNow, "HUB",
            new VortexParty(id, true, false, "Company", name, null, "RUC", ruc, null, null, null, null, true,
                new VortexMasterMeta(1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null)));

    public static VortexEvent Event(string type, object data, string source = "POS", int schemaVersion = 1, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), 1, type, source, schemaVersion, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null,
            JsonSerializer.SerializeToElement(data, VortexJson.Options));

    /// <summary>El evento tal como lo publicó este equipo (para simular que llega a otra caja).</summary>
    public static VortexEvent AsEvent(VortexOutboxEntry entry, string source = "POS") =>
        new(entry.Id, 1, entry.EventType!, source, 1, entry.SubjectType, entry.SubjectId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            null, entry.Payload!.Value);

    private sealed class Context(string deviceTag, Guid store) : IPosContext
    {
        public Guid? UserId => Guid.Parse("00000000-0000-0000-0000-000000000001");
        public string UserName => "Luis";
        public string DeviceTag => deviceTag;
        public Guid? DefaultSiteId => store;
    }
}
