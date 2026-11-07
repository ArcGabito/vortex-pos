# Changelog

Todos los cambios relevantes de este proyecto se documentan en este archivo.

El formato se basa en [Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/)
y el proyecto sigue [Versionado Semántico](https://semver.org/lang/es/).

## [Unreleased]

### Added
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
- **Rediseño monocromático:** barra lateral gris claro con el enlace activo como tarjeta, barra superior con botón
  **Vender**, pie con derechos y sellos, controles blancos con borde, cabeceras de tabla en tinta y pestañas en
  mayúsculas. El Panel muestra lo vendido hoy en una barra oscura y los indicadores en un panel oscuro junto a las
  ventas recientes.
- **Morado .NET como color de la suite:** el naranja de la marca (`#FF5B14`) pasa a `#512BD4`, el morado de .NET,
  en el acento de `app.css`, el logo, el ícono de la app, la pantalla de inicio, la barra de título y Android. La portada
  del inicio de sesión se muestra en tonos morados con `filter: hue-rotate`. Los colores de estado no cambian.
- `sale.completed` lleva la categoría de impuesto de cada línea (`taxCode`). El `taxCode` de los artículos es una
  categoría de la base común (`standard`, `reduced`, `zero`, `exempt`…); `igv`, `exonerado` e `inafecto` siguen valiendo.

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
