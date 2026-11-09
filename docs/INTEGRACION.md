# Integración del POS con el hub y la suite

Vortex POS sigue la guía del hub ([`vortex-api/docs/SISTEMAS.md`](https://github.com/ArcGabito/vortex-api/blob/main/docs/SISTEMAS.md)).
Este documento resume qué hace el POS con cada dato y evento, y cómo conviven varias cajas que venden sin conexión.

## Identidad y sesión

- No hay tabla de usuarios: el login es contra el hub (`ProductCode = "POS"`, clave de sistema en `appsettings.json`
  o en la pantalla de ingreso).
- Los tokens se guardan en `SecureStorage`. Con el offline token vigente la caja abre y vende sin red; el chip de
  sincronización avisa cuando el periodo sin conexión se agota.
- El heartbeat (cada 5 min) renueva el offline token y detecta accesos retirados o equipos dados de baja.
- Cada organización tiene su propia base SQLite (`vortex-pos-{organizationId}.db` en `AppDataDirectory`).

## Datos maestros (dueño: el hub)

| Dato | En el POS | Altas/cambios desde el POS |
|---|---|---|
| Artículos | Catálogo de venta, precio de lista (con IGV si `taxCode = igv`) | Sí (`PUT /api/master/items/{id}`) |
| Sedes | Tiendas donde está cada caja | Alta (`PUT /api/master/sites/{id}`) |
| Clientes | Cliente opcional de la venta (obligatorio al crédito) | Alta (`PUT /api/master/parties/{id}`) |
| Empresa | Moneda | No |
| Listas de precios | Precio de la lista del cliente (`credit.priceListId`) o de la tienda (`siteIds`), con escalas | No (las maneja el ERP) |

La copia local solo se actualiza desde el feed, por `id` y `version`. Un alta hecha en el POS queda **POR ENVIAR**
hasta que vuelve por el feed; si el hub la rechaza aparece en **Configuración → Rechazadas por el hub**.

## Impuestos, monedas y permisos (base común de la suite)

- El impuesto sale del país de la empresa y de su tasa general en el hub (`TaxCatalog.For(country, defaultTaxRate)`):
  incluido en el precio en Perú, LATAM y Europa; sumado al cobrar en EE. UU. El desglose es por tasa y se redondea
  con los decimales de la moneda.
- Roles del POS (`session.Access.Role`, los asigna el OrgAdmin): `cajero` (vende, abre caja, registra clientes),
  `supervisor` (además anula, devuelve, cierra caja, descuentos sin límite y ve la auditoría) y `administrador`
  (todo, también el catálogo). OrgAdmin y PlatformAdmin del hub son administradores. Sin rol asignado: `cajero`.
  Ingresos y retiros de efectivo, reportes X y Z y la configuración de la tienda son de `supervisor` en adelante.
- Tiendas por usuario: si el OrgAdmin asigna tiendas al usuario en el hub (`Access.SiteIds`), solo abre caja en
  ellas y en Ventas y el panel ve solo sus ventas. Sin tiendas asignadas, o siendo OrgAdmin, todas.
- PIN de caja (fase 2b): cada usuario fija su PIN en Configuración (se guarda en el hub con `SetPinAsync`). El POS
  descarga los verificadores de los miembros del POS (`GetPinVerifiersAsync`), los guarda en el almacenamiento seguro
  del equipo y valida el PIN sin conexión (`VortexPin.Verify`) para cambiar de cajero, desbloquear la caja o que un
  supervisor autorice una acción que el rol del cajero no permite; la auditoría registra "autorizado por". El rol que
  vale es el del miembro en el POS (los administradores de la organización entran con su rol de miembro).

## Datos propios del POS

Ventas (líneas y pagos), devoluciones y turnos de caja con sus ingresos, retiros y conteo por denominación. Las ventas
viajan como eventos; los turnos de caja y los tickets en espera son de cada equipo y no se publican. La configuración
de cada tienda (texto del ticket, ancho del papel, cierre ciego, cajón) la comparten sus cajas con
`pos.record.upserted`. La impresora de tickets es de cada caja y se guarda en el equipo.

## Eventos (versión 1)

### Qué publica el POS

| Evento | Cuándo | Id del evento |
|---|---|---|
| `sale.completed` | Al cobrar | Id de la venta |
| `sale.voided` | Al anular una venta sin devoluciones | `DeterministicId("sale_void", saleId)` |
| `sale.returned` | Al registrar una devolución (parcial o total) | Id de la devolución |
| `pos.record.upserted` | Al guardar la configuración de una tienda (solo la leen las otras cajas del POS) | Nuevo en cada cambio |

`sale.completed` lleva el formato v1 (`saleId`, `number`, `siteId`, `customerId`, `subtotal`, `tax`, `total`, `lines`,
`payments`) y agrega `discount`, `createdBy` y, por línea, `name` y `taxable`. En `payments`, el efectivo va por lo
aplicado a la venta (sin el vuelto). Desde la fase 3a cada línea puede traer `presentation` (código de la presentación
del artículo con que se vendió; `quantity` sigue en la unidad del artículo: una caja de 12 son 12) y `listPrice` (el
precio de lista cuando la caja cobró otro con permiso). Ambos son opcionales y aditivos. Desde la 3b la venta trae `coupon` (el
cupón usado) y cada línea `promotionId` (la promoción que le dio descuento; `discount` ya incluye promociones y
descuentos manuales). Desde la 3c trae `priceListId` (la lista de precios que rigió, si hubo) y `listPrice` lleva el
precio de esa lista cuando difiere del precio del artículo.

### Qué consume el POS

| Evento | Efecto |
|---|---|
| `stock.adjusted` (WMS) | Guarda el saldo absoluto (`quantityOnHand`) por tienda y artículo; se muestra al vender y en el catálogo |
| `invoice.issued` (ERP) | Si trae `saleId`, anota el número de comprobante en la venta |
| `invoice.voided` (ERP) | Si trae `saleId`, quita ese número de la venta: queda sin comprobante y el ERP puede emitirle otro |
| `sale.completed`, `sale.voided`, `sale.returned` (otra caja POS) | Ver abajo |
| `pos.record.upserted` (otra caja POS) | Guarda la configuración de la tienda si es más reciente que la que tiene (`updatedAt`; a igual hora, desempata `updatedBy`) |

- Cada evento se aplica una sola vez (`processed_events`), en la misma transacción que su efecto.
- `schemaVersion` distinto de 1 o `data` ilegible se registran como ignorados y no traban el feed.

## Varias cajas en la misma organización

El POS se suscribe también a sus propios eventos (`IncludeOwnEvents = true`):

1. Las ventas de otras cajas se guardan como `origin = 'remote'` y aparecen en **Ventas** y en el panel. Las propias
   se reconocen por su Id y no se duplican.
2. Los documentos se numeran por equipo (`V-3F0C-000012`, `DEV-3F0C-000003`, `CAJA-3F0C-000001`) para no chocar sin
   conexión; el prefijo sale del Id del equipo y se ve en Configuración.
3. El arqueo solo cuenta las ventas y devoluciones hechas en el turno de esa caja.
. Si dos cajas cambian la configuración de la tienda sin conexión, todas terminan con el cambio más reciente.

### Limitaciones conocidas

- Las ventas al crédito se publican con `method = "credit"`; el cobro posterior lo registra el ERP (`payment.received`),
  que el POS todavía no muestra.
- No se emiten comprobantes electrónicos SUNAT desde el POS: el número de boleta o factura lo asigna el ERP.
