using Microsoft.Extensions.Logging;
using VortexApi.Client;
using VortexApi.Client.Sessions;
using VortexApi.Client.Sync;
using VortexPos.Core;
using VortexPos.Core.Domain;
using VortexPos.Core.Services;
using VortexPos.Core.Sync;

using static VortexPos.Services.Txt;

namespace VortexPos.Services;

public enum ConnectionState
{
    /// <summary>Aún no se contactó al hub.</summary>
    Unknown,
    Online,

    /// <summary>Sin red, pero el offline token sigue vigente: se trabaja normalmente y se sincroniza al volver.</summary>
    Offline,

    /// <summary>Sin red y con el periodo sin conexión agotado: hay que conectarse.</summary>
    OfflineExpired,
}

public sealed record SyncStatus(
    bool IsSyncing, DateTimeOffset? LastSyncAt, int Pending, int Failed, string? LastError, VortexSyncResult? LastResult);

/// <summary>
/// Estado de la app: sesión con el hub (login, renovación, heartbeat, modo sin conexión), la base local de la
/// organización y la sincronización periódica. Los componentes se suscriben a <see cref="Changed"/> (estado) y
/// <see cref="DataChanged"/> (datos nuevos que recargar).
/// </summary>
public sealed class AppSession(HubSettings settings, ISessionStore sessionStore, ILogger<AppSession> logger) : IPosContext, IAsyncDisposable
{
    private const string ProductCode = PosSyncHandlers.ProductCode;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _deviceId = DeviceIdentity.Get();
    private VortexApiClient? _client;
    private VortexSessionManager? _sessions;
    private VortexSync? _sync;
    private CancellationTokenSource? _loops;
    private SemaphoreSlim _wake = new(0);
    private DateTimeOffset? _lastSyncAt;
    private string? _lastError;
    private VortexSyncResult? _lastResult;
    private bool _syncing;
    private int _pending, _failed;

    public event Action? Changed;
    public event Action? DataChanged;

    public bool Initialized { get; private set; }
    public VortexSession? Session { get; private set; }
    public PosWorkspace? Workspace { get; private set; }
    public ConnectionState Connection { get; private set; }
    public DateTimeOffset? OfflineValidUntil { get; private set; }

    /// <summary>Motivo del último cierre de sesión forzado (acceso retirado, equipo dado de baja…), para el login.</summary>
    public string? SignedOutReason { get; private set; }

    public bool IsSignedIn => Session is not null && Workspace is not null;
    public string HubUrl => settings.HubUrl;
    public string ProductKey => settings.ProductKey;
    public string DeviceId => _deviceId;
    public SyncStatus Sync => new(_syncing, _lastSyncAt, _pending, _failed, _lastError, _lastResult);

    // ---------- IPosContext ----------

    /// <summary>
    /// Rol efectivo en el POS: el del supervisor que autoriza la acción en curso, el del cajero que entró con su PIN o el
    /// que asignó el OrgAdmin en el hub al usuario de la sesión (<c>cajero</c> si no tiene).
    /// </summary>
    public string Role => _authorizer?.Role ?? _operator?.Role ?? PosPermissions.Matrix.EffectiveRole(Session?.Access.Role, Session?.User.Role);

    public bool Can(string permission) =>
        _authorizer is { } a ? PosPermissions.Matrix.Can(a.Role, "User", permission)
        : _operator is { } o ? PosPermissions.Matrix.Can(o.Role, "User", permission)
        : PosPermissions.Matrix.Can(Session?.Access.Role, Session?.User.Role, permission);

    /// <summary>
    /// Si la acción se puede ofrecer en pantalla: el rol actual la permite, o algún supervisor con PIN en este equipo
    /// podría autorizarla en el momento (al intentarla, la app pide su PIN).
    /// </summary>
    public bool CanOrApprove(string permission) =>
        Can(permission) || Desk.Verifiers.Any(v =>
            PosPermissions.Matrix.Can(PosPermissions.Matrix.EffectiveRole(v.Role, "User"), "User", permission));

