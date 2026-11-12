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
| POS-02 | Descuento por línea y por ticket, con límite por rol | Hecho | 3b |
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
| POS-11 | Listas de precios por tienda, por cliente y por canal | Hecho (por tienda y por cliente) | 3c |
| POS-12 | Promociones con reglas y vigencia (2x1, % por categoría, por horario, por monto) | Hecho | 3b |
| POS-13 | Cupones de un uso o de campaña | Hecho | 3b |
| POS-14 | Cambio de precio en caja solo con permiso, auditado | Hecho | 3a |

### Cobro

| # | Requisito | Estado | Fase |
|---|---|---|---|
| POS-20 | Efectivo con vuelto, tarjeta, billeteras, transferencia y crédito, combinables | Hecho | — |
| POS-21 | Impuestos por país, precios con impuesto incluido o sumado (EE. UU.) | Hecho | 1 |
| POS-22 | Moneda de la empresa con su redondeo | Hecho | 1 |
| POS-23 | Cobro en otra moneda con el tipo de cambio del hub | Hecho | 5b |
| POS-24 | Propinas (por ticket y por mesero) | Hecho | 5b |
| POS-25 | Gift cards y vales con saldo | Hecho | 4b |
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
| POS-42 | Cambios (devolver y llevar otro en la misma operación) | Hecho | 4a |
| POS-43 | Reembolso al medio de pago original | Hecho | 4a |
| POS-44 | Política de devoluciones (plazo, sin ticket solo con supervisor) | Hecho | 4a |

### Clientes

| # | Requisito | Estado | Fase |
|---|---|---|---|
| POS-50 | Cliente del hub en la venta | Hecho | — |
| POS-51 | Alta rápida de cliente en caja con documento | Hecho | 4b |
| POS-52 | Historial del cliente en caja | Hecho | 4a |
| POS-53 | Programa de puntos (acumular y canjear) | Hecho | 4b |
| POS-54 | Cuenta corriente con límite de crédito | Hecho | 4b |
| POS-55 | Apartados (separar con adelanto) | Hecho | 4b |
| POS-56 | Consentimiento para datos y marketing (Ley 29733, LGPD, GDPR) | Hecho | 4 |

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
| POS-101 | Comprobante electrónico en caja por país (boleta y factura en Perú, CFDI, DIAN, DTE) | Parcial (Perú) | 5a |
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

**Estado 3b (2026-11):** hecha, sin las listas de precios: quedan para la 3c con la decisión 1 (en el hub, compartidas
con el ERP y el CRM). Promociones de la cadena como registros del POS (`pos.record.upserted`, kind `promotion`):
porcentaje por artículo, categoría o todo; lleva N paga M (las unidades más baratas salen gratis); descuento al ticket
(monto o porcentaje) desde una compra mínima; con vigencia por fechas, días, horario y tiendas. Se evalúan en Core
(`PromotionEngine`): cada línea recibe a lo más una promoción de línea (primero la que más descuenta en total, a igual
descuento la de menor Id) y después la mejor de ticket, repartida en proporción; dos cajas sin conexión dan el mismo
total. Cupones: activan sus promociones y pueden limitar sus usos, contando también las ventas que llegan de otras
cajas (sin conexión, dos cajas podrían usar el mismo cupón una vez cada una). Descuento manual al ticket repartido entre
las líneas (más del 10 % necesita permiso). La caja muestra la cotización de Core (`QuoteAsync`), así lo que se ve es lo
que se cobra. `sale.completed` gana `coupon` y, por línea, `promotionId`. Pruebas en `PromotionsTests`.

