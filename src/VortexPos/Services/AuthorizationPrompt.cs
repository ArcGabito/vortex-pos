using VortexPos.Core.Domain;

namespace VortexPos.Services;

/// <summary>
/// Pide el PIN de un supervisor cuando la acción que intenta el cajero no está permitida para su rol. El diálogo lo
/// muestra el layout; quien pide espera el supervisor que autorizó (o null si se canceló).
/// </summary>
public sealed class AuthorizationPrompt
{
    private TaskCompletionSource<PosOperator?>? _pending;

    public event Action? Changed;

    /// <summary>Permiso que falta (para mostrarlo), mientras el diálogo está abierto.</summary>
    public string? Permission { get; private set; }

    public Task<PosOperator?> RequestAsync(string permission)
    {
        _pending?.TrySetResult(null);
        _pending = new TaskCompletionSource<PosOperator?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Permission = permission;
        Changed?.Invoke();
        return _pending.Task;
    }

    public void Complete(PosOperator? supervisor)
    {
        Permission = null;
        _pending?.TrySetResult(supervisor);
        _pending = null;
        Changed?.Invoke();
    }
}