    public Guid? UserId => _operator?.UserId ?? Session?.User.Id;
    public string UserName => _operator?.Name ?? Session?.User.FullName ?? "";
    public string? AuthorizedBy => _authorizer?.Name;
    public bool IsOnline => Connection == ConnectionState.Online;
    public string DeviceTag => DeviceIdentity.Tag(_deviceId);

    /// <summary>Tiendas asignadas en el hub (<c>Access.SiteIds</c>). Los administradores de la organización ven todas.</summary>
    public IReadOnlyList<Guid> SiteIds =>
        Session is { } s && s.User.Role is not ("OrgAdmin" or "PlatformAdmin") ? s.Access.SiteIds ?? [] : [];

    public Guid? DefaultSiteId =>
        Session is { } s && Preferences.Default.Get<string?>(DefaultSiteKey(s), null) is { } id && Guid.TryParse(id, out var g) ? g : null;

    // ---------- Operador de la caja (PIN) ----------

    private PosOperator? _operator;
    private PosOperator? _authorizer;
    private DateTimeOffset _lastActivity = DateTimeOffset.UtcNow;
    private const string LockMinutesKey = "vortex.lock.minutes";

    /// <summary>Verificadores de PIN de los miembros del POS (del hub; sin conexión, los últimos guardados en el equipo).</summary>
    public OperatorDesk Desk { get; private set; } = new([]);

    /// <summary>Cajero que entró con su PIN sin cerrar la sesión del hub (null: el usuario de la sesión).</summary>
    public PosOperator? Operator => _operator;

    public bool IsLocked { get; private set; }

    /// <summary>Minutos sin actividad para bloquear la caja (0: no se bloquea sola). Es de este equipo.</summary>
    public int LockMinutes
    {
        get => Preferences.Default.Get(LockMinutesKey, 0);
        set => Preferences.Default.Set(LockMinutesKey, Math.Clamp(value, 0, 240));
    }

    /// <summary>Cambia el cajero de la caja (o desbloquea) con un PIN. Si es el del usuario de la sesión, vuelve a él.</summary>
    public void SwitchOperator(string pin)
    {
        var who = Desk.Identify(pin);
        _operator = who.UserId == Session?.User.Id ? null : who;
        IsLocked = false;
        _lastActivity = DateTimeOffset.UtcNow;
        Changed?.Invoke();
    }

    public void Lock()
    {
        if (!Desk.HasPins)
            return;
        IsLocked = true;
        Changed?.Invoke();
    }

    /// <summary>Lo llama la interfaz en cada interacción, para el bloqueo por inactividad.</summary>
    public void Touch() => _lastActivity = DateTimeOffset.UtcNow;

    private void CheckIdleLock()
    {
        if (!IsLocked && LockMinutes > 0 && Desk.HasPins && DateTimeOffset.UtcNow - _lastActivity > TimeSpan.FromMinutes(LockMinutes))
            Lock();
    }

    /// <summary>
    /// Ejecuta una acción con los permisos de un supervisor que la autorizó con su PIN; la auditoría registra quién
    /// autorizó. Solo dura lo que dura la acción.
    /// </summary>
    public async Task RunAuthorizedAsync(PosOperator supervisor, Func<Task> action)
    {
        _authorizer = supervisor;
        try
        {
            await action();
        }
        finally
        {
            _authorizer = null;
        }
    }

    /// <summary>Fija (o quita, con null) el PIN del usuario de la sesión en el hub. Necesita conexión.</summary>
    public async Task<string?> SetMyPinAsync(string currentPassword, string? pin)
    {
        if (_sessions is not { } sessions || _client is not { } client)
            return T("session.sin_sesion");
        if (_operator is not null)
            return T("session.pin_solo_de_la_sesion");
        try
        {
            await sessions.WithAccessTokenAsync(async token =>
            {
                await client.SetPinAsync(token, currentPassword, string.IsNullOrWhiteSpace(pin) ? null : pin.Trim());
                return 0;
            });
            await LoadPinsAsync(fromHub: true);
            return null;
        }
        catch (VortexApiException ex)
        {
            return ex.Message;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return T("session.necesitas_conexion_para_el_pin");
        }
    }

