using VortexApi.Client;
using VortexPos.Core.Domain;

using static VortexPos.Core.Texts.CoreText;

namespace VortexPos.Core.Services;

/// <summary>
/// Identifica a quien opera la caja por su PIN, contra los verificadores que entrega el hub (<c>GET api/access/pins</c>),
/// también sin conexión: para cambiar de cajero sin cerrar la sesión, desbloquear la pantalla o que un supervisor
/// autorice una acción. Tras <see cref="MaxAttempts"/> PIN equivocados seguidos, espera <see cref="Lockout"/>.
/// </summary>
public sealed class OperatorDesk(IReadOnlyList<VortexPinVerifier> verifiers, TimeProvider? clock = null)
{
    public const int MaxAttempts = 5;
    public static readonly TimeSpan Lockout = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private int _failures;
    private DateTimeOffset? _lockedUntil;

    public IReadOnlyList<VortexPinVerifier> Verifiers { get; } = verifiers;

    /// <summary>Si hay miembros con PIN en este equipo (si no, no se puede cambiar de cajero ni autorizar con PIN).</summary>
    public bool HasPins => Verifiers.Count > 0;

    /// <summary>El miembro dueño del PIN. Un PIN equivocado cuenta como intento fallido.</summary>
    public PosOperator Identify(string pin)
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            if (_lockedUntil is { } until && until > now)
                throw new PosValidationException(T("error.pin_bloqueado", Math.Ceiling((until - now).TotalSeconds)));

            var match = Verifiers.FirstOrDefault(v => VortexPin.Verify(pin?.Trim(), v.PinHash));
            if (match is null)
            {
                if (++_failures >= MaxAttempts)
                {
                    _lockedUntil = now + Lockout;
                    _failures = 0;
                }
                throw new PosValidationException(T("error.pin_incorrecto"));
            }
            _failures = 0;
            _lockedUntil = null;
            return new PosOperator(match.UserId, match.FullName, PosPermissions.Matrix.EffectiveRole(match.Role, "User"));
        }
    }

    /// <summary>El supervisor (o administrador) dueño del PIN, si su rol permite <paramref name="permission"/>.</summary>
    public PosOperator Authorize(string pin, string permission)
    {
        var who = Identify(pin);
        if (!PosPermissions.Matrix.Can(who.Role, "User", permission))
            throw new PosValidationException(T("error.pin_no_autoriza", who.Name, PosPermissions.Describe(permission)));
        return who;
    }
}
