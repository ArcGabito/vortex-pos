using Microsoft.AspNetCore.Components;
using VortexPos.Core;
using VortexPos.Core.Domain;
using VortexPos.Services;

using static VortexPos.Services.Txt;

namespace VortexPos.Components;

/// <summary>
/// Base de las pantallas del POS: acceso a la base de la organización, recarga automática cuando la sincronización
/// trae datos nuevos y ejecución de operaciones con mensajes para el usuario.
/// </summary>
public abstract class PosPage : ComponentBase, IDisposable
{
    [Inject] protected AppSession Session { get; set; } = null!;
    [Inject] protected Toasts Toasts { get; set; } = null!;
    [Inject] protected NavigationManager Nav { get; set; } = null!;
    [Inject] protected TicketPrinter Printer { get; set; } = null!;

    protected PosWorkspace Pos => Session.Workspace!;
    protected bool Loading { get; private set; } = true;

    protected override async Task OnInitializedAsync()
    {
        Session.DataChanged += OnDataChanged;
        await ReloadAsync();
    }

    /// <summary>Carga los datos de la pantalla.</summary>
    protected abstract Task LoadAsync();

    protected async Task ReloadAsync()
    {
        if (Session.Workspace is null)
            return;
        try
        {
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Toasts.Error(T("page.no_se_pudieron_cargar_los", ex.Message));
        }
        finally
        {
            Loading = false;
        }
    }

    private void OnDataChanged() => _ = InvokeAsync(async () =>
    {
        await ReloadAsync();
        StateHasChanged();
    });

    /// <summary>
    /// Ejecuta una operación del POS. Devuelve el mensaje de error para mostrar (o null si salió bien); al terminar
    /// avisa a las demás pantallas y pide sincronizar.
    /// </summary>
    protected async Task<string?> TryAsync(Func<Task> action, string? success = null)
    {
        try
        {
            await action();
        }
        catch (PosValidationException ex)
        {
            return ex.Message;
        }
        catch (Exception ex)
        {
            return T("page.no_se_pudo_completar_la", ex.Message);
        }
        if (success is not null)
            Toasts.Success(success);
        Session.NotifyLocalChange();
        return null;
    }

    /// <summary>Como <see cref="TryAsync"/>, pero el error se muestra como aviso.</summary>
    protected async Task RunAsync(Func<Task> action, string? success = null)
    {
        if (await TryAsync(action, success) is { } error)
            Toasts.Error(error);
    }

    /// <summary>Imprime el ticket de una venta en la impresora de esta caja (con la configuración de su tienda).</summary>
    protected async Task PrintSaleAsync(Guid saleId, bool reprint = false)
    {
        if (await Pos.Sales.GetTicketDataAsync(saleId) is not { } data)
            return;
        var settings = await Pos.Stores.GetAsync(data.Sale.SiteId);
        if (await Printer.PrintAsync(Core.Printing.TicketBuilder.ForSale(data, settings, Fmt.Culture, reprint)) is { } error)
            Toasts.Error(error);
    }

    public virtual void Dispose()
    {
        Session.DataChanged -= OnDataChanged;
        GC.SuppressFinalize(this);
    }
}