    /// <summary>
    /// Trae los verificadores del hub y los guarda en almacenamiento seguro (nunca en la base local); sin conexión usa los
    /// últimos guardados. Un PIN es corto: un verificador expuesto se podría adivinar probando.
    /// </summary>
    private async Task LoadPinsAsync(bool fromHub)
    {
        if (Session is not { } s)
            return;
        var key = $"vortex.pins.{s.Access.OrganizationId:N}";
        IReadOnlyList<VortexPinVerifier>? verifiers = null;
        if (fromHub && _sessions is { } sessions && _client is { } client)
        {
            try
            {
                verifiers = await sessions.WithAccessTokenAsync(token => client.GetPinVerifiersAsync(token));
                await SecureStorage.Default.SetAsync(key, System.Text.Json.JsonSerializer.Serialize(verifiers, VortexJson.Options));
            }
            catch (Exception ex) when (ex is VortexApiException or HttpRequestException or TaskCanceledException)
            {
                logger.LogInformation(ex, "Sin verificadores de PIN nuevos");
            }
        }
        if (verifiers is null && await SecureStorage.Default.GetAsync(key) is { Length: > 0 } json)
            verifiers = System.Text.Json.JsonSerializer.Deserialize<List<VortexPinVerifier>>(json, VortexJson.Options);
        if (verifiers is not null && (Desk.Verifiers.Count != verifiers.Count || !Desk.Verifiers.SequenceEqual(verifiers)))
            Desk = new OperatorDesk(verifiers);
    }

    public void SetDefaultSite(Guid? siteId)
    {
        if (Session is not { } s)
            return;
        if (siteId is { } id)
            Preferences.Default.Set(DefaultSiteKey(s), id.ToString());
        else
            Preferences.Default.Remove(DefaultSiteKey(s));
        Changed?.Invoke();
    }

    private static string DefaultSiteKey(VortexSession s) => $"vortex.default_site.{s.Access.OrganizationId:N}";

    // ---------- Ciclo de vida ----------

