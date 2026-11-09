using Microsoft.Data.Sqlite;

namespace VortexPos.Core.Data;

/// <summary>
/// Base local del POS (SQLite, una por organización). Guarda la copia de los datos maestros del hub, las ventas y
/// turnos de caja propios del POS, el espejo del stock que publica el WMS y la cola de sincronización, para que la caja
/// venda sin conexión y cada venta quede en la misma transacción que su evento.
/// </summary>
public sealed class PosDatabase
{
    public PosDatabase(string path)
    {
        Path = path;
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 30,
        }.ToString();
    }

    public string Path { get; }
    public string ConnectionString { get; }

    public async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await connection.ExecAsync("PRAGMA foreign_keys = OFF; PRAGMA synchronous = NORMAL;");
        return connection;
    }

    /// <summary>Crea o actualiza el esquema. Cada versión se aplica una sola vez (<c>PRAGMA user_version</c>).</summary>
    public async Task MigrateAsync(CancellationToken ct = default)
    {
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        await using var connection = await OpenAsync(ct);
        await connection.ExecAsync("PRAGMA journal_mode = WAL;");
        var current = await connection.ScalarAsync<long>("PRAGMA user_version;");
        for (var version = (int)current; version < Schema.Migrations.Length; version++)
        {
            await using var tx = connection.BeginTransaction();
            await connection.ExecAsync(Schema.Migrations[version], tx);
            await connection.ExecAsync($"PRAGMA user_version = {version + 1};", tx);
            await tx.CommitAsync(ct);
        }
    }
}

