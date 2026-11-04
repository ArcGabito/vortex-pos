using VortexApi.Suite.Security;

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

    public const string Void = "sale.void";
    public const string Return = "sale.return";
    public const string OpenCash = "cash.open";
    public const string CloseCash = "cash.close";

    /// <summary>Registrar clientes nuevos en caja.</summary>
    public const string CreateCustomer = "customer.create";

    /// <summary>Crear o editar artículos y sedes (datos maestros de la suite).</summary>
    public const string EditCatalog = "catalog.edit";

    public const string ViewAudit = "audit.view";

    /// <summary>Descuento máximo de un cajero: 10 % del importe de la línea.</summary>
    public const decimal CashierDiscountLimit = 0.10m;

    public const string Cashier = "cajero";
    public const string Supervisor = "supervisor";

    public static readonly PermissionMatrix Matrix = PermissionMatrix.Create(Cashier,
        (Cashier, [Sell, OpenCash, CreateCustomer]),
        (Supervisor, [Sell, OpenCash, CreateCustomer, DiscountUnlimited, Void, Return, CloseCash, ViewAudit]));

    public static string Describe(string permission) => permission switch
    {
        Sell => "vender",
        DiscountUnlimited => "dar descuentos mayores al 10 %",
        Void => "anular ventas",
        Return => "registrar devoluciones",
        OpenCash => "abrir la caja",
        CloseCash => "cerrar la caja",
        CreateCustomer => "registrar clientes",
        EditCatalog => "editar el catálogo y las tiendas",
        ViewAudit => "ver la auditoría",
        _ => permission,
    };
}
