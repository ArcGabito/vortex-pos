# Hoja de ruta

Guía para que Vortex POS cumpla lo que una cadena de tiendas o restaurantes exige a un punto de venta empresarial y
compita con Square, Lightspeed, Shopify POS, Toast y Loyverse en **Perú, LATAM, EE. UU. y Europa**. Lo común a la
suite está en [ROADMAP-SUITE](https://github.com/ArcGabito/vortex-api/blob/main/docs/ROADMAP-SUITE.md).

Cómo usarla:

1. **Requisitos empresariales** es la lista de lo que debe tener el POS. Cada requisito tiene un código (`POS-xx`), su
   estado y la fase que lo cubre.
2. **Fases** dice en qué orden se construyen, qué datos y eventos necesitan y cuándo se dan por terminadas.
3. Al cerrar una fase se actualiza su **Estado** y el de sus requisitos, y se anota en el `CHANGELOG`.

Los tamaños (S, M, L, XL) son relativos entre fases y se vuelven a estimar al empezar cada una.

## Requisitos empresariales

Estados: **Hecho**, **Parcial** (existe una parte; la fase indica qué falta) y **Pendiente**.

### Venta

| # | Requisito | Estado | Fase |
|---|---|---|---|
| POS-01 | Búsqueda, lector de códigos de barras y categorías | Hecho | — |
| POS-02 | Descuento por línea y por ticket, con límite por rol | Parcial: por línea | 3 |
| POS-03 | Tickets en espera y varias ventas abiertas a la vez | Hecho | 2 |
| POS-04 | Venta por presentación (caja × 12) con los códigos del hub | Hecho | 3a |
| POS-05 | Variantes (talla, color) y modificadores (extras, sin cebolla) | Pendiente | 3 |
| POS-06 | Combos y kits con precio propio | Pendiente | 3 |
| POS-07 | Venta por peso con balanza y etiquetas de peso variable | Parcial: etiquetas de peso variable | 3a |
| POS-08 | Venta de artículos con lote o serie (la serie se registra al vender) | Pendiente | 6 |

### Precios y promociones

| # | Requisito | Estado | Fase |
|---|---|---|---|
| POS-10 | Precio de lista del hub | Hecho | — |
| POS-11 | Listas de precios por tienda, por cliente y por canal | Pendiente | 3 |
| POS-12 | Promociones con reglas y vigencia (2x1, % por categoría, por horario, por monto) | Pendiente | 3 |
| POS-13 | Cupones de un uso o de campaña | Pendiente | 3 |
| POS-14 | Cambio de precio en caja solo con permiso, auditado | Hecho | 3a |

### Cobro

| # | Requisito | Estado | Fase |
|---|---|---|---|
| POS-20 | Efectivo con vuelto, tarjeta, billeteras, transferencia y crédito, combinables | Hecho | — |
| POS-21 | Impuestos por país, precios con impuesto incluido o sumado (EE. UU.) | Hecho | 1 |
| POS-22 | Moneda de la empresa con su redondeo | Hecho | 1 |
| POS-23 | Cobro en otra moneda con el tipo de cambio del hub | Pendiente | 5 |
| POS-24 | Propinas (por ticket y por mesero) | Pendiente | 5 |
| POS-25 | Gift cards y vales con saldo | Pendiente | 4 |
| POS-26 | Terminales de pago integrados (Niubiz, Izipay, Mercado Pago Point, Stripe Terminal, SumUp) | Pendiente | 5 |
| POS-27 | Pagos QR con confirmación automática (Yape, Plin, Pix) | Pendiente | 5 |

### Caja y turnos

| # | Requisito | Estado | Fase |
|---|---|---|---|
| POS-30 | Turnos con fondo inicial, cierre y arqueo con diferencia | Hecho | — |
| POS-31 | Ingresos y retiros de efectivo con motivo | Hecho | 2 |
| POS-32 | Arqueo por denominación y cierre ciego (el cajero no ve el esperado) | Hecho | 2 |
| POS-33 | Reporte X (parcial) y Z (cierre) por caja y por cajero | Hecho | 2 |
| POS-34 | Depósitos al banco desde la caja | Pendiente | 7 |

### Postventa

| # | Requisito | Estado | Fase |
|---|---|---|---|
| POS-40 | Anulación con permiso y auditoría | Hecho | 1 |
| POS-41 | Devolución parcial con reembolso en efectivo | Hecho | — |
| POS-42 | Cambios (devolver y llevar otro en la misma operación) | Pendiente | 4 |
| POS-43 | Reembolso al medio de pago original | Pendiente | 4 |
| POS-44 | Política de devoluciones (plazo, sin ticket solo con supervisor) | Pendiente | 4 |

### Clientes

| # | Requisito | Estado | Fase |
|---|---|---|---|
| POS-50 | Cliente del hub en la venta | Hecho | — |
| POS-51 | Alta rápida de cliente en caja con documento | Pendiente | 4 |
| POS-52 | Historial del cliente en caja | Pendiente | 4 |
| POS-53 | Programa de puntos (acumular y canjear) | Pendiente | 4 |
| POS-54 | Cuenta corriente con límite de crédito | Pendiente | 4 |
| POS-55 | Apartados (separar con adelanto) | Pendiente | 4 |
| POS-56 | Consentimiento para datos y marketing (Ley 29733, LGPD, GDPR) | Pendiente | 4 |

### Hardware

| # | Requisito | Estado | Fase |
|---|---|---|---|
| POS-60 | Lector de códigos en modo teclado | Hecho | — |
| POS-61 | Impresora de tickets ESC/POS (red, USB, Bluetooth), plantilla por tienda y reimpresión | Parcial: por red; USB y Bluetooth pendientes | 2 |
| POS-62 | Cajón de dinero | Hecho | 2 |
| POS-63 | Balanza | Parcial: etiquetas EAN-13 de la balanza; falta la balanza conectada a la caja | 3a |
| POS-64 | Visor o segunda pantalla para el cliente | Pendiente | 8 |
| POS-65 | Escaneo con la cámara del celular | Pendiente | 3 |

### Seguridad y control

| # | Requisito | Estado | Fase |
|---|---|---|---|
| POS-70 | Roles `cajero`, `supervisor`, `administrador` validados en Core | Hecho | 1 |
| POS-71 | Auditoría de anulaciones, devoluciones, descuentos y caja | Hecho | 1 |
| POS-72 | PIN rápido para cambiar de cajero y bloqueo de pantalla | Hecho | 2 |
| POS-73 | Autorización del supervisor en el momento (su PIN en la caja del cajero) | Hecho | 2 |
| POS-74 | Sedes por usuario: cada cajero vende solo en sus tiendas | Hecho | 2 |

### Operación sin conexión y varios equipos

| # | Requisito | Estado | Fase |
|---|---|---|---|
| POS-80 | Vende sin red y sincroniza al volver | Hecho | — |
| POS-81 | Varias cajas por tienda sin duplicar ventas (Ids deterministas) | Hecho | — |
| POS-82 | Configuración de la tienda compartida entre cajas (impresoras, medios de pago, promociones) | Hecho | 2 |

### Inventario y omnicanal

| # | Requisito | Estado | Fase |
|---|---|---|---|
| POS-90 | Stock por tienda informado por el WMS | Hecho | — |
| POS-91 | Ver el stock de otras tiendas y reservar | Pendiente | 6 |
| POS-92 | Pedido a otra tienda o al almacén (entrega o recojo) | Pendiente | 6 |
| POS-93 | Retiro en tienda de pedidos en línea (click & collect) | Pendiente | 6 |

### Comprobante fiscal

| # | Requisito | Estado | Fase |
|---|---|---|---|
| POS-100 | Comprobante emitido por el ERP a partir de la venta | Hecho | — |
| POS-101 | Comprobante electrónico en caja por país (boleta y factura en Perú, CFDI, DIAN, DTE) | Pendiente | 5 |
| POS-102 | Impresora fiscal donde la ley la exige | Pendiente | 5 |

### Reportes

| # | Requisito | Estado | Fase |
|---|---|---|---|
| POS-110 | Panel del día, más vendidos y exportación CSV | Hecho | — |
| POS-111 | Ventas por cajero, por hora, por medio de pago y por categoría | Pendiente | 7 |
| POS-112 | Margen con el costo del WMS | Pendiente | 7 |
| POS-113 | Consolidado multitienda y tablero en tiempo real | Pendiente | 7 |
| POS-114 | Exportación a Excel y PDF | Pendiente | 7 |

### Verticales

| # | Requisito | Estado | Fase |
|---|---|---|---|
| POS-120 | Restaurante: mesas, comandas a cocina (KDS), cursos y división de cuentas | Pendiente | 8 |
| POS-121 | Autoservicio (kiosko) | Pendiente | 8 |

## Reglas que el plan respeta

- Las ventas viajan como `sale.*`. El stock es del WMS (el POS guarda el saldo y nunca lo modifica) y el comprobante,
  del ERP o del paquete de localización compartido.
- Nada fijo para un país: impuestos, monedas y formatos vienen de `VortexApi.Suite`.
- **La caja vende siempre**, con o sin red. Lo que no puede validarse sin conexión (saldo de una gift card, crédito de
  un cliente) define un límite y qué pasa al reconectar.
- Cobrar, anular y devolver guardan el documento y su evento en la misma transacción.
- Módulos opcionales por `features` del hub (`promociones`, `fidelizacion`, `restaurante`…), para venderlos por plan.

## Fase 1: base común de la suite · M

Impuestos por país, monedas, roles del POS, auditoría e idiomas (español, inglés y portugués).

**Terminado cuando:** una tienda en EE. UU. cobra con sales tax sumado al precio, un `cajero` no puede anular y la
anulación del supervisor queda en la auditoría.

**Estado (2026-11):** hecha.

## Fase 2: caja completa y tickets · M

**Objetivo:** que una tienda pueda cerrar el día como lo exige su contador.

- POS-61, POS-62: impresión ESC/POS con plantilla por tienda (logo, datos de la empresa, pie), reimpresión y
  apertura del cajón al cobrar en efectivo.
- POS-31 a POS-33: ingresos y retiros con motivo, arqueo por denominación, cierre ciego y reportes X y Z.
- POS-03: tickets en espera y varias ventas abiertas.
- POS-72 a POS-74: PIN rápido, bloqueo de pantalla, autorización del supervisor con su PIN, sedes por usuario
  (`Access.SiteIds`, ya en el hub).
- POS-82: configuración de tienda compartida entre cajas.

**Datos y eventos:** movimientos de caja en una tabla propia con Id determinista; la configuración de tienda viaja
como `pos.record.upserted` (el último cambio gana, como en el CRM y el WMS).

**Hub:** PIN de usuario (hash, nunca en claro) para el cambio rápido de cajero.

**Terminado cuando:** un turno con ventas, un retiro y un ingreso cierra con reporte Z impreso; el arqueo por
denominación muestra la diferencia; un cajero con PIN cambia de usuario sin cerrar la venta en curso.

**Estado (2026-11):** 2a hecha: ingresos y retiros, arqueo por denominación, cierre ciego, reportes X y Z, tickets en
espera, tickets ESC/POS por red con reimpresión y cajón, configuración de tienda compartida (`pos.record.upserted`) y
tiendas por usuario. Pruebas en `CashRegisterTests`. **Pendiente (2b):** PIN rápido, bloqueo de pantalla y
autorización del supervisor (POS-72, POS-73), que necesitan el PIN de usuario en el hub; impresión por USB y
Bluetooth (POS-61).

**Estado (2026-11), 2b:** hecha. PIN de caja en el hub (vortex-api#31): cambio rápido de cajero sin cerrar la sesión,
bloqueo manual o por inactividad y autorización del supervisor con su PIN cuando el rol no alcanza (queda en la auditoría
quién autorizó). Los verificadores se guardan en el almacenamiento seguro del equipo y se validan sin conexión; tras 5
PIN equivocados se espera un minuto. Pruebas en `OperatorTests`. Pendiente de la fase: USB y Bluetooth (POS-61).

## Fase 3: catálogo y precios · L

**Objetivo:** vender el catálogo de una cadena real, con sus precios y promociones.

- POS-04: venta por presentación con los códigos del `item` del hub (escanear la caja vende la caja).
- POS-05, POS-06: variantes y modificadores; combos y kits.
- POS-07, POS-63: venta por peso con balanza y lectura de etiquetas de peso variable (código con precio o peso).
- POS-11 a POS-14, POS-02: listas de precios, promociones, cupones, descuento por ticket y cambio de precio con permiso.
- POS-65: escaneo con cámara en Android e iOS.

**Datos y eventos:** `sale.completed` gana por línea `presentationId`, `variantId`, `modifiers` y `promotionId`
(aditivos). Las promociones se evalúan en Core con reglas deterministas para que todas las cajas den el mismo total.

**Hub:** variantes como artículos hijos con atributos; listas de precios como dato maestro compartido con el ERP y el
CRM (decisión 1).

**Terminado cuando:** un 2x1 por categoría y un cupón se aplican igual en dos cajas sin conexión; escanear la caja
vende 12 unidades; una etiqueta de balanza agrega el peso correcto.

**Partes:** 3a, presentaciones, etiquetas de balanza y cambio de precio con permiso; 3b, listas de precios, promociones,
cupones y descuento por ticket; 3c, variantes, modificadores, combos, balanza conectada y escaneo con cámara.

**Estado 3a (2026-11):** hecha. El POS guarda las presentaciones que trae el artículo del hub; escanear el código de
una presentación agrega sus unidades (la caja de 12 son 12, al precio unitario) y la línea vendida lleva `presentation`.
Etiquetas de peso variable EAN-13 (prefijo 20 a 29, PLU = SKU numérico del artículo, peso en gramos o precio en
céntimos, con dígito de control), configurables por tienda. Cobrar a otro precio que el de lista necesita
`sale.change_price` (supervisor, o su PIN), queda en la auditoría como `price.override` y la línea lleva `listPrice`.
Pruebas en `CatalogTests`. Además, el buscador de la caja ya no pierde dígitos cuando un lector escribe rápido.

## Fase 4: postventa y clientes · M

- POS-42 a POS-44: cambios, reembolso al medio original y política de devoluciones configurable.
- POS-51 a POS-56: alta rápida de cliente, historial en caja, puntos, cuenta corriente con límite, apartados y
  consentimientos.
- POS-25: gift cards y vales.

**Multiequipo:** los saldos (puntos, gift cards, crédito) se usan en varias tiendas. Sin conexión se permite hasta un
límite configurable y al reconectar se concilia; ver decisión 2.

**Hub:** saldo de puntos y crédito por cliente (`party`), consentimientos del cliente compartidos con el CRM.

**Terminado cuando:** un cambio de talla genera la devolución y la nueva venta en una operación; los puntos ganados en
una tienda se canjean en otra; una gift card no se puede gastar dos veces aunque dos cajas estén sin red (dentro del
límite definido).

## Fase 5: pagos y comprobante fiscal · L

- POS-26, POS-27: terminales integrados y QR con confirmación automática (la caja no marca pagado sin respuesta).
- POS-23, POS-24: cobro en otra moneda con el tipo de cambio del hub (`GetExchangeRateAsync`) y propinas.
- POS-101, POS-102: comprobante electrónico en caja mediante el paquete de localización compartido con el ERP,
  empezando por Perú (boleta y factura UBL 2.1 vía OSE), e impresora fiscal donde se exija.

**Terminado cuando:** una venta con tarjeta se cobra desde la caja sin digitar el monto en el terminal; una boleta
electrónica sale con su código QR y queda aceptada por la OSE; si la OSE no responde, la venta se emite en
contingencia.

## Fase 6: inventario en tienda y omnicanal · M

- POS-91, POS-92: stock de otras tiendas, reservas y pedidos a otra tienda o al almacén (`order.created` hacia el WMS).
- POS-93: retiro en tienda de pedidos en línea (los conectores de comercio electrónico son del hub; fase 7 del WMS).
- POS-08: lote y serie en la venta (`sale.completed` con `serials`, fase 3 del WMS).

**Terminado cuando:** un cliente compra en una tienda un artículo de otra, el WMS recibe el pedido y la caja lo entrega
al llegar.

## Fase 7: reportes y multitienda · M

- POS-111 a POS-114: reportes por cajero, hora, medio de pago y categoría; margen con el costo del WMS
  (`stock.adjusted.unitCost`); consolidado multitienda y tablero en tiempo real; exportación a Excel y PDF.
- POS-34: depósitos al banco, que el ERP concilia.

## Fase 8: verticales · L

- POS-120: modo restaurante (mesas, comandas a cocina con KDS, cursos, división de cuentas, propina por mesero).
- POS-121: autoservicio y POS-64 visor para el cliente.

Se activan con los `features` `restaurante` y `autoservicio`.

## Propuestas al hub

| Propuesta | Tipo | Fase |
|---|---|---|
| PIN de usuario para cambio rápido (hecho, vortex-api#31) | Campo del usuario (hash) | 2 |
| Variantes y atributos del artículo | Campos de `item` | 3 |
| Listas de precios | Dato maestro compartido con ERP y CRM | 3 |
| `presentationId`, `variantId`, `modifiers`, `promotionId` en `sale.completed` | Campos aditivos | 3 |
| Saldo de puntos y crédito del cliente | Campos de `party` o recurso propio | 4 |
| Consentimientos del cliente | Campos de `party` | 4 |
| `serials` en `sale.completed` | Campo aditivo | 6 |
| Paquete de localización fiscal compartido con el ERP | Librería en `VortexApi.Suite` | 5 |

## Decisiones pendientes

| # | Decisión | Recomendación | Antes de |
|---|---|---|---|
| 1 | Dónde viven las listas de precios | En el hub, compartidas por POS, ERP y CRM | Fase 3 |
| 2 | Saldos de gift cards, puntos y crédito sin conexión | Libro de movimientos replicado por eventos y un límite por caja sin red; el hub da el saldo oficial al reconectar | Fase 4 |
| 3 | Quién emite el comprobante de la venta | El POS con el paquete de localización (el cliente se lo lleva); el ERP lo registra | Fase 5 |
| 4 | Restaurante en la misma app o en otra | La misma app con el `feature` `restaurante` | Fase 8 |

## Versiones

| Versión | Contenido |
|---|---|
| 0.2.0 | Fase 1 |
| 0.3.0 | Fase 2 |
| 0.4.0 | Fase 3 |
| 0.5.0 | Fase 4 |
| 0.6.0 | Fase 5 (Perú) |
| 0.7.0 | Fase 6 |
| 0.8.0 | Fase 7 |
| 1.0.0 | Estabilización y piloto con una tienda real |
| 1.x | Fase 8 y localización de otros países |

## Cómo se trabaja cada fase

1. **Hub primero:** las propuestas de la fase se envían a `vortex-api` con pruebas y se vuelve a empaquetar el SDK
   (`scripts/pack-sdk.*`).
2. **Base de datos:** una migración nueva en `PosDatabase`; nunca se edita una publicada.
3. **Ventas:** cada documento y su evento se guardan en la misma transacción (`Outbox`); los que pueden procesar
   varias cajas usan `DeterministicId`.
4. **Permisos y auditoría:** cada acción sensible valida `RequirePermission` y deja su `AuditAsync`.
5. **Pruebas:** unitarias y un escenario en `SyncTests` por cada documento o dato replicado.
6. **Documentación:** los eventos nuevos o ampliados van en [INTEGRACION.md](INTEGRACION.md); los cambios, en el
   `CHANGELOG`; los requisitos cumplidos se marcan en esta guía.
7. **Pantallas:** con los componentes compartidos, en los tres idiomas, probadas en Windows y en la vista móvil.