internal static class Schema
{
    /// <summary>Migraciones en orden. Nunca modificar una ya publicada: agregar una nueva al final.</summary>
    public static readonly string[] Migrations =
    [
        """
        -- Copia local de datos maestros del hub (dueño: hub). pending = cambio local aún no confirmado por el hub.
        CREATE TABLE items (
            id TEXT PRIMARY KEY, sku TEXT NOT NULL, name TEXT NOT NULL, kind TEXT NOT NULL, description TEXT,
            unit TEXT NOT NULL, barcode TEXT, category TEXT, price REAL NOT NULL, currency_code TEXT NOT NULL,
            tax_code TEXT, track_inventory INTEGER NOT NULL, is_active INTEGER NOT NULL, version INTEGER NOT NULL,
            is_deleted INTEGER NOT NULL DEFAULT 0, pending INTEGER NOT NULL DEFAULT 0, updated_at TEXT NOT NULL);
        CREATE INDEX ix_items_sku ON items (sku);
        CREATE INDEX ix_items_barcode ON items (barcode);
        CREATE INDEX ix_items_category ON items (category);

        CREATE TABLE sites (
            id TEXT PRIMARY KEY, code TEXT NOT NULL, name TEXT NOT NULL, kind TEXT NOT NULL, phone TEXT,
            address_line TEXT, city TEXT, country_code TEXT, is_active INTEGER NOT NULL, version INTEGER NOT NULL,
            is_deleted INTEGER NOT NULL DEFAULT 0, pending INTEGER NOT NULL DEFAULT 0, updated_at TEXT NOT NULL);

        CREATE TABLE parties (
            id TEXT PRIMARY KEY, name TEXT NOT NULL, trade_name TEXT, kind TEXT NOT NULL, document_type TEXT,
            document_number TEXT, is_customer INTEGER NOT NULL, is_supplier INTEGER NOT NULL, email TEXT, phone TEXT,
            city TEXT, is_active INTEGER NOT NULL, version INTEGER NOT NULL, is_deleted INTEGER NOT NULL DEFAULT 0,
            pending INTEGER NOT NULL DEFAULT 0, updated_at TEXT NOT NULL);
        CREATE INDEX ix_parties_document ON parties (document_number);

        CREATE TABLE company (
            id INTEGER PRIMARY KEY CHECK (id = 1), legal_name TEXT NOT NULL, trade_name TEXT, tax_id TEXT,
            country_code TEXT NOT NULL, currency_code TEXT NOT NULL, version INTEGER NOT NULL);

        -- Espejo del stock que publica el WMS (stock.adjusted). El POS no lo modifica: solo lo muestra.
        CREATE TABLE stock (
            site_id TEXT NOT NULL, item_id TEXT NOT NULL, on_hand REAL NOT NULL, updated_at TEXT NOT NULL,
            PRIMARY KEY (site_id, item_id));

        -- Datos propios del POS (dueño: POS). Se referencian siempre por el Id del hub.
        CREATE TABLE cash_sessions (
            id TEXT PRIMARY KEY, number TEXT NOT NULL, site_id TEXT NOT NULL, status TEXT NOT NULL,
            opened_at TEXT NOT NULL, opened_by TEXT, opening_float REAL NOT NULL,
            closed_at TEXT, closed_by TEXT, expected_cash REAL, counted_cash REAL, note TEXT);
        CREATE INDEX ix_cash_sessions_status ON cash_sessions (status, opened_at);

        -- origin: 'local' (vendida en este equipo) o 'remote' (otra caja de la organización, llegó por el hub).
        CREATE TABLE sales (
            id TEXT PRIMARY KEY, number TEXT NOT NULL, origin TEXT NOT NULL, site_id TEXT NOT NULL, customer_id TEXT,
            cash_session_id TEXT, status TEXT NOT NULL, currency_code TEXT NOT NULL, subtotal REAL NOT NULL,
            tax REAL NOT NULL, discount REAL NOT NULL, total REAL NOT NULL, cash_received REAL, change_given REAL,
            created_at TEXT NOT NULL, user_name TEXT, voided_at TEXT, void_reason TEXT, invoice_number TEXT);
        CREATE INDEX ix_sales_time ON sales (created_at);
        CREATE INDEX ix_sales_session ON sales (cash_session_id);
        CREATE TABLE sale_lines (
            sale_id TEXT NOT NULL, line_no INTEGER NOT NULL, item_id TEXT NOT NULL, sku TEXT, name TEXT NOT NULL,
            quantity REAL NOT NULL, unit_price REAL NOT NULL, discount REAL NOT NULL, total REAL NOT NULL,
            taxable INTEGER NOT NULL, returned_qty REAL NOT NULL DEFAULT 0, PRIMARY KEY (sale_id, line_no));
        CREATE INDEX ix_sale_lines_item ON sale_lines (item_id);
        CREATE TABLE sale_payments (
            sale_id TEXT NOT NULL, line_no INTEGER NOT NULL, method TEXT NOT NULL, amount REAL NOT NULL, reference TEXT,
            PRIMARY KEY (sale_id, line_no));

        CREATE TABLE sale_returns (
            id TEXT PRIMARY KEY, number TEXT NOT NULL, sale_id TEXT NOT NULL, site_id TEXT NOT NULL,
            cash_session_id TEXT, total REAL NOT NULL, reason TEXT, created_at TEXT NOT NULL, user_name TEXT);
        CREATE INDEX ix_sale_returns_sale ON sale_returns (sale_id);
        CREATE TABLE sale_return_lines (
            return_id TEXT NOT NULL, line_no INTEGER NOT NULL, item_id TEXT NOT NULL, quantity REAL NOT NULL,
            total REAL NOT NULL, PRIMARY KEY (return_id, line_no));

        CREATE TABLE document_sequences (prefix TEXT PRIMARY KEY, value INTEGER NOT NULL);

        -- Integración: eventos ya aplicados (idempotencia) y estado de VortexSync (cursores, cola, rechazos).
        CREATE TABLE processed_events (
            event_id TEXT PRIMARY KEY, type TEXT NOT NULL, processed_at TEXT NOT NULL, note TEXT);
        CREATE TABLE sync_cursors (stream TEXT PRIMARY KEY, cursor INTEGER NOT NULL);
        CREATE TABLE sync_outbox (seq INTEGER PRIMARY KEY AUTOINCREMENT, id TEXT NOT NULL UNIQUE, entry TEXT NOT NULL);
        CREATE TABLE sync_failed (
            id TEXT PRIMARY KEY, entry TEXT NOT NULL, error_code TEXT NOT NULL, error TEXT NOT NULL, failed_at TEXT NOT NULL);
        """,
        // 2: base común de la suite. Impuesto y zona horaria de la empresa (del hub), categoría de impuesto de cada línea
        // vendida y bitácora de auditoría.
        """
        ALTER TABLE company ADD COLUMN default_tax_rate REAL;
        ALTER TABLE company ADD COLUMN time_zone TEXT;
        ALTER TABLE sale_lines ADD COLUMN tax_code TEXT;
        UPDATE sale_lines SET tax_code = CASE taxable WHEN 1 THEN 'standard' ELSE 'exempt' END;
        """ + VortexApi.Suite.Auditing.AuditSchema.Sql,
        // 3: caja completa. Ingresos y retiros de efectivo, conteo por denominación al cerrar, tickets en espera (de cada
        // caja) y configuración de tienda compartida entre cajas (pos.record.upserted, gana el cambio más reciente).
        """
        CREATE TABLE cash_movements (
            id TEXT PRIMARY KEY, cash_session_id TEXT NOT NULL, kind TEXT NOT NULL, amount REAL NOT NULL, reason TEXT NOT NULL,
            created_at TEXT NOT NULL, user_name TEXT);
        CREATE INDEX ix_cash_movements_session ON cash_movements (cash_session_id);
        ALTER TABLE cash_sessions ADD COLUMN denominations TEXT;

        CREATE TABLE held_sales (
            id TEXT PRIMARY KEY, label TEXT, customer_id TEXT, lines TEXT NOT NULL, total REAL NOT NULL, created_at TEXT NOT NULL,
            user_name TEXT);

        CREATE TABLE pos_records (
            kind TEXT NOT NULL, id TEXT NOT NULL, data TEXT NOT NULL, updated_at TEXT NOT NULL, updated_by TEXT,
            PRIMARY KEY (kind, id));
        """,
        // 4: catálogo de la cadena (fase 3a). Presentaciones de cada artículo (del hub: escanear la caja vende la caja) y,
        // en cada línea vendida, la presentación y el precio de lista cuando la caja lo cambió. Se vuelven a bajar los
        // artículos para traer sus presentaciones.
        """
        CREATE TABLE item_presentations (
            item_id TEXT NOT NULL, code TEXT NOT NULL, name TEXT NOT NULL, factor REAL NOT NULL, barcode TEXT,
            PRIMARY KEY (item_id, code));
        CREATE INDEX ix_item_presentations_barcode ON item_presentations (barcode);
        ALTER TABLE sale_lines ADD COLUMN presentation TEXT;
        ALTER TABLE sale_lines ADD COLUMN list_price REAL;
        DELETE FROM sync_cursors WHERE stream = 'master';
        """,
        // 5: promociones y cupones (fase 3b). El cupón de cada venta (para contar sus usos, también los de otras cajas) y
        // la promoción de cada línea. Las promociones son registros del POS (pos_records, kind promotion).
        """
        ALTER TABLE sales ADD COLUMN coupon TEXT;
        CREATE INDEX ix_sales_coupon ON sales (coupon);
        ALTER TABLE sale_lines ADD COLUMN promotion_id TEXT;
        """,
        // 6: listas de precios (fase 3c). Copia de las listas del hub (price_list) con sus escalas; la lista de cada
        // cliente (credit.priceListId) y la que rigió cada venta.
        """
        CREATE TABLE price_lists (
            id TEXT PRIMARY KEY, code TEXT NOT NULL, name TEXT NOT NULL, currency_code TEXT NOT NULL, valid_from TEXT, valid_to TEXT,
            site_ids TEXT NOT NULL DEFAULT '[]', is_active INTEGER NOT NULL, is_deleted INTEGER NOT NULL DEFAULT 0,
            version INTEGER NOT NULL, updated_at TEXT NOT NULL);
        CREATE TABLE price_list_lines (
            list_id TEXT NOT NULL, item_id TEXT NOT NULL, min_qty REAL NOT NULL, price REAL NOT NULL,
            PRIMARY KEY (list_id, item_id, min_qty));
        ALTER TABLE parties ADD COLUMN price_list_id TEXT;
        ALTER TABLE sales ADD COLUMN price_list_id TEXT;
        """,
        // 7: postventa (fase 4a). Cómo se reembolsó cada devolución (medio y cuánto salió en efectivo del cajón; sin dato, todo
        // en efectivo como antes) y, en un cambio, la venta nueva. Referencia y banco de los depósitos de caja.
        """
        ALTER TABLE sale_returns ADD COLUMN refund_method TEXT;
        ALTER TABLE sale_returns ADD COLUMN cash_refund REAL;
        ALTER TABLE sale_returns ADD COLUMN exchange_sale_id TEXT;
        ALTER TABLE cash_movements ADD COLUMN reference TEXT;
        ALTER TABLE cash_movements ADD COLUMN bank TEXT;
        """,
    ];
}
