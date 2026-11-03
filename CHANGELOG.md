# Changelog

Todos los cambios relevantes de este proyecto se documentan en este archivo.

El formato se basa en [Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/)
y el proyecto sigue [Versionado Semántico](https://semver.org/lang/es/).

## [Unreleased]

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
