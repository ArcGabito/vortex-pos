# Changelog

Todos los cambios relevantes de este proyecto se documentan en este archivo.

El formato se basa en [Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/)
y el proyecto sigue [Versionado Semántico](https://semver.org/lang/es/).

## [Unreleased]

### Added
- **Celular (Android):** la app respeta las barras del sistema (estado y gestos) con íconos oscuros; el botón Atrás cierra
  el diálogo o el menú abiertos y vuelve a la página anterior en vez de cerrar la app; las tablas se ven como tarjetas;
  los indicadores del panel en 2×2; en Vender, una barra fija con el total lleva a la venta y cobra; la barra superior
  se compacta. En pantallas táctiles la caja ya no abre el teclado sola.
- **PIN de caja (fase 2b):** cambio rápido de cajero con su PIN sin cerrar la sesión, bloqueo de la caja (manual o tras
  unos minutos sin uso) y autorización del supervisor con su PIN cuando el rol del cajero no permite la acción (anular,
  devolver, descuentos altos, retiros…); la auditoría registra quién autorizó. Los verificadores del hub se guardan en el
  almacenamiento seguro del equipo y funcionan sin conexión; tras 5 PIN equivocados se espera un minuto. "Mi PIN" en
  Configuración.
- **Caja completa (fase 2a):** ingresos y retiros de efectivo con motivo; cierre contando billetes y monedas (o por
  total) y cierre ciego; reportes X (parcial) y Z (cierre) con ventas, impuestos, descuentos, anulaciones,
  devoluciones, cobros por medio, movimientos de efectivo y ventas por cajero. Supervisores y administradores.
- **Tickets en espera:** guardar la venta para retomarla después y atender a otro cliente.
- **Impresión de tickets** en impresoras térmicas ESC/POS por red (puerto 9100), con apertura del cajón al cobrar en
  efectivo, reimpresión desde Ventas, impresión de los reportes X y Z y prueba de impresión en Configuración.
- **Configuración de la tienda** compartida por sus cajas (`pos.record.upserted`): encabezado y pie del ticket, ancho
  del papel (58, 76 u 80 mm), imprimir al cobrar, abrir el cajón y cierre ciego.
- **Tiendas por usuario:** con tiendas asignadas en el hub, el usuario solo abre caja en ellas y solo ve sus ventas.
- **Microsoft Store:** paquete MSIX generado por `.github/workflows/store.yml` (etiqueta `vX.Y.Z`), firmado por la Store
  para instalar sin la advertencia de SmartScreen; envío automático opcional. Guía en `docs/MICROSOFT-STORE.md`.
- **El idioma sigue al usuario:** el que elige en Configuración se guarda en el hub y se usa al entrar en cualquier
  sistema o equipo de la suite. Sin conexión queda en este equipo.
- **Idiomas**: la interfaz en español, inglés y portugués. Se elige en Configuración → Este equipo; por defecto sale
  del país de la empresa (portugués en Brasil y Portugal, inglés en EE. UU. y el Reino Unido, español en el resto).
  Una prueba verifica que cada texto exista en los tres idiomas con los mismos datos variables.
- Los **mensajes de validación y de permisos** salen en el idioma elegido (`CoreText` en Core,
  con el mismo catálogo que la interfaz).
- **Base común de la suite** (`VortexApi.Suite`): impuestos por país (Perú, LATAM, EE. UU., Reino Unido y UE) con
  desglose por tasa y precios con impuesto incluido o sumado al cobrar (EE. UU.), redondeo según la moneda (el peso
  chileno sin decimales) y formatos y hora según el país de la empresa.
- **Roles del POS** asignados en el hub: `cajero` (por defecto), `supervisor` y `administrador`. Anular, devolver,
  cerrar caja, descuentos de más del 10 % y editar el catálogo requieren supervisor o administrador.
- **Auditoría**: aperturas y cierres de caja, descuentos, anulaciones y devoluciones quedan registrados con usuario,
  equipo y hora (pantalla Auditoría).
- `docs/ROADMAP.md`: hoja de ruta para competir con Square, Lightspeed, Shopify POS y Loyverse.

### Changed
- `docs/ROADMAP.md` pasa a ser la guía de requisitos empresariales: cada requisito (`POS-xx`) con su estado y
  la fase que lo cubre, y cada fase con su criterio de terminado, propuestas al hub y decisiones pendientes.
- **Rediseño monocromático:** barra lateral gris claro con el enlace activo como tarjeta, barra superior con botón
  **Vender**, pie con derechos y sellos, controles blancos con borde, cabeceras de tabla en tinta y pestañas en
  mayúsculas. El Panel muestra lo vendido hoy en una barra oscura y los indicadores en un panel oscuro junto a las
  ventas recientes.
- **Morado .NET como color de la suite:** el naranja de la marca (`#FF5B14`) pasa a `#512BD4`, el morado de .NET,
  en el acento de `app.css`, el logo, el ícono de la app, la pantalla de inicio, la barra de título y Android. La portada
  del inicio de sesión se muestra en tonos morados con `filter: hue-rotate`. Los colores de estado no cambian.
- `sale.completed` lleva la categoría de impuesto de cada línea (`taxCode`). El `taxCode` de los artículos es una
  categoría de la base común (`standard`, `reduced`, `zero`, `exempt`…); `igv`, `exonerado` e `inafecto` siguen valiendo.
- **Negro de la marca en lo nativo:** la pantalla de inicio de Android, el ícono, la barra de título de Windows y el logo
  usaban un café oscuro (`#1C120D`) que se veía amarillento; pasan a la tinta de la app (`#16151D`).

### Fixed
- Los indicadores del panel mostraban "S/ 18.5" y "S/ 0": los montos menores a 10 mil se muestran completos ("S/ 18.50").
- Anular, devolver, movimientos y reportes de caja quedaban ocultos para el cajero aunque un supervisor podía autorizarlos
  con su PIN: ahora se ofrecen y, al intentarlos, se pide el PIN.
- Con otro cajero en la caja, "Mi PIN" cambiaba el PIN de quien inició sesión; ahora solo se ofrece a ese usuario.
- La línea sobre el total de la venta se veía cortada en dos.

### Added
- App **.NET MAUI Blazor Hybrid** (Windows, Android, iOS, macOS) con la arquitectura de Vortex WMS: base SQLite por
  organización, sesión con el hub `vortex-api` y sincronización sin conexión.
- **Vender**: búsqueda y lectura de código de barras, artículos por categoría, descuentos por línea, cliente opcional,
  desglose de IGV y pago con efectivo (vuelto), tarjeta, Yape/Plin, transferencia o crédito, combinables.
- **Turnos de caja** con fondo inicial, cobrado por medio de pago y cierre con arqueo.
- **Ventas**: historial de todas las cajas, detalle, anulación, devoluciones parciales y exportación a CSV.
- Catálogo, clientes y tiendas compartidos con la suite; stock por tienda informado por el WMS.
- Eventos `sale.completed`, `sale.voided` y `sale.returned`; consumo de `stock.adjusted` e `invoice.issued`.
- CI (pruebas en Linux y app en Windows), Dependabot, plantilla de pull request, CONTRIBUTING y SECURITY.

### Changed
- Licencia: de SSPL a **AGPL-3.0**, como el resto de la suite.

### Removed
- Plantilla inicial de .NET MAUI (XAML).