    /// <summary>Al abrir la app: si hay una sesión guardada, abre la base y arranca la sincronización (aun sin red).</summary>
    public async Task InitializeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (Initialized)
                return;
            await settings.LoadAsync();
            if (LanguagePreference is { } language)
                Fmt.SetRegion(language, null, null);
            BuildClient();
            if (await _sessions!.GetSessionAsync() is { } session)
                await StartAsync(session);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "No se pudo restaurar la sesión");
        }
        finally
        {
            Initialized = true;
            _gate.Release();
            Changed?.Invoke();
        }
    }

    /// <summary>Inicia sesión. Devuelve el mensaje de error para el usuario, o null si entró.</summary>
    public async Task<string?> LoginAsync(string email, string password, string hubUrl, string productKey)
    {
        if (!HubSettings.TryNormalize(hubUrl, out var url))
            return T("session.la_direccion_del_servidor_no");
        if (string.IsNullOrWhiteSpace(productKey))
            return T("session.falta_la_clave_de_sistema");

        await _gate.WaitAsync();
        try
        {
            await settings.SaveAsync(url, productKey);
            BuildClient();
            var session = await _sessions!.LoginAsync(email.Trim(), password, DeviceInfo.Current.Name,
                DeviceInfo.Current.Platform.ToString());
            AdoptUserLanguage(session);
            SignedOutReason = null;
            await StartAsync(session);
            return null;
        }
        catch (VortexApiException ex)
        {
            return HubErrors.For(ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return T("session.no_se_pudo_conectar_con", url);
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke();
        }
    }

    public async Task SignOutAsync(string? reason = null)
    {
        await StopAsync();
        _operator = null;
        IsLocked = false;
        if (_sessions is not null)
            await _sessions.SignOutAsync();
        Session = null;
        Workspace = null;
        Connection = ConnectionState.Unknown;
        SignedOutReason = reason;
        Changed?.Invoke();
    }

    private void BuildClient()
    {
        if (_sessions is not null)
            _sessions.SessionEnded -= OnSessionEnded;
        _client = new VortexApiClient(new HttpClient { BaseAddress = new Uri(settings.HubUrl), Timeout = TimeSpan.FromSeconds(20) },
            new VortexApiClientOptions { ProductCode = ProductCode, ProductKey = settings.ProductKey });
        _sessions = new VortexSessionManager(_client, sessionStore, _deviceId);
        _sessions.SessionEnded += OnSessionEnded;
    }

    private async Task StartAsync(VortexSession session)
    {
        await StopAsync();
        Session = session;
        var path = Path.Combine(FileSystem.AppDataDirectory, $"vortex-pos-{session.Access.OrganizationId:N}.db");
        Workspace = await PosWorkspace.OpenAsync(path, this);
        _sync = Workspace.SyncHandlers.Register(new VortexSync(_client!, _sessions!, Workspace.SyncStore, PosSyncHandlers.SyncOptions));
        await ApplyRegionAsync();
        _pending = await Workspace.SyncStore.CountPendingAsync();
        _failed = (await Workspace.SyncStore.GetFailedAsync()).Count;
        _lastError = null;

        _loops = new CancellationTokenSource();
        _wake = new SemaphoreSlim(0);
        var ct = _loops.Token;
        _operator = null;
        await LoadPinsAsync(fromHub: false);
        _lastActivity = DateTimeOffset.UtcNow;
        _ = Task.Run(() => HeartbeatLoopAsync(ct), ct);
        _ = Task.Run(() => IdleLoopAsync(ct), ct);
        _ = Task.Run(() => SyncLoopAsync(ct), ct);
    }

    /// <summary>Revisa cada 20 segundos si la caja lleva el tiempo configurado sin actividad, para bloquearla.</summary>
    private async Task IdleLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
        while (await WaitAsync(timer, ct))
            CheckIdleLock();
    }

    private async Task StopAsync()
    {
        if (_loops is null)
            return;
        await _loops.CancelAsync();
        _loops.Dispose();
        _loops = null;
    }

    private void OnSessionEnded(object? sender, VortexApiException ex) =>
        _ = SignOutAsync(HubErrors.For(ex));

    // ---------- Heartbeat y modo sin conexión ----------

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(settings.HeartbeatInterval);
        do
        {
            await HeartbeatAsync(ct);
        } while (await WaitAsync(timer, ct));
    }

    private async Task HeartbeatAsync(CancellationToken ct)
    {
        try
        {
            var heartbeat = await _sessions!.HeartbeatAsync(ct);
            Session = await _sessions.GetSessionAsync(ct) ?? Session;
            OfflineValidUntil = heartbeat.OfflineTokenExpiresAt;
            SetConnection(ConnectionState.Online);
            await LoadPinsAsync(fromHub: true);
            CheckIdleLock();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            await GoOfflineAsync(ct);
            CheckIdleLock();
        }
        catch (VortexApiException ex) when (ex.IsAccessDenied || ex.StatusCode == 401)
        {
            await SignOutAsync(HubErrors.For(ex));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Heartbeat");
        }
    }

    private async Task GoOfflineAsync(CancellationToken ct)
    {
        var offline = await _sessions!.ValidateOfflineAsync(settings.Issuer, ct);
        OfflineValidUntil = offline.Access?.ValidUntil;
        SetConnection(offline.IsValid ? ConnectionState.Offline : ConnectionState.OfflineExpired);
    }

    private void SetConnection(ConnectionState state)
    {
        if (Connection == state)
            return;
        Connection = state;
        Changed?.Invoke();
    }

    // ---------- Sincronización ----------

    /// <summary>Pide sincronizar ya (p. ej. después de registrar una operación).</summary>
    public void SyncNow()
    {
        if (_wake.CurrentCount == 0)
            _wake.Release();
    }

    /// <summary>Avisa a las pantallas que cambiaron datos locales y sube los cambios en cuanto haya red.</summary>
    public void NotifyLocalChange()
    {
        _ = RefreshCountersAsync();
        DataChanged?.Invoke();
        SyncNow();
    }

    private async Task SyncLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await SyncOnceAsync(ct);
            try
            {
                await _wake.WaitAsync(settings.SyncInterval, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task SyncOnceAsync(CancellationToken ct)
    {
        if (_sync is null || Workspace is null)
            return;
        _syncing = true;
        Changed?.Invoke();
        var hadData = false;
        try
        {
            var result = await _sync.SyncAsync(ct);
            _lastResult = result;
            if (result.Completed)
            {
                _lastSyncAt = DateTimeOffset.Now;
                _lastError = null;
                SetConnection(ConnectionState.Online);
            }
            else
            {
                await GoOfflineAsync(ct);
            }
            hadData = result.PulledChanges + result.PulledEvents + result.Failed > 0;
        }
        catch (VortexSyncException ex)
        {
            _lastError = ex.Message;
            logger.LogError(ex, "Sincronización detenida en un cambio del feed");
        }
        catch (VortexApiException ex) when (ex.IsAccessDenied || ex.StatusCode == 401)
        {
            await SignOutAsync(HubErrors.For(ex));
            return;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _lastError = ex is VortexApiException api ? HubErrors.For(api) : ex.Message;
            logger.LogError(ex, "Sincronización");
        }
        finally
        {
            _syncing = false;
        }

        await RefreshCountersAsync();
        if (hadData)
            await ApplyRegionAsync();
        if (hadData)
            DataChanged?.Invoke();
        Changed?.Invoke();
    }

    private async Task RefreshCountersAsync()
    {
        if (Workspace is not { } pos)
            return;
        try
        {
            _pending = await pos.SyncStore.CountPendingAsync();
            _failed = (await pos.SyncStore.GetFailedAsync()).Count;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Contadores de sincronización");
        }
    }

    private const string LanguageKey = "ui.language";

    /// <summary>Idioma elegido en este equipo (es, en, pt), o null para usar el del país de la empresa.</summary>
    public string? LanguagePreference =>
        Preferences.Default.Get<string?>(LanguageKey, null) is { Length: > 0 } l ? VortexApi.Suite.Regional.Languages.Normalize(l) : null;

    /// <summary>Cambia el idioma de la interfaz en este equipo (null = el del país de la empresa).</summary>
    public async Task SetLanguageAsync(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            Preferences.Default.Remove(LanguageKey);
        else
            Preferences.Default.Set(LanguageKey, VortexApi.Suite.Regional.Languages.Normalize(language));
        if (Workspace is null)
            Fmt.SetRegion(LanguagePreference ?? VortexApi.Suite.Regional.Languages.Spanish, null, null);
        else
            await ApplyRegionAsync();
        Changed?.Invoke();
        DataChanged?.Invoke();
        await SaveLanguageInHubAsync();
    }

    /// <summary>El idioma guardado en el hub sigue al usuario: al entrar en un equipo nuevo se usa ese.</summary>
    private static void AdoptUserLanguage(VortexSession session)
    {
        if (session.User.Language is { Length: > 0 } language)
            Preferences.Default.Set(LanguageKey, VortexApi.Suite.Regional.Languages.Normalize(language));
    }

    /// <summary>Guarda el idioma en el hub para los demás sistemas y equipos; sin conexión queda solo en este equipo.</summary>
    private async Task SaveLanguageInHubAsync()
    {
        if (Session is null || _sessions is not { } sessions || _client is not { } client)
            return;
        try
        {
            await sessions.WithAccessTokenAsync(token => client.SetLanguageAsync(token, LanguagePreference));
        }
        catch (Exception ex) when (ex is VortexApiException or HttpRequestException or TaskCanceledException)
        {
            logger.LogInformation(ex, "El idioma quedó solo en este equipo");
        }
    }

    /// <summary>Idioma (el elegido en este equipo o el del país) y formatos de fecha, número y moneda de la empresa.</summary>
    private async Task ApplyRegionAsync()
    {
        try
        {
            if (Workspace is not null && await Workspace.Master.GetCompanyAsync() is { } company)
                Fmt.SetRegion(LanguagePreference ?? VortexApi.Suite.Regional.Languages.DefaultFor(company.CountryCode), company.CountryCode, company.TimeZone);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Región de la empresa");
        }
    }

    public async Task<IReadOnlyList<VortexFailedEntry>> GetFailedAsync() =>
        Workspace is { } pos ? await pos.SyncStore.GetFailedAsync() : [];

    public async Task DismissFailedAsync(Guid id)
    {
        if (Workspace is not { } pos)
            return;
        await pos.SyncStore.DismissFailedAsync(id);
        await RefreshCountersAsync();
        DataChanged?.Invoke();
        Changed?.Invoke();
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
