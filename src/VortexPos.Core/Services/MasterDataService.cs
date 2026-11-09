using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using VortexApi.Client;
using VortexApi.Suite.Money;
using VortexPos.Core.Data;
using VortexPos.Core.Domain;
using VortexPos.Core.Sync;

using static VortexPos.Core.Texts.CoreText;

namespace VortexPos.Core.Services;

/// <summary>
/// Artículos, tiendas, clientes y empresa: datos del hub. El POS guarda una copia que se actualiza <b>solo desde el
/// feed</b> (por Id y versión). Las altas y cambios se guardan localmente como pendientes y se encolan para el hub;
/// nunca existen solo en la base local.
/// </summary>
public sealed partial class MasterDataService(PosDatabase db, IPosContext context, TimeProvider clock)
    : PosService(db, context, clock)
{
    // ---------- Feed del hub ----------

    public async Task ApplyItemAsync(VortexMasterChange<VortexItem> change, CancellationToken ct = default)
    {
        var d = change.Data;
        await using var c = await Db.OpenAsync(ct);
        await using var tx = c.BeginTransaction();
        var applied = await c.ExecAsync("""
            INSERT INTO items (id, sku, name, kind, description, unit, barcode, category, price, currency_code, tax_code,
                               track_inventory, is_active, version, is_deleted, pending, updated_at)
            VALUES ($id, $sku, $name, $kind, $desc, $unit, $barcode, $category, $price, $currency, $tax, $track, $active,
                    $version, $deleted, 0, $updated)
            ON CONFLICT (id) DO UPDATE SET
                sku = excluded.sku, name = excluded.name, kind = excluded.kind, description = excluded.description,
                unit = excluded.unit, barcode = excluded.barcode, category = excluded.category, price = excluded.price,
                currency_code = excluded.currency_code, tax_code = excluded.tax_code,
                track_inventory = excluded.track_inventory, is_active = excluded.is_active, version = excluded.version,
                is_deleted = excluded.is_deleted, pending = 0, updated_at = excluded.updated_at
            WHERE excluded.version > items.version OR (excluded.version = items.version AND items.pending = 0)
            """, tx, ("$id", change.EntityId), ("$sku", d.Sku), ("$name", d.Name), ("$kind", d.Kind), ("$desc", d.Description),
            ("$unit", d.Unit), ("$barcode", d.Barcode), ("$category", d.Category), ("$price", d.Price),
            ("$currency", d.CurrencyCode), ("$tax", d.TaxCode), ("$track", d.TrackInventory), ("$active", d.IsActive),
            ("$version", change.Version), ("$deleted", Deleted(change.Operation, d.Meta)), ("$updated", d.Meta.UpdatedAt));

        // Las presentaciones son parte del artículo: si la versión se aplicó, quedan exactamente las que trae. La misma
        // versión también se aplica si no hay un cambio local sin enviar (son los mismos datos): así, al volver a bajar el catálogo tras la migración 4, los
        // artículos que ya estaban reciben sus presentaciones.
        if (applied > 0)
        {
            await c.ExecAsync("DELETE FROM item_presentations WHERE item_id = $id", tx, ("$id", change.EntityId));
            foreach (var p in (d.Presentations ?? []).Where(p => p.Factor > 0 && !string.IsNullOrWhiteSpace(p.Code)).DistinctBy(p => p.Code))
                await c.ExecAsync("""
                    INSERT INTO item_presentations (item_id, code, name, factor, barcode) VALUES ($id, $code, $name, $factor, $barcode)
                    """, tx, ("$id", change.EntityId), ("$code", p.Code), ("$name", p.Name), ("$factor", p.Factor),
                    ("$barcode", Clean(p.Barcode)));
        }
        await tx.CommitAsync(ct);
    }

    public async Task ApplySiteAsync(VortexMasterChange<VortexSite> change, CancellationToken ct = default)
    {
        var d = change.Data;
        await using var c = await Db.OpenAsync(ct);
        await c.ExecAsync("""
            INSERT INTO sites (id, code, name, kind, phone, address_line, city, country_code, is_active, version, is_deleted, pending, updated_at)
            VALUES ($id, $code, $name, $kind, $phone, $line, $city, $country, $active, $version, $deleted, 0, $updated)
            ON CONFLICT (id) DO UPDATE SET
                code = excluded.code, name = excluded.name, kind = excluded.kind, phone = excluded.phone,
                address_line = excluded.address_line, city = excluded.city, country_code = excluded.country_code,
                is_active = excluded.is_active, version = excluded.version, is_deleted = excluded.is_deleted, pending = 0,
                updated_at = excluded.updated_at
            WHERE excluded.version > sites.version
            """, null, ("$id", change.EntityId), ("$code", d.Code), ("$name", d.Name), ("$kind", d.Kind), ("$phone", d.Phone),
            ("$line", d.Address?.Line), ("$city", d.Address?.City), ("$country", d.Address?.CountryCode),
            ("$active", d.IsActive), ("$version", change.Version), ("$deleted", Deleted(change.Operation, d.Meta)),
            ("$updated", d.Meta.UpdatedAt));
    }

    public async Task ApplyPartyAsync(VortexMasterChange<VortexParty> change, CancellationToken ct = default)
    {
        var d = change.Data;
        await using var c = await Db.OpenAsync(ct);
        await c.ExecAsync("""
            INSERT INTO parties (id, name, trade_name, kind, document_type, document_number, is_customer, is_supplier, email,
                                 phone, city, is_active, version, is_deleted, pending, updated_at, price_list_id, credit_limit)
            VALUES ($id, $name, $trade, $kind, $doctype, $docnum, $customer, $supplier, $email, $phone, $city, $active,
                    $version, $deleted, 0, $updated, $list, $limit)
            ON CONFLICT (id) DO UPDATE SET
                name = excluded.name, trade_name = excluded.trade_name, kind = excluded.kind,
                document_type = excluded.document_type, document_number = excluded.document_number,
                is_customer = excluded.is_customer, is_supplier = excluded.is_supplier, email = excluded.email,
                phone = excluded.phone, city = excluded.city, is_active = excluded.is_active, version = excluded.version,
                is_deleted = excluded.is_deleted, pending = 0, updated_at = excluded.updated_at, price_list_id = excluded.price_list_id,
                credit_limit = excluded.credit_limit
            WHERE excluded.version > parties.version
            """, null, ("$id", change.EntityId), ("$name", d.Name), ("$trade", d.TradeName), ("$kind", d.Kind),
            ("$doctype", d.DocumentType), ("$docnum", d.DocumentNumber), ("$customer", d.IsCustomer),
            ("$supplier", d.IsSupplier), ("$email", d.Email), ("$phone", d.Phone), ("$city", d.Address?.City),
            ("$active", d.IsActive), ("$version", change.Version), ("$deleted", Deleted(change.Operation, d.Meta)),
            ("$updated", d.Meta.UpdatedAt), ("$list", d.Credit?.PriceListId), ("$limit", d.Credit?.CreditLimit));
    }

    /// <summary>Lista de precios del hub (<c>price_list</c>): se guarda entera con sus escalas si es más nueva.</summary>
    public async Task ApplyPriceListAsync(VortexMasterChange<VortexPriceList> change, CancellationToken ct = default)
    {
        var d = change.Data;
        await using var c = await Db.OpenAsync(ct);
        await using var tx = c.BeginTransaction();
        var applied = await c.ExecAsync("""
            INSERT INTO price_lists (id, code, name, currency_code, valid_from, valid_to, site_ids, is_active, is_deleted, version, updated_at)
            VALUES ($id, $code, $name, $currency, $from, $to, $sites, $active, $deleted, $version, $updated)
            ON CONFLICT (id) DO UPDATE SET code = excluded.code, name = excluded.name, currency_code = excluded.currency_code,
                valid_from = excluded.valid_from, valid_to = excluded.valid_to, site_ids = excluded.site_ids,
                is_active = excluded.is_active, is_deleted = excluded.is_deleted, version = excluded.version, updated_at = excluded.updated_at
            WHERE excluded.version > price_lists.version
            """, tx, ("$id", change.EntityId), ("$code", d.Code), ("$name", d.Name), ("$currency", d.CurrencyCode),
            ("$from", d.ValidFrom?.ToString("yyyy-MM-dd")), ("$to", d.ValidTo?.ToString("yyyy-MM-dd")),
            ("$sites", System.Text.Json.JsonSerializer.Serialize(d.SiteIds ?? [])), ("$active", d.IsActive),
            ("$deleted", Deleted(change.Operation, d.Meta)), ("$version", change.Version), ("$updated", d.Meta.UpdatedAt));
        if (applied > 0)
        {
            await c.ExecAsync("DELETE FROM price_list_lines WHERE list_id = $id", tx, ("$id", change.EntityId));
            foreach (var line in (d.Lines ?? []).GroupBy(l => (l.ItemId, l.MinQuantity)).Select(g => g.Last()))
                await c.ExecAsync("INSERT INTO price_list_lines (list_id, item_id, min_qty, price) VALUES ($id, $item, $min, $price)", tx,
                    ("$id", change.EntityId), ("$item", line.ItemId), ("$min", line.MinQuantity), ("$price", line.Price));
        }
        await tx.CommitAsync(ct);
    }

    public async Task ApplyCompanyAsync(VortexMasterChange<VortexCompany> change, CancellationToken ct = default)
    {
        var d = change.Data;
        await using var c = await Db.OpenAsync(ct);
        await c.ExecAsync("""
            INSERT INTO company (id, legal_name, trade_name, tax_id, country_code, currency_code, default_tax_rate, time_zone, version)
            VALUES (1, $legal, $trade, $tax, $country, $currency, $rate, $tz, $version)
            ON CONFLICT (id) DO UPDATE SET legal_name = excluded.legal_name, trade_name = excluded.trade_name,
                tax_id = excluded.tax_id, country_code = excluded.country_code, currency_code = excluded.currency_code,
                default_tax_rate = excluded.default_tax_rate, time_zone = excluded.time_zone, version = excluded.version
            WHERE excluded.version > company.version
            """, null, ("$legal", d.LegalName), ("$trade", d.TradeName), ("$tax", d.TaxId), ("$country", d.CountryCode),
            ("$currency", d.CurrencyCode), ("$rate", d.DefaultTaxRate), ("$tz", d.TimeZone), ("$version", change.Version));
    }

    private static bool Deleted(VortexChangeOperation operation, VortexMasterMeta meta) =>
        operation == VortexChangeOperation.Deleted || meta.IsDeleted;

    // ---------- Altas y cambios (se encolan para el hub) ----------

    public Task<Guid> CreateItemAsync(ItemDraft draft, CancellationToken ct = default) => SaveItemAsync(null, draft, ct);

    public Task<Guid> UpdateItemAsync(Guid id, ItemDraft draft, CancellationToken ct = default) => SaveItemAsync(id, draft, ct);

    private Task<Guid> SaveItemAsync(Guid? existingId, ItemDraft draft, CancellationToken ct)
    {
        RequirePermission(PosPermissions.EditCatalog);
        var sku = draft.Sku.Trim().ToUpperInvariant();
        Require(sku.Length > 0, T("error.el_sku_es_obligatorio"));
        Require(!string.IsNullOrWhiteSpace(draft.Name), T("error.el_nombre_es_obligatorio"));
        Require(!string.IsNullOrWhiteSpace(draft.Unit), T("error.la_unidad_es_obligatoria"));
        Require(draft.Price >= 0, T("error.el_precio_no_puede_ser"));
        Require(draft.CurrencyCode.Trim().Length == 3, T("error.la_moneda_debe_ser_un"));

        return InTransactionAsync(async (c, tx, now) =>
        {
            var duplicate = await c.ScalarAsync<long>(
                "SELECT COUNT(*) FROM items WHERE sku = $sku AND is_deleted = 0 AND id <> $id", tx,
                ("$sku", sku), ("$id", existingId ?? Guid.Empty));
            Require(duplicate == 0, T("error.ya_existe_un_articulo_con", sku));

            var id = existingId ?? Guid.NewGuid();
            var version = existingId is null ? 0 : await c.ScalarAsync<int>("SELECT version FROM items WHERE id = $id", tx, ("$id", id));
            var body = new
            {
                sku, name = draft.Name.Trim(), kind = draft.Kind, description = Clean(draft.Description), unit = draft.Unit.Trim().ToLowerInvariant(),
                barcode = Clean(draft.Barcode), category = Clean(draft.Category), price = draft.Price,
                currencyCode = draft.CurrencyCode.Trim().ToUpperInvariant(), taxCode = VortexApi.Suite.Taxes.TaxCategories.Normalize(draft.TaxCode),
                trackInventory = draft.Kind == "Product" && draft.TrackInventory, isActive = draft.IsActive,
            };

            await c.ExecAsync("""
                INSERT INTO items (id, sku, name, kind, description, unit, barcode, category, price, currency_code, tax_code,
                                   track_inventory, is_active, version, is_deleted, pending, updated_at)
                VALUES ($id, $sku, $name, $kind, $desc, $unit, $barcode, $category, $price, $currency, $tax, $track, $active,
                        0, 0, 1, $now)
                ON CONFLICT (id) DO UPDATE SET sku = $sku, name = $name, kind = $kind, description = $desc, unit = $unit,
                    barcode = $barcode, category = $category, price = $price, currency_code = $currency, tax_code = $tax,
                    track_inventory = $track, is_active = $active, pending = 1, updated_at = $now
                """, tx, ("$id", id), ("$sku", body.sku), ("$name", body.name), ("$kind", body.kind), ("$desc", body.description),
                ("$unit", body.unit), ("$barcode", body.barcode), ("$category", body.category), ("$price", body.price),
                ("$currency", body.currencyCode), ("$tax", body.taxCode), ("$track", body.trackInventory),
                ("$active", body.isActive), ("$now", now));

            await Outbox.EnqueueUpsertAsync(c, tx, now, VortexMasterResource.Items, id, body, version > 0 ? version : null);
            return id;
        }, ct);
    }

    public Task<Guid> CreateSiteAsync(SiteDraft draft, CancellationToken ct = default)
    {
        RequirePermission(PosPermissions.EditCatalog);
        var code = draft.Code.Trim().ToUpperInvariant();
        Require(code.Length > 0, T("error.el_codigo_es_obligatorio"));
        Require(!string.IsNullOrWhiteSpace(draft.Name), T("error.el_nombre_es_obligatorio"));

        return InTransactionAsync(async (c, tx, now) =>
        {
            var duplicate = await c.ScalarAsync<long>("SELECT COUNT(*) FROM sites WHERE code = $code AND is_deleted = 0", tx, ("$code", code));
            Require(duplicate == 0, T("error.ya_existe_una_sede_con", code));

            var id = Guid.NewGuid();
            var body = new
            {
                code, name = draft.Name.Trim(), kind = draft.Kind, phone = Clean(draft.Phone),
                address = new { line = Clean(draft.AddressLine), city = Clean(draft.City), countryCode = draft.CountryCode },
                isActive = draft.IsActive,
            };
            await c.ExecAsync("""
                INSERT INTO sites (id, code, name, kind, phone, address_line, city, country_code, is_active, version, is_deleted, pending, updated_at)
                VALUES ($id, $code, $name, $kind, $phone, $line, $city, $country, $active, 0, 0, 1, $now)
                """, tx, ("$id", id), ("$code", code), ("$name", body.name), ("$kind", body.kind), ("$phone", body.phone),
                ("$line", body.address.line), ("$city", body.address.city), ("$country", body.address.countryCode),
                ("$active", body.isActive), ("$now", now));
            await Outbox.EnqueueUpsertAsync(c, tx, now, VortexMasterResource.Sites, id, body);
            return id;
        }, ct);
    }

    public Task<Guid> CreatePartyAsync(PartyDraft draft, CancellationToken ct = default)
    {
        RequirePermission(PosPermissions.CreateCustomer);
        Require(!string.IsNullOrWhiteSpace(draft.Name), T("error.el_nombre_es_obligatorio"));
        Require(draft.IsCustomer || draft.IsSupplier, T("error.indica_si_es_cliente_proveedor"));
        var docType = Clean(draft.DocumentType)?.ToUpperInvariant();
        var docNumber = Clean(draft.DocumentNumber);
        Require((docType is null) == (docNumber is null), T("error.el_tipo_el_numero_de"));
        if (docType == "RUC")
            Require(Ruc().IsMatch(docNumber!), T("error.el_ruc_debe_tener_11"));
        if (docType == "DNI")
            Require(Dni().IsMatch(docNumber!), T("error.el_dni_debe_tener_8"));

        return InTransactionAsync(async (c, tx, now) =>
        {
            if (docNumber is not null)
            {
                var existing = await c.ScalarAsync<string?>(
                    "SELECT name FROM parties WHERE document_number = $doc AND is_deleted = 0 LIMIT 1", tx, ("$doc", docNumber));
                Require(existing is null, T("error.el_documento_ya_esta_registrado", docNumber, existing));
            }

            var id = Guid.NewGuid();
            var body = new
            {
                name = draft.Name.Trim(), isCustomer = draft.IsCustomer, isSupplier = draft.IsSupplier, kind = draft.Kind,
                documentType = docType, documentNumber = docNumber, email = Clean(draft.Email), phone = Clean(draft.Phone),
                address = draft.City is null ? null : new { city = Clean(draft.City), countryCode = draft.CountryCode },
                isActive = true,
            };
            await c.ExecAsync("""
                INSERT INTO parties (id, name, trade_name, kind, document_type, document_number, is_customer, is_supplier,
                                     email, phone, city, is_active, version, is_deleted, pending, updated_at)
                VALUES ($id, $name, NULL, $kind, $doctype, $docnum, $customer, $supplier, $email, $phone, $city, 1, 0, 0, 1, $now)
                """, tx, ("$id", id), ("$name", body.name), ("$kind", body.kind), ("$doctype", docType), ("$docnum", docNumber),
                ("$customer", body.isCustomer), ("$supplier", body.isSupplier), ("$email", body.email), ("$phone", body.phone),
                ("$city", Clean(draft.City)), ("$now", now));
            await Outbox.EnqueueUpsertAsync(c, tx, now, VortexMasterResource.Parties, id, body);
            return id;
        }, ct);
    }

    // ---------- Consultas ----------

    /// <summary>Catálogo con el stock que informa el WMS en <paramref name="siteId"/> (o en todas las sedes).</summary>
    public async Task<PagedResult<ItemRow>> GetItemsAsync(string? search, string? category, int page, int pageSize,
        bool includeInactive = true, Guid? siteId = null, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        const string where = """
            WHERE i.is_deleted = 0 AND ($all = 1 OR i.is_active = 1)
              AND ($search IS NULL OR i.sku LIKE $like OR i.name LIKE $like OR i.barcode = $search)
              AND ($category IS NULL OR i.category = $category)
            """;
        var args = new (string, object?)[]
        {
            ("$all", includeInactive), ("$search", Clean(search)), ("$like", Like(search)), ("$category", Clean(category)),
            ("$site", siteId), ("$limit", pageSize), ("$offset", (page - 1) * pageSize),
        };
        var total = await c.ScalarAsync<int>($"SELECT COUNT(*) FROM items i {where}", null, args);
        var items = await c.QueryAsync($"""
            SELECT i.*, (SELECT SUM(s.on_hand) FROM stock s WHERE s.item_id = i.id AND ($site IS NULL OR s.site_id = $site)) AS on_hand
            FROM items i {where}
            ORDER BY i.name COLLATE NOCASE LIMIT $limit OFFSET $offset
            """, ReadItem, null, args);
        return new PagedResult<ItemRow>(items, total, page, pageSize);
    }

    public async Task<IReadOnlyList<ItemRow>> SearchItemsAsync(string? term, int limit = 8, Guid? siteId = null, CancellationToken ct = default) =>
        (await GetItemsAsync(term, null, 1, limit, includeInactive: false, siteId, ct)).Items;

    public async Task<IReadOnlyList<ItemPresentationRow>> GetPresentationsAsync(Guid itemId, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await c.QueryAsync("SELECT * FROM item_presentations WHERE item_id = $id ORDER BY factor", ReadPresentation, null,
            ("$id", itemId));
    }

    /// <summary>
    /// Lo que la caja hace con un código escaneado: el código del artículo (o su SKU) agrega una unidad; el de una
    /// presentación agrega las unidades que trae (la caja de 12 son 12); una etiqueta de balanza, si la tienda las usa,
    /// agrega el peso (o lo que equivale al precio impreso). Null si no es de ningún artículo activo.
    /// </summary>
    public async Task<ScanMatch?> ResolveScanAsync(string code, Guid? siteId = null, CancellationToken ct = default)
    {
        code = code.Trim();
        if (code.Length == 0)
            return null;
        await using var c = await Db.OpenAsync(ct);
        const string select = """
            SELECT i.*, (SELECT SUM(s.on_hand) FROM stock s WHERE s.item_id = i.id AND ($site IS NULL OR s.site_id = $site)) AS on_hand
            FROM items i
            """;
        if (await c.FirstOrDefaultAsync($"""
                {select} WHERE i.is_deleted = 0 AND i.is_active = 1 AND (i.barcode = $code OR i.sku = $code COLLATE NOCASE)
                ORDER BY i.barcode = $code DESC LIMIT 1
                """, ReadItem, null, ("$code", code), ("$site", siteId)) is { } item)
            return new ScanMatch(item, 1);

        if (await c.FirstOrDefaultAsync("""
                SELECT p.* FROM item_presentations p JOIN items i ON i.id = p.item_id
                WHERE p.barcode = $code AND i.is_deleted = 0 AND i.is_active = 1 LIMIT 1
                """, ReadPresentation, null, ("$code", code)) is { } presentation)
        {
            var owner = await c.FirstOrDefaultAsync($"{select} WHERE i.id = $id", ReadItem, null, ("$id", presentation.ItemId), ("$site", siteId));
            return new ScanMatch(owner!, presentation.Factor, presentation);
        }

        var site = siteId ?? await DefaultSiteAsync(c, null);
        var store = site is { } s ? await StoreSettingsService.GetAsync(c, null, s) : StoreSettings.Default;
        if (!store.ScaleLabels || !ScaleLabel.TryParse(code, store.ScaleItemDigits, out var plu, out var value) || value <= 0)
            return null;
        // El PLU de la balanza es el SKU numérico del artículo (sin importar los ceros a la izquierda) o su código de barras.
        var weighed = await c.FirstOrDefaultAsync($"""
            {select} WHERE i.is_deleted = 0 AND i.is_active = 1
              AND ((i.sku GLOB '[0-9]*' AND ltrim(i.sku, '0') = $plu) OR i.barcode = $raw)
            LIMIT 1
            """, ReadItem, null, ("$plu", plu.TrimStart('0')), ("$raw", plu), ("$site", siteId));
        if (weighed is null)
            return null;
        decimal quantity;
        if (store.ScaleLabelMode == ScaleLabel.Price)
        {
            if (weighed.Price <= 0)
                return null;
            var amount = value / (decimal)Math.Pow(10, Currencies.Get(weighed.CurrencyCode).Decimals);
            quantity = Math.Round(amount / weighed.Price, 3, MidpointRounding.AwayFromZero);
        }
        else
            quantity = value / 1000m;
        return quantity > 0 ? new ScanMatch(weighed, quantity, Weighed: true) : null;
    }

    private static ItemPresentationRow ReadPresentation(SqliteDataReader r) =>
        new(r.Guid("item_id"), r.Str("code"), r.Str("name"), r.Dec("factor"), r.StrOrNull("barcode"));

    public async Task<ItemRow?> GetItemAsync(Guid id, Guid? siteId = null, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await c.FirstOrDefaultAsync("""
            SELECT i.*, (SELECT SUM(s.on_hand) FROM stock s WHERE s.item_id = i.id AND ($site IS NULL OR s.site_id = $site)) AS on_hand
            FROM items i WHERE i.id = $id
            """, ReadItem, null, ("$id", id), ("$site", siteId));
    }

    public async Task<IReadOnlyList<string>> GetCategoriesAsync(CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await c.QueryAsync("""
            SELECT DISTINCT category FROM items WHERE is_deleted = 0 AND is_active = 1 AND category IS NOT NULL AND category <> ''
            ORDER BY category COLLATE NOCASE
            """, r => r.Str("category"));
    }

    public async Task<IReadOnlyList<SiteRow>> GetSitesAsync(bool activeOnly = false, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await c.QueryAsync("""
            SELECT t.*,
                   (SELECT COUNT(*) FROM sales s WHERE s.site_id = t.id AND s.status = 'completed' AND s.created_at >= $today) AS sales_today,
                   (SELECT COALESCE(SUM(total), 0) FROM sales s WHERE s.site_id = t.id AND s.status = 'completed' AND s.created_at >= $today) AS total_today
            FROM sites t
            WHERE t.is_deleted = 0 AND ($active = 0 OR t.is_active = 1)
            ORDER BY CASE t.kind WHEN 'Store' THEN 0 ELSE 1 END, t.code
            """, r => new SiteRow(r.Guid("id"), r.Str("code"), r.Str("name"), r.Str("kind"), r.StrOrNull("phone"),
                r.StrOrNull("address_line"), r.StrOrNull("city"), r.StrOrNull("country_code"), r.Bool("is_active"),
                r.Int("version"), r.Bool("pending"), r.Int("sales_today"), r.Dec("total_today")), null,
            ("$active", activeOnly), ("$today", StartOfDay()));
    }

    public async Task<PagedResult<PartyRow>> GetPartiesAsync(string? search, bool? customers, bool? suppliers, int page,
        int pageSize, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        const string where = """
            WHERE is_deleted = 0
              AND ($search IS NULL OR name LIKE $like OR trade_name LIKE $like OR document_number LIKE $like OR email LIKE $like)
              AND ($customers IS NULL OR is_customer = $customers)
              AND ($suppliers IS NULL OR is_supplier = $suppliers)
            """;
        var args = new (string, object?)[]
        {
            ("$search", Clean(search)), ("$like", Like(search)), ("$customers", customers), ("$suppliers", suppliers),
            ("$limit", pageSize), ("$offset", (page - 1) * pageSize),
        };
        var total = await c.ScalarAsync<int>($"SELECT COUNT(*) FROM parties {where}", null, args);
        var rows = await c.QueryAsync($"SELECT * FROM parties {where} ORDER BY name COLLATE NOCASE LIMIT $limit OFFSET $offset",
            ReadParty, null, args);
        return new PagedResult<PartyRow>(rows, total, page, pageSize);
    }

    public async Task<IReadOnlyList<Option>> GetPartyOptionsAsync(bool suppliers, CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await c.QueryAsync($"""
            SELECT id, name, document_number FROM parties
            WHERE is_deleted = 0 AND is_active = 1 AND {(suppliers ? "is_supplier" : "is_customer")} = 1
            ORDER BY name COLLATE NOCASE
            """, r => new Option(r.Guid("id"), r.Str("name"), r.StrOrNull("document_number")));
    }

    public async Task<CompanyInfo?> GetCompanyAsync(CancellationToken ct = default)
    {
        await using var c = await Db.OpenAsync(ct);
        return await c.FirstOrDefaultAsync("SELECT * FROM company WHERE id = 1", r => new CompanyInfo(
            r.Str("legal_name"), r.StrOrNull("trade_name"), r.StrOrNull("tax_id"), r.Str("country_code"), r.Str("currency_code"),
            r.DecOrNull("default_tax_rate"), r.StrOrNull("time_zone")));
    }

    internal static ItemRow ReadItem(SqliteDataReader r) => new(
        r.Guid("id"), r.Str("sku"), r.Str("name"), r.Str("kind"), r.StrOrNull("description"), r.Str("unit"),
        r.StrOrNull("barcode"), r.StrOrNull("category"), r.Dec("price"), r.Str("currency_code"), r.StrOrNull("tax_code"),
        r.Bool("track_inventory"), r.Bool("is_active"), r.Int("version"), r.Bool("pending"), r.DecOrNull("on_hand"));

    private static PartyRow ReadParty(SqliteDataReader r) => new(
        r.Guid("id"), r.Str("name"), r.StrOrNull("trade_name"), r.Str("kind"), r.StrOrNull("document_type"),
        r.StrOrNull("document_number"), r.Bool("is_customer"), r.Bool("is_supplier"), r.StrOrNull("email"),
        r.StrOrNull("phone"), r.StrOrNull("city"), r.Bool("is_active"), r.Int("version"), r.Bool("pending"));

    [GeneratedRegex(@"^\d{11}$")]
    private static partial Regex Ruc();

    [GeneratedRegex(@"^\d{8}$")]
    private static partial Regex Dni();
}
