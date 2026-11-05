# Hoja de ruta

Plan para que Vortex POS compita con Square, Lightspeed, Shopify POS y Loyverse en tiendas, cadenas y restaurantes
de **Perú, LATAM, EE. UU. y Europa**. Las fases van en el orden en que conviene construirlas; la fase 1 es común a la
suite ([ROADMAP-SUITE](https://github.com/ArcGabito/vortex-api/blob/main/docs/ROADMAP-SUITE.md)).
Tamaños (S, M, L, XL) relativos entre fases.

## Dónde estamos (0.1.0)

| Área | Hoy | Falta para competir |
|---|---|---|
| Venta | Búsqueda y lector de barras, categorías, descuento por línea, cliente opcional | Variantes (talla/color), modificadores, venta por peso, tickets en espera |
| Cobro | Efectivo con vuelto, tarjeta, Yape/Plin, transferencia, crédito, pagos mixtos | Propinas, vales y gift cards, integración con terminales de pago |
| Caja | Turnos con fondo, arqueo y diferencia | Ingresos y retiros de efectivo, arqueo por denominación, reporte X/Z |
| Postventa | Anulación y devolución parcial con reembolso en efectivo | Cambios, reembolso al medio original, política de devoluciones |
| Precios | Precio de lista del hub | Listas de precios, promociones (2x1, % por categoría, horario), cupones |
| Clientes | Cliente opcional del hub | Fidelización (puntos), historial en caja, cuentas corrientes |
| Hardware | Ninguno | Impresora de tickets (ESC/POS), cajón, visor, balanza |
| Usuarios | Login del hub; todos pueden hacer todo | Roles (cajero, supervisor), PIN rápido, autorización del supervisor |
| Reportes | Panel del día, más vendidos, CSV | Por cajero, por medio de pago, por hora, márgenes |
| Internacional | Perú, IGV, soles, español | Impuestos por país, monedas, idiomas, comprobante fiscal por país |

## Reglas que el plan respeta

- Las ventas viajan como `sale.*`; el stock es del WMS y el comprobante fiscal, del ERP (o del paquete fiscal del POS).
- Nada fijo para un país: impuestos, monedas y formatos vienen de `VortexApi.Suite`.
- La caja vende siempre, con o sin red.
- Módulos opcionales por `features` del hub (`promociones`, `fidelizacion`, `restaurante`…).

## Fase 1: base común de la suite · M

- **Impuestos por país** en la venta (reemplaza el IGV fijo): precio con impuesto (LATAM, UE) o sumado al cobrar
  (EE. UU.), varias tasas en un mismo ticket.
- **Monedas** con su redondeo (el peso chileno no tiene decimales).
- **Roles del POS**: `administrador`, `supervisor`, `cajero`. Anular, devolver, cerrar caja y descuentos sobre un
  límite requieren supervisor.
- **Auditoría** de anulaciones, devoluciones, descuentos, aperturas y cierres de caja.
- **Idiomas** español, inglés y portugués (1b, hecho): la interfaz completa, con selector en Configuración → Este
  equipo (por defecto, el idioma del país de la empresa). Los mensajes de validación
  también salen en el idioma elegido.

**Terminado cuando:** una tienda en EE. UU. cobra con sales tax sumado al precio, un `cajero` no puede anular, y la
anulación del supervisor queda en la auditoría.

## Fase 2: caja completa y tickets · M

- Impresión de tickets ESC/POS (red, USB, Bluetooth) y apertura de cajón; reimpresión.
- Ingresos y retiros de efectivo, arqueo por denominación, reportes X (parcial) y Z (cierre) por caja y cajero.
- Tickets en espera y varias ventas abiertas; PIN rápido para cambiar de cajero.

## Fase 3: catálogo y precios · L

- Variantes (talla, color) y modificadores (extras); venta por peso con balanza.
- Listas de precios por tienda y por cliente; promociones y cupones con reglas y vigencia.
- Cambios y devoluciones con reembolso al medio original y política configurable.

## Fase 4: clientes y fidelización · M

Programa de puntos, historial del cliente en caja, gift cards y vales, ventas a cuenta corriente.

## Fase 5: pagos y comprobante fiscal · L

- Terminales de pago integrados (Niubiz, Izipay, Mercado Pago Point, Stripe Terminal, SumUp).
- Comprobante fiscal en caja por país (boleta y factura electrónica en Perú, ticket fiscal donde se exija), mediante
  el paquete de localización compartido con el ERP.

## Fase 6: verticales y escala · L

Modo restaurante (mesas, comandas a cocina, división de cuentas), autoservicio, multitienda con reportes
consolidados y tablero en tiempo real.

## Propuestas al hub

Tipos de cambio; listas de precios como dato maestro compartido con el ERP y el CRM; PIN de usuario para cambio rápido
en caja.
