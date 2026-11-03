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

La copia local solo se actualiza desde el feed, por `id` y `version`. Un alta hecha en el POS queda **POR ENVIAR**
hasta que vuelve por el feed; si el hub la rechaza aparece en **Configuración → Rechazadas por el hub**.

## Datos propios del POS

Ventas (líneas y pagos), devoluciones y turnos de caja. Las ventas viajan como eventos; los turnos de caja son de
cada equipo y no se publican.

## Eventos (versión 1)

### Qué publica el POS

| Evento | Cuándo | Id del evento |
|---|---|---|
| `sale.completed` | Al cobrar | Id de la venta |
| `sale.voided` | Al anular una venta sin devoluciones | `DeterministicId("sale_void", saleId)` |
| `sale.returned` | Al registrar una devolución (parcial o total) | Id de la devolución |

`sale.completed` lleva el formato v1 (`saleId`, `number`, `siteId`, `customerId`, `subtotal`, `tax`, `total`, `lines`,
`payments`) y agrega `discount`, `createdBy` y, por línea, `name` y `taxable`. En `payments`, el efectivo va por lo
aplicado a la venta (sin el vuelto).

### Qué consume el POS

| Evento | Efecto |
|---|---|
| `stock.adjusted` (WMS) | Guarda el saldo absoluto (`quantityOnHand`) por tienda y artículo; se muestra al vender y en el catálogo |
| `invoice.issued` (ERP) | Si trae `saleId`, anota el número de comprobante en la venta |
| `sale.completed`, `sale.voided`, `sale.returned` (otra caja POS) | Ver abajo |

- Cada evento se aplica una sola vez (`processed_events`), en la misma transacción que su efecto.
- `schemaVersion` distinto de 1 o `data` ilegible se registran como ignorados y no traban el feed.

## Varias cajas en la misma organización

El POS se suscribe también a sus propios eventos (`IncludeOwnEvents = true`):

1. Las ventas de otras cajas se guardan como `origin = 'remote'` y aparecen en **Ventas** y en el panel. Las propias
   se reconocen por su Id y no se duplican.
2. Los documentos se numeran por equipo (`V-3F0C-000012`, `DEV-3F0C-000003`, `CAJA-3F0C-000001`) para no chocar sin
   conexión; el prefijo sale del Id del equipo y se ve en Configuración.
3. El arqueo solo cuenta las ventas y devoluciones hechas en el turno de esa caja.
4. Una anulación hecha en dos cajas a la vez genera el mismo Id de evento y el hub la deduplica.

### Limitaciones conocidas

- Las ventas al crédito se publican con `method = "credit"`; el cobro posterior lo registra el ERP (`payment.received`),
  que el POS todavía no muestra.
- No se emiten comprobantes electrónicos SUNAT desde el POS: el número de boleta o factura lo asigna el ERP.
