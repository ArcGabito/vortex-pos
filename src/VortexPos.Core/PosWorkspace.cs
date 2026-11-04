using VortexPos.Core.Data;
using VortexPos.Core.Domain;
using VortexPos.Core.Services;
using VortexPos.Core.Sync;

namespace VortexPos.Core;

/// <summary>
/// Todo el POS de una organización: su base local y los servicios sobre ella. Cada organización tiene su propia base,
/// así un equipo compartido por dos empresas nunca mezcla datos.
/// </summary>
public sealed class PosWorkspace
{
    private PosWorkspace(PosDatabase db, IPosContext context, TimeProvider clock)
    {
        Database = db;
        Master = new MasterDataService(db, context, clock);
        Sales = new SalesService(db, context, clock);
        Cash = new CashService(db, context, clock);
        Audit = new AuditService(db, context, clock);
        SyncStore = new SqliteSyncStore(db);
        SyncHandlers = new PosSyncHandlers(db, Master, clock);
    }

    public PosDatabase Database { get; }
    public MasterDataService Master { get; }
    public SalesService Sales { get; }
    public CashService Cash { get; }
    public AuditService Audit { get; }
    public SqliteSyncStore SyncStore { get; }
    public PosSyncHandlers SyncHandlers { get; }

    public static async Task<PosWorkspace> OpenAsync(string databasePath, IPosContext context, TimeProvider? clock = null,
        CancellationToken ct = default)
    {
        var db = new PosDatabase(databasePath);
        await db.MigrateAsync(ct);
        return new PosWorkspace(db, context, clock ?? TimeProvider.System);
    }
}