**Estado 3c (2026-11):** hecha. Decisión 1 tomada como se recomendaba: las listas de precios viven en el hub
(`price_list`, vortex-api#36) y las comparten POS, ERP y CRM. El POS guarda la copia (con escalas por cantidad) y la
lista de cada cliente (`credit.priceListId`). Precio base de cada línea: la lista del cliente si está vigente, si no la
de la tienda (la primera por código), si no el precio del artículo; la escala se elige por lo que se lleva del artículo
en toda la venta y la lista solo rige si está en la moneda del artículo. Cobrar al precio de la lista no es un cambio de
precio; otro precio sí (permiso y auditoría, con `listPrice` = el de la lista). La caja muestra la lista que rige y el
catálogo sus precios; al cambiar el cliente se recalcula. `sale.completed` gana `priceListId`. Por canal no se separa:
el POS es un canal (una lista de tienda es la del canal tienda). Pruebas en `PriceListsTests`.

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

**Estado 4a (2026-11):** hecha la parte de postventa. **Cambios**: en la devolución se elige "Cambiar por otro", se
agregan los artículos nuevos y en una sola operación se registran la devolución y la venta nueva; lo devuelto paga lo
nuevo (pago `exchange` en `sale.completed`), el cliente paga la diferencia o se le devuelve por el medio original.
**Reembolso al medio original**: si la venta se pagó con un solo medio vuelve por ese (o por el que se elija); solo el
efectivo sale del cajón (`sale.returned` lleva `refundMethod` y, en un cambio, `exchangeSaleId`). **Política**: plazo de
devolución por tienda (30 días por defecto); dentro del plazo devuelve el cajero, fuera de él o sin ticket solo un
supervisor (en la caja, con su PIN). La devolución sin ticket viaja con `saleId` vacío. **Historial del cliente** en la
caja al elegirlo: compras en todas las tiendas, total, la última y lo que suele llevar. Además, **depósito de caja** en
el banco (con banco y n.º de operación) que se publica como `cash.deposited` para la tesorería del ERP (ERP-39). Pruebas
en `PostSaleTests`. Queda para 4b: alta rápida de cliente en caja, puntos, cuenta corriente, apartados, gift cards y
consentimientos (decisión 2: libro de movimientos replicado con límite sin red, saldo oficial en el hub).

**Estado 4b (2026-11):** hecha. Decisión 2 tomada como se recomendaba: puntos y gift cards son un **libro de
movimientos** (`balance.moved`, vortex-api#41) que todas las cajas reciben; el saldo es la suma, igual en todas al
sincronizar. **Puntos**: el cliente gana por lo que paga (no por lo pagado con puntos o gift card) y canjea como medio de
pago con un mínimo; anular la venta los revierte; ganados en una tienda se canjean en otra. **Gift cards y vales**: se
venden (el efectivo entra al cajón), tienen vigencia y se gastan como medio de pago con su código; **sin red**, una caja
solo deja gastar hasta un límite configurable (0 por defecto), así dos cajas desconectadas no la gastan dos veces más allá
de ese monto. **Cuenta corriente**: la venta al crédito no pasa el límite del cliente (el `credit.creditLimit` del hub)
contando lo que debe de todas las cajas menos los cobros del ERP (`payment.received`); pasarlo pide supervisor.
**Apartados**: con cliente y adelanto, abonos en cualquier medio y, al completar, la venta (pago `layaway`); cancelar
devuelve lo abonado (supervisor). **Alta rápida de cliente** en la caja con documento. Pantalla Saldos y, en la caja,
los puntos y lo que debe el cliente. Pruebas en `BalancesTests`. Fase 4 terminada salvo POS-56 (consentimientos), que
espera el bloque de consentimientos en `party` del hub (fase 7 del CRM).

**POS-56 (2026-11):** hecho con el bloque `consents` de `party` en el hub (vortex-api#46). En el alta rápida y desde el
cliente elegido en la caja se registra si autoriza el tratamiento de sus datos y si acepta promociones (solo con datos
autorizados) por correo, WhatsApp, SMS o llamadas, con la hora, la caja como origen, quién lo registró y la versión de la
política de privacidad de la tienda. Para no borrar lo que la caja no guarda (dirección, notas), el cliente se vuelve a
subir tal como llegó del hub con el bloque nuevo, sin `credit` ni `account`. Fase 4 terminada. Pruebas en `ConsentsTests`.

## Fase 5: pagos y comprobante fiscal · L

- POS-26, POS-27: terminales integrados y QR con confirmación automática (la caja no marca pagado sin respuesta).
- POS-23, POS-24: cobro en otra moneda con el tipo de cambio del hub (`GetExchangeRateAsync`) y propinas.
- POS-101, POS-102: comprobante electrónico en caja mediante el paquete de localización compartido con el ERP,
  empezando por Perú (boleta y factura UBL 2.1 vía OSE), e impresora fiscal donde se exija.

**Terminado cuando:** una venta con tarjeta se cobra desde la caja sin digitar el monto en el terminal; una boleta
electrónica sale con su código QR y queda aceptada por la OSE; si la OSE no responde, la venta se emite en
contingencia.

**Estado 5a (2026-11):** hecha la parte de comprobante electrónico de Perú (POS-101). Decisión 3 tomada como se
recomendaba, con el paquete de localización en el hub (vortex-api#42 y #43): la caja numera la **boleta** (o la
**factura**, con un cliente con RUC válido) en la transacción de la venta con las **series de cada caja** (que viajan a
las demás para que ninguna repita), imprime el ticket con el **QR** (ESC/POS), op. gravada/exonerada/inafecta e IGV
calculados por línea como el XML, y el hub la firma y la envía a SUNAT. **Sin red se emite en contingencia** y se envía
en la siguiente sincronización. **Anular** emite la nota de crédito con motivo 01 y **devolver**, con motivo 07. El estado
(por enviar, aceptado, con observaciones, rechazado, con error) se ve en Ventas, con reintento. Reglas: boleta desde
S/ 700 identifica al cliente; RUC con dígito verificador. Pruebas en `FiscalTests`. Siguen: 5b (otra moneda y propinas)
y 5c (terminales de pago y QR con confirmación); la impresora fiscal (POS-102) no aplica en Perú.

**Estado 5b (2026-11):** hecha. **Otra moneda**: la caja lee los tipos de cambio del hub (`exchange_rate`) y en el cobro
cada pago en efectivo, tarjeta o transferencia puede ir en otra moneda (efectivo, en una sola por venta). Se convierte con
el tipo de **compra** del día (o el último anterior; sin él, el de referencia) y el **vuelto sale en soles**. El efectivo
extranjero no entra al cajón en soles: el arqueo lo muestra aparte, en su moneda. El ticket imprime lo entregado y el T.C.
**Propinas**: monto o 5/10/15 % y para quién (sin dato, el cajero); se cobran con los pagos pero quedan fuera del total y
del comprobante, y el turno muestra las propinas por persona. `sale.completed` agrega `tip`, `tipTo` y en cada pago
`currencyCode`, `foreignAmount` y `exchangeRate`. Pruebas en `CurrencyAndTipsTests`.

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
| 2 | Saldos de gift cards, puntos y crédito sin conexión | Tomada (2026-11): libro de movimientos `balance.moved` replicado entre cajas y límite por caja sin red; el saldo es la suma | Fase 4 |
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
