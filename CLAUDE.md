# CLAUDE.md

Vortex POS: punto de venta de la suite Vortex (ERP, CRM, WMS, POS). App .NET MAUI Blazor Hybrid, AGPL-3.0, textos de
la interfaz y documentación en español (es-PE). Sigue la misma arquitectura que Vortex WMS (la referencia de la suite).

## Comandos

```bash
dotnet test tests/VortexPos.Core.Tests                                   # pruebas de ventas, caja y sincronización
VORTEX_HUB_URL=http://localhost:5299/ dotnet test tests/VortexPos.Core.Tests   # + prueba contra un hub real
dotnet build src/VortexPos -f net10.0-windows10.0.19041.0                # app en Windows (también -f net10.0-android)
dotnet run --project src/VortexPos -f net10.0-windows10.0.19041.0        # compila y abre (en Windows, -t:Run no compila)
scripts/pack-sdk.sh   |   ./scripts/pack-sdk.ps1                         # SDK del hub -> packages/vortex-sdk
```

## Estructura

- `src/VortexPos.Core`: todo lo que no es interfaz. `Data/` (SQLite con Microsoft.Data.Sqlite sin ORM, migraciones
  por `PRAGMA user_version` en `PosDatabase`), `Services/` (`SalesService` cobra, anula y devuelve; `CashService`
  abre y cierra turnos; `MasterDataService`), `Sync/` (store de la sincronización y handlers), `Integration/` (eventos v1).
- `src/VortexPos`: app. `Services/AppSession.cs` (sesión, heartbeat, bucle de sincronización), `Components/Pages`
  (pantallas; heredan `PosPage`), `Components/Shared`, `wwwroot/css/app.css` (sistema de diseño de la suite).
- `tests/VortexPos.Core.Tests`: xUnit sobre bases SQLite temporales; `SyncTests` simula otras cajas y sistemas.

## Reglas

- Cobrar, anular y devolver guardan el documento y encolan su evento en la misma transacción (`Outbox`).
- Una venta no se edita: se anula (Id de evento determinista) o se devuelve en parte.
- Impuestos, monedas, formatos y permisos vienen de `VortexApi.Suite`: nada fijo para un país. Cada acción sensible
  valida `RequirePermission` (`PosPermissions`) y deja su `AuditAsync` en la misma transacción.
- El stock es del WMS: el POS guarda el saldo absoluto de `stock.adjusted` y nunca lo modifica.
- Los handlers de eventos son idempotentes (`processed_events`) y nunca lanzan por datos inválidos.
- Errores para el usuario: `PosValidationException` con el mensaje final en español.
- En Razor, dentro de atributos usa `@(...)` cuando haya comillas y no nombres variables `code` ni `page`.
- UI: reutiliza las clases de `app.css` y los componentes compartidos (`Modal`, `KebabMenu`, `StatTile`,
  `StatusPill`, `Pager`, `ItemPicker`).

## Hub Vortex (identidad, datos maestros e integración)

Este sistema es **Vortex POS** (`ProductCode = "POS"`) y depende del hub `vortex-api`, clonado en `~/vortex-api`.
Antes de tocar login, sesiones, usuarios, clientes, artículos, sedes, datos de la empresa o comunicación con otro
sistema de la suite, lee `~/vortex-api/docs/SISTEMAS.md` y síguelo (`docs/INTEGRACION.md` resume cómo lo aplica el POS).

- Levantar el hub: `cd ~/vortex-api && docker compose up -d && dotnet run --project src/VortexApi --launch-profile http`
  (http://localhost:5299, referencia en /docs). Usuario demo: `luis@vortex.local` / `Suite-Local-2026!`,
  clave de sistema `vxp_pos_dev_key_change_me`.
- SDK: paquete `VortexApi.Client` desde el feed local `packages/vortex-sdk` (`scripts/pack-sdk.*`). Si cambias el
  SDK, hazlo en `~/vortex-api` con sus pruebas y vuelve a empaquetar.
- No crear tablas de usuarios ni contraseñas; no llamar directamente a otros sistemas; usar Ids del hub.
- Si falta algo en el hub, proponer el cambio en `~/vortex-api` en vez de un parche local.
