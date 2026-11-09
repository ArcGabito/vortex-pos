using VortexApi.Suite.Security;

using static VortexPos.Core.Texts.CoreText;

namespace VortexPos.Core.Domain;

/// <summary>
/// Qué puede hacer cada rol del POS. El OrgAdmin asigna el rol en el hub (Sistemas → POS → miembros); quien no tiene
/// rol entra como <c>cajero</c>. Se valida en los servicios de Core, no solo en la interfaz.
/// </summary>
public static class PosPermissions
{
    public const string Sell = "sale.create";

    /// <summary>Descuentos por encima de <see cref="CashierDiscountLimit"/> del importe de la línea.</summary>
    public const string DiscountUnlimited = "sale.discount_unlimited";

    /// <summary>Cobrar un artículo a otro precio que el de lista.</summary>
    public const string ChangePrice = "sale.change_price";

    /// <summary>Crear y cambiar promociones y cupones.</summary>
    public const string ManagePromotions = "promo.manage";

    public const string Void = "sale.void";
    public const string Return = "sale.return";
    public const string OpenCash = "cash.open";
    public const string CloseCash = "cash.close";

    /// <summary>Registrar clientes nuevos en caja.</summary>
    public const string CreateCustomer = "customer.create";

    /// <summary>Crear o editar artículos y sedes (datos maestros de la suite).</summary>
    public const string EditCatalog = "catalog.edit";

    public const string ViewAudit = "audit.view";

    /// <summary>Ingresos y retiros de efectivo del cajón.</summary>
    public const string CashMovement = "cash.movement";

    /// <summary>Reporte X (parcial) del turno, con el efectivo esperado.</summary>
    public const string CashReport = "cash.report";

    /// <summary>Configuración de la tienda que comparten sus cajas (ticket, cierre ciego).</summary>
    public const string StoreSettings = "store.settings";

    /// <summary>Descuento máximo de un cajero: 10 % del importe de la línea.</summary>
    public const decimal CashierDiscountLimit = 0.10m;

    /// <summary>Devolver fuera del plazo de la política o sin ticket.</summary>
    public const string ReturnOverride = "sale.return_override";

    public const string Cashier = "cajero";
    public const string Supervisor = "supervisor";

    public static readonly PermissionMatrix Matrix = PermissionMatrix.Create(Cashier,
        (Cashier, [Sell, OpenCash, CreateCustomer, Return]),
        (Supervisor, [Sell, OpenCash, CreateCustomer, DiscountUnlimited, ChangePrice, ManagePromotions, Void, Return, ReturnOverride, CloseCash, ViewAudit, CashMovement, CashReport,
            StoreSettings]));

    public static string Describe(string permission) => permission switch
    {
        Sell => T("permiso.sell"),
        DiscountUnlimited => T("permiso.discountunlimited"),
        ChangePrice => T("permiso.changeprice"),
        ManagePromotions => T("permiso.managepromotions"),
        Void => T("permiso.void"),
        Return => T("permiso.return"),
        ReturnOverride => T("permiso.returnoverride"),
        OpenCash => T("permiso.opencash"),
        CloseCash => T("permiso.closecash"),
        CreateCustomer => T("permiso.createcustomer"),
        EditCatalog => T("permiso.editcatalog"),
        ViewAudit => T("permiso.viewaudit"),
        CashMovement => T("permiso.cashmovement"),
        CashReport => T("permiso.cashreport"),
        StoreSettings => T("permiso.storesettings"),
        _ => permission,
    };
}
