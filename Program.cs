using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Security.Cryptography;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using MySqlConnector;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowDesktopApp", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

builder.Services.AddSingleton<Db>();
builder.Services.AddSingleton<SheetsReporter>();

var app = builder.Build();

app.UseCors("AllowDesktopApp");

// V50: arranque seguro + reportes de turno 08:00-20:00 / 20:00-08:00.
// Solo crea estructuras faltantes y aplica la configuración oficial; no borra ventas ni inventario.
try
{
    var startupDb = app.Services.GetRequiredService<Db>();
    await using var startupCon = await startupDb.OpenAsync();
    await EnsureCoreSchemaAsync(startupCon);
    await EnsureUserManagementTables(startupCon);
    await EnsureSectorLayoutAsync(startupCon);
    await EnsurePremiumSharedCatalogAsync(startupCon);
    await EnsurePremiumSectorStockAsync(startupCon);
    await EnsureInitialStockLedgerV76Async(startupCon);
    await EnsureMesasEnVivoTables(startupCon);
    await EnsureOfficialBranchAndTableLayout(startupCon);
    await EnsureTablePricingAsync(startupCon);
    await EnsureTablePromotionScopeAsync(startupCon);
    await EnsureTableLayoutSettingsAsync(startupCon);
    await EnsureVentaSyncProtection(startupCon);
    await EnsureAccountingLedger(startupCon);
    try { await new MySqlCommand("ALTER TABLE cobros_mesa ADD COLUMN caja_nombre VARCHAR(100) NOT NULL DEFAULT '' AFTER mesa;", startupCon).ExecuteNonQueryAsync(); } catch { }
    try { await new MySqlCommand("ALTER TABLE cobros_mesa ADD COLUMN sector VARCHAR(20) NOT NULL DEFAULT 'GENERAL' AFTER caja_nombre;", startupCon).ExecuteNonQueryAsync(); } catch { }
    try { await new MySqlCommand("ALTER TABLE cobros_mesa DROP INDEX uk_cobro_sesion;", startupCon).ExecuteNonQueryAsync(); } catch { }
    try { await new MySqlCommand("UPDATE cobros_mesa SET sector=CASE WHEN sucursal_id=1 THEN 'GENERAL' WHEN UPPER(COALESCE(caja_nombre,'')) LIKE '%ABAJO%' THEN 'ABAJO' ELSE 'ARRIBA' END WHERE sector IS NULL OR TRIM(sector)='' OR (sucursal_id=2 AND UPPER(sector)='GENERAL');", startupCon).ExecuteNonQueryAsync(); } catch { }
    try { await new MySqlCommand("ALTER TABLE cobros_mesa ADD UNIQUE KEY uk_cobro_sesion_sector (sucursal_id, sector, session_id);", startupCon).ExecuteNonQueryAsync(); } catch { }
    // V66: los productos servidos en vaso también manejan cantidad real y deben descontarse.
    await using (var vasoStock = new MySqlCommand("UPDATE productos SET sin_limite_stock=0 WHERE categoria='Servidos en vaso';", startupCon))
        await vasoStock.ExecuteNonQueryAsync();
}
catch (Exception ex)
{
    app.Logger.LogError(ex, "No se pudo completar la inicialización segura de MySQL.");
}

app.MapGet("/", () => Results.Ok(new
{
    app = "BILLAR EL BRUJO API",
    status = "online",
    message = "API funcionando correctamente"
}));

app.MapGet("/api/instance", () => Results.Ok(new
{
    ok = true,
    code = GetInstanceCode(),
    locked = IsInstanceLocked(),
    sucursalId = GetInstanceSucursalId(),
    sector = GetInstanceSector(),
    message = IsInstanceLocked()
        ? "Instancia protegida. Solo acepta operaciones de su sucursal/sector configurado."
        : "Instancia compatible/compartida. INSTANCE_SUCURSAL_ID e INSTANCE_SECTOR no están fijados."
}));

app.MapGet("/health", async (Db db, SheetsReporter sheets) =>
{
    try
    {
        await using var con = await db.OpenAsync();
        await EnsureVentaSyncProtection(con);
        await using var cmd = new MySqlCommand("SELECT DATABASE();", con);
        var database = Convert.ToString(await cmd.ExecuteScalarAsync());
        long ventasRaw = Convert.ToInt64(await new MySqlCommand("SELECT COUNT(*) FROM ventas;", con).ExecuteScalarAsync() ?? 0L);
        long ventasCanonicas = Convert.ToInt64(await new MySqlCommand("SELECT COUNT(*) FROM ventas_canonicas;", con).ExecuteScalarAsync() ?? 0L);

        return Results.Ok(new
        {
            ok = true,
            version = "V90_INSTANCIAS_SEPARADAS",
            instanceCode = GetInstanceCode(),
            instanceSucursalId = GetInstanceSucursalId(),
            instanceSector = GetInstanceSector(),
            database,
            mysql = "conectado",
            googleSheets = !InstanceSheetsEnabled() ? "desactivado por seguridad en instancia separada" : (sheets.IsConfigured ? "configurado" : "faltan variables GOOGLE_SHEET_ID y GOOGLE_CREDENTIALS_JSON"),
            ventasRaw,
            ventasCanonicas,
            duplicadosHistoricosIgnorados = Math.Max(0, ventasRaw - ventasCanonicas)
        });
    }
    catch (Exception ex)
    {
        return Results.Problem("No se pudo conectar a MySQL: " + ex.Message);
    }
});

app.MapGet("/api/system/version", () => Results.Ok(new
{
    ok = true,
    apiVersion = "V90_INSTANCIAS_SEPARADAS",
    instanceCode = GetInstanceCode(),
    instanceSucursalId = GetInstanceSucursalId(),
    instanceSector = GetInstanceSector(),
    minimumClientVersion = 167,
    accountingMode = "LIBRO_INMUTABLE_TRANSACCIONAL",
    message = "API V90. Instancias separadas por sucursal/sector, stock remoto idempotente y protección contra cruces entre ARRIBA/ABAJO/EL BRUJO."
}));

app.MapGet("/api/sheets/status", async (SheetsReporter sheets) =>
{
    var test = await sheets.TestConnectionAsync();
    return Results.Ok(test);
});

// V73: prueba REAL contra Google. No basta con que existan las variables de Railway.
// Este endpoint confirma ID, credencial, correo de la cuenta de servicio y permiso sobre el Sheet.
app.MapGet("/api/sheets/test", async (SheetsReporter sheets) =>
{
    var test = await sheets.TestConnectionAsync();
    return test.Ok ? Results.Ok(test) : Results.Json(test, statusCode: StatusCodes.Status503ServiceUnavailable);
});

app.MapGet("/api/sheets/debug", async (Db db, SheetsReporter sheets) =>
{
    try
    {
        var sheetsConnection = await sheets.TestConnectionAsync();
        await using var con = await db.OpenAsync();
        await EnsureCoreSchemaAsync(con);
        await EnsureUserManagementTables(con);
        await EnsureSectorLayoutAsync(con);
        await EnsureVentaSyncProtection(con);
        await EnsureAccountingLedger(con);

        async Task<long> CountAsync(string sql)
            => Convert.ToInt64(await new MySqlCommand(sql, con).ExecuteScalarAsync() ?? 0L);

        long ventasRaw = await CountAsync("SELECT COUNT(*) FROM ventas;");
        long ventasCanonicas = await CountAsync("SELECT COUNT(*) FROM ventas_canonicas;");
        long brujoVentas = await CountAsync("SELECT COUNT(*) FROM ventas_canonicas WHERE sucursal_id=1;");
        long premiuVentas = await CountAsync("SELECT COUNT(*) FROM ventas_canonicas WHERE sucursal_id=2;");
        long cierresBrujo = await CountAsync("SELECT COUNT(*) FROM cierres_turno WHERE sucursal_id=1;");
        long cierresPremiu = await CountAsync("SELECT COUNT(*) FROM cierres_turno WHERE sucursal_id=2;");
        long productosBrujo = await CountAsync("SELECT COUNT(*) FROM productos WHERE sucursal_id=1 AND estado='ACTIVO';");
        long productosPremiu = await CountAsync("SELECT COUNT(*) FROM productos WHERE sucursal_id=2 AND estado='ACTIVO';");

        string? ultimaVenta = Convert.ToString(await new MySqlCommand("SELECT DATE_FORMAT(MAX(fecha),'%Y-%m-%d %H:%i:%s') FROM ventas_canonicas;", con).ExecuteScalarAsync());

        return Results.Ok(new
        {
            ok = true,
            apiVersion = "V79_COMBOS_CANTIDAD_AUTOMATICA",
            googleSheetsConfigured = sheets.IsConfigured,
            googleSheetsConnected = sheetsConnection.Ok,
            googleSheetsMessage = sheetsConnection.Message,
            serviceAccountEmail = sheetsConnection.ServiceAccountEmail,
            spreadsheetTitle = sheetsConnection.SpreadsheetTitle,
            spreadsheetId = sheets.SpreadsheetId,
            ventasRaw,
            ventasCanonicas,
            duplicadosIgnorados = Math.Max(0, ventasRaw - ventasCanonicas),
            elBrujo = new { ventas = brujoVentas, cierres = cierresBrujo, productos = productosBrujo },
            elBrujoPremiu = new { ventas = premiuVentas, cierres = cierresPremiu, productosCompartidos = productosPremiu },
            ultimaVenta
        });
    }
    catch (Exception ex)
    {
        return Results.Problem("Diagnóstico de Google Sheets/MySQL: " + ex.Message);
    }
});

app.MapPost("/api/sheets/sync", async (Db db, SheetsReporter sheets) =>
{
    if (!InstanceSheetsEnabled())
        return Results.BadRequest(new { ok = false, message = "Google Sheets está desactivado por seguridad en esta instancia separada. Usa un GOOGLE_SHEET_ID exclusivo y activa INSTANCE_SHEETS_ENABLED=true solo si realmente quieres sincronizar esta API." });
    if (!sheets.IsConfigured)
        return Results.BadRequest(new { ok = false, message = "Faltan GOOGLE_SHEET_ID y GOOGLE_CREDENTIALS_JSON en Railway." });

    try
    {
        await using (var con = await db.OpenAsync())
        {
            await EnsureCoreSchemaAsync(con);
            await EnsureUserManagementTables(con);
            await EnsureSectorLayoutAsync(con);
            await EnsureVentaSyncProtection(con);
            await EnsureAccountingLedger(con);
        }
        var connection = await sheets.TestConnectionAsync();
        if (!connection.Ok)
            return Results.Json(connection, statusCode: StatusCodes.Status503ServiceUnavailable);

        var result = await sheets.SyncFromDatabaseAsync(db);
        return Results.Ok(new { ok = true, connection, message = result });
    }
    catch (Exception ex)
    {
        return Results.Problem("No se pudo actualizar Google Sheets: " + ex.Message);
    }
});

app.MapGet("/api/sheets/sync", async (Db db, SheetsReporter sheets) =>
{
    if (!InstanceSheetsEnabled())
        return Results.BadRequest(new { ok = false, message = "Google Sheets está desactivado por seguridad en esta instancia separada. Usa un GOOGLE_SHEET_ID exclusivo y activa INSTANCE_SHEETS_ENABLED=true solo si realmente quieres sincronizar esta API." });
    if (!sheets.IsConfigured)
        return Results.BadRequest(new { ok = false, message = "Faltan GOOGLE_SHEET_ID y GOOGLE_CREDENTIALS_JSON en Railway." });

    try
    {
        await using (var con = await db.OpenAsync())
        {
            await EnsureCoreSchemaAsync(con);
            await EnsureUserManagementTables(con);
            await EnsureSectorLayoutAsync(con);
            await EnsureVentaSyncProtection(con);
            await EnsureAccountingLedger(con);
        }
        var connection = await sheets.TestConnectionAsync();
        if (!connection.Ok)
            return Results.Json(connection, statusCode: StatusCodes.Status503ServiceUnavailable);

        var result = await sheets.SyncFromDatabaseAsync(db);
        return Results.Ok(new { ok = true, connection, message = result });
    }
    catch (Exception ex)
    {
        return Results.Problem("No se pudo actualizar Google Sheets: " + ex.Message);
    }
});

// V49 producción: limpieza masiva deshabilitada para proteger los datos reales.
app.MapPost("/api/admin/limpiar-pruebas", () =>
    Results.Json(new { ok = false, message = "Limpieza masiva deshabilitada en producción para proteger los datos." }, statusCode: StatusCodes.Status410Gone));

app.MapGet("/api/admin/limpiar-pruebas", () =>
    Results.Json(new { ok = false, message = "Limpieza masiva deshabilitada en producción para proteger los datos." }, statusCode: StatusCodes.Status410Gone));

app.MapPost("/api/login", async (Db db, LoginRequest req) =>
{
    await using var con = await db.OpenAsync();
    await EnsureUserManagementTables(con);

    string usuario = (req.Usuario ?? "").Trim().ToLowerInvariant();
    string claveIngresada = req.Clave ?? "";

    int id = 0;
    string usuarioDb = "";
    string rol = "";
    int sucursalId = 1;
    string sucursal = "EL BRUJO";
    string nombre = "";
    string caja = "";
    string turno = "MAÑANA";
    string sector = "GENERAL";
    string claveGuardada = "";

    const string sql = """
        SELECT u.id, u.usuario, u.clave, u.rol, u.estado, u.sucursal_id,
               COALESCE(u.nombre_completo, u.usuario) AS nombre_completo,
               COALESCE(u.caja_nombre, '') AS caja_nombre,
               COALESCE(u.turno, 'MAÑANA') AS turno,
               COALESCE(u.sector, CASE WHEN u.sucursal_id=2 THEN 'ARRIBA' ELSE 'GENERAL' END) AS sector,
               CASE WHEN s.id = 2 THEN 'EL BRUJO PREMIU' ELSE 'EL BRUJO' END AS sucursal
        FROM usuarios u
        LEFT JOIN sucursales s ON s.id = u.sucursal_id
        WHERE u.usuario = @usuario AND u.estado = 'ACTIVO'
        LIMIT 1;
    """;

    await using (var cmd = new MySqlCommand(sql, con))
    {
        cmd.Parameters.AddWithValue("@usuario", usuario);
        await using var rd = await cmd.ExecuteReaderAsync();
        if (!await rd.ReadAsync())
            return Results.Unauthorized();

        id = rd.GetInt32("id");
        usuarioDb = rd.GetString("usuario");
        claveGuardada = rd.GetString("clave");
        rol = rd.GetString("rol");
        sucursalId = rd.IsDBNull(rd.GetOrdinal("sucursal_id")) ? 1 : rd.GetInt32("sucursal_id");
        sucursal = rd.IsDBNull(rd.GetOrdinal("sucursal")) ? "TODAS" : rd.GetString("sucursal");
        nombre = rd.IsDBNull(rd.GetOrdinal("nombre_completo")) ? usuarioDb : rd.GetString("nombre_completo");
        caja = rd.IsDBNull(rd.GetOrdinal("caja_nombre")) ? "" : rd.GetString("caja_nombre");
        turno = rd.IsDBNull(rd.GetOrdinal("turno")) ? "MAÑANA" : rd.GetString("turno");
        sector = rd.IsDBNull(rd.GetOrdinal("sector")) ? NormalizarSector(sucursalId, null, caja) : NormalizarSector(sucursalId, rd.GetString("sector"), caja);
    }

    if (!PasswordHasher.Verify(claveIngresada, claveGuardada))
        return Results.Unauthorized();

    if (IsInstanceLocked() && !rol.Contains("ADMIN", StringComparison.OrdinalIgnoreCase) && !InstanceRequestMatches(sucursalId, sector, caja))
        return Results.Json(new { ok=false, message="Este usuario pertenece a otra sucursal/sector y no puede ingresar a esta API." }, statusCode: StatusCodes.Status403Forbidden);

    if (!PasswordHasher.IsHashed(claveGuardada))
        await UpdateUserPasswordHash(con, id, claveIngresada);

    return Results.Ok(new
    {
        id,
        usuario = usuarioDb,
        rol,
        sucursal,
        nombre,
        caja,
        turno,
        sector,
        sucursal_id = sucursalId
    });
});

app.MapGet("/api/admin/usuarios", async (Db db, string clave) =>
{
    const string cleanKey = "ENTREGAR_LIMPIO_2026";
    if (clave != cleanKey) return Results.Unauthorized();

    await using var con = await db.OpenAsync();
    await EnsureUserManagementTables(con);

    const string sql = """
        SELECT u.id,
               u.usuario,
               COALESCE(u.nombre_completo, u.usuario) AS nombre_completo,
               u.rol,
               u.sucursal_id,
               CASE WHEN s.id = 2 THEN 'EL BRUJO PREMIU' ELSE 'EL BRUJO' END AS sucursal,
               COALESCE(u.caja_nombre, '') AS caja_nombre,
               COALESCE(u.turno, 'MAÑANA') AS turno,
               COALESCE(u.sector, CASE WHEN u.sucursal_id=2 THEN 'ARRIBA' ELSE 'GENERAL' END) AS sector,
               u.estado
        FROM usuarios u
        LEFT JOIN sucursales s ON s.id = u.sucursal_id
        ORDER BY u.rol, u.sucursal_id, u.usuario;
    """;

    return Results.Ok(await db.QueryAsync(con, sql, new Dictionary<string, object?>()));
});

app.MapPost("/api/admin/usuarios", async (Db db, string clave, AdminUserRequest req) =>
{
    const string cleanKey = "ENTREGAR_LIMPIO_2026";
    if (clave != cleanKey) return Results.Unauthorized();

    await using var con = await db.OpenAsync();
    await EnsureUserManagementTables(con);

    string usuario = (req.Usuario ?? "").Trim().ToLowerInvariant();
    string pass = (req.Clave ?? "").Trim();
    string rol = NormalizarRol(req.Rol);
    int sucursalId = ResolveInstanceSucursalId(req.SucursalId);
    string estado = string.IsNullOrWhiteSpace(req.Estado) ? "ACTIVO" : req.Estado.Trim().ToUpperInvariant();
    string turno = NormalizarTurno(req.Turno);
    string sector = NormalizarSector(sucursalId, req.Sector, req.CajaNombre);

    if (string.IsNullOrWhiteSpace(usuario))
        return Results.BadRequest(new { ok = false, message = "Usuario requerido." });

    string passHash;
    if (string.IsNullOrWhiteSpace(pass))
    {
        object? actual = null;
        await using (var getPass = new MySqlCommand("SELECT clave FROM usuarios WHERE usuario = @usuario LIMIT 1;", con))
        {
            getPass.Parameters.AddWithValue("@usuario", usuario);
            actual = await getPass.ExecuteScalarAsync();
        }

        passHash = actual == null ? PasswordHasher.Hash("123456") : Convert.ToString(actual) ?? PasswordHasher.Hash("123456");
        if (!PasswordHasher.IsHashed(passHash))
            passHash = PasswordHasher.Hash(passHash);
    }
    else
    {
        passHash = PasswordHasher.Hash(pass);
    }

    await using var cmd = new MySqlCommand("""
        INSERT INTO usuarios
            (usuario, clave, rol, sucursal_id, estado, nombre_completo, caja_nombre, turno, sector)
        VALUES
            (@usuario, @clave, @rol, @sucursal_id, @estado, @nombre_completo, @caja_nombre, @turno, @sector)
        ON DUPLICATE KEY UPDATE
            clave = VALUES(clave),
            rol = VALUES(rol),
            sucursal_id = VALUES(sucursal_id),
            estado = VALUES(estado),
            nombre_completo = VALUES(nombre_completo),
            caja_nombre = VALUES(caja_nombre),
            turno = VALUES(turno),
            sector = VALUES(sector);
    """, con);

    cmd.Parameters.AddWithValue("@usuario", usuario);
    cmd.Parameters.AddWithValue("@clave", passHash);
    cmd.Parameters.AddWithValue("@rol", rol);
    cmd.Parameters.AddWithValue("@sucursal_id", sucursalId);
    cmd.Parameters.AddWithValue("@estado", estado);
    cmd.Parameters.AddWithValue("@nombre_completo", string.IsNullOrWhiteSpace(req.NombreCompleto) ? usuario : req.NombreCompleto.Trim());
    cmd.Parameters.AddWithValue("@caja_nombre", req.CajaNombre ?? "");
    cmd.Parameters.AddWithValue("@turno", turno);
    cmd.Parameters.AddWithValue("@sector", sector);
    await cmd.ExecuteNonQueryAsync();

    return Results.Ok(new
    {
        ok = true,
        usuario,
        rol,
        sucursal_id = sucursalId,
        turno,
        sector,
        estado,
        message = "Usuario guardado."
    });
});

app.MapPost("/api/admin/usuarios/{id:int}/estado", async (Db db, string clave, int id, UserEstadoRequest req) =>
{
    const string cleanKey = "ENTREGAR_LIMPIO_2026";
    if (clave != cleanKey) return Results.Unauthorized();

    await using var con = await db.OpenAsync();
    await EnsureUserManagementTables(con);

    string estado = string.IsNullOrWhiteSpace(req.Estado) ? "INACTIVO" : req.Estado.Trim().ToUpperInvariant();
    if (estado != "ACTIVO" && estado != "INACTIVO")
        return Results.BadRequest(new { ok = false, message = "Estado inválido." });

    await using var cmd = new MySqlCommand("UPDATE usuarios SET estado = @estado WHERE id = @id;", con);
    cmd.Parameters.AddWithValue("@estado", estado);
    cmd.Parameters.AddWithValue("@id", id);
    int rows = await cmd.ExecuteNonQueryAsync();

    return Results.Ok(new { ok = rows > 0, id, estado });
});



app.MapPost("/api/admin/productos/comision", async (Db db, string clave, ProductCommissionRequest req) =>
{
    const string cleanKey = "ENTREGAR_LIMPIO_2026";
    if (clave != cleanKey) return Results.Unauthorized();

    await using var con = await db.OpenAsync();
    await EnsureAppMeseraTables(con);

    string nombre = (req.Nombre ?? "").Trim();
    string tipo = (req.TipoComision ?? "NINGUNA").Trim().ToUpperInvariant();
    bool genera = req.GeneraComision && req.ValorComision > 0;

    if (string.IsNullOrWhiteSpace(nombre))
        return Results.BadRequest(new { ok = false, message = "Nombre del producto requerido." });

    if (!genera)
    {
        tipo = "NINGUNA";
    }
    else if (tipo != "PORCENTAJE" && tipo != "MONTO")
    {
        return Results.BadRequest(new { ok = false, message = "Tipo de comisión inválido. Use PORCENTAJE o MONTO." });
    }

    decimal valor = genera ? req.ValorComision : 0;
    int sucursalId = ResolveInstanceSucursalId(req.SucursalId);
    string sector = NormalizarSectorProducto(sucursalId, req.Sector);

    const string sql = """
        UPDATE productos
        SET genera_comision = @genera_comision,
            tipo_comision = @tipo_comision,
            valor_comision = @valor_comision
        WHERE sucursal_id = @sucursal_id
          AND (@sucursal_id = 2 OR sector = @sector)
          AND estado = 'ACTIVO'
          AND LOWER(nombre) = LOWER(@nombre);
    """;

    await using var cmd = new MySqlCommand(sql, con);
    cmd.Parameters.AddWithValue("@genera_comision", genera ? 1 : 0);
    cmd.Parameters.AddWithValue("@tipo_comision", tipo);
    cmd.Parameters.AddWithValue("@valor_comision", valor);
    cmd.Parameters.AddWithValue("@sucursal_id", sucursalId);
    cmd.Parameters.AddWithValue("@sector", sector);
    cmd.Parameters.AddWithValue("@nombre", nombre);

    int rows = await cmd.ExecuteNonQueryAsync();
    if (rows <= 0)
        return Results.NotFound(new { ok = false, message = "Producto no encontrado en esa sucursal." });

    return Results.Ok(new
    {
        ok = true,
        producto = nombre,
        sucursal_id = sucursalId,
        sector,
        genera_comision = genera,
        tipo_comision = tipo,
        valor_comision = valor
    });
});

app.MapGet("/api/admin/productos/comision", async (Db db, string clave, int sucursalId) =>
{
    const string cleanKey = "ENTREGAR_LIMPIO_2026";
    if (clave != cleanKey) return Results.Unauthorized();

    await using var con = await db.OpenAsync();
    await EnsureAppMeseraTables(con);

    const string sql = """
        SELECT id, sucursal_id, sector, nombre, categoria,
               COALESCE(genera_comision, 0) AS genera_comision,
               COALESCE(tipo_comision, 'NINGUNA') AS tipo_comision,
               COALESCE(valor_comision, 0) AS valor_comision
        FROM productos
        WHERE sucursal_id = @sucursal_id
          AND estado = 'ACTIVO'
        ORDER BY categoria, nombre;
    """;

    return Results.Ok(await db.QueryAsync(con, sql, new Dictionary<string, object?>
    {
        ["@sucursal_id"] = ResolveInstanceSucursalId(sucursalId)
    }));
});


app.MapPost("/api/admin/productos/guardar", async (Db db, SheetsReporter sheets, string clave, AdminProductRequest req) =>
{
    const string cleanKey = "ENTREGAR_LIMPIO_2026";
    if (clave != cleanKey) return Results.Unauthorized();
    if (!InstanceRequestMatches(req.SucursalId, req.Sector))
        return Results.BadRequest(new { ok = false, message = InstanceMismatchMessage(req.SucursalId, req.Sector) });

    int sucursalId = ResolveInstanceSucursalId(req.SucursalId);
    string sector = NormalizarSectorProducto(sucursalId, req.Sector);
    string nombre = (req.Nombre ?? "").Trim();
    if (string.IsNullOrWhiteSpace(nombre))
        return Results.BadRequest(new { ok = false, message = "Nombre del producto requerido." });

    string categoria = NormalizarCategoriaProducto(req.Categoria, nombre);
    string unidadBase = string.IsNullOrWhiteSpace(req.UnidadBase) ? "UNIDAD" : req.UnidadBase.Trim();
    string tipoEntrada = string.IsNullOrWhiteSpace(req.TipoEntrada) ? "PAQUETE" : req.TipoEntrada.Trim();
    int unidadesPorEntrada = req.UnidadesPorEntrada <= 0 ? 1 : req.UnidadesPorEntrada;
    decimal stockActual = Math.Max(0, req.StockActual);
    decimal stockMinimo = Math.Max(0, req.StockMinimo);
    decimal precioCompra = Math.Max(0, req.PrecioCompra);
    string estado = string.Equals(req.Estado, "INACTIVO", StringComparison.OrdinalIgnoreCase) ? "INACTIVO" : "ACTIVO";
    string tipoComision = req.GeneraComision ? (req.TipoComision ?? "PORCENTAJE").Trim().ToUpperInvariant() : "NINGUNA";
    decimal valorComision = req.GeneraComision ? Math.Max(0, req.ValorComision) : 0;
    int rendimientoVaso = req.RendimientoVaso <= 0 ? 10 : Math.Min(req.RendimientoVaso, 100);

    await using var con = await db.OpenAsync();
    await EnsureAppMeseraTables(con);
    await using var tx = await con.BeginTransactionAsync();

    try
    {
        long productoId = 0;
        string nombreAnterior = "";

        // Si la PC ya conoce el ID online, se usa primero. Esto permite renombrar
        // un producto sin crear un duplicado en Railway.
        if (req.ProductoId.GetValueOrDefault() > 0)
        {
            await using var buscarId = new MySqlCommand("SELECT id FROM productos WHERE id = @id AND sucursal_id = @sucursal_id AND sector = @sector AND estado='ACTIVO' LIMIT 1;", con, tx);
            buscarId.Parameters.AddWithValue("@id", req.ProductoId!.Value);
            buscarId.Parameters.AddWithValue("@sucursal_id", sucursalId);
            buscarId.Parameters.AddWithValue("@sector", sector);
            object? foundId = await buscarId.ExecuteScalarAsync();
            if (foundId != null) productoId = Convert.ToInt64(foundId);
        }

        if (productoId <= 0)
        {
            await using var buscar = new MySqlCommand("SELECT id FROM productos WHERE sucursal_id = @sucursal_id AND sector = @sector AND estado='ACTIVO' AND LOWER(TRIM(nombre)) = LOWER(TRIM(@nombre)) ORDER BY id LIMIT 1;", con, tx);
            buscar.Parameters.AddWithValue("@sucursal_id", sucursalId);
            buscar.Parameters.AddWithValue("@sector", sector);
            buscar.Parameters.AddWithValue("@nombre", nombre);
            object? found = await buscar.ExecuteScalarAsync();
            if (found != null) productoId = Convert.ToInt64(found);
        }

        if (productoId > 0)
        {
            await using var oldNameCmd = new MySqlCommand("SELECT nombre FROM productos WHERE id=@id LIMIT 1;", con, tx);
            oldNameCmd.Parameters.AddWithValue("@id", productoId);
            nombreAnterior = Convert.ToString(await oldNameCmd.ExecuteScalarAsync()) ?? "";
        }

        if (productoId <= 0)
        {
            await using var cmd = new MySqlCommand("""
                INSERT INTO productos
                    (sucursal_id, sector, nombre, categoria, unidad_base, stock_actual, stock_minimo, estado,
                     genera_comision, tipo_comision, valor_comision, sin_limite_stock, rendimiento_vaso,
                     tipo_entrada, unidades_por_entrada, precio_compra)
                VALUES
                    (@sucursal_id, @sector, @nombre, @categoria, @unidad_base, @stock_actual, @stock_minimo, @estado,
                     @genera_comision, @tipo_comision, @valor_comision, @sin_limite_stock, @rendimiento_vaso,
                     @tipo_entrada, @unidades_por_entrada, @precio_compra);
                SELECT LAST_INSERT_ID();
            """, con, tx);
            cmd.Parameters.AddWithValue("@sucursal_id", sucursalId);
            cmd.Parameters.AddWithValue("@sector", sector);
            cmd.Parameters.AddWithValue("@nombre", nombre);
            cmd.Parameters.AddWithValue("@categoria", categoria);
            cmd.Parameters.AddWithValue("@unidad_base", unidadBase);
            cmd.Parameters.AddWithValue("@stock_actual", stockActual);
            cmd.Parameters.AddWithValue("@stock_minimo", stockMinimo);
            cmd.Parameters.AddWithValue("@estado", estado);
            cmd.Parameters.AddWithValue("@genera_comision", req.GeneraComision ? 1 : 0);
            cmd.Parameters.AddWithValue("@tipo_comision", tipoComision);
            cmd.Parameters.AddWithValue("@valor_comision", valorComision);
            cmd.Parameters.AddWithValue("@sin_limite_stock", req.SinLimiteStock ? 1 : 0);
            cmd.Parameters.AddWithValue("@rendimiento_vaso", rendimientoVaso);
            cmd.Parameters.AddWithValue("@tipo_entrada", tipoEntrada);
            cmd.Parameters.AddWithValue("@unidades_por_entrada", unidadesPorEntrada);
            cmd.Parameters.AddWithValue("@precio_compra", precioCompra);
            productoId = Convert.ToInt64(await cmd.ExecuteScalarAsync());
        }
        else
        {
            await using var cmd = new MySqlCommand("""
                UPDATE productos
                SET sector = @sector,
                    nombre = @nombre,
                    categoria = @categoria,
                    unidad_base = @unidad_base,
                    -- V67: en productos existentes el stock NO se reemplaza desde una copia local.
                    -- Los aumentos/quitas usan /api/admin/productos/ajustar-stock con DELTA idempotente.
                    stock_minimo = @stock_minimo,
                    estado = @estado,
                    genera_comision = @genera_comision,
                    tipo_comision = @tipo_comision,
                    valor_comision = @valor_comision,
                    sin_limite_stock = @sin_limite_stock,
                    rendimiento_vaso = @rendimiento_vaso,
                    tipo_entrada = @tipo_entrada,
                    unidades_por_entrada = @unidades_por_entrada,
                    precio_compra = @precio_compra
                WHERE id = @id;
            """, con, tx);
            cmd.Parameters.AddWithValue("@sector", sector);
            cmd.Parameters.AddWithValue("@nombre", nombre);
            cmd.Parameters.AddWithValue("@categoria", categoria);
            cmd.Parameters.AddWithValue("@unidad_base", unidadBase);
            cmd.Parameters.AddWithValue("@stock_actual", stockActual);
            cmd.Parameters.AddWithValue("@stock_minimo", stockMinimo);
            cmd.Parameters.AddWithValue("@estado", estado);
            cmd.Parameters.AddWithValue("@genera_comision", req.GeneraComision ? 1 : 0);
            cmd.Parameters.AddWithValue("@tipo_comision", tipoComision);
            cmd.Parameters.AddWithValue("@valor_comision", valorComision);
            cmd.Parameters.AddWithValue("@sin_limite_stock", req.SinLimiteStock ? 1 : 0);
            cmd.Parameters.AddWithValue("@rendimiento_vaso", rendimientoVaso);
            cmd.Parameters.AddWithValue("@tipo_entrada", tipoEntrada);
            cmd.Parameters.AddWithValue("@unidades_por_entrada", unidadesPorEntrada);
            cmd.Parameters.AddWithValue("@precio_compra", precioCompra);
            cmd.Parameters.AddWithValue("@id", productoId);
            await cmd.ExecuteNonQueryAsync();
        }

        var presentacionesGuardadas = new List<object>();

        foreach (var pres in req.Presentaciones ?? new List<AdminProductPresentationRequest>())
        {
            string presNombre = string.IsNullOrWhiteSpace(pres.Nombre) ? unidadBase : pres.Nombre.Trim();
            int cantidadBase = pres.CantidadBase <= 0 ? 1 : pres.CantidadBase;
            decimal precioVenta = Math.Max(0, pres.PrecioVenta);
            string presEstado = string.Equals(pres.Estado, "INACTIVO", StringComparison.OrdinalIgnoreCase) ? "INACTIVO" : "ACTIVO";
            long presId = 0;

            if (pres.PresentacionId.GetValueOrDefault() > 0)
            {
                await using var buscarPresId = new MySqlCommand("SELECT id FROM presentaciones WHERE id = @id AND producto_id = @producto_id LIMIT 1;", con, tx);
                buscarPresId.Parameters.AddWithValue("@id", pres.PresentacionId!.Value);
                buscarPresId.Parameters.AddWithValue("@producto_id", productoId);
                object? foundPresId = await buscarPresId.ExecuteScalarAsync();
                if (foundPresId != null) presId = Convert.ToInt64(foundPresId);
            }

            if (presId <= 0)
            {
                await using var buscarPres = new MySqlCommand("SELECT id FROM presentaciones WHERE producto_id = @producto_id AND LOWER(TRIM(nombre)) = LOWER(TRIM(@nombre)) LIMIT 1;", con, tx);
                buscarPres.Parameters.AddWithValue("@producto_id", productoId);
                buscarPres.Parameters.AddWithValue("@nombre", presNombre);
                object? foundPres = await buscarPres.ExecuteScalarAsync();
                if (foundPres != null) presId = Convert.ToInt64(foundPres);
            }

            if (presId <= 0)
            {
                await using var cmd = new MySqlCommand("""
                    INSERT INTO presentaciones (producto_id, nombre, cantidad_base, precio_venta, estado)
                    VALUES (@producto_id, @nombre, @cantidad_base, @precio_venta, @estado);
                """, con, tx);
                cmd.Parameters.AddWithValue("@producto_id", productoId);
                cmd.Parameters.AddWithValue("@nombre", presNombre);
                cmd.Parameters.AddWithValue("@cantidad_base", cantidadBase);
                cmd.Parameters.AddWithValue("@precio_venta", precioVenta);
                cmd.Parameters.AddWithValue("@estado", presEstado);
                await cmd.ExecuteNonQueryAsync();
                presId = cmd.LastInsertedId;
            }
            else
            {
                await using var cmd = new MySqlCommand("""
                    UPDATE presentaciones SET nombre = @nombre, cantidad_base = @cantidad_base, precio_venta = @precio_venta, estado = @estado WHERE id = @id;
                """, con, tx);
                cmd.Parameters.AddWithValue("@nombre", presNombre);
                cmd.Parameters.AddWithValue("@cantidad_base", cantidadBase);
                cmd.Parameters.AddWithValue("@precio_venta", precioVenta);
                cmd.Parameters.AddWithValue("@estado", presEstado);
                cmd.Parameters.AddWithValue("@id", presId);
                await cmd.ExecuteNonQueryAsync();
            }

            presentacionesGuardadas.Add(new { id = presId, nombre = presNombre });
        }

        if (sucursalId == 2 && !IsPremiumSingleSectorInstance())
            await MirrorPremiumCatalogAsync(con, tx, productoId, sector, nombreAnterior);

        await tx.CommitAsync();
        await TrySyncSheets(db, sheets);
        return Results.Ok(new { ok = true, id = productoId, sucursalId, sector, nombre, categoria, presentaciones = presentacionesGuardadas, message = "Producto guardado y sincronizado." });
    }
    catch (Exception ex)
    {
        await tx.RollbackAsync();
        return Results.Problem("No se pudo guardar el producto: " + ex.Message);
    }
});

// V78: ajuste manual del estado de una botella abierta. Sirve cuando el rendimiento real
// difiere del estimado (derrame, vaso más cargado, etc.) sin tocar el stock de botellas cerradas.
app.MapPost("/api/admin/vasos/estado", async (Db db, string clave, AdminGlassStateRequest req) =>
{
    const string cleanKey = "ENTREGAR_LIMPIO_2026";
    if (clave != cleanKey) return Results.Unauthorized();
    if (req.ProductoId <= 0) return Results.BadRequest(new { ok = false, message = "Producto/botella requerido." });

    int sucursalId = ResolveInstanceSucursalId(req.SucursalId);
    string sector = NormalizarSectorProducto(sucursalId, req.Sector);
    int rendimiento = Math.Clamp(req.Rendimiento <= 0 ? 10 : req.Rendimiento, 1, 100);
    int restantes = Math.Clamp(req.ServiciosRestantes, 0, Math.Max(0, rendimiento - 1));

    await using var con = await db.OpenAsync();
    await EnsureCoreSchemaAsync(con);
    await using var tx = await con.BeginTransactionAsync();
    try
    {
        int exists;
        await using (var check = new MySqlCommand("SELECT COUNT(*) FROM productos WHERE id=@id AND sucursal_id=@s AND sector=@sector AND estado='ACTIVO';", con, tx))
        {
            check.Parameters.AddWithValue("@id", req.ProductoId);
            check.Parameters.AddWithValue("@s", sucursalId);
            check.Parameters.AddWithValue("@sector", sector);
            exists = Convert.ToInt32(await check.ExecuteScalarAsync() ?? 0);
        }
        if (exists <= 0)
        {
            await tx.RollbackAsync();
            return Results.NotFound(new { ok = false, message = "La botella no existe en esa sucursal/sector." });
        }

        await using (var updProduct = new MySqlCommand("UPDATE productos SET rendimiento_vaso=@r WHERE id=@id AND sucursal_id=@s AND sector=@sector;", con, tx))
        {
            updProduct.Parameters.AddWithValue("@r", rendimiento);
            updProduct.Parameters.AddWithValue("@id", req.ProductoId);
            updProduct.Parameters.AddWithValue("@s", sucursalId);
            updProduct.Parameters.AddWithValue("@sector", sector);
            await updProduct.ExecuteNonQueryAsync();
        }

        await using (var state = new MySqlCommand("""
            INSERT INTO vaso_control (sucursal_id, sector, producto_id, rendimiento, servicios_restantes)
            VALUES (@s,@sector,@p,@r,@rest)
            ON DUPLICATE KEY UPDATE rendimiento=VALUES(rendimiento), servicios_restantes=VALUES(servicios_restantes);
        """, con, tx))
        {
            state.Parameters.AddWithValue("@s", sucursalId);
            state.Parameters.AddWithValue("@sector", sector);
            state.Parameters.AddWithValue("@p", req.ProductoId);
            state.Parameters.AddWithValue("@r", rendimiento);
            state.Parameters.AddWithValue("@rest", restantes);
            await state.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
        return Results.Ok(new { ok = true, sucursalId, sector, productoId = req.ProductoId, rendimiento, serviciosRestantes = restantes });
    }
    catch (Exception ex)
    {
        await tx.RollbackAsync();
        return Results.Problem("No se pudo ajustar la botella abierta: " + ex.Message);
    }
});

app.MapPost("/api/admin/productos/ajustar-stock", async (Db db, string clave, AdminStockAdjustmentRequest req) =>
{
    const string cleanKey = "ENTREGAR_LIMPIO_2026";
    if (clave != cleanKey) return Results.Unauthorized();
    if (!InstanceRequestMatches(req.SucursalId, req.Sector))
        return Results.BadRequest(new { ok = false, message = InstanceMismatchMessage(req.SucursalId, req.Sector) });
    if (string.IsNullOrWhiteSpace(req.OperationKey)) return Results.BadRequest(new { ok = false, message = "operationKey requerido." });
    if (req.Delta == 0) return Results.BadRequest(new { ok = false, message = "El ajuste no puede ser cero." });

    int sucursalId = ResolveInstanceSucursalId(req.SucursalId);
    string sector = NormalizarSectorProducto(sucursalId, req.Sector);
    string nombre = (req.Nombre ?? "").Trim();

    await using var con = await db.OpenAsync();
    await EnsurePremiumSectorStockAsync(con);
    await using var tx = await con.BeginTransactionAsync();
    try
    {
        await using (var create = new MySqlCommand("""
            CREATE TABLE IF NOT EXISTS movimientos_inventario_admin (
                id BIGINT AUTO_INCREMENT PRIMARY KEY,
                operation_key VARCHAR(100) NOT NULL,
                sucursal_id INT NOT NULL,
                sector VARCHAR(20) NOT NULL DEFAULT 'GENERAL',
                producto_id BIGINT NOT NULL,
                delta DECIMAL(14,4) NOT NULL,
                motivo VARCHAR(200) NULL,
                fecha DATETIME NOT NULL,
                UNIQUE KEY uk_mov_inv_admin_operation (operation_key)
            );
        """, con, tx)) await create.ExecuteNonQueryAsync();

        long productoId = 0;
        if (req.ProductoId.GetValueOrDefault() > 0)
        {
            await using var q = new MySqlCommand("SELECT id FROM productos WHERE id=@id AND sucursal_id=@s AND sector=@sector AND estado='ACTIVO' LIMIT 1;", con, tx);
            q.Parameters.AddWithValue("@id", req.ProductoId!.Value);
            q.Parameters.AddWithValue("@s", sucursalId);
            q.Parameters.AddWithValue("@sector", sector);
            object? v = await q.ExecuteScalarAsync();
            if (v != null) productoId = Convert.ToInt64(v);
        }
        if (productoId <= 0 && !string.IsNullOrWhiteSpace(nombre))
        {
            await using var q = new MySqlCommand("SELECT id FROM productos WHERE sucursal_id=@s AND sector=@sector AND estado='ACTIVO' AND LOWER(TRIM(nombre))=LOWER(TRIM(@n)) ORDER BY id LIMIT 1;", con, tx);
            q.Parameters.AddWithValue("@s", sucursalId);
            q.Parameters.AddWithValue("@sector", sector);
            q.Parameters.AddWithValue("@n", nombre);
            object? v = await q.ExecuteScalarAsync();
            if (v != null) productoId = Convert.ToInt64(v);
        }
        if (productoId <= 0) return Results.NotFound(new { ok = false, message = "Producto no encontrado en Railway." });

        int inserted;
        await using (var ins = new MySqlCommand("""
            INSERT IGNORE INTO movimientos_inventario_admin
            (operation_key, sucursal_id, sector, producto_id, delta, motivo, fecha)
            VALUES (@key,@s,@sector,@p,@delta,@motivo,NOW());
        """, con, tx))
        {
            ins.Parameters.AddWithValue("@key", req.OperationKey.Trim());
            ins.Parameters.AddWithValue("@s", sucursalId);
            ins.Parameters.AddWithValue("@sector", sector);
            ins.Parameters.AddWithValue("@p", productoId);
            ins.Parameters.AddWithValue("@delta", req.Delta);
            ins.Parameters.AddWithValue("@motivo", req.Motivo ?? "AJUSTE");
            inserted = await ins.ExecuteNonQueryAsync();
        }

        if (inserted > 0)
        {
            await using var up = new MySqlCommand("""
                UPDATE productos
                SET stock_actual = stock_actual + @delta
                WHERE id=@p AND (@delta >= 0 OR stock_actual >= -@delta);
            """, con, tx);
            up.Parameters.AddWithValue("@delta", req.Delta);
            up.Parameters.AddWithValue("@p", productoId);
            int changed = await up.ExecuteNonQueryAsync();
            if (changed == 0)
                throw new InvalidOperationException("No hay inventario suficiente para aplicar el ajuste.");
        }

        decimal stock;
        await using (var q = new MySqlCommand("SELECT stock_actual FROM productos WHERE id=@p LIMIT 1;", con, tx))
        {
            q.Parameters.AddWithValue("@p", productoId);
            stock = Convert.ToDecimal(await q.ExecuteScalarAsync() ?? 0m);
        }
        await tx.CommitAsync();
        return Results.Ok(new { ok = true, duplicado = inserted == 0, productoId, stockActual = stock });
    }
    catch (Exception ex)
    {
        await tx.RollbackAsync();
        return Results.BadRequest(new { ok = false, message = ex.Message });
    }
});

app.MapGet("/api/admin/productos/detalle", async (Db db, string clave, int sucursalId, string? sector) =>
{
    const string cleanKey = "ENTREGAR_LIMPIO_2026";
    if (clave != cleanKey) return Results.Unauthorized();

    await using var con = await db.OpenAsync();
    await EnsureAppMeseraTables(con);
    await EnsureSectorLayoutAsync(con);
    await EnsurePremiumSharedCatalogAsync(con);
    await EnsurePremiumSectorStockAsync(con);

    int sid = ResolveInstanceSucursalId(sucursalId);

    string sectorFiltro = IsInstanceLocked() ? GetInstanceSector() :
        (sid == 2 && !string.IsNullOrWhiteSpace(sector) ? NormalizarSectorProducto(sid, sector) : (sid == 1 ? "GENERAL" : ""));
    const string sql = """
        SELECT p.id AS producto_id, p.sucursal_id, p.sector, p.nombre, p.categoria, p.unidad_base,
               p.stock_actual, p.stock_minimo, COALESCE(p.sin_limite_stock,0) AS sin_limite_stock,
               COALESCE(p.tipo_entrada,'PAQUETE') AS tipo_entrada,
               COALESCE(p.unidades_por_entrada,1) AS unidades_por_entrada,
               COALESCE(p.precio_compra,0) AS precio_compra,
               COALESCE(p.genera_comision,0) AS genera_comision,
               COALESCE(p.tipo_comision,'NINGUNA') AS tipo_comision,
               COALESCE(p.valor_comision,0) AS valor_comision,
               COALESCE(p.rendimiento_vaso,10) AS rendimiento_vaso,
               COALESCE(vc.servicios_restantes,0) AS vasos_restantes,
               p.estado,
               pr.id AS presentacion_id, pr.nombre AS presentacion,
               pr.cantidad_base, pr.precio_venta, pr.estado AS presentacion_estado
        FROM productos p
        LEFT JOIN presentaciones pr ON pr.producto_id = p.id
        LEFT JOIN vaso_control vc ON vc.sucursal_id=p.sucursal_id AND vc.sector=p.sector AND vc.producto_id=p.id
        WHERE p.sucursal_id = @sucursal_id
          AND (@sector = '' OR p.sector = @sector)
          AND p.estado='ACTIVO'
        ORDER BY p.sector, p.categoria, p.nombre, pr.cantidad_base;
    """;

    return Results.Ok(await db.QueryAsync(con, sql, new Dictionary<string, object?>
    {
        ["@sucursal_id"] = sid,
        ["@sector"] = sectorFiltro
    }));
});

app.MapPost("/api/app-mesera/login", async (Db db, LoginRequest req) =>
{
    await using var con = await db.OpenAsync();
    await EnsureUserManagementTables(con);
    await EnsureAppMeseraTables(con);

    string usuario = (req.Usuario ?? "").Trim().ToLowerInvariant();
    string claveIngresada = req.Clave ?? "";

    int id = 0;
    int sucursalId = 1;
    string usuarioDb = "";
    string rol = "";
    string sucursal = "EL BRUJO";
    string nombre = "";
    string turno = "MAÑANA";
    string sector = "GENERAL";
    string claveGuardada = "";

    const string sql = """
        SELECT u.id, u.usuario, u.clave, u.rol, u.estado, u.sucursal_id,
               COALESCE(u.nombre_completo, u.usuario) AS nombre_completo,
               COALESCE(u.caja_nombre, '') AS caja_nombre,
               COALESCE(u.turno, 'MAÑANA') AS turno,
               COALESCE(u.sector, CASE WHEN u.sucursal_id=2 THEN 'ARRIBA' ELSE 'GENERAL' END) AS sector,
               CASE WHEN s.id = 2 THEN 'EL BRUJO PREMIU' ELSE 'EL BRUJO' END AS sucursal
        FROM usuarios u
        LEFT JOIN sucursales s ON s.id = u.sucursal_id
        WHERE u.usuario = @usuario AND u.estado = 'ACTIVO'
        LIMIT 1;
    """;

    await using (var cmd = new MySqlCommand(sql, con))
    {
        cmd.Parameters.AddWithValue("@usuario", usuario);
        await using var rd = await cmd.ExecuteReaderAsync();
        if (!await rd.ReadAsync())
            return Results.Unauthorized();

        id = rd.GetInt32("id");
        usuarioDb = rd.GetString("usuario");
        claveGuardada = rd.GetString("clave");
        rol = rd.GetString("rol");
        sucursalId = rd.IsDBNull(rd.GetOrdinal("sucursal_id")) ? 1 : rd.GetInt32("sucursal_id");
        sucursal = rd.IsDBNull(rd.GetOrdinal("sucursal")) ? "EL BRUJO" : rd.GetString("sucursal");
        nombre = rd.IsDBNull(rd.GetOrdinal("nombre_completo")) ? usuarioDb : rd.GetString("nombre_completo");
        turno = rd.IsDBNull(rd.GetOrdinal("turno")) ? "MAÑANA" : rd.GetString("turno");
        sector = rd.IsDBNull(rd.GetOrdinal("sector")) ? NormalizarSector(sucursalId, null) : NormalizarSector(sucursalId, rd.GetString("sector"));
    }

    if (!PasswordHasher.Verify(claveIngresada, claveGuardada))
        return Results.Unauthorized();

    if (!rol.Contains("MESERA", StringComparison.OrdinalIgnoreCase) &&
        !rol.Contains("MESERO", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { ok = false, message = "Este usuario no tiene rol de mesera." });
    }

    if (!PasswordHasher.IsHashed(claveGuardada))
        await UpdateUserPasswordHash(con, id, claveIngresada);

    return Results.Ok(new
    {
        ok = true,
        id,
        usuario = usuarioDb,
        nombre,
        rol,
        turno,
        sector,
        sucursal_id = sucursalId,
        sucursal
    });
});

app.MapGet("/api/app-mesera/mesas", async (Db db, int sucursalId, string? usuario, string? sector) =>
{
    await using var con = await db.OpenAsync();
    await EnsureAppMeseraTables(con);
    await EnsureMesasEnVivoTables(con);

    int sid = ResolveInstanceSucursalId(sucursalId);
    string sectorEfectivo = NormalizarSector(sid, sector);
    if (!string.IsNullOrWhiteSpace(usuario))
    {
        await using var sectorCmd = new MySqlCommand("SELECT sucursal_id, sector FROM usuarios WHERE usuario=@usuario AND estado='ACTIVO' LIMIT 1;", con);
        sectorCmd.Parameters.AddWithValue("@usuario", usuario.Trim().ToLowerInvariant());
        await using var rd = await sectorCmd.ExecuteReaderAsync();
        if (!await rd.ReadAsync()) return Results.Unauthorized();
        int usuarioSucursal = rd.GetInt32(0);
        if (usuarioSucursal != sid) return Results.BadRequest(new { ok=false, message="El usuario no pertenece a esta sucursal." });
        sectorEfectivo = NormalizarSector(sid, rd.IsDBNull(1) ? null : rd.GetString(1));
    }

    var layout = await EnsureTableLayoutSettingsAsync(con);
    int limite = sid == 1 ? layout.brujo : (sectorEfectivo == "ABAJO" ? layout.premiumAbajo : layout.premiumArriba);
    const string sql = """
        WITH RECURSIVE nums AS (
            SELECT 1 AS n
            UNION ALL
            SELECT n + 1 FROM nums WHERE n < @limite
        )
        SELECT nums.n AS mesa_id,
               CONCAT('Mesa ', nums.n) AS mesa,
               COALESCE(me.estado, 'LIBRE') AS estado,
               COALESCE(me.total_consumo, 0) AS total_consumo,
               me.fin_programado,
               me.cajero,
               @sector AS sector
        FROM nums
        LEFT JOIN mesa_estados me
          ON me.sucursal_id=@sucursalId
         AND me.sector=@sector
         AND me.mesa_id=nums.n
        ORDER BY nums.n;
    """;

    return Results.Ok(await db.QueryAsync(con, sql, new Dictionary<string, object?>
    {
        ["@sucursalId"] = sid,
        ["@sector"] = sectorEfectivo,
        ["@limite"] = limite
    }));
});

app.MapGet("/api/app-mesera/productos", async (Db db, int sucursalId, string? usuario, string? sector) =>
{
    await using var con = await db.OpenAsync();
    await EnsureAppMeseraTables(con);
    await EnsureSectorLayoutAsync(con);
    await EnsurePremiumSharedCatalogAsync(con);
    await EnsurePremiumSectorStockAsync(con);

    int sid = ResolveInstanceSucursalId(sucursalId);
    string sectorEfectivo = NormalizarSector(sid, sector);
    if (!string.IsNullOrWhiteSpace(usuario))
    {
        await using var sectorCmd = new MySqlCommand("SELECT sucursal_id, sector FROM usuarios WHERE usuario=@usuario AND estado='ACTIVO' LIMIT 1;", con);
        sectorCmd.Parameters.AddWithValue("@usuario", usuario.Trim().ToLowerInvariant());
        await using var rd = await sectorCmd.ExecuteReaderAsync();
        if (!await rd.ReadAsync()) return Results.Unauthorized();
        int usuarioSucursal = rd.GetInt32(0);
        if (usuarioSucursal != sid) return Results.BadRequest(new { ok=false, message="El usuario no pertenece a esta sucursal." });
        sectorEfectivo = NormalizarSector(sid, rd.IsDBNull(1) ? null : rd.GetString(1));
    }

    const string sql = """
        SELECT p.id AS producto_id,
               p.sector,
               p.nombre AS producto,
               p.categoria,
               p.stock_actual,
               COALESCE(p.sin_limite_stock, 0) AS sin_limite_stock,
               pr.id AS presentacion_id,
               COALESCE(pr.nombre, 'UNIDAD') AS presentacion,
               COALESCE(pr.precio_venta, 0) AS precio,
               COALESCE(p.genera_comision, 0) AS genera_comision,
               COALESCE(p.tipo_comision, 'NINGUNA') AS tipo_comision,
               COALESCE(p.valor_comision, 0) AS valor_comision
        FROM productos p
        LEFT JOIN presentaciones pr ON pr.producto_id = p.id AND pr.estado = 'ACTIVO'
        WHERE p.sucursal_id = @sucursalId
          AND p.sector = @sector
          AND p.estado = 'ACTIVO'
        ORDER BY
            CASE
                WHEN p.categoria = 'Agua' THEN 1
                WHEN p.categoria = 'Energizantes' THEN 2
                WHEN p.categoria = 'Sodas' THEN 3
                WHEN p.categoria IN ('Cocas', 'Coca machucada') THEN 4
                WHEN p.categoria = 'Cervezas' THEN 5
                WHEN p.categoria = 'Tragos / Botellas' THEN 6
                WHEN p.categoria = 'Servidos en vaso' THEN 7
                WHEN p.categoria = 'Cigarros' THEN 8
                WHEN p.categoria = 'Snacks y piqueos' THEN 9
                WHEN p.categoria = 'Dulces y golosinas' THEN 10
                WHEN p.categoria = 'Combos / Promos' THEN 11
                WHEN p.categoria = 'Accesorios' THEN 12
                WHEN p.categoria = 'Ceniceros' THEN 13
                ELSE 99
            END,
            p.nombre, pr.nombre;
    """;

    var rows = await db.QueryAsync(con, sql, new Dictionary<string, object?>
    {
        ["@sucursalId"] = sid,
        ["@sector"] = sectorEfectivo
    });

    // V66: combos/promociones muestran disponibilidad calculada desde los productos físicos reales.
    List<CompositeInventoryDb.StockProduct> stockCatalog = await CompositeInventoryDb.LoadSnapshotAsync(con, sid, sectorEfectivo);
    foreach (var row in rows)
    {
        string nombre = Convert.ToString(row.TryGetValue("producto", out var n) ? n : "") ?? "";
        string categoria = Convert.ToString(row.TryGetValue("categoria", out var cat) ? cat : "") ?? "";
        if (CompositeInventoryDb.IsComposite(nombre, categoria))
        {
            row["stock_actual"] = CompositeInventoryDb.AvailableSales(stockCatalog, nombre);
            row["sin_limite_stock"] = 0;
            row["detalle_inventario"] = CompositeInventoryDb.DescribeAvailable(stockCatalog, nombre);
        }
        else
        {
            row["detalle_inventario"] = nombre;
        }
    }

    return Results.Ok(rows);
});

app.MapGet("/api/app-mesera/test", async (Db db, int sucursalId) =>
{
    await using var con = await db.OpenAsync();
    await EnsureAppMeseraTables(con);
    await EnsureMesasEnVivoTables(con);

    long productos = Convert.ToInt64(await new MySqlCommand("SELECT COUNT(*) FROM productos WHERE sucursal_id = " + sucursalId + " AND estado = 'ACTIVO';", con).ExecuteScalarAsync() ?? 0);
    long presentaciones = Convert.ToInt64(await new MySqlCommand("SELECT COUNT(*) FROM presentaciones pr INNER JOIN productos p ON p.id = pr.producto_id WHERE p.sucursal_id = " + sucursalId + " AND pr.estado = 'ACTIVO';", con).ExecuteScalarAsync() ?? 0);
    long mesasVivas = Convert.ToInt64(await new MySqlCommand("SELECT COUNT(*) FROM mesa_estados WHERE sucursal_id = " + sucursalId + ";", con).ExecuteScalarAsync() ?? 0);
    long pedidosPendientes = Convert.ToInt64(await new MySqlCommand("SELECT COUNT(*) FROM pedidos_movil WHERE sucursal_id = " + sucursalId + " AND estado = 'PENDIENTE';", con).ExecuteScalarAsync() ?? 0);

    return Results.Ok(new
    {
        ok = true,
        version = "V40_DIRECTO_ESTABLE",
        sucursalId,
        productos,
        presentaciones,
        mesasVivas,
        pedidosPendientes
    });
});

app.MapPost("/api/app-mesera/pedidos", async (Db db, AppPedidoMovilRequest req) =>
{
    await using var con = await db.OpenAsync();
    await EnsureAppMeseraTables(con);
    await EnsureMesasEnVivoTables(con);

    bool esCortesia =
        (req.Observacion ?? "").StartsWith("CORTESIA_MESA", StringComparison.OrdinalIgnoreCase) ||
        (req.MesaId <= 0 && (req.Mesa ?? "").Contains("CORTES", StringComparison.OrdinalIgnoreCase)); // compatibilidad con pedidos antiguos

    if (req.Cantidad <= 0)
        return Results.BadRequest(new { ok = false, message = "Cantidad inválida." });

    // V62: el sector se obtiene del usuario autenticado en Railway, no del celular.
    // Así una mesera de ARRIBA jamás puede descontar productos de ABAJO (y viceversa).
    string sectorPedido;
    await using (var userSectorCmd = new MySqlCommand("SELECT sucursal_id, rol, sector FROM usuarios WHERE usuario=@usuario AND estado='ACTIVO' LIMIT 1;", con))
    {
        userSectorCmd.Parameters.AddWithValue("@usuario", (req.MeseraUsuario ?? "").Trim().ToLowerInvariant());
        await using var userRd = await userSectorCmd.ExecuteReaderAsync();
        if (!await userRd.ReadAsync()) return Results.Unauthorized();
        int userBranch = userRd.GetInt32(0);
        string userRole = userRd.IsDBNull(1) ? "" : userRd.GetString(1);
        if (userBranch != req.SucursalId || (!userRole.Contains("MESERA", StringComparison.OrdinalIgnoreCase) && !userRole.Contains("MESERO", StringComparison.OrdinalIgnoreCase)))
            return Results.BadRequest(new { ok=false, message="Usuario de mesera inválido para esta sucursal." });
        sectorPedido = NormalizarSector(req.SucursalId, userRd.IsDBNull(2) ? null : userRd.GetString(2));
    }

    // V40: TODOS los pedidos de la App Mesera deben pertenecer a una mesa activa.
    if (req.MesaId <= 0)
        return Results.BadRequest(new { ok = false, message = "El pedido debe cargarse a una mesa activa." });

    const string mesaActivaSql = """
        SELECT estado
        FROM mesa_estados
        WHERE sucursal_id = @sucursal_id
          AND sector = @sector
          AND mesa_id = @mesa_id
        LIMIT 1;
    """;

    await using (var mesaCmd = new MySqlCommand(mesaActivaSql, con))
    {
        mesaCmd.Parameters.AddWithValue("@sucursal_id", req.SucursalId);
        mesaCmd.Parameters.AddWithValue("@sector", sectorPedido);
        mesaCmd.Parameters.AddWithValue("@mesa_id", req.MesaId);
        string estadoMesa = Convert.ToString(await mesaCmd.ExecuteScalarAsync()) ?? "";

        if (string.IsNullOrWhiteSpace(estadoMesa) ||
            estadoMesa.Equals("LIBRE", StringComparison.OrdinalIgnoreCase) ||
            estadoMesa.Equals("RESERVADA", StringComparison.OrdinalIgnoreCase) ||
            estadoMesa.Equals("INACTIVA", StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new { ok = false, message = "La mesa seleccionada ya no está en juego. Actualiza las mesas e inténtalo otra vez." });
        }
    }

    // El servidor impone precio, presentación y disponibilidad reales del catálogo.
    const string validarSql = """
        SELECT p.id,
               p.nombre,
               p.categoria,
               p.stock_actual,
               COALESCE(p.sin_limite_stock, 0) AS sin_limite_stock,
               COALESCE(pr.cantidad_base, 1) AS cantidad_base,
               COALESCE(
                   pr.precio_venta,
                   (SELECT pr2.precio_venta
                      FROM presentaciones pr2
                     WHERE pr2.producto_id = p.id
                       AND pr2.estado = 'ACTIVO'
                     ORDER BY pr2.id
                     LIMIT 1),
                   0
               ) AS precio_catalogo
          FROM productos p
          LEFT JOIN presentaciones pr
                 ON pr.id = @presentacion_id
                AND pr.producto_id = p.id
                AND pr.estado = 'ACTIVO'
         WHERE p.id = @id
           AND p.sucursal_id = @sucursal_id
           AND p.sector = @sector
           AND p.estado = 'ACTIVO'
         LIMIT 1;
    """;

    decimal precioCatalogo = 0M;
    decimal stockActual = 0M;
    decimal cantidadBase = 1M;
    bool sinLimiteStock = false;
    string nombreCatalogo = "";
    string categoriaCatalogo = "";

    await using (var validar = new MySqlCommand(validarSql, con))
    {
        validar.Parameters.AddWithValue("@id", req.ProductoId);
        validar.Parameters.AddWithValue("@presentacion_id", req.PresentacionId);
        validar.Parameters.AddWithValue("@sucursal_id", req.SucursalId);
        validar.Parameters.AddWithValue("@sector", NormalizarSectorProducto(req.SucursalId, sectorPedido));

        await using var rd = await validar.ExecuteReaderAsync();
        if (!await rd.ReadAsync())
            return Results.BadRequest(new { ok = false, message = "Producto no encontrado o inactivo." });

        nombreCatalogo = rd.IsDBNull(rd.GetOrdinal("nombre")) ? (req.Producto ?? "") : rd.GetString(rd.GetOrdinal("nombre"));
        categoriaCatalogo = rd.IsDBNull(rd.GetOrdinal("categoria")) ? "" : rd.GetString(rd.GetOrdinal("categoria"));
        precioCatalogo = rd.IsDBNull(rd.GetOrdinal("precio_catalogo")) ? 0M : rd.GetDecimal(rd.GetOrdinal("precio_catalogo"));
        stockActual = rd.IsDBNull(rd.GetOrdinal("stock_actual")) ? 0M : rd.GetDecimal(rd.GetOrdinal("stock_actual"));
        cantidadBase = rd.IsDBNull(rd.GetOrdinal("cantidad_base")) ? 1M : rd.GetDecimal(rd.GetOrdinal("cantidad_base"));
        sinLimiteStock = !rd.IsDBNull(rd.GetOrdinal("sin_limite_stock")) && rd.GetInt32(rd.GetOrdinal("sin_limite_stock")) == 1;
    }

    if (precioCatalogo <= 0)
        return Results.BadRequest(new { ok = false, message = "El producto no tiene precio de venta válido." });

    bool esCompuesto = CompositeInventoryDb.IsComposite(nombreCatalogo);
    decimal unidadesSolicitadas = Math.Max(1M, cantidadBase) * req.Cantidad;
    if (esCompuesto)
    {
        List<CompositeInventoryDb.StockProduct> snapshot = await CompositeInventoryDb.LoadSnapshotAsync(con, req.SucursalId, sectorPedido);
        decimal disponibles = await CompositeInventoryDb.AvailableSalesAsync(con, req.SucursalId, sectorPedido, nombreCatalogo);
        if (disponibles < req.Cantidad)
        {
            return Results.Conflict(new
            {
                ok = false,
                code = "STOCK_COMPONENTES_INSUFICIENTE",
                message = "No hay inventario físico suficiente para armar/servir " + nombreCatalogo + ".",
                disponible = disponibles,
                solicitado = req.Cantidad,
                detalle = CompositeInventoryDb.DescribeAvailable(snapshot, nombreCatalogo)
            });
        }
    }
    else if (!sinLimiteStock && stockActual < unidadesSolicitadas)
    {
        return Results.Conflict(new
        {
            ok = false,
            code = "STOCK_INSUFICIENTE",
            message = "Stock insuficiente para completar el pedido.",
            disponible = stockActual,
            solicitado = unidadesSolicitadas
        });
    }

    decimal precioAplicado = precioCatalogo;
    decimal subtotal = req.Cantidad * precioAplicado;
    // V40: solo la invitación/cortesía a la mesera genera comisión fija de Bs. 5 por unidad.
    bool generaComisionAplicada = esCortesia;
    string tipoComisionAplicada = esCortesia ? "MONTO" : "NINGUNA";
    decimal valorComisionAplicada = esCortesia ? 5M : 0M;
    string syncKey = string.IsNullOrWhiteSpace(req.SyncKey) ? Guid.NewGuid().ToString("N") : req.SyncKey;

    await using var tx = await con.BeginTransactionAsync();

    try
    {
        // V78: primero se reclama la sync_key del pedido DENTRO de la transacción.
        // El INSERT IGNORE UNIQUE ocurre ANTES de tocar stock. Si dos reintentos iguales
        // llegan al mismo tiempo, solo uno inserta la cabecera y solo ese puede reservar inventario.
        const string claimPedidoSql = """
            INSERT IGNORE INTO pedidos_movil
                (sucursal_id, sector, mesa_id, mesa, mesera_usuario, mesera_nombre, fecha, estado, total, observacion, sync_key)
            VALUES
                (@sucursal_id, @sector, @mesa_id, @mesa, @mesera_usuario, @mesera_nombre, NOW(), 'PENDIENTE', @total, @observacion, @sync_key);
        """;
        int pedidoInsertado;
        await using (var claim = new MySqlCommand(claimPedidoSql, con, tx))
        {
            claim.Parameters.AddWithValue("@sucursal_id", req.SucursalId);
            claim.Parameters.AddWithValue("@sector", sectorPedido);
            claim.Parameters.AddWithValue("@mesa_id", req.MesaId);
            claim.Parameters.AddWithValue("@mesa", req.Mesa);
            claim.Parameters.AddWithValue("@mesera_usuario", req.MeseraUsuario);
            claim.Parameters.AddWithValue("@mesera_nombre", req.MeseraNombre);
            claim.Parameters.AddWithValue("@total", subtotal);
            claim.Parameters.AddWithValue("@observacion", req.Observacion ?? "");
            claim.Parameters.AddWithValue("@sync_key", syncKey);
            pedidoInsertado = await claim.ExecuteNonQueryAsync();
        }

        long pedidoId;
        await using (var getPedido = new MySqlCommand("SELECT id FROM pedidos_movil WHERE sync_key=@sync_key LIMIT 1;", con, tx))
        {
            getPedido.Parameters.AddWithValue("@sync_key", syncKey);
            pedidoId = Convert.ToInt64(await getPedido.ExecuteScalarAsync() ?? 0L);
        }
        if (pedidoId <= 0)
            throw new InvalidOperationException("No se pudo asegurar la identidad única del pedido móvil.");

        if (pedidoInsertado == 0)
        {
            await tx.CommitAsync();
            return Results.Ok(new
            {
                ok = true,
                pedido_id = pedidoId,
                estado = "PENDIENTE",
                sector = sectorPedido,
                total = subtotal,
                comision_calculada = generaComisionAplicada ? 5M * req.Cantidad : 0M,
                inventario_aplicado = "",
                idempotent = true,
                message = "Pedido ya recibido anteriormente. No se volvió a descontar inventario."
            });
        }

        // Reserva/descuenta el inventario UNA sola vez al recibir un pedido móvil NUEVO.
        string inventarioAplicado = "";
        if (esCompuesto)
        {
            var reserva = await CompositeInventoryDb.ReserveAsync(con, tx, req.SucursalId, sectorPedido, nombreCatalogo, req.Cantidad);
            if (!reserva.Ok)
            {
                await tx.RollbackAsync();
                return Results.Conflict(new { ok = false, code = "STOCK_COMPONENTES_INSUFICIENTE", message = reserva.Error });
            }
            inventarioAplicado = reserva.Description;
        }
        else if (!sinLimiteStock)
        {
            await using var reservarStock = new MySqlCommand("""
                UPDATE productos
                SET stock_actual = stock_actual - @unidades
                WHERE id = @producto_id
                  AND sucursal_id = @sucursal_id
                  AND sector = @sector
                  AND estado = 'ACTIVO'
                  AND COALESCE(sin_limite_stock, 0) = 0
                  AND stock_actual >= @unidades;
            """, con, tx);
            reservarStock.Parameters.AddWithValue("@unidades", unidadesSolicitadas);
            reservarStock.Parameters.AddWithValue("@producto_id", req.ProductoId);
            reservarStock.Parameters.AddWithValue("@sucursal_id", req.SucursalId);
            reservarStock.Parameters.AddWithValue("@sector", NormalizarSectorProducto(req.SucursalId, sectorPedido));

            int filas = await reservarStock.ExecuteNonQueryAsync();
            if (filas != 1)
            {
                await tx.RollbackAsync();
                return Results.Conflict(new
                {
                    ok = false,
                    code = "STOCK_INSUFICIENTE",
                    message = "Stock insuficiente. Otro pedido pudo haber usado las últimas unidades. Actualiza el catálogo e inténtalo otra vez."
                });
            }
        }

        await using (var del = new MySqlCommand("DELETE FROM detalle_pedidos_movil WHERE pedido_id = @pedido_id;", con, tx))
        {
            del.Parameters.AddWithValue("@pedido_id", pedidoId);
            await del.ExecuteNonQueryAsync();
        }

        const string detSql = """
            INSERT INTO detalle_pedidos_movil
                (pedido_id, sector, producto_id, presentacion_id, producto, presentacion, cantidad, precio_unitario, subtotal,
                 genera_comision, tipo_comision, valor_comision, comision_calculada)
            VALUES
                (@pedido_id, @sector, @producto_id, @presentacion_id, @producto, @presentacion, @cantidad, @precio_unitario, @subtotal,
                 @genera_comision, @tipo_comision, @valor_comision, @comision_calculada);
        """;

        decimal comision = generaComisionAplicada
            ? (tipoComisionAplicada.Equals("PORCENTAJE", StringComparison.OrdinalIgnoreCase)
                ? subtotal * (valorComisionAplicada / 100M)
                : valorComisionAplicada * req.Cantidad)
            : 0M;

        await using var detCmd = new MySqlCommand(detSql, con, tx);
        detCmd.Parameters.AddWithValue("@pedido_id", pedidoId);
        detCmd.Parameters.AddWithValue("@sector", sectorPedido);
        detCmd.Parameters.AddWithValue("@producto_id", req.ProductoId);
        detCmd.Parameters.AddWithValue("@presentacion_id", req.PresentacionId);
        detCmd.Parameters.AddWithValue("@producto", req.Producto);
        detCmd.Parameters.AddWithValue("@presentacion", req.Presentacion);
        detCmd.Parameters.AddWithValue("@cantidad", req.Cantidad);
        detCmd.Parameters.AddWithValue("@precio_unitario", precioAplicado);
        detCmd.Parameters.AddWithValue("@subtotal", subtotal);
        detCmd.Parameters.AddWithValue("@genera_comision", generaComisionAplicada);
        detCmd.Parameters.AddWithValue("@tipo_comision", tipoComisionAplicada);
        detCmd.Parameters.AddWithValue("@valor_comision", valorComisionAplicada);
        detCmd.Parameters.AddWithValue("@comision_calculada", comision);
        await detCmd.ExecuteNonQueryAsync();

        await tx.CommitAsync();

        return Results.Ok(new
        {
            ok = true,
            pedido_id = pedidoId,
            estado = "PENDIENTE",
            sector = sectorPedido,
            total = subtotal,
            comision_calculada = comision,
            inventario_aplicado = inventarioAplicado,
            message = esCortesia ? "Cortesía recibida. Comisión: Bs. " + comision.ToString("0.00") + ". Se cargará automáticamente a la mesa activa." : "Pedido recibido. Se cargará automáticamente a la mesa activa."
        });
    }
    catch (Exception ex)
    {
        await tx.RollbackAsync();
        return Results.Problem("No se pudo registrar el pedido móvil: " + ex.Message);
    }
});


app.MapPost("/api/app-mesera/reportes-producto", async (Db db, ProductReportRequest req) =>
{
    await using var con = await db.OpenAsync();
    await EnsureAppMeseraTables(con);

    if (req.Cantidad <= 0)
        return Results.BadRequest(new { ok = false, message = "Cantidad inválida." });

    string motivo = string.IsNullOrWhiteSpace(req.Motivo) ? "DAÑADO/PERDIDO" : req.Motivo.Trim().ToUpperInvariant();
    string syncKey = string.IsNullOrWhiteSpace(req.SyncKey) ? Guid.NewGuid().ToString("N") : req.SyncKey;
    decimal costo = Math.Max(0, req.Cantidad * req.PrecioUnitario);
    int sucursalId = ResolveInstanceSucursalId(req.SucursalId);
    string sectorReporte = NormalizarSector(sucursalId, req.Sector);
    if (!string.IsNullOrWhiteSpace(req.Usuario))
    {
        await using var sectorCmd = new MySqlCommand("SELECT sucursal_id, sector FROM usuarios WHERE usuario=@usuario AND estado='ACTIVO' LIMIT 1;", con);
        sectorCmd.Parameters.AddWithValue("@usuario", req.Usuario.Trim().ToLowerInvariant());
        await using var rd = await sectorCmd.ExecuteReaderAsync();
        if (await rd.ReadAsync())
        {
            int sid = rd.GetInt32(0);
            if (sid != sucursalId) return Results.BadRequest(new { ok=false, message="El usuario no pertenece a esta sucursal." });
            sectorReporte = NormalizarSector(sucursalId, rd.IsDBNull(1) ? null : rd.GetString(1));
        }
    }

    await using var tx = await con.BeginTransactionAsync();
    try
    {
        // INSERT IGNORE hace que reintentar el mismo syncKey no vuelva a descontar inventario.
        const string sql = """
            INSERT IGNORE INTO reportes_productos_movil
                (sucursal_id, sector, turno, usuario, nombre, fecha, producto_id, presentacion_id,
                 producto, presentacion, cantidad, precio_unitario, costo_perdido, motivo, observacion, sync_key)
            VALUES
                (@sucursal_id, @sector, @turno, @usuario, @nombre, NOW(), @producto_id, @presentacion_id,
                 @producto, @presentacion, @cantidad, @precio_unitario, @costo_perdido, @motivo, @observacion, @sync_key);
        """;

        await using var cmd = new MySqlCommand(sql, con, tx);
        cmd.Parameters.AddWithValue("@sucursal_id", sucursalId);
        cmd.Parameters.AddWithValue("@sector", sectorReporte);
        cmd.Parameters.AddWithValue("@turno", string.IsNullOrWhiteSpace(req.Turno) ? "MAÑANA" : req.Turno.Trim().ToUpperInvariant());
        cmd.Parameters.AddWithValue("@usuario", req.Usuario ?? "");
        cmd.Parameters.AddWithValue("@nombre", req.Nombre ?? "");
        cmd.Parameters.AddWithValue("@producto_id", req.ProductoId);
        cmd.Parameters.AddWithValue("@presentacion_id", req.PresentacionId);
        cmd.Parameters.AddWithValue("@producto", req.Producto ?? "");
        cmd.Parameters.AddWithValue("@presentacion", req.Presentacion ?? "");
        cmd.Parameters.AddWithValue("@cantidad", req.Cantidad);
        cmd.Parameters.AddWithValue("@precio_unitario", Math.Max(0, req.PrecioUnitario));
        cmd.Parameters.AddWithValue("@costo_perdido", costo);
        cmd.Parameters.AddWithValue("@motivo", motivo);
        cmd.Parameters.AddWithValue("@observacion", req.Observacion ?? "");
        cmd.Parameters.AddWithValue("@sync_key", syncKey);
        int insertado = await cmd.ExecuteNonQueryAsync();

        // Para un producto físico roto/perdido, el inventario real baja también.
        // Los reportes de infraestructura usan producto_id=0 y no tocan inventario.
        if (insertado == 1 && req.ProductoId > 0)
        {
            decimal factor = 1M;
            if (req.PresentacionId > 0)
            {
                await using var factorCmd = new MySqlCommand("""
                    SELECT COALESCE(cantidad_base, 1)
                    FROM presentaciones
                    WHERE id=@presentacion_id AND producto_id=@producto_id
                    LIMIT 1;
                """, con, tx);
                factorCmd.Parameters.AddWithValue("@presentacion_id", req.PresentacionId);
                factorCmd.Parameters.AddWithValue("@producto_id", req.ProductoId);
                object? factorObj = await factorCmd.ExecuteScalarAsync();
                if (factorObj != null && factorObj != DBNull.Value)
                    factor = Math.Max(1M, Convert.ToDecimal(factorObj));
            }

            decimal unidades = Math.Max(0, req.Cantidad * factor);
            await using var stockCmd = new MySqlCommand("""
                UPDATE productos
                SET stock_actual = CASE
                    WHEN COALESCE(sin_limite_stock, 0) = 1
                    THEN stock_actual
                    ELSE GREATEST(stock_actual - @unidades, 0)
                END
                WHERE id=@producto_id AND sucursal_id=@sucursal_id AND sector=@sector;
            """, con, tx);
            stockCmd.Parameters.AddWithValue("@unidades", unidades);
            stockCmd.Parameters.AddWithValue("@producto_id", req.ProductoId);
            stockCmd.Parameters.AddWithValue("@sucursal_id", sucursalId);
            stockCmd.Parameters.AddWithValue("@sector", NormalizarSectorProducto(sucursalId, sectorReporte));
            await stockCmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
        return Results.Ok(new
        {
            ok = true,
            costo_perdido = costo,
            duplicado = insertado == 0,
            message = req.ProductoId > 0 ? "Reporte registrado e inventario actualizado." : "Reporte de daño del establecimiento registrado."
        });
    }
    catch (Exception ex)
    {
        await tx.RollbackAsync();
        return Results.Problem("No se pudo registrar el reporte: " + ex.Message);
    }
});

app.MapGet("/api/admin/productos-reportados", async (Db db, string clave) =>
{
    const string cleanKey = "ENTREGAR_LIMPIO_2026";
    if (clave != cleanKey) return Results.Unauthorized();

    await using var con = await db.OpenAsync();
    await EnsureAppMeseraTables(con);

    const string sql = """
        SELECT r.id, r.sucursal_id, r.sector,
               CASE WHEN s.id = 2 THEN 'EL BRUJO PREMIU' ELSE 'EL BRUJO' END AS sucursal,
               r.turno, r.usuario, r.nombre, r.fecha,
               r.producto, r.presentacion, r.cantidad, r.precio_unitario,
               r.costo_perdido, r.motivo, r.observacion
        FROM reportes_productos_movil r
        LEFT JOIN sucursales s ON s.id = r.sucursal_id
        ORDER BY r.fecha DESC;
    """;

    return Results.Ok(await db.QueryAsync(con, sql));
});


app.MapGet("/api/app-mesera/pedidos-pendientes", async (Db db, int sucursalId, string? caja, string? sector) =>
{
    await using var con = await db.OpenAsync();
    await EnsureAppMeseraTables(con);
    await EnsureSectorLayoutAsync(con);
    string sectorFiltro = NormalizarSector(sucursalId, sector, caja);

    const string sql = """
        SELECT p.id, p.sucursal_id, p.sector, p.mesa_id, p.mesa, p.mesera_usuario, p.mesera_nombre,
               p.fecha, p.estado,
               CASE
                   WHEN (UPPER(COALESCE(p.observacion, '')) LIKE 'CORTESIA_MESA%' OR (p.mesa_id <= 0 AND UPPER(COALESCE(p.mesa, '')) LIKE '%CORTES%'))
                   THEN COALESCE((
                       SELECT SUM(dd.cantidad * COALESCE(ppr.precio_venta, dd.precio_unitario, 0))
                       FROM detalle_pedidos_movil dd
                       LEFT JOIN presentaciones ppr
                              ON ppr.id = dd.presentacion_id
                             AND ppr.producto_id = dd.producto_id
                             AND ppr.estado = 'ACTIVO'
                       WHERE dd.pedido_id = p.id
                   ), p.total)
                   ELSE p.total
               END AS total,
               p.observacion,
               d.producto_id, d.presentacion_id, d.producto, d.presentacion, d.cantidad,
               CASE
                   WHEN (UPPER(COALESCE(p.observacion, '')) LIKE 'CORTESIA_MESA%' OR (p.mesa_id <= 0 AND UPPER(COALESCE(p.mesa, '')) LIKE '%CORTES%'))
                   THEN COALESCE(pr.precio_venta, d.precio_unitario, 0)
                   ELSE d.precio_unitario
               END AS precio_unitario,
               CASE
                   WHEN (UPPER(COALESCE(p.observacion, '')) LIKE 'CORTESIA_MESA%' OR (p.mesa_id <= 0 AND UPPER(COALESCE(p.mesa, '')) LIKE '%CORTES%'))
                   THEN d.cantidad * COALESCE(pr.precio_venta, d.precio_unitario, 0)
                   ELSE d.subtotal
               END AS subtotal,
               d.genera_comision, d.tipo_comision, d.valor_comision,
               d.comision_calculada
        FROM pedidos_movil p
        INNER JOIN detalle_pedidos_movil d ON d.pedido_id = p.id
        LEFT JOIN presentaciones pr
               ON pr.id = d.presentacion_id
              AND pr.producto_id = d.producto_id
              AND pr.estado = 'ACTIVO'
        WHERE p.sucursal_id = @sucursalId
          AND p.sector = @sector
          AND p.estado = 'PENDIENTE'
        ORDER BY p.fecha;
    """;

    return Results.Ok(await db.QueryAsync(con, sql, new Dictionary<string, object?>
    {
        ["@sucursalId"] = sucursalId,
        ["@sector"] = sectorFiltro
    }));
});

app.MapPost("/api/app-mesera/pedidos/{id:long}/estado", async (Db db, SheetsReporter sheets, long id, PedidoEstadoRequest req) =>
{
    await using var con = await db.OpenAsync();
    await EnsureAppMeseraTables(con);

    string estado = (req.Estado ?? "").Trim().ToUpperInvariant();
    if (estado != "ACEPTADO" && estado != "RECHAZADO" && estado != "ENTREGADO")
        return Results.BadRequest(new { ok = false, message = "Estado inválido." });

    string pedidoSector = "GENERAL";
    int pedidoSucursal = 1;
    await using (var pedidoInfo = new MySqlCommand("SELECT sucursal_id, sector FROM pedidos_movil WHERE id=@id LIMIT 1;", con))
    {
        pedidoInfo.Parameters.AddWithValue("@id", id);
        await using var rd = await pedidoInfo.ExecuteReaderAsync();
        if (!await rd.ReadAsync()) return Results.NotFound(new { ok=false, message="Pedido no encontrado." });
        pedidoSucursal = rd.GetInt32(0);
        pedidoSector = NormalizarSector(pedidoSucursal, rd.IsDBNull(1) ? null : rd.GetString(1));
    }
    if (!string.IsNullOrWhiteSpace(req.CajeroUsuario))
    {
        await using var userInfo = new MySqlCommand("SELECT sucursal_id, sector FROM usuarios WHERE usuario=@usuario AND estado='ACTIVO' LIMIT 1;", con);
        userInfo.Parameters.AddWithValue("@usuario", req.CajeroUsuario.Trim().ToLowerInvariant());
        await using var rd = await userInfo.ExecuteReaderAsync();
        if (!await rd.ReadAsync()) return Results.Unauthorized();
        int userBranch = rd.GetInt32(0);
        string userSector = NormalizarSector(userBranch, rd.IsDBNull(1) ? null : rd.GetString(1));
        if (userBranch != pedidoSucursal || !string.Equals(userSector, pedidoSector, StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { ok=false, message="Este pedido pertenece a otro sector/caja." });
    }

    await using var tx = await con.BeginTransactionAsync();

    try
    {
        const string updateSql = """
            UPDATE pedidos_movil
            SET estado = @estado,
                cajero_usuario = @cajero,
                fecha_respuesta = NOW()
            WHERE id = @id;
        """;

        await using var cmd = new MySqlCommand(updateSql, con, tx);
        cmd.Parameters.AddWithValue("@estado", estado);
        cmd.Parameters.AddWithValue("@cajero", req.CajeroUsuario ?? "");
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync();

        if (estado == "ACEPTADO" || estado == "ENTREGADO")
        {
            // V36: corrige también cortesías antiguas que fueron guardadas en Bs. 0 por V35.
            const string fixCourtesyDetailSql = """
                UPDATE detalle_pedidos_movil d
                INNER JOIN pedidos_movil p ON p.id = d.pedido_id
                LEFT JOIN presentaciones pr
                       ON pr.id = d.presentacion_id
                      AND pr.producto_id = d.producto_id
                      AND pr.estado = 'ACTIVO'
                SET d.precio_unitario = COALESCE(pr.precio_venta, d.precio_unitario, 0),
                    d.subtotal = d.cantidad * COALESCE(pr.precio_venta, d.precio_unitario, 0)
                WHERE p.id = @id
                  AND (
                      UPPER(COALESCE(p.observacion, '')) LIKE 'CORTESIA_MESA%'
                      OR (p.mesa_id <= 0 AND UPPER(COALESCE(p.mesa, '')) LIKE '%CORTES%')
                  );
            """;

            await using (var fixDetail = new MySqlCommand(fixCourtesyDetailSql, con, tx))
            {
                fixDetail.Parameters.AddWithValue("@id", id);
                await fixDetail.ExecuteNonQueryAsync();
            }

            const string fixCourtesyTotalSql = """
                UPDATE pedidos_movil p
                SET p.total = COALESCE((
                    SELECT SUM(d.subtotal)
                    FROM detalle_pedidos_movil d
                    WHERE d.pedido_id = p.id
                ), p.total)
                WHERE p.id = @id
                  AND (
                      UPPER(COALESCE(p.observacion, '')) LIKE 'CORTESIA_MESA%'
                      OR (p.mesa_id <= 0 AND UPPER(COALESCE(p.mesa, '')) LIKE '%CORTES%')
                  );
            """;

            await using (var fixTotal = new MySqlCommand(fixCourtesyTotalSql, con, tx))
            {
                fixTotal.Parameters.AddWithValue("@id", id);
                await fixTotal.ExecuteNonQueryAsync();
            }

            const string comSql = """
                INSERT INTO comisiones_meseras
                    (pedido_id, sucursal_id, sector, mesa_id, mesera_usuario, mesera_nombre, fecha,
                     producto, cantidad, venta_total, comision_total, estado)
                SELECT p.id, p.sucursal_id, p.sector, p.mesa_id, p.mesera_usuario, p.mesera_nombre, NOW(),
                       d.producto, d.cantidad, d.subtotal, d.comision_calculada, 'PENDIENTE_PAGO'
                FROM pedidos_movil p
                INNER JOIN detalle_pedidos_movil d ON d.pedido_id = p.id
                WHERE p.id = @id AND d.comision_calculada > 0
                ON DUPLICATE KEY UPDATE
                    venta_total = VALUES(venta_total),
                    comision_total = VALUES(comision_total),
                    estado = VALUES(estado);
            """;

            await using var comCmd = new MySqlCommand(comSql, con, tx);
            comCmd.Parameters.AddWithValue("@id", id);
            await comCmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
        await TrySyncSheets(db, sheets);

        return Results.Ok(new { ok = true, pedido_id = id, estado });
    }
    catch (Exception ex)
    {
        await tx.RollbackAsync();
        return Results.Problem("No se pudo cambiar estado del pedido: " + ex.Message);
    }
});

app.MapGet("/api/app-mesera/comisiones", async (Db db, int sucursalId, string? meseraUsuario) =>
{
    await using var con = await db.OpenAsync();
    await EnsureAppMeseraTables(con);

    const string sql = """
        SELECT sucursal_id, sector, mesera_usuario, mesera_nombre, DATE(fecha) AS fecha,
               SUM(venta_total) AS total_vendido,
               SUM(comision_total) AS total_comision
        FROM comisiones_meseras
        WHERE sucursal_id = @sucursalId
          AND (@meseraUsuario IS NULL OR mesera_usuario = @meseraUsuario)
        GROUP BY sucursal_id, sector, mesera_usuario, mesera_nombre, DATE(fecha)
        ORDER BY fecha DESC, mesera_nombre;
    """;

    return Results.Ok(await db.QueryAsync(con, sql, new Dictionary<string, object?>
    {
        ["@sucursalId"] = sucursalId,
        ["@meseraUsuario"] = meseraUsuario
    }));
});

app.MapGet("/api/sucursales", async (Db db) =>
{
    await using var con = await db.OpenAsync();
    await EnsureMesasEnVivoTables(con);
    await EnsureOfficialBranchAndTableLayout(con);
    var rows = await db.QueryAsync(con, "SELECT id, nombre, direccion, estado FROM sucursales ORDER BY id;");
    return Results.Ok(rows);
});

app.MapGet("/api/config/tarifa-mesas", async (Db db) =>
{
    await using var con = await db.OpenAsync();
    var pricing = await EnsureTablePricingAsync(con);
    var promoScope = await EnsureTablePromotionScopeAsync(con);
    return Results.Ok(new
    {
        ok = true,
        // Compatibilidad: precioHora sigue siendo la tarifa NORMAL.
        precioHora = pricing.normal,
        precioNormal = pricing.normal,
        precioPromoLunes = pricing.promoLunes,
        precioPrivada = pricing.privada,
        precioPromoPrivada = pricing.promoPrivada,
        promoLunesActiva = pricing.promoActiva,
        promoLunesSucursal = promoScope.mondayBranch,
        promoLunesSector = promoScope.mondaySector,
        promoTemporalActiva = promoScope.tempEnabled,
        promoTemporalDesde = PromoDateText(promoScope.tempStart),
        promoTemporalHasta = PromoDateText(promoScope.tempEnd),
        promoTemporalSucursal = promoScope.tempBranch,
        promoTemporalSector = promoScope.tempSector
    });
});

app.MapPost("/api/admin/tarifa-mesas", async (Db db, string clave, TableRateRequest req) =>
{
    const string cleanKey = "ENTREGAR_LIMPIO_2026";
    if (clave != cleanKey) return Results.Unauthorized();

    decimal normal = req.PrecioNormal > 0 ? req.PrecioNormal : req.PrecioHora;
    decimal promo = req.PrecioPromoLunes > 0 ? req.PrecioPromoLunes : 10m;
    decimal privada = req.PrecioPrivada > 0 ? req.PrecioPrivada : 40m;
    decimal promoPrivada = req.PrecioPromoPrivada > 0 ? req.PrecioPromoPrivada : 20m;
    if (normal <= 0 || promo <= 0 || privada <= 0 || promoPrivada <= 0)
        return Results.BadRequest(new { ok = false, message = "Las tarifas NORMAL, PROMO NORMAL, PRIVADA y PROMO PRIVADA deben ser mayores a 0." });

    normal = Math.Round(normal, 2);
    promo = Math.Round(promo, 2);
    privada = Math.Round(privada, 2);
    promoPrivada = Math.Round(promoPrivada, 2);

    await using var con = await db.OpenAsync();
    await EnsureTablePricingAsync(con);
    await EnsureTablePromotionScopeAsync(con);

    int mondayBranch = Math.Clamp(req.PromoLunesSucursal, 0, 2);
    int mondaySector = Math.Clamp(req.PromoLunesSector, 0, 2);
    int tempBranch = Math.Clamp(req.PromoTemporalSucursal, 0, 2);
    int tempSector = Math.Clamp(req.PromoTemporalSector, 0, 2);
    int tempStart = ParsePromoDate(req.PromoTemporalDesde);
    int tempEnd = ParsePromoDate(req.PromoTemporalHasta);
    if (req.PromoTemporalActiva && (tempStart <= 0 || tempEnd <= 0))
        return Results.BadRequest(new { ok = false, message = "La promoción temporal requiere fecha Desde y Hasta válidas." });
    if (tempStart > 0 && tempEnd > 0 && tempEnd < tempStart)
        (tempStart, tempEnd) = (tempEnd, tempStart);

    await using var tx = await con.BeginTransactionAsync();
    try
    {
        async Task SaveValue(string key, decimal value)
        {
            await using var cmd = new MySqlCommand("""
                INSERT INTO configuracion_sistema (clave, valor_decimal, actualizado)
                VALUES (@clave, @valor, NOW())
                ON DUPLICATE KEY UPDATE valor_decimal=VALUES(valor_decimal), actualizado=NOW();
            """, con, tx);
            cmd.Parameters.AddWithValue("@clave", key);
            cmd.Parameters.AddWithValue("@valor", value);
            await cmd.ExecuteNonQueryAsync();
        }

        await SaveValue("TARIFA_MESA_HORA", normal);
        await SaveValue("TARIFA_MESA_NORMAL", normal);
        await SaveValue("TARIFA_MESA_PROMO_LUNES", promo);
        await SaveValue("TARIFA_MESA_PRIVADA", privada);
        await SaveValue("TARIFA_MESA_PROMO_PRIVADA", promoPrivada);
        await SaveValue("PROMO_LUNES_ACTIVA", req.PromoLunesActiva ? 1m : 0m);
        await SaveValue("PROMO_LUNES_SUCURSAL", mondayBranch);
        await SaveValue("PROMO_LUNES_SECTOR", mondaySector);
        await SaveValue("PROMO_TEMPORAL_ACTIVA", req.PromoTemporalActiva ? 1m : 0m);
        await SaveValue("PROMO_TEMPORAL_DESDE", tempStart);
        await SaveValue("PROMO_TEMPORAL_HASTA", tempEnd);
        await SaveValue("PROMO_TEMPORAL_SUCURSAL", tempBranch);
        await SaveValue("PROMO_TEMPORAL_SECTOR", tempSector);

        await using (var mesas = new MySqlCommand("""
            UPDATE mesas
            SET precio_hora = CASE
                WHEN UPPER(TRIM(COALESCE(tipo_mesa,'NORMAL')))='PRIVADA' THEN @privada
                ELSE @normal
            END;
        """, con, tx))
        {
            mesas.Parameters.AddWithValue("@normal", normal);
            mesas.Parameters.AddWithValue("@privada", privada);
            await mesas.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
    }
    catch (Exception ex)
    {
        await tx.RollbackAsync();
        return Results.Problem("No se pudieron guardar las tarifas: " + ex.Message);
    }

    return Results.Ok(new
    {
        ok = true,
        precioHora = normal,
        precioNormal = normal,
        precioPromoLunes = promo,
        precioPrivada = privada,
        precioPromoPrivada = promoPrivada,
        promoLunesActiva = req.PromoLunesActiva,
        promoLunesSucursal = mondayBranch,
        promoLunesSector = mondaySector,
        promoTemporalActiva = req.PromoTemporalActiva,
        promoTemporalDesde = PromoDateText(tempStart),
        promoTemporalHasta = PromoDateText(tempEnd),
        promoTemporalSucursal = tempBranch,
        promoTemporalSector = tempSector,
        message = "Precios/promociones guardados. NORMAL y PRIVADA usan su propia tarifa promocional al iniciar; las sesiones abiertas conservan su tarifa original."
    });
});


app.MapGet("/api/config/mesas-layout", async (Db db) =>
{
    await using var con = await db.OpenAsync();
    var layout = await EnsureTableLayoutSettingsAsync(con);
    return Results.Ok(new
    {
        ok = true,
        elBrujo = layout.brujo,
        premiumArriba = layout.premiumArriba,
        premiumAbajo = layout.premiumAbajo,
        mesaPrivadaPremiumArriba = layout.privadaPremiumArriba
    });
});

app.MapPost("/api/admin/config/mesas-layout", async (Db db, string clave, TableLayoutRequest req) =>
{
    const string cleanKey = "ENTREGAR_LIMPIO_2026";
    if (clave != cleanKey) return Results.Unauthorized();
    int sid = ResolveInstanceSucursalId(req.SucursalId);
    string sector = NormalizarSector(sid, req.Sector);
    int cantidad = Math.Clamp(req.CantidadMesas, 1, 29);
    await using var con = await db.OpenAsync();
    await EnsureTableLayoutSettingsAsync(con);
    string key = sid == 1 ? "MESAS_EL_BRUJO_GENERAL" : (sector == "ABAJO" ? "MESAS_PREMIU_ABAJO" : "MESAS_PREMIU_ARRIBA");
    await using var cmd = new MySqlCommand("""
        INSERT INTO configuracion_sistema(clave,valor_decimal,actualizado)
        VALUES(@k,@v,NOW())
        ON DUPLICATE KEY UPDATE valor_decimal=VALUES(valor_decimal),actualizado=NOW();
    """, con);
    cmd.Parameters.AddWithValue("@k", key);
    cmd.Parameters.AddWithValue("@v", cantidad);
    await cmd.ExecuteNonQueryAsync();
    var layout = await EnsureTableLayoutSettingsAsync(con);
    return Results.Ok(new { ok=true, sucursalId=sid, sector, cantidadMesas=cantidad, layout });
});

app.MapGet("/api/mesas", async (Db db, int? sucursalId) =>
{
    await using var con = await db.OpenAsync();
    await EnsureMesasEnVivoTables(con);
    await EnsureOfficialBranchAndTableLayout(con);

    const string sql = """
        SELECT m.id, m.sucursal_id, CASE WHEN s.id = 2 THEN 'EL BRUJO PREMIU' ELSE 'EL BRUJO' END AS sucursal, m.nombre, m.precio_hora, m.estado
        FROM mesas m
        INNER JOIN sucursales s ON s.id = m.sucursal_id
        WHERE (@sucursalId IS NULL OR m.sucursal_id = @sucursalId)
          AND m.estado <> 'INACTIVA'
        ORDER BY m.sucursal_id, m.id;
    """;

    var rows = await db.QueryAsync(con, sql, new Dictionary<string, object?>
    {
        ["@sucursalId"] = sucursalId
    });

    return Results.Ok(rows);
});

app.MapGet("/api/productos", async (Db db, int? sucursalId) =>
{
    await using var con = await db.OpenAsync();
    await EnsureAppMeseraTables(con);

    const string sql = """
        SELECT p.id, p.sucursal_id, CASE WHEN s.id = 2 THEN 'EL BRUJO PREMIU' ELSE 'EL BRUJO' END AS sucursal, p.nombre, p.categoria,
               p.unidad_base,
               CASE WHEN p.sucursal_id=2 THEN (SELECT COALESCE(SUM(p2.stock_actual),0) FROM productos p2 WHERE p2.sucursal_id=p.sucursal_id AND p2.estado='ACTIVO' AND LOWER(TRIM(p2.nombre))=LOWER(TRIM(p.nombre))) ELSE p.stock_actual END AS stock_actual,
               p.stock_minimo, COALESCE(p.sin_limite_stock, 0) AS sin_limite_stock, p.estado
        FROM productos p
        INNER JOIN (
            SELECT sucursal_id, LOWER(TRIM(nombre)) AS nombre_norm,
                   COALESCE(MIN(CASE WHEN estado='ACTIVO' THEN id END), MIN(id)) AS id_canonico
            FROM productos
            GROUP BY sucursal_id, LOWER(TRIM(nombre))
        ) canon ON canon.id_canonico = p.id
        INNER JOIN sucursales s ON s.id = p.sucursal_id
        WHERE (@sucursalId IS NULL OR p.sucursal_id = @sucursalId)
        ORDER BY p.sucursal_id,
                 CASE
                    WHEN p.categoria = 'Agua' THEN 1
                    WHEN p.categoria = 'Energizantes' THEN 2
                    WHEN p.categoria = 'Sodas' THEN 3
                    WHEN p.categoria IN ('Cocas', 'Coca machucada') THEN 4
                    WHEN p.categoria = 'Cervezas' THEN 5
                    WHEN p.categoria = 'Tragos / Botellas' THEN 6
                    WHEN p.categoria = 'Servidos en vaso' THEN 7
                    WHEN p.categoria = 'Cigarros' THEN 8
                    WHEN p.categoria = 'Snacks y piqueos' THEN 9
                    WHEN p.categoria = 'Dulces y golosinas' THEN 10
                    WHEN p.categoria = 'Combos / Promos' THEN 11
                    WHEN p.categoria = 'Accesorios' THEN 12
                    WHEN p.categoria = 'Ceniceros' THEN 13
                    WHEN p.categoria IN ('Otros', 'Otros / Extras', 'Varios') THEN 14
                    ELSE 99
                 END,
                 p.nombre;
    """;

    var rows = await db.QueryAsync(con, sql, new Dictionary<string, object?>
    {
        ["@sucursalId"] = sucursalId
    });

    return Results.Ok(rows);
});

app.MapPost("/api/admin/cargar-catalogo-local", async (Db db, string clave) =>
{
    const string cleanKey = "ENTREGAR_LIMPIO_2026";
    if (clave != cleanKey) return Results.Unauthorized();

    await using var con = await db.OpenAsync();
    await EnsureAppMeseraTables(con);

    int insertados = 0;
    int actualizados = 0;

    foreach (int sucursalId in new[] { 1, 2 })
    {
        foreach (var item in CatalogoProductosLocalV19())
        {
            bool updated = await UpsertCatalogoProductoLocal(con, sucursalId, item.nombre, item.categoria, item.precio, "Unidad", 5);
            if (updated) actualizados++; else insertados++;
        }

        foreach (var item in CatalogoCombosPromosLocalV19())
        {
            bool updated = await UpsertCatalogoProductoLocal(con, sucursalId, item.nombre, item.categoria, item.precio, item.detalle, 2);
            if (updated) actualizados++; else insertados++;
        }
    }

    return Results.Ok(new
    {
        ok = true,
        version = "V19_CATALOGO_LOCAL_DULCES",
        message = "Catálogo local cargado en Railway: productos, dulces, combos y promociones.",
        insertados,
        actualizados,
        nota = "No se cargó PRUEBA porque parece dato de prueba."
    });
});

app.MapGet("/api/admin/cargar-catalogo-local", async (Db db, string clave) =>
{
    const string cleanKey = "ENTREGAR_LIMPIO_2026";
    if (clave != cleanKey) return Results.Unauthorized();

    await using var con = await db.OpenAsync();
    await EnsureAppMeseraTables(con);

    int insertados = 0;
    int actualizados = 0;

    foreach (int sucursalId in new[] { 1, 2 })
    {
        foreach (var item in CatalogoProductosLocalV19())
        {
            bool updated = await UpsertCatalogoProductoLocal(con, sucursalId, item.nombre, item.categoria, item.precio, "Unidad", 5);
            if (updated) actualizados++; else insertados++;
        }

        foreach (var item in CatalogoCombosPromosLocalV19())
        {
            bool updated = await UpsertCatalogoProductoLocal(con, sucursalId, item.nombre, item.categoria, item.precio, item.detalle, 2);
            if (updated) actualizados++; else insertados++;
        }
    }

    return Results.Ok(new
    {
        ok = true,
        version = "V19_CATALOGO_LOCAL_DULCES",
        message = "Catálogo local cargado en Railway: productos, dulces, combos y promociones.",
        insertados,
        actualizados,
        nota = "No se cargó PRUEBA porque parece dato de prueba."
    });
});

app.MapPost("/api/admin/cargar-catalogo-final", async (Db db, SheetsReporter sheets, string clave) =>
{
    const string cleanKey = "ENTREGAR_LIMPIO_2026";
    if (clave != cleanKey) return Results.Unauthorized();

    await using var con = await db.OpenAsync();
    await EnsureAppMeseraTables(con);

    int insertados = 0;
    int actualizados = 0;

    foreach (int sucursalId in new[] { 1, 2 })
    {
        foreach (var item in CatalogoFinalV29())
        {
            bool updated = await UpsertCatalogoFinalV29(con, sucursalId, item.nombre, item.categoria, item.cantidad, item.precio, item.sinLimiteStock);
            if (updated) actualizados++; else insertados++;
        }
    }

    await TrySyncSheets(db, sheets);

    return Results.Ok(new
    {
        ok = true,
        version = "V29_CATALOGO_FINAL_STOCK",
        message = "Catálogo final cargado: cantidades, precios y productos en vaso con control real de inventario.",
        productos = CatalogoFinalV29().Length,
        sucursales = 2,
        insertados,
        actualizados,
        nota = "Los productos de categoría Servidos en vaso descuentan su cantidad real del inventario."
    });
});

app.MapGet("/api/admin/cargar-catalogo-final", async (Db db, SheetsReporter sheets, string clave) =>
{
    const string cleanKey = "ENTREGAR_LIMPIO_2026";
    if (clave != cleanKey) return Results.Unauthorized();

    await using var con = await db.OpenAsync();
    await EnsureAppMeseraTables(con);

    int insertados = 0;
    int actualizados = 0;

    foreach (int sucursalId in new[] { 1, 2 })
    {
        foreach (var item in CatalogoFinalV29())
        {
            bool updated = await UpsertCatalogoFinalV29(con, sucursalId, item.nombre, item.categoria, item.cantidad, item.precio, item.sinLimiteStock);
            if (updated) actualizados++; else insertados++;
        }
    }

    await TrySyncSheets(db, sheets);

    return Results.Ok(new
    {
        ok = true,
        version = "V29_CATALOGO_FINAL_STOCK",
        message = "Catálogo final cargado: cantidades, precios y productos en vaso con control real de inventario.",
        productos = CatalogoFinalV29().Length,
        sucursales = 2,
        insertados,
        actualizados,
        nota = "Los productos de categoría Servidos en vaso descuentan su cantidad real del inventario."
    });
});



app.MapPost("/api/admin/aplicar-stock-inicial", async (Db db, SheetsReporter sheets, string clave, int sucursalId = 1) =>
{
    return await AplicarStockTxtPaquetesV33(db, sheets, clave, sucursalId);
});

app.MapGet("/api/admin/aplicar-stock-inicial", async (Db db, SheetsReporter sheets, string clave, int sucursalId = 1) =>
{
    return await AplicarStockTxtPaquetesV33(db, sheets, clave, sucursalId);
});

app.MapPost("/api/admin/aplicar-stock-txt", async (Db db, SheetsReporter sheets, string clave, int sucursalId = 1) =>
{
    return await AplicarStockTxtPaquetesV33(db, sheets, clave, sucursalId);
});

app.MapGet("/api/admin/aplicar-stock-txt", async (Db db, SheetsReporter sheets, string clave, int sucursalId = 1) =>
{
    return await AplicarStockTxtPaquetesV33(db, sheets, clave, sucursalId);
});

app.MapPost("/api/productos", async (Db db, SheetsReporter sheets, string clave, ProductoRequest p) =>
{
    // Endpoint legado protegido: el alta normal de productos se realiza desde
    // /api/admin/productos/guardar, que conserva toda la estructura del producto.
    const string cleanKey = "ENTREGAR_LIMPIO_2026";
    if (clave != cleanKey) return Results.Unauthorized();
    if (p.SucursalId != 1 && p.SucursalId != 2)
        return Results.BadRequest(new { ok = false, message = "Sucursal inválida." });
    if (string.IsNullOrWhiteSpace(p.Nombre))
        return Results.BadRequest(new { ok = false, message = "El nombre del producto es obligatorio." });

    await using var con = await db.OpenAsync();
    await EnsureAppMeseraTables(con);

    string nombre = p.Nombre.Trim();
    string categoria = NormalizarCategoriaProducto(p.Categoria, nombre);
    string unidadBase = string.IsNullOrWhiteSpace(p.UnidadBase) ? "UNIDAD" : p.UnidadBase.Trim().ToUpperInvariant();
    long id = 0;

    await using (var buscar = new MySqlCommand("SELECT id FROM productos WHERE sucursal_id=@sucursal_id AND LOWER(TRIM(nombre))=LOWER(TRIM(@nombre)) LIMIT 1;", con))
    {
        buscar.Parameters.AddWithValue("@sucursal_id", p.SucursalId);
        buscar.Parameters.AddWithValue("@nombre", nombre);
        object? found = await buscar.ExecuteScalarAsync();
        if (found != null) id = Convert.ToInt64(found);
    }

    if (id <= 0)
    {
        await using var cmd = new MySqlCommand("""
            INSERT INTO productos (sucursal_id, nombre, categoria, unidad_base, stock_actual, stock_minimo, estado)
            VALUES (@sucursal_id, @nombre, @categoria, @unidad_base, @stock_actual, @stock_minimo, 'ACTIVO');
            SELECT LAST_INSERT_ID();
        """, con);
        cmd.Parameters.AddWithValue("@sucursal_id", p.SucursalId);
        cmd.Parameters.AddWithValue("@nombre", nombre);
        cmd.Parameters.AddWithValue("@categoria", categoria);
        cmd.Parameters.AddWithValue("@unidad_base", unidadBase);
        cmd.Parameters.AddWithValue("@stock_actual", Math.Max(0, p.StockActual));
        cmd.Parameters.AddWithValue("@stock_minimo", Math.Max(0, p.StockMinimo));
        id = Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }
    else
    {
        await using var cmd = new MySqlCommand("""
            UPDATE productos
            SET categoria=@categoria, unidad_base=@unidad_base,
                stock_actual=@stock_actual, stock_minimo=@stock_minimo, estado='ACTIVO'
            WHERE id=@id;
        """, con);
        cmd.Parameters.AddWithValue("@categoria", categoria);
        cmd.Parameters.AddWithValue("@unidad_base", unidadBase);
        cmd.Parameters.AddWithValue("@stock_actual", Math.Max(0, p.StockActual));
        cmd.Parameters.AddWithValue("@stock_minimo", Math.Max(0, p.StockMinimo));
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    await TrySyncSheets(db, sheets);
    return Results.Ok(new { ok = true, id, categoria });
});

app.MapPost("/api/ventas", async (Db db, SheetsReporter sheets, VentaRequest venta) =>
{
    await using var con = await db.OpenAsync();
    await EnsureAppMeseraTables(con);
    await EnsureVentaSyncProtection(con);
    await EnsureAccountingLedger(con);

    // V40: conserva la división real de un pago MIXTO.
    try { await new MySqlCommand("ALTER TABLE ventas ADD COLUMN efectivo DECIMAL(10,2) NOT NULL DEFAULT 0;", con).ExecuteNonQueryAsync(); } catch { }
    try { await new MySqlCommand("ALTER TABLE ventas ADD COLUMN qr DECIMAL(10,2) NOT NULL DEFAULT 0;", con).ExecuteNonQueryAsync(); } catch { }

    await using var tx = await con.BeginTransactionAsync();

    try
    {
        // V45: bloqueo de cajas antiguas. Evita que una versión sin OperationKey/cola offline
        // vuelva a inflar ventas o stock.
        if (!venta.ClientVersion.HasValue || venta.ClientVersion.Value < 167)
            return Results.Json(new { ok = false, message = "Caja desactualizada. Se requiere Caja V167 o superior para garantizar sincronización anti-inflación de inventario.", minimumClientVersion = 167 }, statusCode: StatusCodes.Status426UpgradeRequired);

        string syncKey = string.IsNullOrWhiteSpace(venta.SyncKey)
            ? Guid.NewGuid().ToString("N")
            : venta.SyncKey.Trim();
        string operationKey = string.IsNullOrWhiteSpace(venta.OperationKey)
            ? ""
            : venta.OperationKey.Trim();

        // V47: toda Caja V128+ debe traer las dos identidades. Si falta una, NO se inventa
        // una nueva en el servidor, porque eso podría transformar un reintento en otra venta.
        if (string.IsNullOrWhiteSpace(venta.SyncKey) || string.IsNullOrWhiteSpace(operationKey))
            return Results.BadRequest(new { ok = false, message = "El cobro llegó sin SyncKey u OperationKey. Se bloqueó para evitar duplicación.", minimumClientVersion = 167 });

        // V54: candados de servidor. Serializan reintentos simultáneos aunque una base histórica
        // todavía no haya podido crear todos los índices UNIQUE por duplicados antiguos.
        // La conexión libera automáticamente estos candados al terminar la solicitud.
        var guard = await AcquireSaleGuardsAsync(con, tx, venta, operationKey);
        if (!guard.ok)
            return Results.Json(new { ok = false, message = guard.message }, statusCode: StatusCodes.Status409Conflict);

        // V44: tercera defensa. Para clientes antiguos que todavía no envían OperationKey,
        // el servidor construye una huella contable determinística. De este modo, si la misma
        // venta vuelve con OTRO sync_key por una versión antigua, Railway la reconoce como la
        // misma operación y no vuelve a insertar detalle, descontar stock ni sumar recaudación.
        string legacyFingerprint = string.IsNullOrWhiteSpace(operationKey)
            ? BuildLegacyAccountingFingerprint(venta)
            : "";

        // V42: defensa estricta. Una sync_key representa una sola venta inmutable.
        // Si el mismo request llega otra vez por reintento de red, devolvemos la venta existente
        // y NO volvemos a tocar detalle, stock ni importes.
        long ventaExistenteId = 0;
        await using (var existeCmd = new MySqlCommand("""
            SELECT id, sucursal_id, cajero, tipo, metodo_pago, COALESCE(efectivo,0), COALESCE(qr,0), total
            FROM ventas WHERE sync_key = @sync_key LIMIT 1;
        """, con, tx))
        {
            existeCmd.Parameters.AddWithValue("@sync_key", syncKey);
            await using var rd = await existeCmd.ExecuteReaderAsync();
            if (await rd.ReadAsync())
            {
                ventaExistenteId = rd.GetInt64(0);
                bool mismo = rd.GetInt32(1) == venta.SucursalId
                    && string.Equals(rd.GetString(2), venta.Cajero ?? "", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(rd.GetString(3), venta.Tipo ?? "", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(rd.GetString(4), venta.MetodoPago ?? "", StringComparison.OrdinalIgnoreCase)
                    && Math.Abs(rd.GetDecimal(5) - Math.Max(0, venta.Efectivo)) < 0.01m
                    && Math.Abs(rd.GetDecimal(6) - Math.Max(0, venta.Qr)) < 0.01m
                    && Math.Abs(rd.GetDecimal(7) - venta.Total) < 0.01m;
                if (!mismo)
                    return Results.Conflict(new { ok = false, message = "La misma sync_key ya existe con datos distintos. Se bloqueó el cobro para evitar duplicación o alteración.", syncKey, ventaId = ventaExistenteId });
            }
        }

        if (ventaExistenteId > 0)
        {
            await EnsureLedgerForExistingSaleAsync(con, tx, ventaExistenteId, venta, syncKey, operationKey);
            await tx.CommitAsync();
            return Results.Ok(new { ok = true, id = ventaExistenteId, syncKey, operationKey, duplicated = false, idempotent = true });
        }

        // V43: aunque una segunda PC genere otra sync_key, la misma operation_key
        // no puede representar dos cobros diferentes.
        if (!string.IsNullOrWhiteSpace(operationKey))
        {
            long opVentaId = 0;
            await using (var opCmd = new MySqlCommand("""
                SELECT id, sucursal_id, cajero, tipo, metodo_pago, COALESCE(efectivo,0), COALESCE(qr,0), total
                FROM ventas WHERE operation_key = @operation_key LIMIT 1;
            """, con, tx))
            {
                opCmd.Parameters.AddWithValue("@operation_key", operationKey);
                await using var rd = await opCmd.ExecuteReaderAsync();
                if (await rd.ReadAsync())
                {
                    opVentaId = rd.GetInt64(0);
                    bool mismo = rd.GetInt32(1) == venta.SucursalId
                        && string.Equals(rd.GetString(2), venta.Cajero ?? "", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(rd.GetString(3), venta.Tipo ?? "", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(rd.GetString(4), venta.MetodoPago ?? "", StringComparison.OrdinalIgnoreCase)
                        && Math.Abs(rd.GetDecimal(5) - Math.Max(0, venta.Efectivo)) < 0.01m
                        && Math.Abs(rd.GetDecimal(6) - Math.Max(0, venta.Qr)) < 0.01m
                        && Math.Abs(rd.GetDecimal(7) - venta.Total) < 0.01m;
                    if (!mismo)
                        return Results.Conflict(new { ok = false, message = "La operación ya fue cobrada con datos distintos. Se bloqueó un posible doble cobro entre computadoras.", operationKey, ventaId = opVentaId });
                }
            }

            if (opVentaId > 0)
            {
                await EnsureLedgerForExistingSaleAsync(con, tx, opVentaId, venta, syncKey, operationKey);
                await tx.CommitAsync();
                return Results.Ok(new { ok = true, id = opVentaId, syncKey, operationKey, duplicated = false, idempotent = true, sameOperation = true });
            }
        }

        // V44: respaldo para versiones antiguas sin OperationKey.
        // No se usa para clientes nuevos, porque OperationKey es una identidad más fuerte.
        if (!string.IsNullOrWhiteSpace(legacyFingerprint))
        {
            long huellaVentaId = 0;
            await using (var fpCmd = new MySqlCommand("SELECT id FROM ventas WHERE legacy_fingerprint = @fp LIMIT 1;", con, tx))
            {
                fpCmd.Parameters.AddWithValue("@fp", legacyFingerprint);
                var existingFp = await fpCmd.ExecuteScalarAsync();
                if (existingFp != null) huellaVentaId = Convert.ToInt64(existingFp);
            }

            if (huellaVentaId > 0)
            {
                await EnsureLedgerForExistingSaleAsync(con, tx, huellaVentaId, venta, syncKey, operationKey);
                await tx.CommitAsync();
                return Results.Ok(new { ok = true, id = huellaVentaId, syncKey, operationKey, legacyFingerprint, idempotent = true, sameLegacyFingerprint = true });
            }
        }

        if (venta.Total <= 0 || venta.Efectivo < 0 || venta.Qr < 0)
            return Results.BadRequest(new { ok = false, message = "Los importes no pueden ser negativos." });

        string metodoSeguro = (venta.MetodoPago ?? "").Trim().ToUpperInvariant();
        decimal sumaPago = Math.Round(Math.Max(0, venta.Efectivo) + Math.Max(0, venta.Qr), 2);
        decimal totalSeguro = Math.Round(venta.Total, 2);
        bool pagoCuadra = metodoSeguro switch
        {
            "EFECTIVO" => Math.Abs(Math.Max(0, venta.Efectivo) - totalSeguro) < 0.01m && Math.Abs(venta.Qr) < 0.01m,
            "QR" => Math.Abs(Math.Max(0, venta.Qr) - totalSeguro) < 0.01m && Math.Abs(venta.Efectivo) < 0.01m,
            "TRANSFERENCIA" => Math.Abs(venta.Efectivo) < 0.01m && Math.Abs(venta.Qr) < 0.01m && totalSeguro > 0m,
            "MIXTO" => Math.Abs(sumaPago - totalSeguro) < 0.01m,
            _ => false
        };
        if (!pagoCuadra)
            return Results.BadRequest(new { ok = false, message = "El método de pago no cuadra con el total. Se bloqueó el registro para evitar descuadres.", total = totalSeguro, efectivo = venta.Efectivo, qr = venta.Qr, metodo = metodoSeguro });

        // V43: valida el contenido económico antes de tocar detalle o stock.
        // DIRECTA y CONSUMO_MESA deben cuadrar con la suma de productos redondeada hacia arriba a Bs. 0,50.
        // MESA puede incluir además el tiempo, por eso los productos nunca pueden superar el total cobrado.
        if (venta.Detalle == null)
            return Results.BadRequest(new { ok = false, message = "El detalle de la venta es obligatorio." });
        if (venta.Detalle.Any(d => d.Cantidad <= 0 || d.PrecioUnitario < 0 || d.Subtotal < 0))
            return Results.BadRequest(new { ok = false, message = "El detalle contiene cantidades o importes inválidos. Se bloqueó la venta." });

        foreach (var d in venta.Detalle)
        {
            decimal esperadoLinea = Math.Round(d.Cantidad * d.PrecioUnitario, 2);
            if (Math.Abs(esperadoLinea - Math.Round(d.Subtotal, 2)) > 0.02m)
                return Results.BadRequest(new { ok = false, message = "Un producto no cuadra con cantidad x precio. Se bloqueó la venta.", producto = d.Producto, cantidad = d.Cantidad, precio = d.PrecioUnitario, subtotal = d.Subtotal, esperado = esperadoLinea });
        }

        decimal detalleTotal = Math.Round(venta.Detalle.Sum(d => d.Subtotal), 2);
        decimal detalleRedondeado = Math.Ceiling(detalleTotal * 2m) / 2m;
        string tipoSeguro = (venta.Tipo ?? "").Trim().ToUpperInvariant();
        // V48: todo cobro ligado a una mesa debe identificar la sesión real.
        // CONSUMO_MESA admite varios pagos parciales; MESA admite un único cierre final.
        if ((tipoSeguro == "MESA" || tipoSeguro == "CONSUMO_MESA") && (!venta.SessionId.HasValue || venta.SessionId.Value <= 0))
            return Results.BadRequest(new { ok = false, message = "El cobro de mesa llegó sin SessionId. Se bloqueó para evitar mezclar turnos, mesas o pagos parciales." });

        if ((tipoSeguro == "DIRECTA" || tipoSeguro == "CONSUMO_MESA") && Math.Abs(detalleRedondeado - totalSeguro) > 0.01m)
            return Results.BadRequest(new { ok = false, message = "El total cobrado no cuadra con los productos. Se bloqueó para evitar desviaciones.", detalle = detalleTotal, esperado = detalleRedondeado, recibido = totalSeguro });
        if (tipoSeguro == "MESA" && detalleTotal - totalSeguro > 0.01m)
            return Results.BadRequest(new { ok = false, message = "Los productos superan el total cobrado de la mesa. Se bloqueó para evitar un descuadre.", detalle = detalleTotal, total = totalSeguro });

        // V60: TODO pago parcial de productos debe identificar cada línea con ConsumptionKey.
        // Una misma key no puede repetirse dentro del request ni existir en otra venta.
        if (tipoSeguro == "CONSUMO_MESA")
        {
            List<string> partialKeys = venta.Detalle
                .Select(d => (d.ConsumptionKey ?? "").Trim())
                .ToList();
            if (partialKeys.Any(string.IsNullOrWhiteSpace))
                return Results.BadRequest(new { ok = false, message = "Un pago parcial llegó con productos sin ConsumptionKey. Se bloqueó para evitar doble cobro.", minimumClientVersion = 167 });
            if (partialKeys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != partialKeys.Count)
                return Results.BadRequest(new { ok = false, message = "El mismo producto aparece repetido dentro del pago parcial. Se bloqueó para evitar inflación.", minimumClientVersion = 167 });
        }

        // V48: un cierre final de sesión se contabiliza una sola vez, aunque llegue con otra OperationKey.
        if (tipoSeguro == "MESA" && venta.SessionId.HasValue)
        {
            await using var finalCmd = new MySqlCommand("""
                SELECT id, sync_key, COALESCE(operation_key,'')
                FROM ventas
                WHERE sucursal_id = @sucursal_id
                  AND session_id = @session_id
                  AND UPPER(TRIM(tipo)) = 'MESA'
                  AND UPPER(TRIM(COALESCE(caja_nombre,''))) = UPPER(TRIM(@caja_nombre))
                ORDER BY id LIMIT 1;
            """, con, tx);
            finalCmd.Parameters.AddWithValue("@sucursal_id", venta.SucursalId);
            finalCmd.Parameters.AddWithValue("@session_id", venta.SessionId.Value);
            finalCmd.Parameters.AddWithValue("@caja_nombre", venta.CajaNombre ?? "");
            await using var finalRd = await finalCmd.ExecuteReaderAsync();
            if (await finalRd.ReadAsync())
            {
                long existingFinalId = finalRd.GetInt64(0);
                string existingSync = finalRd.IsDBNull(1) ? "" : finalRd.GetString(1);
                string existingOp = finalRd.IsDBNull(2) ? "" : finalRd.GetString(2);
                if (string.Equals(existingSync, syncKey, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(existingOp, operationKey, StringComparison.OrdinalIgnoreCase))
                {
                    await finalRd.DisposeAsync();
                    await tx.CommitAsync();
                    return Results.Ok(new { ok = true, id = existingFinalId, syncKey, operationKey, idempotent = true, sameSessionFinal = true });
                }
                return Results.Conflict(new { ok = false, message = "Esta sesión de mesa ya tiene un cobro final confirmado. Se bloqueó un segundo cierre.", sessionId = venta.SessionId, ventaId = existingFinalId });
            }
        }

        // V48: una línea de consumo identificada no puede aparecer en dos ventas distintas.
        // Esto evita que un producto pagado parcialmente reaparezca al cierre por una copia online atrasada.
        foreach (var d in venta.Detalle)
        {
            string consumptionKey = (d.ConsumptionKey ?? "").Trim();
            if (string.IsNullOrWhiteSpace(consumptionKey)) continue;
            await using var ckCmd = new MySqlCommand("SELECT venta_id FROM detalle_ventas WHERE consumption_key = @ck LIMIT 1;", con, tx);
            ckCmd.Parameters.AddWithValue("@ck", consumptionKey);
            object? existingCk = await ckCmd.ExecuteScalarAsync();
            if (existingCk != null)
                return Results.Conflict(new { ok = false, message = "Este producto de mesa ya fue cobrado anteriormente. Se bloqueó para evitar doble cobro.", consumptionKey, ventaId = Convert.ToInt64(existingCk) });
        }

        bool ventaYaExistia = false;

        const string ventaSql = """
            INSERT IGNORE INTO ventas (sucursal_id, cajero, caja_nombre, turno, fecha, tipo, metodo_pago, efectivo, qr, total, sync_key, operation_key, legacy_fingerprint, session_id)
            VALUES (@sucursal_id, @cajero, @caja_nombre, @turno, @fecha, @tipo, @metodo_pago, @efectivo, @qr, @total, @sync_key, NULLIF(@operation_key,''), NULLIF(@legacy_fingerprint,''), @session_id);
        """;

        await using var ventaCmd = new MySqlCommand(ventaSql, con, tx);
        ventaCmd.Parameters.AddWithValue("@sucursal_id", venta.SucursalId);
        ventaCmd.Parameters.AddWithValue("@cajero", venta.Cajero);
        ventaCmd.Parameters.AddWithValue("@caja_nombre", string.IsNullOrWhiteSpace(venta.CajaNombre) ? (object)DBNull.Value : venta.CajaNombre.Trim());
        ventaCmd.Parameters.AddWithValue("@turno", string.IsNullOrWhiteSpace(venta.Turno) ? HoraATurno(venta.Fecha) : NormalizarTurno(venta.Turno));
        ventaCmd.Parameters.AddWithValue("@fecha", venta.Fecha);
        ventaCmd.Parameters.AddWithValue("@tipo", venta.Tipo);
        ventaCmd.Parameters.AddWithValue("@metodo_pago", venta.MetodoPago);
        ventaCmd.Parameters.AddWithValue("@efectivo", Math.Max(0, venta.Efectivo));
        ventaCmd.Parameters.AddWithValue("@qr", Math.Max(0, venta.Qr));
        ventaCmd.Parameters.AddWithValue("@total", venta.Total);
        ventaCmd.Parameters.AddWithValue("@sync_key", syncKey);
        ventaCmd.Parameters.AddWithValue("@operation_key", operationKey);
        ventaCmd.Parameters.AddWithValue("@legacy_fingerprint", legacyFingerprint);
        ventaCmd.Parameters.AddWithValue("@session_id", venta.SessionId.HasValue ? venta.SessionId.Value : DBNull.Value);

        int insertedRows = await ventaCmd.ExecuteNonQueryAsync();
        long ventaId;
        await using (var idCmd = new MySqlCommand("""
            SELECT id, sucursal_id, cajero, tipo, metodo_pago, COALESCE(efectivo,0), COALESCE(qr,0), total
            FROM ventas
            WHERE sync_key = @sync_key
               OR (@operation_key <> '' AND operation_key = @operation_key)
               OR (@legacy_fingerprint <> '' AND legacy_fingerprint = @legacy_fingerprint)
            ORDER BY CASE
                WHEN sync_key = @sync_key THEN 0
                WHEN @operation_key <> '' AND operation_key = @operation_key THEN 1
                ELSE 2
            END
            LIMIT 1;
        """, con, tx))
        {
            idCmd.Parameters.AddWithValue("@sync_key", syncKey);
            idCmd.Parameters.AddWithValue("@operation_key", operationKey);
            idCmd.Parameters.AddWithValue("@legacy_fingerprint", legacyFingerprint);
            await using var rd = await idCmd.ExecuteReaderAsync();
            if (!await rd.ReadAsync())
                return Results.Conflict(new { ok = false, message = "No se pudo asegurar la identidad única del cobro. No se modificó inventario.", syncKey, operationKey });

            ventaId = rd.GetInt64(0);
            bool mismo = rd.GetInt32(1) == venta.SucursalId
                && string.Equals(rd.GetString(2), venta.Cajero ?? "", StringComparison.OrdinalIgnoreCase)
                && string.Equals(rd.GetString(3), venta.Tipo ?? "", StringComparison.OrdinalIgnoreCase)
                && string.Equals(rd.GetString(4), venta.MetodoPago ?? "", StringComparison.OrdinalIgnoreCase)
                && Math.Abs(rd.GetDecimal(5) - Math.Max(0, venta.Efectivo)) < 0.01m
                && Math.Abs(rd.GetDecimal(6) - Math.Max(0, venta.Qr)) < 0.01m
                && Math.Abs(rd.GetDecimal(7) - venta.Total) < 0.01m;
            if (!mismo)
                return Results.Conflict(new { ok = false, message = "Se detectó una colisión de identidad con otro cobro. Operación bloqueada.", syncKey, operationKey, ventaId });
        }

        ventaYaExistia = insertedRows == 0;
        if (ventaYaExistia)
        {
            await tx.CommitAsync();
            return Results.Ok(new { ok = true, id = ventaId, syncKey, operationKey, idempotent = true });
        }

        await using (var del = new MySqlCommand("DELETE FROM detalle_ventas WHERE venta_id = @venta_id;", con, tx))
        {
            del.Parameters.AddWithValue("@venta_id", ventaId);
            await del.ExecuteNonQueryAsync();
        }

        for (int detalleIndex = 0; detalleIndex < venta.Detalle.Count; detalleIndex++)
        {
            var d = venta.Detalle[detalleIndex];
            string consumptionKey = (d.ConsumptionKey ?? "").Trim();
            string lineKey = BuildSaleLineKey(operationKey, syncKey, detalleIndex, d);
            string nombreProducto = string.IsNullOrWhiteSpace(d.Producto) ? "Producto" : d.Producto.Trim();
            string nombrePresentacion = string.IsNullOrWhiteSpace(d.Presentacion) ? "Unidad" : d.Presentacion.Trim();
            string sectorDetalle = NormalizarSector(venta.SucursalId, d.Sector, venta.CajaNombre);
            decimal cantidadBase = d.CantidadBase <= 0 ? d.Cantidad : d.CantidadBase;

            // Los ID de SQLite/JSON de la PC no se reutilizan como ID de MySQL.
            // Railway resuelve producto por sucursal + nombre y presentación por producto + nombre.
            // Así no se crean duplicados ni se pisa otro producto cuando los ID locales difieren.
            long productoId;
            await using (var findProd = new MySqlCommand("""
                SELECT id
                FROM productos
                WHERE sucursal_id = @sucursal_id
                  AND sector = @sector
                  AND LOWER(TRIM(nombre)) = LOWER(TRIM(@nombre))
                ORDER BY id
                LIMIT 1;
            """, con, tx))
            {
                findProd.Parameters.AddWithValue("@sucursal_id", venta.SucursalId);
                findProd.Parameters.AddWithValue("@sector", NormalizarSectorProducto(venta.SucursalId, sectorDetalle));
                findProd.Parameters.AddWithValue("@nombre", nombreProducto);
                var found = await findProd.ExecuteScalarAsync();
                if (found != null)
                {
                    productoId = Convert.ToInt64(found);
                    await using var activar = new MySqlCommand("UPDATE productos SET estado = 'ACTIVO' WHERE id = @id;", con, tx);
                    activar.Parameters.AddWithValue("@id", productoId);
                    await activar.ExecuteNonQueryAsync();
                }
                else
                {
                    string categoriaNueva = NormalizarCategoriaProducto(null, nombreProducto);
                    await using var insertProd = new MySqlCommand("""
                        INSERT INTO productos
                        (sucursal_id, sector, nombre, categoria, tipo_entrada, unidad_base, unidades_por_entrada,
                         precio_compra, stock_actual, stock_minimo, estado)
                        VALUES
                        (@sucursal_id, @sector, @nombre, @categoria, 'UNIDAD', 'UNIDAD', 1, 0, 0, 0, 'ACTIVO');
                        SELECT LAST_INSERT_ID();
                    """, con, tx);
                    insertProd.Parameters.AddWithValue("@sucursal_id", venta.SucursalId);
                    insertProd.Parameters.AddWithValue("@sector", NormalizarSectorProducto(venta.SucursalId, sectorDetalle));
                    insertProd.Parameters.AddWithValue("@nombre", nombreProducto);
                    insertProd.Parameters.AddWithValue("@categoria", categoriaNueva);
                    productoId = Convert.ToInt64(await insertProd.ExecuteScalarAsync());
                }
            }

            long presentacionId;
            await using (var findPres = new MySqlCommand("""
                SELECT id
                FROM presentaciones
                WHERE producto_id = @producto_id
                  AND LOWER(TRIM(nombre)) = LOWER(TRIM(@nombre))
                ORDER BY id
                LIMIT 1;
            """, con, tx))
            {
                findPres.Parameters.AddWithValue("@producto_id", productoId);
                findPres.Parameters.AddWithValue("@nombre", nombrePresentacion);
                var found = await findPres.ExecuteScalarAsync();
                if (found != null)
                {
                    presentacionId = Convert.ToInt64(found);
                    await using var updatePres = new MySqlCommand("""
                        UPDATE presentaciones
                        SET cantidad_base = @cantidad_base,
                            precio_venta = @precio_venta,
                            estado = 'ACTIVO'
                        WHERE id = @id;
                    """, con, tx);
                    updatePres.Parameters.AddWithValue("@cantidad_base", cantidadBase);
                    updatePres.Parameters.AddWithValue("@precio_venta", d.PrecioUnitario);
                    updatePres.Parameters.AddWithValue("@id", presentacionId);
                    await updatePres.ExecuteNonQueryAsync();
                }
                else
                {
                    await using var insertPres = new MySqlCommand("""
                        INSERT INTO presentaciones (producto_id, nombre, cantidad_base, precio_venta, estado)
                        VALUES (@producto_id, @nombre, @cantidad_base, @precio_venta, 'ACTIVO');
                        SELECT LAST_INSERT_ID();
                    """, con, tx);
                    insertPres.Parameters.AddWithValue("@producto_id", productoId);
                    insertPres.Parameters.AddWithValue("@nombre", nombrePresentacion);
                    insertPres.Parameters.AddWithValue("@cantidad_base", cantidadBase);
                    insertPres.Parameters.AddWithValue("@precio_venta", d.PrecioUnitario);
                    presentacionId = Convert.ToInt64(await insertPres.ExecuteScalarAsync());
                }
            }

            const string detalleSql = """
                INSERT IGNORE INTO detalle_ventas
                (venta_id, sector, producto_id, presentacion_id, producto, presentacion, cantidad, precio_unitario, subtotal, line_key, consumption_key)
                VALUES
                (@venta_id, @sector, @producto_id, @presentacion_id, @producto, @presentacion, @cantidad, @precio_unitario, @subtotal, @line_key, NULLIF(@consumption_key,''));
            """;

            await using var detCmd = new MySqlCommand(detalleSql, con, tx);
            detCmd.Parameters.AddWithValue("@venta_id", ventaId);
            detCmd.Parameters.AddWithValue("@sector", sectorDetalle);
            detCmd.Parameters.AddWithValue("@producto_id", productoId);
            detCmd.Parameters.AddWithValue("@presentacion_id", presentacionId);
            detCmd.Parameters.AddWithValue("@producto", d.Producto);
            detCmd.Parameters.AddWithValue("@presentacion", d.Presentacion);
            detCmd.Parameters.AddWithValue("@cantidad", d.Cantidad);
            detCmd.Parameters.AddWithValue("@precio_unitario", d.PrecioUnitario);
            detCmd.Parameters.AddWithValue("@subtotal", d.Subtotal);
            detCmd.Parameters.AddWithValue("@line_key", lineKey);
            detCmd.Parameters.AddWithValue("@consumption_key", consumptionKey);
            int detailInserted = await detCmd.ExecuteNonQueryAsync();

            // V47: el inventario tiene su propio libro idempotente. Aun si por un error de red o
            // programación se intenta procesar otra vez la misma línea, movement_key solo puede
            // existir una vez y el stock NO se vuelve a descontar.
            if (!ventaYaExistia && detailInserted > 0 && !d.StockAlreadyDiscountedOnline)
            {
                int movementInserted;
                await using (var movementCmd = new MySqlCommand("""
                    INSERT IGNORE INTO stock_movimientos_venta
                    (movement_key, venta_id, sucursal_id, producto_id, presentacion_id, cantidad_base, fecha)
                    VALUES
                    (@movement_key, @venta_id, @sucursal_id, @producto_id, @presentacion_id, @cantidad_base, @fecha);
                """, con, tx))
                {
                    movementCmd.Parameters.AddWithValue("@movement_key", string.IsNullOrWhiteSpace(consumptionKey) ? lineKey : consumptionKey);
                    movementCmd.Parameters.AddWithValue("@venta_id", ventaId);
                    movementCmd.Parameters.AddWithValue("@sucursal_id", venta.SucursalId);
                    movementCmd.Parameters.AddWithValue("@producto_id", productoId);
                    movementCmd.Parameters.AddWithValue("@presentacion_id", presentacionId);
                    movementCmd.Parameters.AddWithValue("@cantidad_base", cantidadBase);
                    movementCmd.Parameters.AddWithValue("@fecha", venta.Fecha);
                    movementInserted = await movementCmd.ExecuteNonQueryAsync();
                }

                if (movementInserted > 0)
                {
                    if (CompositeInventoryDb.IsComposite(nombreProducto))
                    {
                        var reservaCompuesta = await CompositeInventoryDb.ReserveAsync(con, tx, venta.SucursalId, sectorDetalle, nombreProducto, d.Cantidad);
                        if (!reservaCompuesta.Ok)
                            throw new InvalidOperationException(reservaCompuesta.Error);
                    }
                    else
                    {
                        await using var stockCmd = new MySqlCommand("""
                            UPDATE productos
                            SET stock_actual = CASE
                                WHEN COALESCE(sin_limite_stock, 0) = 1
                                THEN stock_actual
                                ELSE stock_actual - @cantidad_base
                            END
                            WHERE id = @producto_id AND sector=@sector;
                        """, con, tx);
                        stockCmd.Parameters.AddWithValue("@cantidad_base", cantidadBase);
                        stockCmd.Parameters.AddWithValue("@producto_id", productoId);
                        stockCmd.Parameters.AddWithValue("@sector", NormalizarSectorProducto(venta.SucursalId, sectorDetalle));
                        await stockCmd.ExecuteNonQueryAsync();
                    }
                }
            }
        }

        // V45: libro contable inmutable dentro de la MISMA transacción que venta+detalle+stock.
        // Si este registro falla, se revierte toda la operación y no queda un cobro a medias.
        await using (var ledgerCmd = new MySqlCommand("""
            INSERT INTO libro_caja
            (venta_id, sucursal_id, cajero, fecha, tipo, metodo_pago, efectivo, qr, total, sync_key, operation_key, session_id, estado)
            VALUES
            (@venta_id, @sucursal_id, @cajero, @fecha, @tipo, @metodo_pago, @efectivo, @qr, @total, @sync_key, NULLIF(@operation_key,''), @session_id, 'CONFIRMADA');
        """, con, tx))
        {
            ledgerCmd.Parameters.AddWithValue("@venta_id", ventaId);
            ledgerCmd.Parameters.AddWithValue("@sucursal_id", venta.SucursalId);
            ledgerCmd.Parameters.AddWithValue("@cajero", venta.Cajero ?? "");
            ledgerCmd.Parameters.AddWithValue("@fecha", venta.Fecha);
            ledgerCmd.Parameters.AddWithValue("@tipo", venta.Tipo ?? "VENTA");
            ledgerCmd.Parameters.AddWithValue("@metodo_pago", venta.MetodoPago ?? "");
            ledgerCmd.Parameters.AddWithValue("@efectivo", Math.Max(0, venta.Efectivo));
            ledgerCmd.Parameters.AddWithValue("@qr", Math.Max(0, venta.Qr));
            ledgerCmd.Parameters.AddWithValue("@total", venta.Total);
            ledgerCmd.Parameters.AddWithValue("@sync_key", syncKey);
            ledgerCmd.Parameters.AddWithValue("@operation_key", operationKey);
            ledgerCmd.Parameters.AddWithValue("@session_id", venta.SessionId.HasValue ? venta.SessionId.Value : DBNull.Value);
            await ledgerCmd.ExecuteNonQueryAsync();
        }

        await using (var auditCmd = new MySqlCommand("""
            INSERT INTO auditoria_contable
            (fecha, usuario, sucursal_id, accion, entidad, entidad_id, detalle)
            VALUES (NOW(), @usuario, @sucursal_id, 'CONFIRMAR_COBRO', 'VENTA', @entidad_id, @detalle);
        """, con, tx))
        {
            auditCmd.Parameters.AddWithValue("@usuario", venta.Cajero ?? "");
            auditCmd.Parameters.AddWithValue("@sucursal_id", venta.SucursalId);
            auditCmd.Parameters.AddWithValue("@entidad_id", ventaId);
            auditCmd.Parameters.AddWithValue("@detalle", $"{venta.Tipo}|{venta.MetodoPago}|{venta.Total:0.00}|{syncKey}|{operationKey}");
            await auditCmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();

        await TrySyncSheets(db, sheets);

        return Results.Ok(new { ok = true, id = ventaId, syncKey, operationKey });
    }
    catch (Exception ex)
    {
        await tx.RollbackAsync();
        return Results.Problem("Error al guardar venta: " + ex.Message);
    }
});

app.MapGet("/api/ventas", async (Db db, int? sucursalId) =>
{
    await using var con = await db.OpenAsync();
    await EnsureVentaSyncProtection(con);
    try { await new MySqlCommand("ALTER TABLE ventas ADD COLUMN efectivo DECIMAL(10,2) NOT NULL DEFAULT 0;", con).ExecuteNonQueryAsync(); } catch { }
    try { await new MySqlCommand("ALTER TABLE ventas ADD COLUMN qr DECIMAL(10,2) NOT NULL DEFAULT 0;", con).ExecuteNonQueryAsync(); } catch { }

    const string sql = """
        SELECT v.id, v.sucursal_id, CASE WHEN s.id = 2 THEN 'EL BRUJO PREMIU' ELSE 'EL BRUJO' END AS sucursal, v.cajero,
               COALESCE(NULLIF(v.caja_nombre,''), NULLIF(u.caja_nombre,''), CASE WHEN v.sucursal_id=1 THEN 'CAJA ÚNICA' ELSE 'SIN CAJA' END) AS caja_nombre,
               COALESCE(NULLIF(v.turno,''), NULLIF(u.turno,''), CASE WHEN TIME(v.fecha) >= '08:00:00' AND TIME(v.fecha) < '20:00:00' THEN 'MAÑANA' ELSE 'NOCHE' END) AS turno,
               v.fecha, v.tipo, v.metodo_pago, COALESCE(v.efectivo,0) AS efectivo, COALESCE(v.qr,0) AS qr, v.total, v.sync_key, COALESCE(v.operation_key,'') AS operation_key, v.session_id
        FROM ventas_canonicas v
        INNER JOIN sucursales s ON s.id = v.sucursal_id
        LEFT JOIN usuarios u ON u.usuario=v.cajero AND u.sucursal_id=v.sucursal_id
        WHERE (@sucursalId IS NULL OR v.sucursal_id = @sucursalId)
        ORDER BY v.fecha DESC, v.id DESC
        LIMIT 10000;
    """;

    var rows = await db.QueryAsync(con, sql, new Dictionary<string, object?>
    {
        ["@sucursalId"] = sucursalId
    });

    return Results.Ok(rows);
});



app.MapGet("/api/detalle-ventas", async (Db db, int? sucursalId) =>
{
    await using var con = await db.OpenAsync();
    await EnsureVentaSyncProtection(con);

    string where = sucursalId.HasValue ? "WHERE v.sucursal_id = @sucursal_id" : "";

    string sql = $"""
        SELECT d.venta_id AS id_venta,
               CASE WHEN s.id = 2 THEN 'EL BRUJO PREMIU' ELSE 'EL BRUJO' END AS sucursal,
               v.cajero,
               COALESCE(NULLIF(d.sector,''), CASE WHEN v.sucursal_id=2 THEN 'ARRIBA' ELSE 'GENERAL' END) AS sector,
               d.producto,
               d.presentacion,
               d.cantidad,
               d.precio_unitario AS precio,
               d.subtotal,
               COALESCE(d.consumption_key,'') AS consumption_key
        FROM detalle_ventas_canonico d
        INNER JOIN ventas_canonicas v ON v.id = d.venta_id
        INNER JOIN sucursales s ON s.id = v.sucursal_id
        {where}
        ORDER BY d.venta_id DESC, d.id DESC;
    """;

    Dictionary<string, object?>? parameters = sucursalId.HasValue
        ? new Dictionary<string, object?> { ["@sucursal_id"] = sucursalId.Value }
        : null;

    return Results.Ok(await db.QueryAsync(con, sql, parameters));
});

app.MapGet("/api/admin/conciliacion", async (Db db, string? clave, int? sucursalId) =>
{
    if (clave != "ENTREGAR_LIMPIO_2026") return Results.Unauthorized();
    await using var con = await db.OpenAsync();
    await EnsureAccountingLedger(con);

    await EnsureVentaSyncProtection(con);
    const string sql = """
        SELECT
            COALESCE((SELECT SUM(total) FROM ventas_canonicas WHERE (@sucursalId IS NULL OR sucursal_id=@sucursalId)),0) AS ventas_total,
            COALESCE((SELECT SUM(l.total) FROM libro_caja l INNER JOIN ventas_canonicas v ON v.id=l.venta_id WHERE l.estado='CONFIRMADA' AND (@sucursalId IS NULL OR l.sucursal_id=@sucursalId)),0) AS libro_total,
            COALESCE((SELECT SUM(l.efectivo+l.qr+CASE WHEN UPPER(l.metodo_pago)='TRANSFERENCIA' THEN l.total ELSE 0 END) FROM libro_caja l INNER JOIN ventas_canonicas v ON v.id=l.venta_id WHERE l.estado='CONFIRMADA' AND (@sucursalId IS NULL OR l.sucursal_id=@sucursalId)),0) AS medios_total,
            COALESCE((SELECT COUNT(*) FROM ventas_canonicas WHERE (@sucursalId IS NULL OR sucursal_id=@sucursalId)),0) AS ventas_count,
            COALESCE((SELECT COUNT(*) FROM libro_caja l INNER JOIN ventas_canonicas v ON v.id=l.venta_id WHERE l.estado='CONFIRMADA' AND (@sucursalId IS NULL OR l.sucursal_id=@sucursalId)),0) AS libro_count,
            COALESCE((SELECT COUNT(*) FROM ventas WHERE (@sucursalId IS NULL OR sucursal_id=@sucursalId)),0) AS ventas_raw_count;
    """;
    await using var cmd = new MySqlCommand(sql, con);
    cmd.Parameters.AddWithValue("@sucursalId", sucursalId.HasValue ? sucursalId.Value : DBNull.Value);
    await using var rd = await cmd.ExecuteReaderAsync();
    await rd.ReadAsync();
    decimal ventasTotal = rd.GetDecimal(0);
    decimal libroTotal = rd.GetDecimal(1);
    decimal mediosTotal = rd.GetDecimal(2);
    long ventasCount = rd.GetInt64(3);
    long libroCount = rd.GetInt64(4);
    long ventasRawCount = rd.GetInt64(5);
    long duplicadosHistoricosIgnorados = Math.Max(0, ventasRawCount - ventasCount);
    bool cuadra = Math.Abs(ventasTotal-libroTotal) < 0.01m && Math.Abs(libroTotal-mediosTotal) < 0.01m && ventasCount == libroCount;
    return Results.Ok(new { ok=true, cuadra, ventasTotal, libroTotal, mediosTotal, ventasCount, libroCount, ventasRawCount, duplicadosHistoricosIgnorados, diferenciaVentasLibro = ventasTotal-libroTotal, diferenciaLibroMedios = libroTotal-mediosTotal });
});

app.MapPost("/api/cobros-mesa", async (Db db, SheetsReporter sheets, CobroMesaRequest c) =>
{
    await using var con = await db.OpenAsync();

    await using (var create = new MySqlCommand("""
        CREATE TABLE IF NOT EXISTS cobros_mesa (
            id BIGINT AUTO_INCREMENT PRIMARY KEY,
            sucursal_id INT NOT NULL,
            session_id INT NULL,
            mesa_id INT NULL,
            mesa VARCHAR(100) NOT NULL,
            caja_nombre VARCHAR(100) NOT NULL DEFAULT '',
            sector VARCHAR(20) NOT NULL DEFAULT 'GENERAL',
            cajero VARCHAR(100) NOT NULL,
            mesera VARCHAR(150) NULL,
            fecha DATETIME NOT NULL,
            tiempo VARCHAR(50) NULL,
            total_mesa DECIMAL(10,2) NOT NULL DEFAULT 0,
            total_consumo DECIMAL(10,2) NOT NULL DEFAULT 0,
            total_cobrado DECIMAL(10,2) NOT NULL DEFAULT 0,
            metodo_pago VARCHAR(50) NOT NULL,
            sync_key VARCHAR(180) NOT NULL UNIQUE,
            UNIQUE KEY uk_cobro_sesion_sector (sucursal_id, sector, session_id)
        );
    """, con))
    {
        await create.ExecuteNonQueryAsync();
    }

    // V49: una sesión solo puede tener un cobro de mesa confirmado.
    try { await new MySqlCommand("ALTER TABLE cobros_mesa ADD UNIQUE KEY uk_cobro_sesion_sector (sucursal_id, sector, session_id);", con).ExecuteNonQueryAsync(); } catch { }

    // V37: el detalle de tiempo ahora incluye modalidad, tiempo real, horas cobradas y tarifa.
    try
    {
        await using var widen = new MySqlCommand("ALTER TABLE cobros_mesa MODIFY COLUMN tiempo VARCHAR(220) NULL;", con);
        await widen.ExecuteNonQueryAsync();
    }
    catch { }

    string syncKey = string.IsNullOrWhiteSpace(c.SyncKey) ? Guid.NewGuid().ToString("N") : c.SyncKey;
    string cobroSector = NormalizarSector(c.SucursalId, c.Sector, c.CajaNombre);

    // V49: aunque otra PC o un reintento cambie sync_key, la misma SessionId no puede cobrarse dos veces.
    if (c.SessionId.HasValue && c.SessionId.Value > 0)
    {
        await using var sameSession = new MySqlCommand("""
            SELECT id, COALESCE(mesa_id,0), total_mesa, total_consumo, total_cobrado, metodo_pago, sync_key
            FROM cobros_mesa
            WHERE sucursal_id=@sucursal_id AND sector=@sector AND session_id=@session_id
            ORDER BY id LIMIT 1;
        """, con);
        sameSession.Parameters.AddWithValue("@sucursal_id", c.SucursalId);
        sameSession.Parameters.AddWithValue("@sector", cobroSector);
        sameSession.Parameters.AddWithValue("@session_id", c.SessionId.Value);
        await using var rdSession = await sameSession.ExecuteReaderAsync();
        if (await rdSession.ReadAsync())
        {
            long idExistente = rdSession.GetInt64(0);
            bool mismo = rdSession.GetInt32(1) == (c.MesaId ?? 0)
                && Math.Abs(rdSession.GetDecimal(2) - c.TotalMesa) < 0.01m
                && Math.Abs(rdSession.GetDecimal(3) - c.TotalConsumo) < 0.01m
                && Math.Abs(rdSession.GetDecimal(4) - c.TotalCobrado) < 0.01m
                && string.Equals(rdSession.GetString(5), c.MetodoPago ?? "", StringComparison.OrdinalIgnoreCase);
            string oldSync = rdSession.IsDBNull(6) ? "" : rdSession.GetString(6);
            if (!mismo)
                return Results.Conflict(new { ok = false, message = "Esta sesión ya fue cobrada con datos distintos. Se bloqueó para evitar doble cobro o alteración.", sessionId = c.SessionId, id = idExistente });
            return Results.Ok(new { ok = true, syncKey = oldSync, id = idExistente, idempotent = true, sameSession = true });
        }
    }

    // V42: cobro de mesa idempotente e inmutable por sync_key.
    await using (var existing = new MySqlCommand("""
        SELECT id, sucursal_id, COALESCE(session_id,0), COALESCE(mesa_id,0), total_mesa, total_consumo, total_cobrado, metodo_pago
        FROM cobros_mesa WHERE sync_key = @sync_key LIMIT 1;
    """, con))
    {
        existing.Parameters.AddWithValue("@sync_key", syncKey);
        await using var rd = await existing.ExecuteReaderAsync();
        if (await rd.ReadAsync())
        {
            bool mismo = rd.GetInt32(1) == c.SucursalId
                && rd.GetInt32(2) == (c.SessionId ?? 0)
                && rd.GetInt32(3) == (c.MesaId ?? 0)
                && Math.Abs(rd.GetDecimal(4) - c.TotalMesa) < 0.01m
                && Math.Abs(rd.GetDecimal(5) - c.TotalConsumo) < 0.01m
                && Math.Abs(rd.GetDecimal(6) - c.TotalCobrado) < 0.01m
                && string.Equals(rd.GetString(7), c.MetodoPago ?? "", StringComparison.OrdinalIgnoreCase);
            long idExistente = rd.GetInt64(0);
            if (!mismo)
                return Results.Conflict(new { ok = false, message = "La misma sync_key de cobro ya existe con datos distintos. Se bloqueó para evitar doble cobro.", syncKey, id = idExistente });
            return Results.Ok(new { ok = true, syncKey, id = idExistente, idempotent = true });
        }
    }

    if (c.TotalMesa < 0 || c.TotalConsumo < 0 || c.TotalCobrado < 0)
        return Results.BadRequest(new { ok = false, message = "Los totales del cobro no pueden ser negativos." });

    decimal esperadoCobro = Math.Round(c.TotalMesa + c.TotalConsumo, 2);
    if (Math.Abs(Math.Round(c.TotalCobrado, 2) - esperadoCobro) > 0.51m)
        return Results.BadRequest(new { ok = false, message = "El total cobrado no coincide con mesa + consumo. Se bloqueó para evitar descuadre.", esperado = esperadoCobro, recibido = c.TotalCobrado });

    const string sql = """
        INSERT INTO cobros_mesa
        (sucursal_id, session_id, mesa_id, mesa, caja_nombre, sector, cajero, mesera, fecha, tiempo, total_mesa, total_consumo, total_cobrado, metodo_pago, sync_key)
        VALUES
        (@sucursal_id, @session_id, @mesa_id, @mesa, @caja_nombre, @sector, @cajero, @mesera, @fecha, @tiempo, @total_mesa, @total_consumo, @total_cobrado, @metodo_pago, @sync_key)
        ON DUPLICATE KEY UPDATE
            sync_key = VALUES(sync_key);
    """;

    await using var cmd = new MySqlCommand(sql, con);
    cmd.Parameters.AddWithValue("@sucursal_id", c.SucursalId);
    cmd.Parameters.AddWithValue("@session_id", c.SessionId.HasValue ? c.SessionId.Value : DBNull.Value);
    cmd.Parameters.AddWithValue("@mesa_id", c.MesaId.HasValue ? c.MesaId.Value : DBNull.Value);
    cmd.Parameters.AddWithValue("@mesa", c.Mesa ?? "");
    cmd.Parameters.AddWithValue("@caja_nombre", c.CajaNombre ?? "");
    cmd.Parameters.AddWithValue("@sector", cobroSector);
    cmd.Parameters.AddWithValue("@cajero", c.Cajero ?? "");
    cmd.Parameters.AddWithValue("@mesera", c.Mesera ?? "");
    cmd.Parameters.AddWithValue("@fecha", c.Fecha);
    cmd.Parameters.AddWithValue("@tiempo", c.Tiempo ?? "");
    cmd.Parameters.AddWithValue("@total_mesa", c.TotalMesa);
    cmd.Parameters.AddWithValue("@total_consumo", c.TotalConsumo);
    cmd.Parameters.AddWithValue("@total_cobrado", c.TotalCobrado);
    cmd.Parameters.AddWithValue("@metodo_pago", c.MetodoPago ?? "");
    cmd.Parameters.AddWithValue("@sync_key", syncKey);

    await cmd.ExecuteNonQueryAsync();

    await TrySyncSheets(db, sheets);

    return Results.Ok(new { ok = true, syncKey });
});


app.MapGet("/api/cobros-mesa", async (Db db, int? sucursalId) =>
{
    await using var con = await db.OpenAsync();

    await using (var create = new MySqlCommand("""
        CREATE TABLE IF NOT EXISTS cobros_mesa (
            id BIGINT AUTO_INCREMENT PRIMARY KEY,
            sucursal_id INT NOT NULL,
            session_id INT NULL,
            mesa_id INT NULL,
            mesa VARCHAR(100) NOT NULL,
            caja_nombre VARCHAR(100) NOT NULL DEFAULT '',
            sector VARCHAR(20) NOT NULL DEFAULT 'GENERAL',
            cajero VARCHAR(100) NOT NULL,
            mesera VARCHAR(150) NULL,
            fecha DATETIME NOT NULL,
            tiempo VARCHAR(50) NULL,
            total_mesa DECIMAL(10,2) NOT NULL DEFAULT 0,
            total_consumo DECIMAL(10,2) NOT NULL DEFAULT 0,
            total_cobrado DECIMAL(10,2) NOT NULL DEFAULT 0,
            metodo_pago VARCHAR(50) NOT NULL,
            sync_key VARCHAR(180) NOT NULL UNIQUE,
            UNIQUE KEY uk_cobro_sesion_sector (sucursal_id, sector, session_id)
        );
    """, con))
    {
        await create.ExecuteNonQueryAsync();
    }

    try
    {
        await using var widen = new MySqlCommand("ALTER TABLE cobros_mesa MODIFY COLUMN tiempo VARCHAR(220) NULL;", con);
        await widen.ExecuteNonQueryAsync();
    }
    catch { }

    string where = sucursalId.HasValue ? "WHERE c.sucursal_id = @sucursal_id" : "";

    string sql = $"""
        SELECT c.id, c.session_id, DATE(c.fecha) AS fecha, TIME(c.fecha) AS hora,
               CASE WHEN s.id = 2 THEN 'EL BRUJO PREMIU' ELSE 'EL BRUJO' END AS sucursal,
               c.mesa, c.caja_nombre, c.sector, c.cajero, c.mesera, c.tiempo,
               c.total_mesa, c.total_consumo, c.total_cobrado, c.metodo_pago
        FROM cobros_mesa c
        INNER JOIN sucursales s ON s.id = c.sucursal_id
        {where}
        ORDER BY c.fecha DESC, c.id DESC;
    """;

    Dictionary<string, object?>? parameters = sucursalId.HasValue
        ? new Dictionary<string, object?> { ["@sucursal_id"] = sucursalId.Value }
        : null;

    return Results.Ok(await db.QueryAsync(con, sql, parameters));
});


app.MapPost("/api/mesas/estado", async (Db db, SheetsReporter sheets, MesaEstadoRequest m) =>
{
    await using var con = await db.OpenAsync();
    await EnsureMesasEnVivoTables(con);

    string sector = NormalizarSector(m.SucursalId, m.Sector);
    var layoutActual = await EnsureTableLayoutSettingsAsync(con);
    int maxMesa = m.SucursalId == 2
        ? (sector == "ABAJO" ? layoutActual.premiumAbajo : layoutActual.premiumArriba)
        : layoutActual.brujo;
    if (m.MesaId <= 0 || m.MesaId > maxMesa)
        return Results.BadRequest(new { ok=false, message="Mesa fuera del rango oficial para esta caja/sector.", sector, mesaId=m.MesaId, maxMesa });

    string syncKey = string.IsNullOrWhiteSpace(m.SyncKey)
        ? $"MESA-{m.SucursalId}-{sector}-{m.MesaId}"
        : m.SyncKey;

    const string sql = """
        INSERT INTO mesa_estados
        (sucursal_id, sector, mesa_id, mesa, estado, cajero, inicio, fin_programado, minutos, tarifa_hora, total_mesa, total_consumo, total_general, cliente_reserva, actualizado, sync_key)
        VALUES
        (@sucursal_id, @sector, @mesa_id, @mesa, @estado, @cajero, @inicio, @fin_programado, @minutos, @tarifa_hora, @total_mesa, @total_consumo, @total_general, @cliente_reserva, NOW(), @sync_key)
        ON DUPLICATE KEY UPDATE
            mesa = VALUES(mesa),
            estado = VALUES(estado),
            cajero = VALUES(cajero),
            inicio = VALUES(inicio),
            fin_programado = VALUES(fin_programado),
            minutos = VALUES(minutos),
            tarifa_hora = VALUES(tarifa_hora),
            total_mesa = VALUES(total_mesa),
            total_consumo = VALUES(total_consumo),
            total_general = VALUES(total_general),
            cliente_reserva = VALUES(cliente_reserva),
            actualizado = NOW(),
            sync_key = VALUES(sync_key);
    """;

    await using var cmd = new MySqlCommand(sql, con);
    cmd.Parameters.AddWithValue("@sucursal_id", m.SucursalId);
    cmd.Parameters.AddWithValue("@sector", sector);
    cmd.Parameters.AddWithValue("@mesa_id", m.MesaId);
    cmd.Parameters.AddWithValue("@mesa", m.Mesa ?? ("Mesa " + m.MesaId));
    cmd.Parameters.AddWithValue("@estado", m.Estado ?? "LIBRE");
    cmd.Parameters.AddWithValue("@cajero", m.Cajero ?? "");
    cmd.Parameters.AddWithValue("@inicio", m.Inicio.HasValue ? m.Inicio.Value : DBNull.Value);
    cmd.Parameters.AddWithValue("@fin_programado", m.FinProgramado.HasValue ? m.FinProgramado.Value : DBNull.Value);
    cmd.Parameters.AddWithValue("@minutos", m.Minutos);
    decimal tarifaVigente = m.TarifaHora > 0 ? m.TarifaHora : await EnsureGlobalTableRateAsync(con);
    cmd.Parameters.AddWithValue("@tarifa_hora", tarifaVigente);
    cmd.Parameters.AddWithValue("@total_mesa", m.TotalMesa);
    cmd.Parameters.AddWithValue("@total_consumo", m.TotalConsumo);
    cmd.Parameters.AddWithValue("@total_general", m.TotalGeneral);
    cmd.Parameters.AddWithValue("@cliente_reserva", m.ClienteReserva ?? "");
    cmd.Parameters.AddWithValue("@sync_key", syncKey);
    await cmd.ExecuteNonQueryAsync();

    await using (var del = new MySqlCommand("DELETE FROM mesa_consumos_vivos WHERE sucursal_id=@sucursal_id AND sector=@sector AND mesa_id=@mesa_id;", con))
    {
        del.Parameters.AddWithValue("@sucursal_id", m.SucursalId);
        del.Parameters.AddWithValue("@sector", sector);
        del.Parameters.AddWithValue("@mesa_id", m.MesaId);
        await del.ExecuteNonQueryAsync();
    }

    foreach (var d in m.Detalle ?? new List<MesaConsumoVivoRequest>())
    {
        await using var det = new MySqlCommand("""
            INSERT INTO mesa_consumos_vivos
            (sucursal_id, mesa_id, sector, producto, presentacion, cantidad, precio_unitario, subtotal, mobile_order_id, stock_already_discounted_online, consumption_key, actualizado)
            VALUES
            (@sucursal_id, @mesa_id, @sector, @producto, @presentacion, @cantidad, @precio_unitario, @subtotal, @mobile_order_id, @stock_already_discounted_online, NULLIF(@consumption_key,''), NOW());
        """, con);
        det.Parameters.AddWithValue("@sucursal_id", m.SucursalId);
        det.Parameters.AddWithValue("@mesa_id", m.MesaId);
        det.Parameters.AddWithValue("@sector", sector);
        det.Parameters.AddWithValue("@producto", d.Producto ?? "");
        det.Parameters.AddWithValue("@presentacion", d.Presentacion ?? "");
        det.Parameters.AddWithValue("@cantidad", d.Cantidad);
        det.Parameters.AddWithValue("@precio_unitario", d.PrecioUnitario);
        det.Parameters.AddWithValue("@subtotal", d.Subtotal);
        det.Parameters.AddWithValue("@mobile_order_id", Math.Max(0, d.MobileOrderId));
        det.Parameters.AddWithValue("@stock_already_discounted_online", d.StockAlreadyDiscountedOnline ? 1 : 0);
        det.Parameters.AddWithValue("@consumption_key", (d.ConsumptionKey ?? "").Trim());
        await det.ExecuteNonQueryAsync();
    }

    return Results.Ok(new { ok = true, syncKey, sector });
});

app.MapGet("/api/mesas/estado", async (Db db, int? sucursalId, string? sector) =>
{
    await using var con = await db.OpenAsync();
    await EnsureMesasEnVivoTables(con);

    int? sid = sucursalId.HasValue ? (sucursalId.Value == 2 ? 2 : 1) : null;
    string? sectorFiltro = sid.HasValue ? NormalizarSector(sid.Value, sector) : null;
    string where = sid.HasValue ? "WHERE e.sucursal_id=@sucursal_id AND e.sector=@sector" : "";

    string sql = $"""
        SELECT e.sucursal_id,
               CASE WHEN e.sucursal_id = 2 THEN 'EL BRUJO PREMIU' ELSE 'EL BRUJO' END AS sucursal,
               e.sector, e.mesa_id, e.mesa, e.estado, e.cajero, e.inicio, e.fin_programado,
               e.minutos, e.tarifa_hora, e.total_mesa, e.total_consumo, e.total_general,
               e.cliente_reserva, e.actualizado
        FROM mesa_estados e
        {where}
        ORDER BY e.sucursal_id, e.sector, e.mesa_id;
    """;

    Dictionary<string, object?>? parameters = sid.HasValue
        ? new Dictionary<string, object?> { ["@sucursal_id"] = sid.Value, ["@sector"] = sectorFiltro! }
        : null;
    return Results.Ok(await db.QueryAsync(con, sql, parameters));
});

app.MapGet("/api/mesas/consumos-vivos", async (Db db, int? sucursalId, string? sector) =>
{
    await using var con = await db.OpenAsync();
    await EnsureMesasEnVivoTables(con);

    int? sid = sucursalId.HasValue ? (sucursalId.Value == 2 ? 2 : 1) : null;
    string? sectorFiltro = sid.HasValue ? NormalizarSector(sid.Value, sector) : null;
    string where = sid.HasValue ? "WHERE c.sucursal_id=@sucursal_id AND c.sector=@sector" : "";

    string sql = $"""
        SELECT c.sucursal_id,
               CASE WHEN c.sucursal_id = 2 THEN 'EL BRUJO PREMIU' ELSE 'EL BRUJO' END AS sucursal,
               c.mesa_id, c.sector, c.producto, c.presentacion, c.cantidad, c.precio_unitario, c.subtotal,
               c.mobile_order_id, c.stock_already_discounted_online, COALESCE(c.consumption_key,'') AS consumption_key
        FROM mesa_consumos_vivos c
        {where}
        ORDER BY c.sucursal_id, c.sector, c.mesa_id, c.id;
    """;

    Dictionary<string, object?>? parameters = sid.HasValue
        ? new Dictionary<string, object?> { ["@sucursal_id"] = sid.Value, ["@sector"] = sectorFiltro! }
        : null;
    return Results.Ok(await db.QueryAsync(con, sql, parameters));
});

app.MapPost("/api/reservas", async (Db db, SheetsReporter sheets, ReservaRequest r) =>
{
    await using var con = await db.OpenAsync();
    string syncKey = string.IsNullOrWhiteSpace(r.SyncKey) ? Guid.NewGuid().ToString("N") : r.SyncKey;

    const string sql = """
        INSERT INTO reservas
        (sucursal_id, mesa_id, cliente, celular, fecha_reserva, minutos, estado, cajero, sync_key)
        VALUES
        (@sucursal_id, @mesa_id, @cliente, @celular, @fecha_reserva, @minutos, @estado, @cajero, @sync_key)
        ON DUPLICATE KEY UPDATE
            cliente = VALUES(cliente),
            celular = VALUES(celular),
            fecha_reserva = VALUES(fecha_reserva),
            minutos = VALUES(minutos),
            estado = VALUES(estado);
    """;

    await using var cmd = new MySqlCommand(sql, con);
    cmd.Parameters.AddWithValue("@sucursal_id", r.SucursalId);
    cmd.Parameters.AddWithValue("@mesa_id", r.MesaId);
    cmd.Parameters.AddWithValue("@cliente", r.Cliente);
    cmd.Parameters.AddWithValue("@celular", r.Celular ?? "");
    cmd.Parameters.AddWithValue("@fecha_reserva", r.FechaReserva);
    cmd.Parameters.AddWithValue("@minutos", r.Minutos);
    cmd.Parameters.AddWithValue("@estado", r.Estado);
    cmd.Parameters.AddWithValue("@cajero", r.Cajero ?? "");
    cmd.Parameters.AddWithValue("@sync_key", syncKey);

    await cmd.ExecuteNonQueryAsync();

    await TrySyncSheets(db, sheets);

    return Results.Ok(new { ok = true, syncKey });
});

app.MapPost("/api/propinas", async (Db db, SheetsReporter sheets, PropinaRequest p) =>
{
    await using var con = await db.OpenAsync();
    string syncKey = string.IsNullOrWhiteSpace(p.SyncKey) ? Guid.NewGuid().ToString("N") : p.SyncKey;

    const string sql = """
        INSERT INTO propinas
        (sucursal_id, mesa_id, mesera, cajero, fecha, monto, sync_key)
        VALUES
        (@sucursal_id, @mesa_id, @mesera, @cajero, @fecha, @monto, @sync_key)
        ON DUPLICATE KEY UPDATE
            monto = VALUES(monto);
    """;

    await using var cmd = new MySqlCommand(sql, con);
    cmd.Parameters.AddWithValue("@sucursal_id", p.SucursalId);
    cmd.Parameters.AddWithValue("@mesa_id", p.MesaId.HasValue ? p.MesaId.Value : DBNull.Value);
    cmd.Parameters.AddWithValue("@mesera", p.Mesera);
    cmd.Parameters.AddWithValue("@cajero", p.Cajero);
    cmd.Parameters.AddWithValue("@fecha", p.Fecha);
    cmd.Parameters.AddWithValue("@monto", p.Monto);
    cmd.Parameters.AddWithValue("@sync_key", syncKey);

    await cmd.ExecuteNonQueryAsync();

    await TrySyncSheets(db, sheets);

    return Results.Ok(new { ok = true, syncKey });
});


// V35: cierre de turno/arqueo inmutable.
// La caja envía una fotografía completa del turno; el Administrador la consulta después.
app.MapPost("/api/cierres-turno", async (Db db, SheetsReporter sheets, ShiftCloseRequest r) =>
{
    await using var con = await db.OpenAsync();
    await EnsureShiftCloseTables(con);

    // V87: aun si llega un cliente antiguo sin sync_key, la identidad de respaldo incluye CAJA.
    // De este modo ARRIBA y ABAJO nunca chocan por compartir usuario/turno/fecha.
    string syncKey = string.IsNullOrWhiteSpace(r.SyncKey)
        ? "CIERRE-" + r.SucursalId + "-" + (r.Caja ?? "").Trim().ToUpperInvariant().Replace(" ", "") + "-" + (r.CajeroUsuario ?? "") + "-" + r.Inicio.Ticks
        : r.SyncKey.Trim();

    // V86: identidad NATURAL del cierre. Aunque una PC reintente con otra sync_key,
    // el mismo cajero/caja/turno/ventana solo puede tener un cierre en Railway.
    string closeIdentity = string.Join("|", new[]
    {
        r.SucursalId.ToString(),
        (r.CajeroUsuario ?? "").Trim().ToUpperInvariant(),
        (r.Caja ?? "").Trim().ToUpperInvariant(),
        NormalizarTurno(r.Turno),
        r.Inicio.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture)
    });
    string cierreKey = "NAT-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(closeIdentity))).ToLowerInvariant();

    // V56: el servidor vuelve a calcular el dinero del arqueo usando el libro canónico de VENTAS.
    // Así un cierre local desactualizado no puede dejar Bs. 0 si Railway ya tiene ventas confirmadas.
    int canonicalTransactions = Math.Max(0, r.TransaccionesTotal);
    int canonicalCashTransactions = Math.Max(0, r.TransaccionesEfectivo);
    int canonicalQrTransactions = Math.Max(0, r.TransaccionesQr);
    int canonicalTransferTransactions = Math.Max(0, r.TransaccionesTransferencia);
    decimal canonicalCash = Math.Max(0, r.Efectivo);
    decimal canonicalQr = Math.Max(0, r.Qr);
    decimal canonicalTransfer = Math.Max(0, r.Transferencia);
    decimal canonicalProduct = Math.Max(0, r.ProductosTotal);
    decimal canonicalTable = Math.Max(0, r.MesasTotal);
    decimal canonicalTotal = Math.Max(0, r.TotalGenerado);
    bool reconciledFromSales = false;

    try
    {
        await EnsureVentaSyncProtection(con);
        await using var reconcile = new MySqlCommand("""
            SELECT COUNT(*) AS operaciones,
                   COALESCE(SUM(CASE WHEN efectivo > 0 THEN 1 ELSE 0 END),0) AS ops_efectivo,
                   COALESCE(SUM(CASE WHEN qr > 0 THEN 1 ELSE 0 END),0) AS ops_qr,
                   COALESCE(SUM(CASE WHEN UPPER(metodo_pago)='TRANSFERENCIA' THEN 1 ELSE 0 END),0) AS ops_transferencia,
                   COALESCE(SUM(efectivo),0) AS efectivo,
                   COALESCE(SUM(qr),0) AS qr,
                   COALESCE(SUM(CASE WHEN UPPER(v.metodo_pago)='TRANSFERENCIA' THEN v.total ELSE 0 END),0) AS transferencia,
                   COALESCE(SUM(CASE
                       WHEN UPPER(v.tipo) IN ('DIRECTA','CONSUMO_MESA') THEN v.total
                       WHEN UPPER(v.tipo)='MESA' THEN LEAST(v.total, COALESCE(dt.detalle_total,0))
                       ELSE 0 END),0) AS productos_total,
                   COALESCE(SUM(CASE
                       WHEN UPPER(v.tipo)='MESA' THEN GREATEST(v.total - LEAST(v.total, COALESCE(dt.detalle_total,0)),0)
                       ELSE 0 END),0) AS mesas_total,
                   COALESCE(SUM(v.total),0) AS total
            FROM ventas_canonicas v
            LEFT JOIN usuarios u ON u.usuario = v.cajero AND u.sucursal_id = v.sucursal_id
            LEFT JOIN (
                SELECT venta_id, SUM(subtotal) AS detalle_total
                FROM detalle_ventas_canonico
                GROUP BY venta_id
            ) dt ON dt.venta_id = v.id
            WHERE v.sucursal_id = @sucursal_id
              AND v.cajero = @cajero
              AND UPPER(TRIM(CASE
                    WHEN v.sucursal_id=1 THEN 'CAJA ÚNICA'
                    ELSE COALESCE(NULLIF(v.caja_nombre,''), NULLIF(u.caja_nombre,''), '')
                  END)) = UPPER(TRIM(@caja))
              AND UPPER(COALESCE(NULLIF(v.turno,''), CASE
                    WHEN TIME(v.fecha) >= '08:00:00' AND TIME(v.fecha) < '20:00:00' THEN 'MAÑANA'
                    ELSE 'NOCHE' END)) = @turno
              AND v.fecha >= @inicio AND v.fecha < @fin;
        """, con);
        reconcile.Parameters.AddWithValue("@sucursal_id", r.SucursalId);
        reconcile.Parameters.AddWithValue("@cajero", r.CajeroUsuario ?? "");
        reconcile.Parameters.AddWithValue("@caja", r.SucursalId == 1 ? "CAJA ÚNICA" : (r.Caja ?? ""));
        reconcile.Parameters.AddWithValue("@turno", NormalizarTurno(r.Turno));
        reconcile.Parameters.AddWithValue("@inicio", r.Inicio);
        reconcile.Parameters.AddWithValue("@fin", r.Fin);
        await using var rr = await reconcile.ExecuteReaderAsync();
        if (await rr.ReadAsync())
        {
            int count = Convert.ToInt32(rr["operaciones"] ?? 0);
            if (count > 0)
            {
                canonicalTransactions = count;
                canonicalCashTransactions = Convert.ToInt32(rr["ops_efectivo"] ?? 0);
                canonicalQrTransactions = Convert.ToInt32(rr["ops_qr"] ?? 0);
                canonicalTransferTransactions = Convert.ToInt32(rr["ops_transferencia"] ?? 0);
                canonicalCash = Convert.ToDecimal(rr["efectivo"] ?? 0m);
                canonicalQr = Convert.ToDecimal(rr["qr"] ?? 0m);
                canonicalTransfer = Convert.ToDecimal(rr["transferencia"] ?? 0m);
                canonicalProduct = Convert.ToDecimal(rr["productos_total"] ?? 0m);
                canonicalTable = Convert.ToDecimal(rr["mesas_total"] ?? 0m);
                canonicalTotal = Convert.ToDecimal(rr["total"] ?? 0m);
                reconciledFromSales = true;
            }
        }
    }
    catch { /* si aún no llegaron ventas al servidor, se conserva la fotografía enviada por la caja */ }

    // V86: si este cierre ya existe por identidad natural, adoptar su sync_key ANTES del upsert.
    // Esto cubre datos históricos y evita que dos claves distintas del mismo turno creen filas separadas.
    try
    {
        await using var existingClose = new MySqlCommand("""
            SELECT id, sync_key, cierre_key
            FROM cierres_turno
            WHERE sucursal_id=@sid
              AND UPPER(TRIM(cajero_usuario))=UPPER(TRIM(@cajero))
              AND UPPER(TRIM(COALESCE(caja,'')))=UPPER(TRIM(@caja))
              AND UPPER(TRIM(turno))=UPPER(TRIM(@turno))
              AND inicio=@inicio
            ORDER BY id
            LIMIT 1;
        """, con);
        existingClose.Parameters.AddWithValue("@sid", r.SucursalId);
        existingClose.Parameters.AddWithValue("@cajero", r.CajeroUsuario ?? "");
        existingClose.Parameters.AddWithValue("@caja", r.Caja ?? "");
        existingClose.Parameters.AddWithValue("@turno", NormalizarTurno(r.Turno));
        existingClose.Parameters.AddWithValue("@inicio", r.Inicio);
        long existingId = 0;
        string existingKey = "";
        string existingNatural = "";
        await using (var er = await existingClose.ExecuteReaderAsync())
        {
            if (await er.ReadAsync())
            {
                existingId = er.GetInt64(0);
                existingKey = er.IsDBNull(1) ? "" : er.GetString(1);
                existingNatural = er.IsDBNull(2) ? "" : er.GetString(2);
            }
        }
        if (existingId > 0)
        {
            if (!string.IsNullOrWhiteSpace(existingKey)) syncKey = existingKey;
            if (string.IsNullOrWhiteSpace(existingNatural))
            {
                try
                {
                    await using var markNatural = new MySqlCommand("UPDATE cierres_turno SET cierre_key=@k WHERE id=@id AND (cierre_key IS NULL OR TRIM(cierre_key)='');", con);
                    markNatural.Parameters.AddWithValue("@k", cierreKey);
                    markNatural.Parameters.AddWithValue("@id", existingId);
                    await markNatural.ExecuteNonQueryAsync();
                }
                catch { }
            }
        }
    }
    catch { }

    const string sql = """
        INSERT INTO cierres_turno
        (
            sucursal_id, sucursal, cajero_usuario, cajero_nombre, caja, turno,
            inicio, fin, hora_entrada, fecha_cierre,
            transacciones_total, transacciones_efectivo, transacciones_qr,
            transacciones_tarjeta, transacciones_transferencia,
            efectivo, qr, tarjeta, transferencia, sin_metodo,
            productos_total, mesas_total, minutos_jugados, propinas_total,
            cortesias_valor, comisiones_total, gastos_total, perdidas_total,
            total_generado, neto_turno, observaciones, detalle_json, estado, cierre_key, sync_key
        )
        VALUES
        (
            @sucursal_id, @sucursal, @cajero_usuario, @cajero_nombre, @caja, @turno,
            @inicio, @fin, @hora_entrada, @fecha_cierre,
            @transacciones_total, @transacciones_efectivo, @transacciones_qr,
            @transacciones_tarjeta, @transacciones_transferencia,
            @efectivo, @qr, @tarjeta, @transferencia, @sin_metodo,
            @productos_total, @mesas_total, @minutos_jugados, @propinas_total,
            @cortesias_valor, @comisiones_total, @gastos_total, @perdidas_total,
            @total_generado, @neto_turno, @observaciones, @detalle_json, 'CERRADO', @cierre_key, @sync_key
        )
        ON DUPLICATE KEY UPDATE
            fecha_cierre = VALUES(fecha_cierre),
            transacciones_total = VALUES(transacciones_total),
            transacciones_efectivo = VALUES(transacciones_efectivo),
            transacciones_qr = VALUES(transacciones_qr),
            transacciones_tarjeta = VALUES(transacciones_tarjeta),
            transacciones_transferencia = VALUES(transacciones_transferencia),
            efectivo = VALUES(efectivo),
            qr = VALUES(qr),
            tarjeta = VALUES(tarjeta),
            transferencia = VALUES(transferencia),
            sin_metodo = VALUES(sin_metodo),
            productos_total = VALUES(productos_total),
            mesas_total = VALUES(mesas_total),
            minutos_jugados = VALUES(minutos_jugados),
            propinas_total = VALUES(propinas_total),
            cortesias_valor = VALUES(cortesias_valor),
            comisiones_total = VALUES(comisiones_total),
            gastos_total = VALUES(gastos_total),
            perdidas_total = VALUES(perdidas_total),
            total_generado = VALUES(total_generado),
            neto_turno = VALUES(neto_turno),
            observaciones = VALUES(observaciones),
            detalle_json = VALUES(detalle_json),
            estado = CASE
                WHEN cierres_turno.estado='REABIERTO'
                     AND cierres_turno.ultima_reapertura IS NOT NULL
                     AND VALUES(fecha_cierre) <= cierres_turno.ultima_reapertura
                THEN 'REABIERTO'
                ELSE 'CERRADO'
            END,
            cierre_key = VALUES(cierre_key);
    """;

    await using (var cmd = new MySqlCommand(sql, con))
    {
        cmd.Parameters.AddWithValue("@sucursal_id", r.SucursalId);
        cmd.Parameters.AddWithValue("@sucursal", string.IsNullOrWhiteSpace(r.Sucursal) ? (r.SucursalId == 2 ? "EL BRUJO PREMIU" : "EL BRUJO") : r.Sucursal.Trim());
        cmd.Parameters.AddWithValue("@cajero_usuario", r.CajeroUsuario ?? "");
        cmd.Parameters.AddWithValue("@cajero_nombre", r.CajeroNombre ?? "");
        cmd.Parameters.AddWithValue("@caja", r.Caja ?? "");
        cmd.Parameters.AddWithValue("@turno", NormalizarTurno(r.Turno));
        cmd.Parameters.AddWithValue("@inicio", r.Inicio);
        cmd.Parameters.AddWithValue("@fin", r.Fin);
        cmd.Parameters.AddWithValue("@hora_entrada", r.HoraEntrada == DateTime.MinValue ? r.Inicio : r.HoraEntrada);
        cmd.Parameters.AddWithValue("@fecha_cierre", r.FechaCierre);
        cmd.Parameters.AddWithValue("@transacciones_total", canonicalTransactions);
        cmd.Parameters.AddWithValue("@transacciones_efectivo", canonicalCashTransactions);
        cmd.Parameters.AddWithValue("@transacciones_qr", canonicalQrTransactions);
        cmd.Parameters.AddWithValue("@transacciones_tarjeta", Math.Max(0, r.TransaccionesTarjeta));
        cmd.Parameters.AddWithValue("@transacciones_transferencia", reconciledFromSales ? canonicalTransferTransactions : Math.Max(0, r.TransaccionesTransferencia));
        cmd.Parameters.AddWithValue("@efectivo", canonicalCash);
        cmd.Parameters.AddWithValue("@qr", canonicalQr);
        cmd.Parameters.AddWithValue("@tarjeta", Math.Max(0, r.Tarjeta));
        cmd.Parameters.AddWithValue("@transferencia", reconciledFromSales ? canonicalTransfer : Math.Max(0, r.Transferencia));
        cmd.Parameters.AddWithValue("@sin_metodo", Math.Max(0, r.SinMetodo));
        cmd.Parameters.AddWithValue("@productos_total", reconciledFromSales ? canonicalProduct : Math.Max(0, r.ProductosTotal));
        cmd.Parameters.AddWithValue("@mesas_total", reconciledFromSales ? canonicalTable : Math.Max(0, r.MesasTotal));
        cmd.Parameters.AddWithValue("@minutos_jugados", Math.Max(0, r.MinutosJugados));
        cmd.Parameters.AddWithValue("@propinas_total", Math.Max(0, r.PropinasTotal));
        cmd.Parameters.AddWithValue("@cortesias_valor", Math.Max(0, r.CortesiasValor));
        cmd.Parameters.AddWithValue("@comisiones_total", Math.Max(0, r.ComisionesTotal));
        cmd.Parameters.AddWithValue("@gastos_total", Math.Max(0, r.GastosTotal));
        cmd.Parameters.AddWithValue("@perdidas_total", Math.Max(0, r.PerdidasTotal));
        cmd.Parameters.AddWithValue("@total_generado", canonicalTotal);
        cmd.Parameters.AddWithValue("@neto_turno", reconciledFromSales ? canonicalTotal - Math.Max(0, r.GastosTotal) : r.NetoTurno);
        cmd.Parameters.AddWithValue("@observaciones", r.Observaciones ?? "");
        cmd.Parameters.AddWithValue("@detalle_json", string.IsNullOrWhiteSpace(r.DetalleJson) ? "[]" : r.DetalleJson);
        cmd.Parameters.AddWithValue("@cierre_key", cierreKey);
        cmd.Parameters.AddWithValue("@sync_key", syncKey);
        await cmd.ExecuteNonQueryAsync();
    }

    long id = 0;
    string canonicalSyncKey = syncKey;
    await using (var idCmd = new MySqlCommand("SELECT id, sync_key FROM cierres_turno WHERE cierre_key=@cierre_key OR sync_key=@sync_key ORDER BY id LIMIT 1;", con))
    {
        idCmd.Parameters.AddWithValue("@cierre_key", cierreKey);
        idCmd.Parameters.AddWithValue("@sync_key", syncKey);
        await using var idRd = await idCmd.ExecuteReaderAsync();
        if (await idRd.ReadAsync())
        {
            id = idRd.GetInt64(0);
            canonicalSyncKey = idRd.IsDBNull(1) ? syncKey : idRd.GetString(1);
        }
    }

    await TrySyncSheets(db, sheets);
    return Results.Ok(new { ok = true, id, syncKey = canonicalSyncKey, cierreKey, idempotent = !string.Equals(canonicalSyncKey, syncKey, StringComparison.OrdinalIgnoreCase), reconciledFromSales, canonicalTransactions, canonicalCash, canonicalQr, canonicalTransfer, canonicalTotal, message = reconciledFromSales ? "Arqueo guardado y conciliado contra ventas únicas de Railway, incluida transferencia." : "Arqueo guardado para Administración." });
});

app.MapGet("/api/admin/cierres-turno", async (Db db, string clave, int? sucursalId) =>
{
    const string adminKey = "ENTREGAR_LIMPIO_2026";
    if (clave != adminKey) return Results.Unauthorized();

    await using var con = await db.OpenAsync();
    await EnsureShiftCloseTables(con);

    string sql = """
        SELECT
            id,
            sync_key,
            sucursal_id,
            sucursal,
            cajero_usuario,
            cajero_nombre,
            caja,
            turno,
            inicio,
            fin,
            hora_entrada,
            fecha_cierre,
            transacciones_total,
            transacciones_efectivo,
            transacciones_qr,
            transacciones_tarjeta,
            transacciones_transferencia,
            efectivo,
            qr,
            tarjeta,
            transferencia,
            sin_metodo,
            productos_total,
            mesas_total,
            minutos_jugados,
            propinas_total,
            cortesias_valor,
            comisiones_total,
            gastos_total,
            perdidas_total,
            total_generado,
            neto_turno,
            observaciones,
            detalle_json,
            COALESCE(estado,'CERRADO') AS estado,
            COALESCE(reapertura_count,0) AS reapertura_count,
            ultima_reapertura,
            COALESCE(reabierto_por,'') AS reabierto_por,
            COALESCE(motivo_reapertura,'') AS motivo_reapertura
        FROM cierres_turno c
        WHERE c.id = (
            SELECT MIN(c2.id) FROM cierres_turno c2
            WHERE c2.sucursal_id=c.sucursal_id
              AND UPPER(TRIM(c2.cajero_usuario))=UPPER(TRIM(c.cajero_usuario))
              AND UPPER(TRIM(COALESCE(c2.caja,'')))=UPPER(TRIM(COALESCE(c.caja,'')))
              AND UPPER(TRIM(c2.turno))=UPPER(TRIM(c.turno))
              AND c2.inicio=c.inicio
        )
    """;

    if (sucursalId.HasValue && sucursalId.Value > 0)
        sql += " AND c.sucursal_id = @sucursal_id";

    sql += " ORDER BY c.fecha_cierre DESC, c.id DESC;";

    await using var cmd = new MySqlCommand(sql, con);
    if (sucursalId.HasValue && sucursalId.Value > 0)
        cmd.Parameters.AddWithValue("@sucursal_id", sucursalId.Value);

    List<Dictionary<string, object?>> rows = new();
    await using var rd = await cmd.ExecuteReaderAsync();
    while (await rd.ReadAsync())
    {
        Dictionary<string, object?> row = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < rd.FieldCount; i++)
            row[rd.GetName(i)] = rd.IsDBNull(i) ? null : rd.GetValue(i);
        rows.Add(row);
    }

    return Results.Ok(rows);
});


// V89: reapertura administrativa segura.
// NO borra ventas ni arqueos. Marca el cierre como REABIERTO y deja auditoría.
app.MapPost("/api/admin/cierres-turno/reabrir", async (Db db, string clave, ReopenShiftRequest req) =>
{
    const string adminKey = "ENTREGAR_LIMPIO_2026";
    if (clave != adminKey) return Results.Unauthorized();

    string caja = req.SucursalId == 1 ? "CAJA ÚNICA" : (req.Caja ?? "").Trim();
    if (req.SucursalId <= 0 ||
        string.IsNullOrWhiteSpace(caja) ||
        string.IsNullOrWhiteSpace(req.Turno) ||
        string.IsNullOrWhiteSpace(req.CajeroUsuario) ||
        req.Inicio == DateTime.MinValue)
        return Results.BadRequest(new { ok = false, message = "Faltan datos para identificar exactamente el turno." });

    string motivo = (req.Motivo ?? "").Trim();
    if (motivo.Length < 5)
        return Results.BadRequest(new { ok = false, message = "Escribe un motivo de reapertura de al menos 5 caracteres." });

    string adminUsuario = string.IsNullOrWhiteSpace(req.AdminUsuario) ? "ADMIN" : req.AdminUsuario.Trim();

    await using var con = await db.OpenAsync();
    await EnsureShiftCloseTables(con);

    // Buscar primero por ID online si está disponible; si no, usar la identidad natural exacta.
    long cierreId = 0;
    string estadoActual = "";
    await using (var find = new MySqlCommand("""
        SELECT id, COALESCE(estado,'CERRADO') AS estado
        FROM cierres_turno
        WHERE (
                @id > 0 AND id=@id
              )
           OR (
                sucursal_id=@sid
                AND UPPER(TRIM(COALESCE(caja,'')))=UPPER(TRIM(@caja))
                AND UPPER(TRIM(turno))=UPPER(TRIM(@turno))
                AND UPPER(TRIM(cajero_usuario))=UPPER(TRIM(@cajero))
                AND inicio=@inicio
              )
        ORDER BY CASE WHEN id=@id THEN 0 ELSE 1 END, id
        LIMIT 1;
    """, con))
    {
        find.Parameters.AddWithValue("@id", Math.Max(0, req.CierreId));
        find.Parameters.AddWithValue("@sid", req.SucursalId);
        find.Parameters.AddWithValue("@caja", caja);
        find.Parameters.AddWithValue("@turno", NormalizarTurno(req.Turno));
        find.Parameters.AddWithValue("@cajero", req.CajeroUsuario.Trim());
        find.Parameters.AddWithValue("@inicio", req.Inicio);
        await using var rd = await find.ExecuteReaderAsync();
        if (await rd.ReadAsync())
        {
            cierreId = rd.GetInt64(0);
            estadoActual = rd.IsDBNull(1) ? "CERRADO" : rd.GetString(1);
        }
    }

    if (cierreId <= 0)
        return Results.NotFound(new { ok = false, message = "No se encontró ese cierre. Actualiza la lista de arqueos e inténtalo nuevamente." });

    if (string.Equals(estadoActual, "REABIERTO", StringComparison.OrdinalIgnoreCase))
        return Results.Ok(new { ok = true, id = cierreId, estado = "REABIERTO", idempotent = true, message = "Ese turno ya estaba reabierto." });

    await using (var upd = new MySqlCommand("""
        UPDATE cierres_turno
        SET estado='REABIERTO',
            reapertura_count=COALESCE(reapertura_count,0)+1,
            ultima_reapertura=NOW(),
            reabierto_por=@admin,
            motivo_reapertura=@motivo
        WHERE id=@id;
    """, con))
    {
        upd.Parameters.AddWithValue("@admin", adminUsuario);
        upd.Parameters.AddWithValue("@motivo", motivo);
        upd.Parameters.AddWithValue("@id", cierreId);
        await upd.ExecuteNonQueryAsync();
    }

    return Results.Ok(new
    {
        ok = true,
        id = cierreId,
        estado = "REABIERTO",
        message = "Turno reabierto. El arqueo y las ventas se conservaron; el cierre final actualizará el mismo registro."
    });
});


// V75: consulta segura para la APP ADMIN remota.
// Lee ventas canónicas y arqueos de Railway; no depende de Google Sheets.
app.MapPost("/api/admin/reportes/consulta", async (Db db, AdminReportQueryRequest req) =>
{
    await using var con = await db.OpenAsync();
    await EnsureVentaSyncProtection(con);
    await EnsureShiftCloseTables(con);

    if (!await ValidarAdministradorAsync(con, req.Usuario, req.Clave))
        return Results.Unauthorized();

    int sucursalId = ResolveInstanceSucursalId(req.SucursalId);
    DateTime desde = (req.Desde ?? DateTime.Today.AddDays(-7)).Date;
    DateTime hasta = (req.Hasta ?? DateTime.Today).Date;
    if (hasta < desde) (desde, hasta) = (hasta, desde);
    if ((hasta - desde).TotalDays > 370)
        return Results.BadRequest(new { ok=false, message="El rango máximo permitido es de 370 días." });

    string turno = NormalizarFiltroTurno(req.Turno);
    string sector = NormalizarFiltroSector(sucursalId, req.Sector);

    var pars = new Dictionary<string, object?>
    {
        ["@sucursal_id"] = sucursalId,
        ["@desde"] = desde,
        ["@hasta"] = hasta,
        ["@turno"] = turno,
        ["@sector"] = sector
    };

    // Una fila por producto. Totales y medios de pago aparecen solo en la primera
    // línea de cada venta para que Excel se pueda sumar sin inflar el dinero.
    string ventasSql = """
        SELECT z.id_venta, z.fecha_turno, z.turno, z.fecha, z.hora,
               z.cajero_usuario, z.cajero_nombre, z.cajero_usuario AS cajero, z.caja, z.sector,
               z.tipo, z.mesa, z.mesera, z.tiempo_mesa,
               z.producto, z.presentacion, z.cantidad, z.precio_unitario, z.subtotal_producto,
               CASE WHEN z.linea_venta=1 THEN z.costo_tiempo_base ELSE 0 END AS costo_tiempo,
               CASE WHEN z.linea_venta=1 THEN z.ajuste_redondeo_base ELSE 0 END AS ajuste_redondeo,
               z.subtotal_producto
                 + CASE WHEN z.linea_venta=1 THEN z.costo_tiempo_base ELSE 0 END
                 + CASE WHEN z.linea_venta=1 THEN z.ajuste_redondeo_base ELSE 0 END AS total_linea,
               z.metodo_pago,
               CASE WHEN z.linea_venta=1 THEN z.efectivo ELSE 0 END AS efectivo,
               CASE WHEN z.linea_venta=1 THEN z.qr ELSE 0 END AS qr,
               CASE WHEN z.linea_venta=1 THEN z.transferencia ELSE 0 END AS transferencia,
               CASE WHEN z.linea_venta=1 THEN z.total ELSE 0 END AS total_venta,
               z.sync_key
        FROM (
            SELECT v.id AS id_venta,
                   CASE
                       WHEN UPPER(COALESCE(NULLIF(v.turno,''), CASE WHEN TIME(v.fecha) >= '08:00:00' AND TIME(v.fecha) < '20:00:00' THEN 'MAÑANA' ELSE 'NOCHE' END)) = 'NOCHE'
                            AND TIME(v.fecha) < '20:00:00'
                           THEN DATE(DATE_SUB(v.fecha, INTERVAL 1 DAY))
                       ELSE DATE(v.fecha)
                   END AS fecha_turno,
                   COALESCE(NULLIF(v.turno,''), CASE WHEN TIME(v.fecha) >= '08:00:00' AND TIME(v.fecha) < '20:00:00' THEN 'MAÑANA' ELSE 'NOCHE' END) AS turno,
                   DATE(v.fecha) AS fecha,
                   TIME(v.fecha) AS hora,
                   v.cajero AS cajero_usuario,
                   COALESCE(NULLIF(u.nombre_completo,''), v.cajero) AS cajero_nombre,
                   CASE
                       WHEN v.sucursal_id=1 THEN 'CAJA ÚNICA'
                       WHEN UPPER(COALESCE(NULLIF(v.caja_nombre,''),u.caja_nombre,'')) IN ('CAJA 2','CAJA ABAJO') THEN 'CAJA ABAJO'
                       WHEN UPPER(COALESCE(NULLIF(v.caja_nombre,''),u.caja_nombre,'')) IN ('CAJA 1','CAJA ARRIBA') THEN 'CAJA ARRIBA'
                       ELSE COALESCE(NULLIF(v.caja_nombre,''),NULLIF(u.caja_nombre,''),'SIN CAJA')
                   END AS caja,
                   CASE
                       WHEN v.sucursal_id=1 THEN 'GENERAL'
                       WHEN UPPER(COALESCE(NULLIF(d.sector,''),NULLIF(cm.sector,''),NULLIF(u.sector,''),NULLIF(v.caja_nombre,''),'')) IN ('ABAJO','CAJA 2','CAJA ABAJO') THEN 'ABAJO'
                       ELSE 'ARRIBA'
                   END AS sector,
                   v.tipo,
                   CASE
                       WHEN COALESCE(cm.mesa,'')<>'' THEN cm.mesa
                       WHEN UPPER(COALESCE(v.tipo,''))='DIRECTA' THEN 'BAR / VENTA DIRECTA'
                       WHEN COALESCE(v.session_id,0)>0 THEN CONCAT('SESIÓN ',v.session_id)
                       ELSE ''
                   END AS mesa,
                   COALESCE(cm.mesera,'') AS mesera,
                   COALESCE(cm.tiempo,'') AS tiempo_mesa,
                   COALESCE(d.producto,'') AS producto,
                   COALESCE(d.presentacion,'') AS presentacion,
                   COALESCE(d.cantidad,0) AS cantidad,
                   COALESCE(d.precio_unitario,0) AS precio_unitario,
                   COALESCE(d.subtotal,0) AS subtotal_producto,
                   CASE WHEN UPPER(COALESCE(v.tipo,''))='MESA'
                        THEN GREATEST(COALESCE(cm.total_mesa, v.total - SUM(COALESCE(d.subtotal,0)) OVER (PARTITION BY v.id)),0)
                        ELSE 0 END AS costo_tiempo_base,
                   v.total
                     - SUM(COALESCE(d.subtotal,0)) OVER (PARTITION BY v.id)
                     - CASE WHEN UPPER(COALESCE(v.tipo,''))='MESA'
                            THEN GREATEST(COALESCE(cm.total_mesa, v.total - SUM(COALESCE(d.subtotal,0)) OVER (PARTITION BY v.id)),0)
                            ELSE 0 END AS ajuste_redondeo_base,
                   v.metodo_pago,
                   COALESCE(v.efectivo,0) AS efectivo,
                   COALESCE(v.qr,0) AS qr,
                   CASE WHEN UPPER(COALESCE(v.metodo_pago,''))='TRANSFERENCIA' THEN v.total ELSE 0 END AS transferencia,
                   v.total,
                   COALESCE(v.sync_key,'') AS sync_key,
                   d.id AS detalle_id,
                   ROW_NUMBER() OVER (PARTITION BY v.id ORDER BY COALESCE(d.id,0)) AS linea_venta
            FROM ventas_canonicas v
            LEFT JOIN detalle_ventas_canonico d ON d.venta_id=v.id
            LEFT JOIN usuarios u ON u.usuario=v.cajero AND u.sucursal_id=v.sucursal_id
            LEFT JOIN cobros_mesa cm ON cm.sucursal_id=v.sucursal_id
                AND cm.session_id=v.session_id
                AND UPPER(TRIM(COALESCE(cm.caja_nombre,'')))=UPPER(TRIM(COALESCE(v.caja_nombre,'')))
            WHERE v.sucursal_id=@sucursal_id
        ) z
        WHERE z.fecha_turno BETWEEN @desde AND @hasta
          AND (@turno='' OR UPPER(z.turno)=@turno)
          AND (@sector='' OR UPPER(z.sector)=@sector)
        ORDER BY z.fecha DESC, z.hora DESC, z.id_venta DESC, COALESCE(z.detalle_id,0);
    """;

    var ventas = await db.QueryAsync(con, ventasSql, pars);

    string arqueosSql = """
        SELECT c.id, c.sync_key, c.sucursal_id, c.sucursal, c.cajero_usuario, c.cajero_nombre,
               c.caja,
               CASE WHEN c.sucursal_id=1 THEN 'GENERAL'
                    WHEN UPPER(COALESCE(c.caja,'')) LIKE '%ABAJO%' THEN 'ABAJO'
                    ELSE 'ARRIBA' END AS sector,
               c.turno, c.inicio, c.fin, c.hora_entrada, c.fecha_cierre,
               c.transacciones_total, c.transacciones_efectivo, c.transacciones_qr,
               c.transacciones_tarjeta, c.transacciones_transferencia,
               c.efectivo, c.qr, c.tarjeta, c.transferencia, c.sin_metodo,
               c.productos_total, c.mesas_total, c.minutos_jugados, c.propinas_total,
               c.cortesias_valor, c.comisiones_total, c.gastos_total, c.perdidas_total,
               c.total_generado, c.neto_turno, c.observaciones
        FROM cierres_turno c
        WHERE c.sucursal_id=@sucursal_id
          AND c.id = (
              SELECT MIN(c2.id) FROM cierres_turno c2
              WHERE c2.sucursal_id=c.sucursal_id
                AND UPPER(TRIM(c2.cajero_usuario))=UPPER(TRIM(c.cajero_usuario))
                AND UPPER(TRIM(COALESCE(c2.caja,'')))=UPPER(TRIM(COALESCE(c.caja,'')))
                AND UPPER(TRIM(c2.turno))=UPPER(TRIM(c.turno))
                AND c2.inicio=c.inicio
          )
          AND DATE(COALESCE(c.inicio,c.fecha_cierre)) BETWEEN @desde AND @hasta
          AND (@turno='' OR UPPER(COALESCE(c.turno,''))=@turno)
          AND (@sector='' OR UPPER(CASE WHEN c.sucursal_id=1 THEN 'GENERAL'
                    WHEN UPPER(COALESCE(c.caja,'')) LIKE '%ABAJO%' THEN 'ABAJO'
                    ELSE 'ARRIBA' END)=@sector)
        ORDER BY c.fecha_cierre DESC, c.id DESC;
    """;

    var arqueos = await db.QueryAsync(con, arqueosSql, pars);

    decimal totalVentas=0, efectivo=0, qr=0, transferencia=0, costoTiempo=0, productos=0;
    int transacciones=0;
    var ventasUnicas = new HashSet<long>();
    foreach (var r in ventas)
    {
        long id = ToLong(r, "id_venta");
        decimal total = ToDecimal(r, "total_venta");
        if (total != 0 && ventasUnicas.Add(id))
        {
            transacciones++;
            totalVentas += total;
            efectivo += ToDecimal(r, "efectivo");
            qr += ToDecimal(r, "qr");
            transferencia += ToDecimal(r, "transferencia");
            costoTiempo += ToDecimal(r, "costo_tiempo");
        }
        productos += ToDecimal(r, "subtotal_producto");
    }

    decimal gastos=0, netoArqueos=0, propinas=0, comisiones=0;
    foreach (var r in arqueos)
    {
        gastos += ToDecimal(r, "gastos_total");
        netoArqueos += ToDecimal(r, "neto_turno");
        propinas += ToDecimal(r, "propinas_total");
        comisiones += ToDecimal(r, "comisiones_total");
    }

    return Results.Ok(new
    {
        ok=true,
        sucursalId,
        sucursal = sucursalId==2 ? "EL BRUJO PREMIU" : "EL BRUJO",
        desde,
        hasta,
        turno = string.IsNullOrEmpty(turno) ? "TODOS" : turno,
        sector = string.IsNullOrEmpty(sector) ? "TODOS" : sector,
        resumen = new
        {
            transacciones,
            totalVentas = Math.Round(totalVentas,2),
            efectivo = Math.Round(efectivo,2),
            qr = Math.Round(qr,2),
            transferencia = Math.Round(transferencia,2),
            ventaProductos = Math.Round(productos,2),
            cobroTiempo = Math.Round(costoTiempo,2),
            arqueos = arqueos.Count,
            gastos = Math.Round(gastos,2),
            propinas = Math.Round(propinas,2),
            comisionesMeseras = Math.Round(comisiones,2),
            netoArqueos = Math.Round(netoArqueos,2)
        },
        ventas,
        arqueos
    });
});


// V76: inventario remoto del Administrador. Un catálogo, existencias por sector en PREMIU.
app.MapPost("/api/admin/inventario/consulta", async (Db db, AdminInventoryQueryRequest req) =>
{
    await using var con = await db.OpenAsync();
    await EnsureAppMeseraTables(con);
    await EnsureSectorLayoutAsync(con);
    await EnsurePremiumSectorStockAsync(con);
    if (!await ValidarAdministradorAsync(con, req.Usuario, req.Clave)) return Results.Unauthorized();
    int sid = ResolveInstanceSucursalId(req.SucursalId);

    await using (var create = new MySqlCommand("""
        CREATE TABLE IF NOT EXISTS movimientos_inventario_admin (
            id BIGINT AUTO_INCREMENT PRIMARY KEY,
            operation_key VARCHAR(100) NOT NULL,
            sucursal_id INT NOT NULL,
            sector VARCHAR(20) NOT NULL DEFAULT 'GENERAL',
            producto_id BIGINT NOT NULL,
            delta DECIMAL(14,4) NOT NULL,
            motivo VARCHAR(200) NULL,
            fecha DATETIME NOT NULL,
            UNIQUE KEY uk_mov_inv_admin_operation (operation_key)
        );
    """, con)) await create.ExecuteNonQueryAsync();

    string sql = sid == 2 ? """
        SELECT MIN(p.nombre) AS producto,
               MIN(p.categoria) AS categoria,
               MIN(p.unidad_base) AS unidad_base,
               MAX(CASE WHEN p.sector='ARRIBA' THEN p.id ELSE 0 END) AS producto_id_arriba,
               MAX(CASE WHEN p.sector='ABAJO' THEN p.id ELSE 0 END) AS producto_id_abajo,
               MAX(CASE WHEN p.sector='ARRIBA' THEN p.stock_actual ELSE 0 END) AS stock_arriba,
               MAX(CASE WHEN p.sector='ABAJO' THEN p.stock_actual ELSE 0 END) AS stock_abajo,
               MAX(CASE WHEN p.sector='ARRIBA' THEN p.stock_actual ELSE 0 END)
                 + MAX(CASE WHEN p.sector='ABAJO' THEN p.stock_actual ELSE 0 END) AS stock_total,
               COALESCE(SUM(CASE WHEN m.delta>0 AND m.sector='ARRIBA' THEN m.delta ELSE 0 END),0) AS entradas_arriba,
               COALESCE(SUM(CASE WHEN m.delta>0 AND m.sector='ABAJO' THEN m.delta ELSE 0 END),0) AS entradas_abajo,
               COALESCE(SUM(CASE WHEN m.delta>0 AND UPPER(COALESCE(m.motivo,'')) NOT LIKE 'TRANSFERENCIA%' THEN m.delta ELSE 0 END),0) AS entradas_total,
               MAX(p.stock_minimo) AS stock_minimo,
               MAX(COALESCE(p.sin_limite_stock,0)) AS sin_limite_stock,
               MAX(COALESCE(p.rendimiento_vaso,10)) AS rendimiento_vaso,
               MAX(CASE WHEN p.sector='ARRIBA' THEN COALESCE(vc.servicios_restantes,0) ELSE 0 END) AS vasos_abiertos_arriba,
               MAX(CASE WHEN p.sector='ABAJO' THEN COALESCE(vc.servicios_restantes,0) ELSE 0 END) AS vasos_abiertos_abajo
        FROM productos p
        LEFT JOIN movimientos_inventario_admin m ON m.producto_id=p.id AND m.sucursal_id=p.sucursal_id
        LEFT JOIN vaso_control vc ON vc.sucursal_id=p.sucursal_id AND vc.sector=p.sector AND vc.producto_id=p.id
        WHERE p.sucursal_id=2 AND p.estado='ACTIVO'
          AND p.sector IN ('ARRIBA','ABAJO')
          AND (@instance_sector = '' OR p.sector = @instance_sector)
        GROUP BY LOWER(TRIM(p.nombre))
        ORDER BY categoria, producto;
    """ : """
        SELECT p.id AS producto_id,p.nombre AS producto,p.categoria,p.unidad_base,
               p.stock_actual AS stock_actual,
               COALESCE(SUM(CASE WHEN m.delta>0 THEN m.delta ELSE 0 END),0) AS entradas_total,
               p.stock_minimo,COALESCE(p.sin_limite_stock,0) AS sin_limite_stock,
               COALESCE(p.rendimiento_vaso,10) AS rendimiento_vaso,
               COALESCE(vc.servicios_restantes,0) AS vasos_abiertos
        FROM productos p
        LEFT JOIN movimientos_inventario_admin m ON m.producto_id=p.id AND m.sucursal_id=p.sucursal_id
        LEFT JOIN vaso_control vc ON vc.sucursal_id=p.sucursal_id AND vc.sector=p.sector AND vc.producto_id=p.id
        WHERE p.sucursal_id=1 AND p.estado='ACTIVO' AND p.sector='GENERAL'
        GROUP BY p.id,p.nombre,p.categoria,p.unidad_base,p.stock_actual,p.stock_minimo,p.sin_limite_stock,p.rendimiento_vaso,vc.servicios_restantes
        ORDER BY p.categoria,p.nombre;
    """;
    var items = await db.QueryAsync(con, sql, sid == 2 ? new Dictionary<string, object?>
    {
        ["@instance_sector"] = IsPremiumSingleSectorInstance() ? GetInstanceSector() : ""
    } : null);
    return Results.Ok(new
    {
        ok=true,
        sucursalId=sid,
        sucursal=sid==2?"EL BRUJO PREMIU":"EL BRUJO",
        inventarioCompartidoCatalogo=true,
        stockSeparadoPorSector=sid==2,
        instanceCode=GetInstanceCode(),
        instanceSector=GetInstanceSector(),
        nota=IsPremiumSingleSectorInstance()? $"API aislada: solo {GetInstanceSector()}." : (sid==2?"Un solo catálogo; ARRIBA y ABAJO tienen cantidades independientes.":"Inventario GENERAL de EL BRUJO."),
        items
    });
});

// V85: reposición remota idempotente desde la App Administrador.
// Cada operation_key puede aplicarse una sola vez, incluso si el celular reintenta por mala señal.
app.MapPost("/api/admin/inventario/reponer", async (Db db, AdminInventoryAdjustRequest req) =>
{
    if (!InstanceRequestMatches(req.SucursalId, req.Sector))
        return Results.BadRequest(new { ok = false, message = InstanceMismatchMessage(req.SucursalId, req.Sector) });
    await using var con = await db.OpenAsync();
    await EnsureAppMeseraTables(con);
    await EnsureSectorLayoutAsync(con);
    await EnsurePremiumSectorStockAsync(con);
    if (!await ValidarAdministradorAsync(con, req.Usuario, req.Clave)) return Results.Unauthorized();

    int sid = ResolveInstanceSucursalId(req.SucursalId);
    string producto = (req.Producto ?? "").Trim();
    if (string.IsNullOrWhiteSpace(producto)) return Results.BadRequest(new { ok=false, message="Selecciona un producto." });
    if (req.Cantidad <= 0 || req.Cantidad > 100000m) return Results.BadRequest(new { ok=false, message="Cantidad inválida." });

    string sector = sid == 2 ? NormalizarSector(2, req.Sector) : "GENERAL";
    string operationKey = string.IsNullOrWhiteSpace(req.OperationKey)
        ? "APPSTOCK-" + Guid.NewGuid().ToString("N")
        : req.OperationKey.Trim();
    if (operationKey.Length > 100) operationKey = operationKey[..100];
    string motivo = string.IsNullOrWhiteSpace(req.Motivo) ? "REPOSICION APP ADMIN" : req.Motivo.Trim();
    if (motivo.Length > 200) motivo = motivo[..200];

    await using (var create = new MySqlCommand("""
        CREATE TABLE IF NOT EXISTS movimientos_inventario_admin (
            id BIGINT AUTO_INCREMENT PRIMARY KEY,
            operation_key VARCHAR(100) NOT NULL,
            sucursal_id INT NOT NULL,
            sector VARCHAR(20) NOT NULL DEFAULT 'GENERAL',
            producto_id BIGINT NOT NULL,
            delta DECIMAL(14,4) NOT NULL,
            motivo VARCHAR(200) NULL,
            fecha DATETIME NOT NULL,
            UNIQUE KEY uk_mov_inv_admin_operation (operation_key)
        );
    """, con)) await create.ExecuteNonQueryAsync();

    await using var tx = await con.BeginTransactionAsync();
    try
    {
        long productoId = 0;
        decimal stockAntes = 0m;
        string nombreReal = producto;
        string lookupSql = req.ProductoId.HasValue && req.ProductoId.Value > 0
            ? """
              SELECT id, nombre, stock_actual
              FROM productos
              WHERE id=@pid AND sucursal_id=@sid AND sector=@sector AND estado='ACTIVO'
              LIMIT 1
              FOR UPDATE;
              """
            : """
              SELECT id, nombre, stock_actual
              FROM productos
              WHERE sucursal_id=@sid AND sector=@sector AND estado='ACTIVO'
                AND LOWER(TRIM(nombre))=LOWER(TRIM(@nombre))
              ORDER BY id
              LIMIT 1
              FOR UPDATE;
              """;
        await using (var q = new MySqlCommand(lookupSql, con, tx))
        {
            q.Parameters.AddWithValue("@sid", sid);
            q.Parameters.AddWithValue("@sector", sector);
            if (req.ProductoId.HasValue && req.ProductoId.Value > 0)
                q.Parameters.AddWithValue("@pid", req.ProductoId.Value);
            else
                q.Parameters.AddWithValue("@nombre", producto);
            await using var rr = await q.ExecuteReaderAsync();
            if (await rr.ReadAsync())
            {
                productoId = Convert.ToInt64(rr["id"]);
                nombreReal = rr.IsDBNull(rr.GetOrdinal("nombre")) ? producto : rr.GetString("nombre");
                stockAntes = Convert.ToDecimal(rr["stock_actual"]);
            }
        }
        if (productoId <= 0)
            throw new InvalidOperationException("Producto no encontrado en " + (sid == 2 ? sector : "EL BRUJO") + ". Actualiza el inventario e inténtalo nuevamente.");

        int movementInserted;
        await using (var mv = new MySqlCommand("""
            INSERT IGNORE INTO movimientos_inventario_admin
            (operation_key,sucursal_id,sector,producto_id,delta,motivo,fecha)
            VALUES(@k,@sid,@sector,@pid,@delta,@motivo,NOW());
        """, con, tx))
        {
            mv.Parameters.AddWithValue("@k", operationKey);
            mv.Parameters.AddWithValue("@sid", sid);
            mv.Parameters.AddWithValue("@sector", sector);
            mv.Parameters.AddWithValue("@pid", productoId);
            mv.Parameters.AddWithValue("@delta", req.Cantidad);
            mv.Parameters.AddWithValue("@motivo", motivo);
            movementInserted = await mv.ExecuteNonQueryAsync();
        }

        if (movementInserted == 0)
        {
            await tx.CommitAsync();
            return Results.Ok(new { ok=true, idempotent=true, operationKey, producto=nombreReal, productoId, sector, cantidad=req.Cantidad, message="Esta reposición ya había sido aplicada. No se volvió a sumar stock." });
        }

        await using (var up = new MySqlCommand("UPDATE productos SET stock_actual=stock_actual+@q WHERE id=@id;", con, tx))
        {
            up.Parameters.AddWithValue("@q", req.Cantidad);
            up.Parameters.AddWithValue("@id", productoId);
            if (await up.ExecuteNonQueryAsync() != 1) throw new InvalidOperationException("No se pudo actualizar el inventario.");
        }

        await tx.CommitAsync();
        decimal stockDespues = stockAntes + req.Cantidad;
        return Results.Ok(new { ok=true, idempotent=false, operationKey, producto=nombreReal, productoId, sector, cantidad=req.Cantidad, stockAntes, stockDespues, message="Stock actualizado correctamente." });
    }
    catch (Exception ex)
    {
        try { await tx.RollbackAsync(); } catch { }
        return Results.BadRequest(new { ok=false, message=ex.Message });
    }
});

app.MapPost("/api/admin/inventario/transferir", async (Db db, AdminInventoryTransferRequest req) =>
{
    if (IsPremiumSingleSectorInstance())
        return Results.BadRequest(new { ok=false, message="Esta API pertenece a un solo sector. El traspaso ARRIBA/ABAJO debe hacerse desde el Administrador General entre las dos APIs, no dentro de una sola API." });
    await using var con = await db.OpenAsync();
    await EnsurePremiumSectorStockAsync(con);
    if (!await ValidarAdministradorAsync(con, req.Usuario, req.Clave)) return Results.Unauthorized();
    if (req.Cantidad <= 0) return Results.BadRequest(new {ok=false,message="Cantidad inválida."});
    string origen=NormalizarSector(2,req.SectorOrigen), destino=NormalizarSector(2,req.SectorDestino);
    if (origen==destino) return Results.BadRequest(new {ok=false,message="Origen y destino deben ser diferentes."});
    await using var tx=await con.BeginTransactionAsync();
    try
    {
        long origenId=0,destinoId=0;
        foreach (string sec in new[]{origen,destino})
        {
            await using var q=new MySqlCommand("SELECT id FROM productos WHERE sucursal_id=2 AND sector=@s AND estado='ACTIVO' AND LOWER(TRIM(nombre))=LOWER(TRIM(@n)) LIMIT 1;",con,tx);
            q.Parameters.AddWithValue("@s",sec); q.Parameters.AddWithValue("@n",req.Producto);
            object? o=await q.ExecuteScalarAsync();
            if(o==null) throw new InvalidOperationException("Producto no encontrado en "+sec+".");
            if(sec==origen) origenId=Convert.ToInt64(o); else destinoId=Convert.ToInt64(o);
        }
        await using(var down=new MySqlCommand("UPDATE productos SET stock_actual=stock_actual-@q WHERE id=@id AND stock_actual>=@q;",con,tx))
        { down.Parameters.AddWithValue("@q",req.Cantidad); down.Parameters.AddWithValue("@id",origenId); if(await down.ExecuteNonQueryAsync()!=1) throw new InvalidOperationException("Stock insuficiente en "+origen+"."); }
        await using(var up=new MySqlCommand("UPDATE productos SET stock_actual=stock_actual+@q WHERE id=@id;",con,tx))
        { up.Parameters.AddWithValue("@q",req.Cantidad); up.Parameters.AddWithValue("@id",destinoId); await up.ExecuteNonQueryAsync(); }
        string key="TRF-"+Guid.NewGuid().ToString("N");
        foreach(var x in new[]{(id:origenId,sec:origen,delta:-req.Cantidad),(id:destinoId,sec:destino,delta:req.Cantidad)})
        {
            await using var mv=new MySqlCommand("INSERT INTO movimientos_inventario_admin(operation_key,sucursal_id,sector,producto_id,delta,motivo,fecha) VALUES(@k,2,@s,@p,@d,@m,NOW());",con,tx);
            mv.Parameters.AddWithValue("@k",key+"-"+x.sec); mv.Parameters.AddWithValue("@s",x.sec); mv.Parameters.AddWithValue("@p",x.id); mv.Parameters.AddWithValue("@d",x.delta); mv.Parameters.AddWithValue("@m","TRANSFERENCIA "+origen+" -> "+destino); await mv.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
        return Results.Ok(new{ok=true,producto=req.Producto,cantidad=req.Cantidad,origen,destino});
    }
    catch(Exception ex){await tx.RollbackAsync();return Results.BadRequest(new{ok=false,message=ex.Message});}
});

app.MapGet("/api/reportes/resumen", async (Db db) =>
{
    await using var con = await db.OpenAsync();
    await EnsureVentaSyncProtection(con);

    const string sql = """
        SELECT
            CASE WHEN s.id = 2 THEN 'EL BRUJO PREMIU' ELSE 'EL BRUJO' END AS sucursal,
            COALESCE(SUM(v.total), 0) AS total_ventas,
            COUNT(v.id) AS cantidad_ventas
        FROM sucursales s
        LEFT JOIN ventas_canonicas v ON v.sucursal_id = s.id
        GROUP BY s.id, s.nombre
        ORDER BY s.id;
    """;

    var porSucursal = await db.QueryAsync(con, sql);

    const string totalSql = """
        SELECT
            COALESCE(SUM(total), 0) AS total_general,
            COUNT(id) AS cantidad_ventas
        FROM ventas_canonicas;
    """;

    var total = await db.QueryAsync(con, totalSql);

    return Results.Ok(new { porSucursal, total });
});

static async Task<bool> ValidarAdministradorAsync(MySqlConnection con, string? usuario, string? clave)
{
    string u=(usuario ?? "").Trim();
    string c=clave ?? "";
    if (u.Length==0 || c.Length==0) return false;
    await using var cmd=new MySqlCommand("SELECT clave, rol, estado FROM usuarios WHERE usuario=@u LIMIT 1;",con);
    cmd.Parameters.AddWithValue("@u",u);
    await using var rd=await cmd.ExecuteReaderAsync();
    if (!await rd.ReadAsync()) return false;
    string guardada=rd.IsDBNull(0)?"":rd.GetString(0);
    string rol=rd.IsDBNull(1)?"":rd.GetString(1);
    string estado=rd.IsDBNull(2)?"":rd.GetString(2);
    return estado.Equals("ACTIVO",StringComparison.OrdinalIgnoreCase)
        && rol.Equals("ADMINISTRADOR",StringComparison.OrdinalIgnoreCase)
        && PasswordHasher.Verify(c,guardada);
}

static string NormalizarFiltroTurno(string? turno)
{
    string t=(turno ?? "").Trim().ToUpperInvariant();
    if (t is "TODOS" or "TODO" or "TODAS") return "";
    if (t is "DIA" or "DÍA" or "MAÑANA" or "MANANA") return "MAÑANA";
    if (t is "NOCHE") return "NOCHE";
    return "";
}

static string NormalizarFiltroSector(int sucursalId, string? sector)
{
    if (sucursalId!=2) return "";
    string s=(sector ?? "").Trim().ToUpperInvariant();
    if (s.Contains("ABAJO")) return "ABAJO";
    if (s.Contains("ARRIBA")) return "ARRIBA";
    return "";
}

static decimal ToDecimal(Dictionary<string,object?> r,string key)
{
    if (!r.TryGetValue(key,out var v) || v is null || v is DBNull) return 0m;
    try { return Convert.ToDecimal(v); } catch { return 0m; }
}

static long ToLong(Dictionary<string,object?> r,string key)
{
    if (!r.TryGetValue(key,out var v) || v is null || v is DBNull) return 0L;
    try { return Convert.ToInt64(v); } catch { return 0L; }
}

app.Run();

static async Task TrySyncSheets(Db db, SheetsReporter sheets)
{
    if (!InstanceSheetsEnabled()) return;
    if (!sheets.IsConfigured) return;

    try
    {
        await sheets.SyncFromDatabaseAsync(db);
    }
    catch (Exception ex)
    {
        // No se debe perder la venta si Google Sheets falla.
        // La venta ya queda guardada en MySQL y luego se puede forzar /api/sheets/sync.
        Console.Error.WriteLine("[GoogleSheets] Sincronización pendiente: " + ex.Message);
    }
}


static async Task<IResult> AplicarStockTxtPaquetesV33(Db db, SheetsReporter sheets, string clave, int sucursalId)
{
    const string cleanKey = "ENTREGAR_LIMPIO_2026";
    if (clave != cleanKey) return Results.Unauthorized();
    if (sucursalId != 1 && sucursalId != 2)
        return Results.BadRequest(new { ok = false, message = "sucursalId debe ser 1 o 2." });

    await using var con = await db.OpenAsync();
    await EnsureAppMeseraTables(con);

    int productosActualizados = 0;
    int productosSinLimite = 0;
    var faltantes = new List<string>();
    var revisarUnidades = new List<string>();

    foreach (var item in StockSoloTxtV30())
    {
        long productoId = 0;
        int unidadesPorEntrada = 1;
        bool sinLimite = false;
        string nombreEncontrado = item.aliases[0];

        foreach (string alias in item.aliases)
        {
            await using var buscar = new MySqlCommand("""
                SELECT id,
                       GREATEST(COALESCE(unidades_por_entrada, 1), 1) AS unidades_por_entrada,
                       COALESCE(sin_limite_stock, 0) AS sin_limite_stock,
                       nombre
                FROM productos
                WHERE sucursal_id = @sucursal_id
                  AND sector = @sector
                  AND UPPER(TRIM(nombre)) = UPPER(TRIM(@nombre))
                  AND estado = 'ACTIVO'
                LIMIT 1;
            """, con);
            buscar.Parameters.AddWithValue("@sucursal_id", sucursalId);
            buscar.Parameters.AddWithValue("@sector", sucursalId == 2 ? "ABAJO" : "GENERAL");
            buscar.Parameters.AddWithValue("@nombre", alias);

            await using var reader = await buscar.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                productoId = reader.GetInt64("id");
                unidadesPorEntrada = reader.GetInt32("unidades_por_entrada");
                sinLimite = reader.GetInt32("sin_limite_stock") == 1;
                nombreEncontrado = reader.GetString("nombre");
                break;
            }
        }

        if (productoId <= 0)
        {
            faltantes.Add(item.aliases[0]);
            continue;
        }

        if (sinLimite)
        {
            productosSinLimite++;
            continue;
        }

        if (unidadesPorEntrada == 1 && item.cantidad > 0)
            revisarUnidades.Add(nombreEncontrado);

        decimal stockTotal = Math.Max(0, item.cantidad * unidadesPorEntrada);

        await using var actualizar = new MySqlCommand("""
            UPDATE productos
            SET stock_actual = @stock_actual,
                tipo_entrada = CASE WHEN @unidades_por_entrada > 1 THEN 'PAQUETE' ELSE tipo_entrada END
            WHERE id = @id;
        """, con);
        actualizar.Parameters.AddWithValue("@stock_actual", stockTotal);
        actualizar.Parameters.AddWithValue("@unidades_por_entrada", unidadesPorEntrada);
        actualizar.Parameters.AddWithValue("@id", productoId);
        await actualizar.ExecuteNonQueryAsync();
        productosActualizados++;
    }

    await TrySyncSheets(db, sheets);

    return Results.Ok(new
    {
        ok = true,
        version = "V40_DIRECTO_ESTABLE",
        message = "Stock calculado desde el TXT como cantidad de paquetes/entradas por unidades_por_entrada.",
        formula = "stock_actual = cantidad_TXT × unidades_por_entrada",
        sucursalId,
        productosActualizados,
        productosSinLimite,
        faltantes,
        revisarUnidades,
        nota = "Los productos con unidades_por_entrada = 1 se calculan 1 a 1. Si realmente llegan en paquetes de 6, 12, 24, etc., configure primero ese valor desde Productos / Stock."
    });
}

static async Task<IResult> AplicarStockInicialReferenciaV32(Db db, SheetsReporter sheets, string clave, int sucursalId)
{
    const string cleanKey = "ENTREGAR_LIMPIO_2026";
    if (clave != cleanKey) return Results.Unauthorized();
    if (sucursalId != 1 && sucursalId != 2)
        return Results.BadRequest(new { ok = false, message = "sucursalId debe ser 1 o 2." });

    await using var con = await db.OpenAsync();
    await EnsureAppMeseraTables(con);

    int productosActualizados = 0;
    var faltantes = new List<string>();

    foreach (var item in StockInicialReferenciaV32())
    {
        long productoId = 0;

        foreach (string alias in item.aliases)
        {
            await using var buscar = new MySqlCommand("""
                SELECT id
                FROM productos
                WHERE sucursal_id = @sucursal_id
                  AND sector = @sector
                  AND UPPER(TRIM(nombre)) = UPPER(TRIM(@nombre))
                  AND estado = 'ACTIVO'
                LIMIT 1;
            """, con);
            buscar.Parameters.AddWithValue("@sucursal_id", sucursalId);
            buscar.Parameters.AddWithValue("@sector", sucursalId == 2 ? "ABAJO" : "GENERAL");
            buscar.Parameters.AddWithValue("@nombre", alias);

            object? found = await buscar.ExecuteScalarAsync();
            if (found != null)
            {
                productoId = Convert.ToInt64(found);
                break;
            }
        }

        if (productoId <= 0)
        {
            faltantes.Add(item.aliases[0]);
            continue;
        }

        await using var actualizar = new MySqlCommand("""
            UPDATE productos
            SET stock_actual = @stock_actual
            WHERE id = @id;
        """, con);
        actualizar.Parameters.AddWithValue("@stock_actual", item.cantidad);
        actualizar.Parameters.AddWithValue("@id", productoId);
        await actualizar.ExecuteNonQueryAsync();
        productosActualizados++;
    }

    await TrySyncSheets(db, sheets);

    return Results.Ok(new
    {
        ok = true,
        version = "V40_DIRECTO_ESTABLE",
        message = "Stock inicial cargado con las cantidades visibles en las capturas del inventario.",
        sucursalId,
        productosActualizados,
        faltantes,
        nota = "Solo se modifica stock_actual. No se cambian precios, categorías, presentaciones ni comisiones."
    });
}

static (decimal cantidad, string[] aliases)[] StockInicialReferenciaV32() => new (decimal cantidad, string[] aliases)[]
{
                (2m, new[] { "RON ABUELO", "ABUELO" }),
                (13m, new[] { "AGUA 2 LITROS", "AGUA 2L" }),
                (12m, new[] { "AGUA PERSONAL CON GAS", "AGUA CON GAS" }),
                (17m, new[] { "AGUA PERSONAL SIN GAS", "AGUA SIN GAS" }),
                (11m, new[] { "AGUA TONICA" }),
                (5m, new[] { "ALIKAL" }),
                (0m, new[] { "BELDEN" }),
                (44m, new[] { "BICO SABORES" }),
                (13m, new[] { "BLACK" }),
                (22m, new[] { "CERVEZA AMSTEL" }),
                (0m, new[] { "CERVEZA CONTI" }),
                (49m, new[] { "CERVEZA CORONA" }),
                (152m, new[] { "CERVEZA PACEÑA" }),
                (48m, new[] { "CERVEZA SKUL", "CERVEZA SKOL" }),
                (18m, new[] { "CICLON" }),
                (10m, new[] { "CIGARRO BOHEM DOUBLE GRANDE" }),
                (18m, new[] { "CIGARRO BOHEM UND" }),
                (0m, new[] { "CIGARRO BOHEN BLACK" }),
                (0m, new[] { "CIGARRO BOHEN SANDIA" }),
                (20m, new[] { "CIGARRO BOHEN UNIDAD" }),
                (5m, new[] { "CIGARRO BOHEN YOGOURT" }),
                (31m, new[] { "CIGARRO CAMEL ACTIVA UNID" }),
                (1m, new[] { "CIGARRO CAMEL ATIVO CHICO" }),
                (9m, new[] { "CIGARRO CAMEL GRANDE ACTIVA" }),
                (27m, new[] { "CIGARRO CAMEL SANDI UNIDAD" }),
                (11m, new[] { "CIGARRO CAMEL SANDIA CHICO" }),
                (11m, new[] { "CIGARRO CAMEL SANDIA GRANDE" }),
                (6m, new[] { "CIGARRO HILLS SANDI" }),
                (0m, new[] { "CIGARRO HILS" }),
                (16m, new[] { "CINCERO", "CENICERO" }),
                (89m, new[] { "CLORETS" }),
                (0m, new[] { "COCA EL BRUJO BICO STEVIA" }),
                (0m, new[] { "COCA EL BRUJO CHICLE", "COCA EL BRUJO CHILE" }),
                (3m, new[] { "COCA EL BRUJO MARACUYA" }),
                (3m, new[] { "COCA EL BRUJO MEDUSA" }),
                (2m, new[] { "COCA EL BRUJO RED BULL" }),
                (8m, new[] { "COCA EL BRUJO SANDIA RED BULL" }),
                (13m, new[] { "COCA EL BRUJO YOGOURT RED BULL" }),
                (0m, new[] { "COCA EL BRUJO YOGUBOLL" }),
                (6m, new[] { "COMBO FERNET" }),
                (10m, new[] { "COMBO FLOR DE CAÑA" }),
                (10m, new[] { "COMBO GIN" }),
                (10m, new[] { "COMBO HABANA" }),
                (15m, new[] { "COPAS DE VINO" }),
                (4m, new[] { "DOCILE MINTY" }),
                (5m, new[] { "ENCENDEDOR" }),
                (3m, new[] { "FERNET" }),
                (4m, new[] { "FLOW ACHACHAIRU" }),
                (0m, new[] { "FLOW CHUFLAY" }),
                (11m, new[] { "FLOW SIN AZUCAR" }),
                (19m, new[] { "FOUR LOCO" }),
                (2m, new[] { "GIN ROSADO" }),
                (63m, new[] { "GROSSO" }),
                (13m, new[] { "HALLS" }),
                (24m, new[] { "ICE 51" }),
                (8m, new[] { "NACHO NORMAL" }),
                (9m, new[] { "NACHOS PICANTES" }),
                (8m, new[] { "NOCHE ICE" }),
                (12m, new[] { "NACHO MAX QUESO" }),
                (10m, new[] { "PAPAS NORMALES" }),
                (0m, new[] { "PAPAS PICANTES" }),
                (28m, new[] { "PASTILLAS EUCALIPTO" }),
                (12m, new[] { "PASTILLAS MINT" }),
                (12m, new[] { "POWER CHICO" }),
                (6m, new[] { "POWER GRANDE" }),
                (20m, new[] { "PROMO AMSTEL" }),
                (30m, new[] { "PROMO AMSTEL X 3" }),
                (20m, new[] { "PROMO CONTI" }),
                (30m, new[] { "PROMO CONTI X 3" }),
                (20m, new[] { "PROMO CORONA" }),
                (50m, new[] { "PROMO PACEÑA" }),
                (17m, new[] { "PROMO VASO DE FERNET" }),
                (6m, new[] { "PAPA NAX" }),
                (8m, new[] { "PIZONES CHOCOLATE" }),
                (10m, new[] { "PIZONES PICANTES" }),
                (2m, new[] { "PLATANITO CHIPS" }),
                (1m, new[] { "QUISQUE BLACK LABEL", "WHIKY BLACK LABEL" }),
                (23m, new[] { "RED BULL" }),
                (1m, new[] { "RON DE COCO OLD" }),
                (6m, new[] { "RON FLOR DE CAÑA" }),
                (3m, new[] { "RON HABANA 7 AÑOS" }),
                (7m, new[] { "SANTE GRANDE" }),
                (7m, new[] { "SANTE PEQUEÑO" }),
                (10m, new[] { "SODA COCA COLA 2 LITROS" }),
                (7m, new[] { "SODA COCA COLA 3 LITROS" }),
                (13m, new[] { "SODA FANTA 2 LITROS" }),
                (13m, new[] { "SODA PEQUE COCA COLA VARIOS" }),
                (10m, new[] { "SODA SPRITE 2 LITROS" }),
                (7m, new[] { "TAKIS" }),
                (2m, new[] { "TEQUILA JOSE CUERVO" }),
                (51m, new[] { "VASO DE FERNET + COCA COLA 2L E 3L" }),
                (10m, new[] { "VASO DE RON" }),
                (10m, new[] { "VASO DE WISKIE", "VASO DE WHISKY" }),
                (4m, new[] { "VASO TEQUILERO" }),
                (37m, new[] { "VASOS CERVECEROS" }),
                (20m, new[] { "VASOS DE SODA" }),
                (4m, new[] { "VINO BLANCO" }),
                (17m, new[] { "VINO TINTO" }),
                (64m, new[] { "CHICLE" }),
                (15m, new[] { "CHICLE GRANDE" }),
                (30m, new[] { "CHICLE PEQUEÑO" }),
                (1m, new[] { "CHUPETE" }),
                (0m, new[] { "MABEL" }),
                (4m, new[] { "MIX NAX" }),
};


static (decimal cantidad, string[] aliases)[] StockSoloTxtV30() => new (decimal cantidad, string[] aliases)[]
{
    (9m, new[] { "AGUA 2 LITROS", "AGUA 2L" }),
    (11m, new[] { "AGUA PERSONAL CON GAS", "AGUA CON GAS" }),
    (21m, new[] { "AGUA PERSONAL SIN GAS", "AGUA SIN GAS" }),
    (11m, new[] { "AGUA TONICA" }),
    (13m, new[] { "SANTE GRANDE" }),
    (7m, new[] { "SANTE PEQUEÑO" }),
    (7m, new[] { "BLACK" }),
    (14m, new[] { "CICLON" }),
    (5m, new[] { "POWER CHICO" }),
    (4m, new[] { "POWER GRANDE" }),
    (22m, new[] { "RED BULL" }),
    (4m, new[] { "SODA COCA COLA 2 LITROS", "COCA COLA 2L" }),
    (5m, new[] { "SODA COCA COLA 3 LITROS", "COCA COLA 3L" }),
    (12m, new[] { "SODA FANTA 2 LITROS", "FANTA 2L" }),
    (36m, new[] { "SODA PEQUE COCA COLA VARIOS", "PEQUE" }),
    (15m, new[] { "SODA SPRITE 2 LITROS", "SPRITE 2L" }),
    (5m, new[] { "COCA AMAIRE N" }),
    (15m, new[] { "COCA EL BRUJO BICO STEVIA", "COCA BICO ESTEBIA" }),
    (9m, new[] { "COCA EL BRUJO MARACUYA", "COCA MARACUYA" }),
    (15m, new[] { "COCA EL BRUJO MEDUSA", "COCA MEDUSA" }),
    (28m, new[] { "COCA MEDUSA N" }),
    (8m, new[] { "COCA EL BRUJO RED BULL", "COCA REDBUL" }),
    (3m, new[] { "COCA EL BRUJO SANDIA RED BULL", "COCA SANDIA REDBUL" }),
    (8m, new[] { "COCA EL BRUJO YOGUBOLL", "COCA YOGUBOL" }),
    (4m, new[] { "COCA YOGUBOL N" }),
    (8m, new[] { "COCA EL BRUJO YOGOURT RED BULL", "COCA YOGURT REDBUL" }),
    (10m, new[] { "CIGARRO BOHEN BLACK", "BOHEM BLACK" }),
    (16m, new[] { "CIGARRO BOHEN SANDIA", "BOHEM SANDIA" }),
    (0m, new[] { "CIGARRO BOHEN YOGOURT", "BOHEM YOGURT" }),
    (9m, new[] { "CIGARRO CAMEL ACTIVA UNID", "CAMEL ACTIVA" }),
    (1m, new[] { "CIGARRO CAMEL ATIVO CHICO", "CAMEL CHICO ACTIVA" }),
    (43m, new[] { "CIGARRO CAMEL SANDIA CHICO", "CAMEL CHICO SANDIA" }),
    (13m, new[] { "CIGARRO CAMEL SANDIA GRANDE", "CAMEL SANDIA" }),
    (10m, new[] { "CIGARRO HILS", "HILLS" }),
    (10m, new[] { "CIGARRO HILLS SANDI", "HILLS SANDIA" }),
    (1m, new[] { "CERVEZA AMSTEL" }),
    (16m, new[] { "CERVEZA CORONA" }),
    (44m, new[] { "CERVEZA SKUL", "CERVEZA SKOL" }),
    (1m, new[] { "AMARULA" }),
    (2m, new[] { "FERNET" }),
    (15m, new[] { "RON FLOR DE CAÑA", "FLOR DE CAÑA" }),
    (22m, new[] { "FLOW ACHACHAIRU" }),
    (12m, new[] { "FLOW CHUFLAY" }),
    (12m, new[] { "FLOW NENE" }),
    (17m, new[] { "FOUR LOCO" }),
    (2m, new[] { "GIN ROSADO", "GIN" }),
    (3m, new[] { "RON HABANA 7 AÑOS", "HAVANA" }),
    (20m, new[] { "ICE 51" }),
    (1m, new[] { "NOCHE ICE" }),
    (0m, new[] { "RON DE COCO OLD", "OLD" }),
    (2m, new[] { "RON ABUELO", "ABUELO" }),
    (2m, new[] { "TEQUILA JOSE CUERVO", "TEQUILA" }),
    (4m, new[] { "VINO BLANCO" }),
    (14m, new[] { "VINO TINTO" }),
    (1m, new[] { "QUISQUE BLACK LABEL", "WHIKY BLACK LABEL" }),
    (2m, new[] { "COMBO FERNET" }),
    (3m, new[] { "COMBO FLOR DE CAÑA" }),
    (2m, new[] { "COMBO RON ABUELO" }),
    (10m, new[] { "VASO CHUFLAY" }),
    (10m, new[] { "VASO FERNET" }),
    (10m, new[] { "VASO FLOR DE CAÑA" }),
    (10m, new[] { "VASO RUM/RON ABUELO" }),
    (10m, new[] { "VASO TEQUILA" }),
    (10m, new[] { "VASO VINO" }),
    (10m, new[] { "VASO VINO tinto" }),
    (10m, new[] { "VASO WHISKY" }),
    (0m, new[] { "NACHO LIMON" }),
    (8m, new[] { "NACHO NORMAL" }),
    (7m, new[] { "NACHOS PICANTES", "NACHO PICANTE" }),
    (10m, new[] { "NACHO MAX QUESO", "NACHO SABOR QUESO" }),
    (13m, new[] { "PAPA CHURRAZCO" }),
    (4m, new[] { "PAPA NAX", "PAPA NAX NORMALES" }),
    (4m, new[] { "PIZONES CHOCOLATE", "PINZONES CHOCOLATE" }),
    (9m, new[] { "PIZONES PICANTES", "PIZONES PICANTE" }),
    (5m, new[] { "PLATANITO CHIPS", "PLATANITO" }),
    (0m, new[] { "TAKIS" }),
    (0m, new[] { "ARCOR" }),
    (0m, new[] { "BELDEN" }),
    (44m, new[] { "CHICLE" }),
    (11m, new[] { "CHICLE GRANDE" }),
    (10m, new[] { "CHICLE PEQUEÑO" }),
    (0m, new[] { "CHUPETE" }),
    (73m, new[] { "CLORETS" }),
    (14m, new[] { "COCA EL BRUJO CHICLE", "COCA CHICLE" }),
    (4m, new[] { "COCA CHICLE N" }),
    (12m, new[] { "PASTILLAS EUCALIPTO", "EUCALIPTO" }),
    (24m, new[] { "GROSSO", "GROSO" }),
    (10m, new[] { "HALLS" }),
    (0m, new[] { "MABEL" }),
    (6m, new[] { "PASTILLAS MINT", "MINT" }),
    (4m, new[] { "DOCILE MINTY", "MINTY" }),
    (2m, new[] { "ALIKAL" }),
    (15m, new[] { "COPAS DE VINO", "COPA" }),
    (14m, new[] { "MESAS" }),
    (30m, new[] { "SILLAS" }),
    (10m, new[] { "VASO DE WISKIE", "VASOS DE WISKI" }),
    (4m, new[] { "VASO TEQUILERO", "VASOS TEQUILERO" }),
    (86m, new[] { "VICO" }),
};

static (string nombre, string categoria, decimal cantidad, decimal precio, bool sinLimiteStock)[] CatalogoFinalV29() => new (string nombre, string categoria, decimal cantidad, decimal precio, bool sinLimiteStock)[]
{
    ("AGUA 2L", "Agua", 9m, 20.00m, false),
    ("AGUA CON GAS", "Agua", 11m, 10.00m, false),
    ("AGUA SIN GAS", "Agua", 21m, 10.00m, false),
    ("AGUA TONICA", "Agua", 11m, 20.00m, false),
    ("SANTE GRANDE", "Agua", 13m, 25.00m, false),
    ("SANTE PEQUEÑO", "Agua", 7m, 18.00m, false),
    ("BLACK", "Energizantes", 7m, 20.00m, false),
    ("CICLON", "Energizantes", 14m, 20.00m, false),
    ("POWER CHICO", "Energizantes", 5m, 15.00m, false),
    ("POWER GRANDE", "Energizantes", 4m, 25.00m, false),
    ("RED BULL", "Energizantes", 22m, 30.00m, false),
    ("COCA COLA 2L", "Sodas", 4m, 25.00m, false),
    ("COCA COLA 3L", "Sodas", 5m, 30.00m, false),
    ("FANTA 2L", "Sodas", 12m, 25.00m, false),
    ("PEQUE", "Sodas", 36m, 6.00m, false),
    ("SPRITE 2L", "Sodas", 15m, 25.00m, false),
    ("COCA AMAIRE N", "Coca machucada", 5m, 55.00m, false),
    ("COCA BICO ESTEBIA", "Coca machucada", 15m, 55.00m, false),
    ("COCA MARACUYA", "Coca machucada", 9m, 55.00m, false),
    ("COCA MEDUSA", "Coca machucada", 15m, 65.00m, false),
    ("COCA MEDUSA N", "Coca machucada", 28m, 65.00m, false),
    ("COCA REDBUL", "Coca machucada", 8m, 55.00m, false),
    ("COCA SANDIA REDBUL", "Coca machucada", 3m, 55.00m, false),
    ("COCA YOGUBOL", "Coca machucada", 8m, 55.00m, false),
    ("COCA YOGUBOL N", "Coca machucada", 4m, 55.00m, false),
    ("COCA YOGURT REDBUL", "Coca machucada", 8m, 55.00m, false),
    ("BOHEM BLACK", "Cigarros", 10m, 30.00m, false),
    ("BOHEM SANDIA", "Cigarros", 16m, 25.00m, false),
    ("BOHEM YOGURT", "Cigarros", 0m, 25.00m, false),
    ("CAMEL ACTIVA", "Cigarros", 9m, 2.00m, false),
    ("CAMEL CHICO ACTIVA", "Cigarros", 1m, 18.00m, false),
    ("CAMEL CHICO SANDIA", "Cigarros", 43m, 20.00m, false),
    ("CAMEL SANDIA", "Cigarros", 13m, 30.00m, false),
    ("HILLS", "Cigarros", 10m, 18.00m, false),
    ("HILLS SANDIA", "Cigarros", 10m, 18.00m, false),
    ("CERVEZA AMSTEL", "Cervezas", 1m, 22.00m, false),
    ("CERVEZA CORONA", "Cervezas", 16m, 25.00m, false),
    ("CERVEZA SKOL", "Cervezas", 44m, 10.00m, false),
    ("AMARULA", "Tragos / Botellas", 1m, 50.00m, false),
    ("FERNET", "Tragos / Botellas", 2m, 275.00m, false),
    ("FLOR DE CAÑA", "Tragos / Botellas", 15m, 275.00m, false),
    ("FLOW ACHACHAIRU", "Tragos / Botellas", 22m, 25.00m, false),
    ("FLOW CHUFLAY", "Tragos / Botellas", 12m, 25.00m, false),
    ("FLOW NENE", "Tragos / Botellas", 12m, 25.00m, false),
    ("FOUR LOCO", "Tragos / Botellas", 17m, 70.00m, false),
    ("GIN", "Tragos / Botellas", 2m, 275.00m, false),
    ("HAVANA", "Tragos / Botellas", 3m, 425.00m, false),
    ("ICE 51", "Tragos / Botellas", 20m, 30.00m, false),
    ("NOCHE ICE", "Tragos / Botellas", 1m, 25.00m, false),
    ("OLD", "Tragos / Botellas", 0m, 300.00m, false),
    ("RON ABUELO", "Tragos / Botellas", 2m, 300.00m, false),
    ("TEQUILA", "Tragos / Botellas", 2m, 200.00m, false),
    ("VINO BLANCO", "Tragos / Botellas", 4m, 50.00m, false),
    ("VINO TINTO", "Tragos / Botellas", 14m, 50.00m, false),
    ("WHIKY BLACK LABEL", "Tragos / Botellas", 1m, 800.00m, false),
    ("COMBO FERNET", "Combos / Promos", 2m, 320.00m, false),
    ("COMBO FLOR DE CAÑA", "Combos / Promos", 3m, 310.00m, false),
    ("COMBO RON ABUELO", "Combos / Promos", 2m, 340.00m, false),
    ("VASO CHUFLAY", "Servidos en vaso", 10m, 25.00m, false),
    ("VASO FERNET", "Servidos en vaso", 10m, 25.00m, false),
    ("VASO FLOR DE CAÑA", "Servidos en vaso", 10m, 25.00m, false),
    ("VASO RUM/RON ABUELO", "Servidos en vaso", 10m, 25.00m, false),
    ("VASO TEQUILA", "Servidos en vaso", 10m, 20.00m, false),
    ("VASO VINO", "Servidos en vaso", 10m, 15.00m, false),
    ("VASO VINO tinto", "Servidos en vaso", 10m, 15.00m, false),
    ("VASO WHISKY", "Servidos en vaso", 10m, 35.00m, false),
    ("NACHO LIMON", "Snacks y piqueos", 0m, 5.00m, false),
    ("NACHO NORMAL", "Snacks y piqueos", 8m, 5.00m, false),
    ("NACHO PICANTE", "Snacks y piqueos", 7m, 5.00m, false),
    ("NACHO SABOR QUESO", "Snacks y piqueos", 10m, 5.00m, false),
    ("PAPA CHURRAZCO", "Snacks y piqueos", 13m, 5.00m, false),
    ("PAPA NAX NORMALES", "Snacks y piqueos", 4m, 5.00m, false),
    ("PINZONES CHOCOLATE", "Snacks y piqueos", 4m, 5.00m, false),
    ("PIZONES PICANTE", "Snacks y piqueos", 9m, 5.00m, false),
    ("PLATANITO", "Snacks y piqueos", 5m, 5.00m, false),
    ("TAKIS", "Snacks y piqueos", 0m, 8.00m, false),
    ("ARCOR", "Dulces y golosinas", 0m, 1.00m, false),
    ("BELDEN", "Dulces y golosinas", 0m, 8.00m, false),
    ("CHICLE", "Dulces y golosinas", 44m, 1.00m, false),
    ("CHICLE GRANDE", "Dulces y golosinas", 11m, 4.00m, false),
    ("CHICLE PEQUEÑO", "Dulces y golosinas", 10m, 1.00m, false),
    ("CHUPETE", "Dulces y golosinas", 0m, 2.00m, false),
    ("CLORETS", "Dulces y golosinas", 73m, 1.00m, false),
    ("COCA CHICLE", "Dulces y golosinas", 14m, 55.00m, false),
    ("COCA CHICLE N", "Dulces y golosinas", 4m, 55.00m, false),
    ("EUCALIPTO", "Dulces y golosinas", 12m, 0.50m, false),
    ("GROSO", "Dulces y golosinas", 24m, 1.00m, false),
    ("HALLS", "Dulces y golosinas", 10m, 8.00m, false),
    ("MABEL", "Dulces y golosinas", 0m, 6.00m, false),
    ("MINT", "Dulces y golosinas", 6m, 0.50m, false),
    ("MINTY", "Dulces y golosinas", 4m, 5.00m, false),
    ("ALIKAL", "Otros / Extras", 2m, 10.00m, false),
    ("COPA", "Otros / Extras", 15m, 10.00m, false),
    ("MESAS", "Otros / Extras", 14m, 0.00m, false),
    ("SILLAS", "Otros / Extras", 30m, 0.00m, false),
    ("VASOS DE WISKI", "Otros / Extras", 10m, 10.00m, false),
    ("VASOS TEQUILERO", "Otros / Extras", 4m, 10.00m, false),
    ("VICO", "Otros / Extras", 86m, 1.00m, false)
};

static (string nombre, string categoria, decimal precio)[] CatalogoProductosLocalV19() => new (string nombre, string categoria, decimal precio)[]
{
    ("AGUA 2 LITROS", "Bebidas", 20.00m),
    ("AGUA PERSONAL CON GAS", "Bebidas", 10.00m),
    ("AGUA PERSONAL SIN GAS", "Bebidas", 10.00m),
    ("AGUA TONICA", "Bebidas", 20.00m),
    ("CICLON", "Bebidas", 20.00m),
    ("COCA EL BRUJO MARACUYA", "Bebidas", 25.00m),
    ("COCA EL BRUJO MEDUSA", "Bebidas", 35.00m),
    ("COCA EL BRUJO RED BULL", "Bebidas", 25.00m),
    ("COCA EL BRUJO SANDIA RED BULL", "Bebidas", 25.00m),
    ("COCA EL BRUJO YOGOURT RED BULL", "Bebidas", 25.00m),
    ("COCA EL BRUJO YOGUBOLL", "Bebidas", 25.00m),
    ("FLOW ACHACHAIRU", "Bebidas", 25.00m),
    ("FLOW CHUFLAY", "Bebidas", 25.00m),
    ("FLOW SIN AZUCAR", "Bebidas", 25.00m),
    ("POWER CHICO", "Bebidas", 18.00m),
    ("POWER GRANDE", "Bebidas", 25.00m),
    ("RED BULL", "Bebidas", 30.00m),
    ("SODA COCA COLA 2 LITROS", "Bebidas", 25.00m),
    ("SODA COCA COLA 3 LITROS", "Bebidas", 30.00m),
    ("SODA FANTA 2 LITROS", "Bebidas", 25.00m),
    ("SODA PEQUE COCA COLA VARIOS", "Bebidas", 6.00m),
    ("SODA SPRITE 2 LITROS", "Bebidas", 25.00m),
    ("VASOS DE SODA", "Bebidas", 10.00m),
    ("CERVEZA AMSTEL", "Cervezas", 22.00m),
    ("CERVEZA CONTI", "Cervezas", 20.00m),
    ("CERVEZA CORONA", "Cervezas", 25.00m),
    ("CERVEZA PACEÑA", "Cervezas", 30.00m),
    ("CERVEZA SKUL", "Cervezas", 10.00m),
    ("BLACK", "Botellas/Tragos", 20.00m),
    ("FERNET", "Botellas/Tragos", 275.00m),
    ("FOUR LOCO", "Botellas/Tragos", 70.00m),
    ("GIN ROSADO", "Botellas/Tragos", 275.00m),
    ("ICE 51", "Botellas/Tragos", 30.00m),
    ("NOCHE ICE", "Botellas/Tragos", 25.00m),
    ("QUISQUE BLACK LABEL", "Botellas/Tragos", 800.00m),
    ("RON ABUELO", "Botellas/Tragos", 300.00m),
    ("RON DE COCO OLD", "Botellas/Tragos", 300.00m),
    ("RON FLOR DE CAÑA", "Botellas/Tragos", 275.00m),
    ("RON HABANA 7 AÑOS", "Botellas/Tragos", 425.00m),
    ("TEQUILA JOSE CUERVO", "Botellas/Tragos", 200.00m),
    ("VASO DE FERNET + COCA COLA 2L E 3L", "Botellas/Tragos", 20.00m),
    ("VASO DE RON", "Botellas/Tragos", 20.00m),
    ("VASO DE WISKIE", "Botellas/Tragos", 10.00m),
    ("VINO BLANCO", "Botellas/Tragos", 50.00m),
    ("VINO TINTO", "Botellas/Tragos", 50.00m),
    ("CIGARRO BOHEM DOUBLE GRANDE", "Cigarros", 30.00m),
    ("CIGARRO BOHEM UND", "Cigarros", 2.00m),
    ("CIGARRO BOHEN BLACK", "Cigarros", 30.00m),
    ("CIGARRO BOHEN SANDIA", "Cigarros", 25.00m),
    ("CIGARRO BOHEN UNIDAD", "Cigarros", 2.00m),
    ("CIGARRO BOHEN YOGOURT", "Cigarros", 25.00m),
    ("CIGARRO CAMEL ACTIVA UNID", "Cigarros", 2.00m),
    ("CIGARRO CAMEL ATIVO CHICO", "Cigarros", 18.00m),
    ("CIGARRO CAMEL GRANDE ACTIVA", "Cigarros", 30.00m),
    ("CIGARRO CAMEL SANDI UNIDAD", "Cigarros", 2.00m),
    ("CIGARRO CAMEL SANDIA CHICO", "Cigarros", 20.00m),
    ("CIGARRO CAMEL SANDIA GRANDE", "Cigarros", 30.00m),
    ("CIGARRO HILLS SANDI", "Cigarros", 18.00m),
    ("CIGARRO HILS", "Cigarros", 18.00m),
    ("BICO SABORES", "Dulces", 5.00m),
    ("CHICLE", "Dulces", 1.00m),
    ("CHICLE GRANDE", "Dulces", 4.00m),
    ("CHICLE PEQUEÑO", "Dulces", 1.00m),
    ("CHUPETE", "Dulces", 2.00m),
    ("CLORETS", "Dulces", 1.00m),
    ("COCA EL BRUJO BICO STEVIA", "Dulces", 25.00m),
    ("COCA EL BRUJO CHICLE", "Dulces", 25.00m),
    ("DOCILE MINTY", "Dulces", 5.00m),
    ("GROSSO", "Dulces", 1.00m),
    ("HALLS", "Dulces", 8.00m),
    ("MABEL", "Dulces", 6.00m),
    ("PASTILLAS EUCALIPTO", "Dulces", 0.50m),
    ("PASTILLAS MINT", "Dulces", 0.50m),
    ("MIX NAX", "Snacks", 8.00m),
    ("NACHO MAX QUESO", "Snacks", 5.00m),
    ("NACHO NORMAL", "Snacks", 5.00m),
    ("NACHOS PICANTES", "Snacks", 5.00m),
    ("PAPA NAX", "Snacks", 5.00m),
    ("PAPAS NORMALES", "Snacks", 5.00m),
    ("PAPAS PICANTES", "Snacks", 5.00m),
    ("PIZONES CHOCOLATE", "Snacks", 5.00m),
    ("PIZONES PICANTES", "Snacks", 5.00m),
    ("PLATANITO CHIPS", "Snacks", 5.00m),
    ("SANTE GRANDE", "Snacks", 25.00m),
    ("SANTE PEQUEÑO", "Snacks", 18.00m),
    ("TAKIS", "Snacks", 8.00m),
    ("CINCERO", "Vasos/Accesorios", 10.00m),
    ("COPAS DE VINO", "Vasos/Accesorios", 10.00m),
    ("ENCENDEDOR", "Vasos/Accesorios", 3.00m),
    ("VASO TEQUILERO", "Vasos/Accesorios", 10.00m),
    ("VASOS CERVECEROS", "Vasos/Accesorios", 10.00m),
    ("ALIKAL", "Varios", 10.00m),
    ("BELDEN", "Varios", 8.00m)
};

static (string nombre, string categoria, decimal precio, string detalle)[] CatalogoCombosPromosLocalV19() => new (string nombre, string categoria, decimal precio, string detalle)[]
{
    ("COMBO FERNET", "Combo", 300.00m, "1x FERNET + 1x SODA COCA COLA 2 LITROS"),
    ("COMBO FLOR DE CAÑA", "Combo", 300.00m, "1x RON FLOR DE CAÑA + 1x SODA COCA COLA 2 LITROS"),
    ("COMBO GIN", "Combo", 300.00m, "1x SANTE GRANDE + 1x GIN ROSADO"),
    ("COMBO HABANA", "Combo", 450.00m, "1x RON HABANA 7 AÑOS + 1x SODA COCA COLA 2 LITROS"),
    ("PROMO AMSTEL", "Promoción", 100.00m, "5x CERVEZA AMSTEL"),
    ("PROMO AMSTEL X 3", "Promoción", 60.00m, "3x CERVEZA AMSTEL"),
    ("PROMO CONTI", "Promoción", 100.00m, "5x CERVEZA CONTI"),
    ("PROMO CONTI X 3", "Promoción", 60.00m, "3x CERVEZA CONTI"),
    ("PROMO CORONA", "Promoción", 110.00m, "5x CERVEZA CORONA"),
    ("PROMO PACEÑA", "Promoción", 120.00m, "5x CERVEZA PACEÑA"),
    ("PROMO VASO DE FERNET", "Promoción", 15.00m, "1x VASO DE FERNET")
};

static async Task<bool> UpsertCatalogoFinalV29(MySqlConnection con, int sucursalId, string nombre, string categoria, decimal cantidad, decimal precio, bool sinLimiteStock)
{
    categoria = NormalizarCategoriaProducto(categoria, nombre);
    long productoId = 0;

    await using (var buscar = new MySqlCommand("SELECT id FROM productos WHERE sucursal_id = @sucursal_id AND nombre = @nombre LIMIT 1;", con))
    {
        buscar.Parameters.AddWithValue("@sucursal_id", sucursalId);
        buscar.Parameters.AddWithValue("@nombre", nombre);
        object? found = await buscar.ExecuteScalarAsync();
        if (found != null) productoId = Convert.ToInt64(found);
    }

    bool existed = productoId > 0;
    decimal stockActual = sinLimiteStock ? 0 : cantidad;
    decimal stockMinimo = sinLimiteStock ? 0 : 30;
    string unidadBase = sinLimiteStock ? "SIN LÍMITE" : "UNIDAD";

    if (!existed)
    {
        await using var cmd = new MySqlCommand("""
            INSERT INTO productos
                (sucursal_id, nombre, categoria, unidad_base, stock_actual, stock_minimo, estado, genera_comision, tipo_comision, valor_comision, sin_limite_stock)
            VALUES
                (@sucursal_id, @nombre, @categoria, @unidad_base, @stock_actual, @stock_minimo, 'ACTIVO', @genera_comision, @tipo_comision, @valor_comision, @sin_limite_stock);
            SELECT LAST_INSERT_ID();
        """, con);
        cmd.Parameters.AddWithValue("@sucursal_id", sucursalId);
        cmd.Parameters.AddWithValue("@nombre", nombre);
        cmd.Parameters.AddWithValue("@categoria", categoria);
        cmd.Parameters.AddWithValue("@unidad_base", unidadBase);
        cmd.Parameters.AddWithValue("@stock_actual", stockActual);
        cmd.Parameters.AddWithValue("@stock_minimo", stockMinimo);
        cmd.Parameters.AddWithValue("@genera_comision", EsProductoConComision(nombre));
        cmd.Parameters.AddWithValue("@tipo_comision", EsProductoConComision(nombre) ? "PORCENTAJE" : "NINGUNA");
        cmd.Parameters.AddWithValue("@valor_comision", EsProductoConComision(nombre) ? 10 : 0);
        cmd.Parameters.AddWithValue("@sin_limite_stock", sinLimiteStock ? 1 : 0);
        productoId = Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }
    else
    {
        await using var cmd = new MySqlCommand("""
            UPDATE productos
            SET categoria = @categoria,
                unidad_base = @unidad_base,
                stock_actual = @stock_actual,
                stock_minimo = @stock_minimo,
                estado = 'ACTIVO',
                sin_limite_stock = @sin_limite_stock
            WHERE id = @id;
        """, con);
        cmd.Parameters.AddWithValue("@categoria", categoria);
        cmd.Parameters.AddWithValue("@unidad_base", unidadBase);
        cmd.Parameters.AddWithValue("@stock_actual", stockActual);
        cmd.Parameters.AddWithValue("@stock_minimo", stockMinimo);
        cmd.Parameters.AddWithValue("@sin_limite_stock", sinLimiteStock ? 1 : 0);
        cmd.Parameters.AddWithValue("@id", productoId);
        await cmd.ExecuteNonQueryAsync();
    }

    long presId = 0;
    await using (var buscarPres = new MySqlCommand("SELECT id FROM presentaciones WHERE producto_id = @producto_id AND estado = 'ACTIVO' LIMIT 1;", con))
    {
        buscarPres.Parameters.AddWithValue("@producto_id", productoId);
        object? foundPres = await buscarPres.ExecuteScalarAsync();
        if (foundPres != null) presId = Convert.ToInt64(foundPres);
    }

    if (presId <= 0)
    {
        await using var cmd = new MySqlCommand("""
            INSERT INTO presentaciones (producto_id, nombre, cantidad_base, precio_venta, estado)
            VALUES (@producto_id, 'Unidad', 1, @precio_venta, 'ACTIVO');
        """, con);
        cmd.Parameters.AddWithValue("@producto_id", productoId);
        cmd.Parameters.AddWithValue("@precio_venta", precio);
        await cmd.ExecuteNonQueryAsync();
    }
    else
    {
        await using var cmd = new MySqlCommand("""
            UPDATE presentaciones
            SET nombre = 'Unidad',
                cantidad_base = 1,
                precio_venta = @precio_venta,
                estado = 'ACTIVO'
            WHERE id = @id;
        """, con);
        cmd.Parameters.AddWithValue("@precio_venta", precio);
        cmd.Parameters.AddWithValue("@id", presId);
        await cmd.ExecuteNonQueryAsync();
    }

    return existed;
}

static async Task<bool> UpsertCatalogoProductoLocal(MySqlConnection con, int sucursalId, string nombre, string categoria, decimal precio, string presentacion, int minimo)
{
    categoria = NormalizarCategoriaProducto(categoria, nombre);
    long productoId = 0;

    await using (var buscar = new MySqlCommand("SELECT id FROM productos WHERE sucursal_id = @sucursal_id AND nombre = @nombre LIMIT 1;", con))
    {
        buscar.Parameters.AddWithValue("@sucursal_id", sucursalId);
        buscar.Parameters.AddWithValue("@nombre", nombre);
        object? found = await buscar.ExecuteScalarAsync();
        if (found != null) productoId = Convert.ToInt64(found);
    }

    bool existed = productoId > 0;

    if (!existed)
    {
        await using var cmd = new MySqlCommand("""
            INSERT INTO productos
                (sucursal_id, nombre, categoria, unidad_base, stock_actual, stock_minimo, estado, genera_comision, tipo_comision, valor_comision)
            VALUES
                (@sucursal_id, @nombre, @categoria, 'UNIDAD', 0, @minimo, 'ACTIVO', @genera_comision, @tipo_comision, @valor_comision);
            SELECT LAST_INSERT_ID();
        """, con);
        cmd.Parameters.AddWithValue("@sucursal_id", sucursalId);
        cmd.Parameters.AddWithValue("@nombre", nombre);
        cmd.Parameters.AddWithValue("@categoria", categoria);
        cmd.Parameters.AddWithValue("@minimo", minimo);
        cmd.Parameters.AddWithValue("@genera_comision", EsProductoConComision(nombre));
        cmd.Parameters.AddWithValue("@tipo_comision", EsProductoConComision(nombre) ? "PORCENTAJE" : "NINGUNA");
        cmd.Parameters.AddWithValue("@valor_comision", EsProductoConComision(nombre) ? 10 : 0);
        productoId = Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }
    else
    {
        await using var cmd = new MySqlCommand("""
            UPDATE productos
            SET categoria = @categoria,
                unidad_base = 'UNIDAD',
                stock_minimo = @minimo,
                estado = 'ACTIVO',
                genera_comision = @genera_comision,
                tipo_comision = @tipo_comision,
                valor_comision = @valor_comision
            WHERE id = @id;
        """, con);
        cmd.Parameters.AddWithValue("@categoria", categoria);
        cmd.Parameters.AddWithValue("@minimo", minimo);
        cmd.Parameters.AddWithValue("@genera_comision", EsProductoConComision(nombre));
        cmd.Parameters.AddWithValue("@tipo_comision", EsProductoConComision(nombre) ? "PORCENTAJE" : "NINGUNA");
        cmd.Parameters.AddWithValue("@valor_comision", EsProductoConComision(nombre) ? 10 : 0);
        cmd.Parameters.AddWithValue("@id", productoId);
        await cmd.ExecuteNonQueryAsync();
    }

    long presId = 0;
    await using (var buscarPres = new MySqlCommand("SELECT id FROM presentaciones WHERE producto_id = @producto_id AND estado = 'ACTIVO' LIMIT 1;", con))
    {
        buscarPres.Parameters.AddWithValue("@producto_id", productoId);
        object? foundPres = await buscarPres.ExecuteScalarAsync();
        if (foundPres != null) presId = Convert.ToInt64(foundPres);
    }

    if (presId <= 0)
    {
        await using var cmd = new MySqlCommand("""
            INSERT INTO presentaciones (producto_id, nombre, cantidad_base, precio_venta, estado)
            VALUES (@producto_id, @nombre, 1, @precio_venta, 'ACTIVO');
        """, con);
        cmd.Parameters.AddWithValue("@producto_id", productoId);
        cmd.Parameters.AddWithValue("@nombre", presentacion);
        cmd.Parameters.AddWithValue("@precio_venta", precio);
        await cmd.ExecuteNonQueryAsync();
    }
    else
    {
        await using var cmd = new MySqlCommand("""
            UPDATE presentaciones
            SET nombre = @nombre,
                cantidad_base = 1,
                precio_venta = @precio_venta,
                estado = 'ACTIVO'
            WHERE id = @id;
        """, con);
        cmd.Parameters.AddWithValue("@nombre", presentacion);
        cmd.Parameters.AddWithValue("@precio_venta", precio);
        cmd.Parameters.AddWithValue("@id", presId);
        await cmd.ExecuteNonQueryAsync();
    }

    return existed;
}

static bool EsProductoConComision(string nombre)
{
    string n = (nombre ?? "").ToUpperInvariant();
    return n.Contains("RON") || n.Contains("TEQUILA") || n.Contains("GIN") || n.Contains("FERNET") || n.Contains("WHISK") || n.Contains("WISKIE") || n.Contains("ABUELO") || n.Contains("HABANA") || n.Contains("BLACK LABEL");
}


static async Task UpdateUserPasswordHash(MySqlConnection con, int userId, string plainPassword)
{
    await using var cmd = new MySqlCommand("UPDATE usuarios SET clave = @clave WHERE id = @id;", con);
    cmd.Parameters.AddWithValue("@clave", PasswordHasher.Hash(plainPassword));
    cmd.Parameters.AddWithValue("@id", userId);
    await cmd.ExecuteNonQueryAsync();
}

static async Task HashPlainUserPasswords(MySqlConnection con)
{
    var pendientes = new List<(int id, string clave)>();

    await using (var cmd = new MySqlCommand("SELECT id, clave FROM usuarios;", con))
    await using (var rd = await cmd.ExecuteReaderAsync())
    {
        while (await rd.ReadAsync())
        {
            string clave = rd.IsDBNull(rd.GetOrdinal("clave")) ? "" : rd.GetString("clave");
            if (!PasswordHasher.IsHashed(clave))
                pendientes.Add((rd.GetInt32("id"), clave));
        }
    }

    foreach (var item in pendientes)
    {
        await using var update = new MySqlCommand("UPDATE usuarios SET clave = @clave WHERE id = @id;", con);
        update.Parameters.AddWithValue("@clave", PasswordHasher.Hash(item.clave));
        update.Parameters.AddWithValue("@id", item.id);
        await update.ExecuteNonQueryAsync();
    }
}


static async Task<(bool ok, string message)> AcquireSaleGuardsAsync(MySqlConnection con, MySqlTransaction tx, VentaRequest venta, string operationKey)
{
    static string HashKey(string value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value ?? ""));
        return Convert.ToHexString(hash).Substring(0, 32);
    }

    var lockNames = new SortedSet<string>(StringComparer.Ordinal);
    lockNames.Add("SALEOP_" + HashKey(operationKey));

    string tipo = (venta.Tipo ?? "").Trim().ToUpperInvariant();
    if (tipo == "MESA" && venta.SessionId.HasValue && venta.SessionId.Value > 0)
        lockNames.Add("MESAFINAL_" + HashKey(
            venta.SucursalId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" +
            (venta.CajaNombre ?? "").Trim().ToUpperInvariant() + "|" +
            venta.SessionId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    if (venta.Detalle != null)
    {
        foreach (var d in venta.Detalle)
        {
            string ck = (d.ConsumptionKey ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(ck))
                lockNames.Add("CONSUMO_" + HashKey(ck));
        }
    }

    foreach (string lockName in lockNames)
    {
        await using var cmd = new MySqlCommand("SELECT GET_LOCK(@name, 10);", con, tx);
        cmd.Parameters.AddWithValue("@name", lockName.Length > 64 ? lockName.Substring(0, 64) : lockName);
        object? result = await cmd.ExecuteScalarAsync();
        if (result == null || Convert.ToInt32(result) != 1)
            return (false, "Otra operación igual se está procesando. El cobro fue bloqueado temporalmente para evitar duplicación; vuelva a intentar una sola vez.");
    }

    return (true, "OK");
}

static async Task EnsureLedgerForExistingSaleAsync(MySqlConnection con, MySqlTransaction tx, long ventaId, VentaRequest venta, string syncKey, string operationKey)
{
    await using var cmd = new MySqlCommand("""
        INSERT IGNORE INTO libro_caja
        (venta_id, sucursal_id, cajero, fecha, tipo, metodo_pago, efectivo, qr, total, sync_key, operation_key, session_id, estado)
        VALUES
        (@venta_id, @sucursal_id, @cajero, @fecha, @tipo, @metodo_pago, @efectivo, @qr, @total, @sync_key, NULLIF(@operation_key,''), @session_id, 'CONFIRMADA');
    """, con, tx);
    cmd.Parameters.AddWithValue("@venta_id", ventaId);
    cmd.Parameters.AddWithValue("@sucursal_id", venta.SucursalId);
    cmd.Parameters.AddWithValue("@cajero", venta.Cajero ?? "");
    cmd.Parameters.AddWithValue("@fecha", venta.Fecha);
    cmd.Parameters.AddWithValue("@tipo", venta.Tipo ?? "VENTA");
    cmd.Parameters.AddWithValue("@metodo_pago", venta.MetodoPago ?? "");
    cmd.Parameters.AddWithValue("@efectivo", Math.Max(0, venta.Efectivo));
    cmd.Parameters.AddWithValue("@qr", Math.Max(0, venta.Qr));
    cmd.Parameters.AddWithValue("@total", venta.Total);
    cmd.Parameters.AddWithValue("@sync_key", syncKey);
    cmd.Parameters.AddWithValue("@operation_key", operationKey);
    cmd.Parameters.AddWithValue("@session_id", venta.SessionId.HasValue ? venta.SessionId.Value : DBNull.Value);
    await cmd.ExecuteNonQueryAsync();
}

static async Task EnsureAccountingLedger(MySqlConnection con)
{
    await using (var cmd = new MySqlCommand("""
        CREATE TABLE IF NOT EXISTS libro_caja (
            id BIGINT AUTO_INCREMENT PRIMARY KEY,
            venta_id BIGINT NOT NULL,
            sucursal_id INT NOT NULL,
            cajero VARCHAR(100) NOT NULL,
            caja_nombre VARCHAR(60) NULL,
            turno VARCHAR(20) NULL,
            fecha DATETIME NOT NULL,
            tipo VARCHAR(60) NOT NULL,
            metodo_pago VARCHAR(30) NOT NULL,
            efectivo DECIMAL(12,2) NOT NULL DEFAULT 0,
            qr DECIMAL(12,2) NOT NULL DEFAULT 0,
            total DECIMAL(12,2) NOT NULL,
            sync_key VARCHAR(220) NOT NULL,
            operation_key VARCHAR(220) NULL,
            session_id INT NULL,
            estado VARCHAR(20) NOT NULL DEFAULT 'CONFIRMADA',
            creado_en TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
            UNIQUE KEY uk_libro_venta (venta_id),
            UNIQUE KEY uk_libro_sync (sync_key),
            UNIQUE KEY uk_libro_operation (operation_key)
        );
    """, con)) await cmd.ExecuteNonQueryAsync();
    try { await new MySqlCommand("ALTER TABLE libro_caja ADD COLUMN session_id INT NULL AFTER operation_key;", con).ExecuteNonQueryAsync(); } catch { }

    await using (var cmd = new MySqlCommand("""
        CREATE TABLE IF NOT EXISTS auditoria_contable (
            id BIGINT AUTO_INCREMENT PRIMARY KEY,
            fecha DATETIME NOT NULL,
            usuario VARCHAR(100) NOT NULL,
            sucursal_id INT NOT NULL,
            accion VARCHAR(80) NOT NULL,
            entidad VARCHAR(80) NOT NULL,
            entidad_id BIGINT NOT NULL,
            detalle TEXT NULL
        );
    """, con)) await cmd.ExecuteNonQueryAsync();
}


static string BuildSaleLineKey(string operationKey, string syncKey, int index, VentaDetalleRequest d)
{
    string raw = string.Join("|",
        string.IsNullOrWhiteSpace(operationKey) ? syncKey : operationKey,
        index.ToString(System.Globalization.CultureInfo.InvariantCulture),
        (d.Producto ?? "").Trim().ToUpperInvariant(),
        (d.Presentacion ?? "").Trim().ToUpperInvariant(),
        d.Cantidad.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture),
        d.CantidadBase.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture),
        d.PrecioUnitario.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
        d.Subtotal.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));

    using var sha = SHA256.Create();
    return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
}

static async Task EnsureVentaSyncProtection(MySqlConnection con)
{
    // V41: protege ventas por sync_key. No borra datos existentes automaticamente.
    // Si ya existen duplicados historicos, la migracion V41 incluida debe ejecutarse una sola vez.
    try
    {
        await using var normalize = new MySqlCommand("""
            UPDATE ventas
            SET sync_key = CONCAT('LEGACY-VENTA-', id)
            WHERE sync_key IS NULL OR TRIM(sync_key) = '';
        """, con);
        await normalize.ExecuteNonQueryAsync();
    }
    catch { }

    try
    {
        await using var idx = new MySqlCommand("""
            ALTER TABLE ventas
            ADD UNIQUE KEY uk_ventas_sync_key (sync_key);
        """, con);
        await idx.ExecuteNonQueryAsync();
    }
    catch
    {
        // Si falla por duplicados historicos, la API sigue operativa.
        // Ejecutar MIGRACION_V41_UNIQUE_SYNC_KEY.sql para respaldar y consolidar duplicados.
    }

    // V43: segunda identidad de seguridad. operation_key representa el COBRO de negocio,
    // no el intento de sincronización. Dos PCs no pueden confirmar la misma operación.
    try { await new MySqlCommand("ALTER TABLE ventas ADD COLUMN operation_key VARCHAR(220) NULL AFTER sync_key;", con).ExecuteNonQueryAsync(); } catch { }
    try { await new MySqlCommand("ALTER TABLE ventas ADD UNIQUE KEY uk_ventas_operation_key (operation_key);", con).ExecuteNonQueryAsync(); } catch { }
    // V59: legacy_fingerprint debe existir ANTES de agregar session_id. En bases antiguas el orden
    // anterior podía dejar session_id sin crear si legacy_fingerprint todavía no existía.
    try { await new MySqlCommand("ALTER TABLE ventas ADD COLUMN legacy_fingerprint VARCHAR(64) NULL AFTER operation_key;", con).ExecuteNonQueryAsync(); } catch { }
    // V48: relación explícita con sesión de mesa y clave de consumo cobrado.
    try { await new MySqlCommand("ALTER TABLE ventas ADD COLUMN session_id INT NULL AFTER legacy_fingerprint;", con).ExecuteNonQueryAsync(); } catch { }
    try { await new MySqlCommand("ALTER TABLE ventas ADD INDEX idx_ventas_session (sucursal_id, session_id, tipo);", con).ExecuteNonQueryAsync(); } catch { }
    // V56: conservar caja y turno EXACTOS de la venta.
    try { await new MySqlCommand("ALTER TABLE ventas ADD COLUMN caja_nombre VARCHAR(60) NULL AFTER cajero;", con).ExecuteNonQueryAsync(); } catch { }
    try { await new MySqlCommand("ALTER TABLE ventas ADD COLUMN turno VARCHAR(20) NULL AFTER caja_nombre;", con).ExecuteNonQueryAsync(); } catch { }

    // V56: backfill histórico. El arqueo no depende de cómo esté configurado hoy el usuario:
    // el turno se deriva de la hora REAL del cobro y la caja se toma del usuario cuando falta.
    try
    {
        await new MySqlCommand("""
            UPDATE ventas v
            LEFT JOIN usuarios u ON u.usuario = v.cajero AND u.sucursal_id = v.sucursal_id
            SET v.caja_nombre = CASE
                    WHEN v.caja_nombre IS NOT NULL AND TRIM(v.caja_nombre) <> '' THEN v.caja_nombre
                    WHEN v.sucursal_id = 1 THEN 'CAJA ÚNICA'
                    ELSE NULLIF(TRIM(COALESCE(u.caja_nombre,'')), '')
                END,
                v.turno = CASE
                    WHEN TIME(v.fecha) >= '08:00:00' AND TIME(v.fecha) < '20:00:00' THEN 'MAÑANA'
                    ELSE 'NOCHE'
                END
            WHERE v.caja_nombre IS NULL OR TRIM(v.caja_nombre) = ''
               OR v.turno IS NULL OR TRIM(v.turno) = '';
        """, con).ExecuteNonQueryAsync();
    }
    catch { }

    // V44: huella de respaldo para cajas antiguas que no mandan operation_key.
    // NULL para ventas nuevas con OperationKey; hash SHA-256 para solicitudes legacy.
    try { await new MySqlCommand("ALTER TABLE ventas ADD COLUMN legacy_fingerprint VARCHAR(64) NULL AFTER operation_key;", con).ExecuteNonQueryAsync(); } catch { }

    // V47: identidad por línea de detalle + libro de movimientos de stock.
    // line_key puede quedar NULL en registros históricos, pero toda venta V128+ lo llena.
    try { await new MySqlCommand("ALTER TABLE detalle_ventas ADD COLUMN line_key VARCHAR(64) NULL;", con).ExecuteNonQueryAsync(); } catch { }
    try { await new MySqlCommand("ALTER TABLE detalle_ventas ADD UNIQUE KEY uk_detalle_line_key (line_key);", con).ExecuteNonQueryAsync(); } catch { }
    try { await new MySqlCommand("ALTER TABLE detalle_ventas ADD COLUMN consumption_key VARCHAR(180) NULL;", con).ExecuteNonQueryAsync(); } catch { }
    try { await new MySqlCommand("ALTER TABLE detalle_ventas ADD UNIQUE KEY uk_detalle_consumption_key (consumption_key);", con).ExecuteNonQueryAsync(); } catch { }
    try
    {
        await new MySqlCommand("""
            CREATE TABLE IF NOT EXISTS stock_movimientos_venta (
                id BIGINT AUTO_INCREMENT PRIMARY KEY,
                movement_key VARCHAR(64) NOT NULL,
                venta_id BIGINT NOT NULL,
                sucursal_id INT NOT NULL,
                producto_id BIGINT NOT NULL,
                presentacion_id BIGINT NULL,
                cantidad_base DECIMAL(12,4) NOT NULL,
                fecha DATETIME NOT NULL,
                creado_en TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
                UNIQUE KEY uk_stock_movimiento_key (movement_key),
                KEY ix_stock_mov_venta (venta_id),
                KEY ix_stock_mov_producto (sucursal_id, producto_id, fecha)
            );
        """, con).ExecuteNonQueryAsync();
    }
    catch { }
    try { await new MySqlCommand("ALTER TABLE ventas ADD UNIQUE KEY uk_ventas_legacy_fingerprint (legacy_fingerprint);", con).ExecuteNonQueryAsync(); } catch { }

    // V59: vistas contables canónicas. NO borran ventas ni detalles históricos; garantizan que
    // arqueos, reportes, Excel/Sheets y descargas lean una sola copia por operación/línea.
    try
    {
        await new MySqlCommand("""
            CREATE OR REPLACE VIEW detalle_ventas_canonico AS
            SELECT id, venta_id, sector, producto_id, presentacion_id, producto, presentacion,
                   cantidad, precio_unitario, subtotal, line_key, consumption_key
            FROM (
                SELECT d.*,
                       ROW_NUMBER() OVER (
                           PARTITION BY CASE
                               WHEN d.consumption_key IS NOT NULL AND TRIM(d.consumption_key) <> ''
                                   THEN CONCAT('CONS|', UPPER(TRIM(d.consumption_key)))
                               WHEN d.line_key IS NOT NULL AND TRIM(d.line_key) <> ''
                                   THEN CONCAT('LINE|', LOWER(TRIM(d.line_key)))
                               ELSE CONCAT(
                                   'ECON|', d.venta_id, '|', UPPER(TRIM(COALESCE(d.producto,''))), '|',
                                   UPPER(TRIM(COALESCE(d.presentacion,''))), '|',
                                   CAST(ROUND(COALESCE(d.cantidad,0),4) AS CHAR), '|',
                                   CAST(ROUND(COALESCE(d.precio_unitario,0),2) AS CHAR), '|',
                                   CAST(ROUND(COALESCE(d.subtotal,0),2) AS CHAR)
                               )
                           END
                           ORDER BY d.id
                       ) AS rn
                FROM detalle_ventas d
            ) ranked
            WHERE rn = 1;
        """, con).ExecuteNonQueryAsync();

        await new MySqlCommand("""
            CREATE OR REPLACE VIEW ventas_canonicas AS
            SELECT id, sucursal_id, cajero, caja_nombre, turno, fecha, tipo, metodo_pago,
                   efectivo, qr, total, sync_key, operation_key, legacy_fingerprint, session_id
            FROM (
                SELECT v.*,
                       ROW_NUMBER() OVER (
                           PARTITION BY CASE
                               WHEN v.operation_key IS NOT NULL AND TRIM(v.operation_key) <> ''
                                   THEN CONCAT('OP|', UPPER(TRIM(v.operation_key)))
                               WHEN v.legacy_fingerprint IS NOT NULL AND TRIM(v.legacy_fingerprint) <> ''
                                   THEN CONCAT('FP|', LOWER(TRIM(v.legacy_fingerprint)))
                               WHEN UPPER(TRIM(COALESCE(v.tipo,''))) = 'MESA' AND COALESCE(v.session_id,0) > 0
                                   THEN CONCAT('MESA|', v.sucursal_id, '|', UPPER(TRIM(COALESCE(v.caja_nombre,''))), '|', v.session_id)
                               ELSE CONCAT(
                                   'ECON|', v.sucursal_id, '|', UPPER(TRIM(COALESCE(v.cajero,''))), '|',
                                   UPPER(TRIM(COALESCE(v.tipo,''))), '|', UPPER(TRIM(COALESCE(v.metodo_pago,''))), '|',
                                   DATE_FORMAT(v.fecha, '%Y%m%d%H%i%s'), '|',
                                   CAST(ROUND(COALESCE(v.efectivo,0),2) AS CHAR), '|',
                                   CAST(ROUND(COALESCE(v.qr,0),2) AS CHAR), '|',
                                   CAST(ROUND(COALESCE(v.total,0),2) AS CHAR), '|',
                                   COALESCE(df.detalle_fingerprint,'SINDETALLE')
                               )
                           END
                           ORDER BY v.id
                       ) AS rn
                FROM ventas v
                LEFT JOIN (
                    SELECT dc.venta_id,
                           SHA2(GROUP_CONCAT(
                               CONCAT(
                                   UPPER(TRIM(COALESCE(dc.producto,''))), '~',
                                   UPPER(TRIM(COALESCE(dc.presentacion,''))), '~',
                                   CAST(ROUND(COALESCE(dc.cantidad,0),4) AS CHAR), '~',
                                   CAST(ROUND(COALESCE(dc.subtotal,0),2) AS CHAR)
                               )
                               ORDER BY UPPER(TRIM(COALESCE(dc.producto,''))),
                                        UPPER(TRIM(COALESCE(dc.presentacion,''))),
                                        dc.cantidad, dc.subtotal
                               SEPARATOR ';'
                           ), 256) AS detalle_fingerprint
                    FROM detalle_ventas_canonico dc
                    GROUP BY dc.venta_id
                ) df ON df.venta_id = v.id
                WHERE v.total > 0
            ) ranked
            WHERE rn = 1;
        """, con).ExecuteNonQueryAsync();
    }
    catch
    {
        // Si MySQL todavía está aplicando una migración, los endpoints de escritura siguen operativos.
        // El siguiente arranque reintentará crear las vistas sin borrar datos.
    }

}

static string FingerprintNormalize(string? value)
{
    return (value ?? string.Empty).Trim().ToUpperInvariant();
}

static string FingerprintDecimal(decimal value)
{
    return Math.Round(value, 2).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
}

static string BuildLegacyAccountingFingerprint(VentaRequest venta)
{
    // La precisión se normaliza a segundo para coincidir con copias históricas guardadas en DATETIME.
    DateTime fecha = venta.Fecha;
    string moment = fecha.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);

    var detalle = new List<string>();
    if (venta.Detalle is not null)
    {
        foreach (var d in venta.Detalle)
        {
            detalle.Add(
                FingerprintNormalize(d.Producto) + "~" +
                FingerprintNormalize(d.Presentacion) + "~" +
                d.Cantidad.ToString(System.Globalization.CultureInfo.InvariantCulture) + "~" +
                FingerprintDecimal(d.Subtotal));
        }
    }
    detalle.Sort(StringComparer.OrdinalIgnoreCase);

    string canonical = string.Join("|", new[]
    {
        venta.SucursalId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        FingerprintNormalize(venta.Cajero),
        FingerprintNormalize(venta.Tipo),
        FingerprintNormalize(venta.MetodoPago),
        moment,
        FingerprintDecimal(venta.Total),
        FingerprintDecimal(Math.Max(0, venta.Efectivo)),
        FingerprintDecimal(Math.Max(0, venta.Qr)),
        string.Join(";", detalle)
    });

    byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
    return Convert.ToHexString(hash).ToLowerInvariant();
}

static async Task EnsureCoreSchemaAsync(MySqlConnection con)
{
    // V49: tablas base para una base Railway vacía. Todas las sentencias son aditivas.
    string[] statements =
    {
        """
        CREATE TABLE IF NOT EXISTS sucursales (
            id INT AUTO_INCREMENT PRIMARY KEY,
            nombre VARCHAR(120) NOT NULL,
            direccion VARCHAR(220) NOT NULL DEFAULT '',
            estado VARCHAR(20) NOT NULL DEFAULT 'ACTIVO'
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS usuarios (
            id INT AUTO_INCREMENT PRIMARY KEY,
            usuario VARCHAR(100) NOT NULL,
            clave VARCHAR(255) NOT NULL,
            rol VARCHAR(40) NOT NULL,
            sucursal_id INT NOT NULL DEFAULT 1,
            estado VARCHAR(20) NOT NULL DEFAULT 'ACTIVO',
            nombre_completo VARCHAR(180) NULL,
            caja_nombre VARCHAR(60) NULL,
            turno VARCHAR(20) NOT NULL DEFAULT 'MAÑANA',
            sector VARCHAR(20) NOT NULL DEFAULT 'GENERAL',
            UNIQUE KEY uk_usuarios_usuario (usuario),
            KEY ix_usuarios_sucursal (sucursal_id)
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS mesas (
            id INT AUTO_INCREMENT PRIMARY KEY,
            sucursal_id INT NOT NULL,
            nombre VARCHAR(100) NOT NULL,
            precio_hora DECIMAL(12,2) NOT NULL DEFAULT 20.00,
            estado VARCHAR(20) NOT NULL DEFAULT 'LIBRE',
            tipo_mesa VARCHAR(30) NOT NULL DEFAULT 'NORMAL',
            UNIQUE KEY uk_mesas_sucursal_nombre (sucursal_id, nombre),
            KEY ix_mesas_sucursal (sucursal_id)
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS productos (
            id BIGINT AUTO_INCREMENT PRIMARY KEY,
            sucursal_id INT NOT NULL,
            sector VARCHAR(20) NOT NULL DEFAULT 'GENERAL',
            nombre VARCHAR(180) NOT NULL,
            categoria VARCHAR(100) NOT NULL DEFAULT 'Otros',
            tipo_entrada VARCHAR(60) NOT NULL DEFAULT 'UNIDAD',
            unidad_base VARCHAR(60) NOT NULL DEFAULT 'UNIDAD',
            unidades_por_entrada INT NOT NULL DEFAULT 1,
            precio_compra DECIMAL(12,2) NOT NULL DEFAULT 0,
            stock_actual DECIMAL(14,4) NOT NULL DEFAULT 0,
            stock_minimo DECIMAL(14,4) NOT NULL DEFAULT 0,
            estado VARCHAR(20) NOT NULL DEFAULT 'ACTIVO',
            genera_comision TINYINT(1) NOT NULL DEFAULT 0,
            tipo_comision VARCHAR(30) NOT NULL DEFAULT 'NINGUNA',
            valor_comision DECIMAL(12,2) NOT NULL DEFAULT 0,
            sin_limite_stock TINYINT(1) NOT NULL DEFAULT 0,
            rendimiento_vaso INT NOT NULL DEFAULT 10,
            KEY ix_productos_sucursal_sector_nombre (sucursal_id, sector, nombre),
            KEY ix_productos_sucursal_estado (sucursal_id, estado)
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS vaso_control (
            sucursal_id INT NOT NULL,
            sector VARCHAR(20) NOT NULL DEFAULT 'GENERAL',
            producto_id BIGINT NOT NULL,
            rendimiento INT NOT NULL DEFAULT 10,
            servicios_restantes INT NOT NULL DEFAULT 0,
            actualizado TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
            PRIMARY KEY (sucursal_id, sector, producto_id)
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS presentaciones (
            id BIGINT AUTO_INCREMENT PRIMARY KEY,
            producto_id BIGINT NOT NULL,
            nombre VARCHAR(120) NOT NULL,
            cantidad_base DECIMAL(14,4) NOT NULL DEFAULT 1,
            precio_venta DECIMAL(12,2) NOT NULL DEFAULT 0,
            estado VARCHAR(20) NOT NULL DEFAULT 'ACTIVO',
            UNIQUE KEY uk_presentaciones_producto_nombre (producto_id, nombre),
            KEY ix_presentaciones_producto (producto_id)
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS ventas (
            id BIGINT AUTO_INCREMENT PRIMARY KEY,
            sucursal_id INT NOT NULL,
            cajero VARCHAR(100) NOT NULL,
            fecha DATETIME NOT NULL,
            tipo VARCHAR(60) NOT NULL,
            metodo_pago VARCHAR(30) NOT NULL,
            efectivo DECIMAL(12,2) NOT NULL DEFAULT 0,
            qr DECIMAL(12,2) NOT NULL DEFAULT 0,
            total DECIMAL(12,2) NOT NULL,
            sync_key VARCHAR(220) NOT NULL,
            operation_key VARCHAR(220) NULL,
            legacy_fingerprint VARCHAR(64) NULL,
            session_id INT NULL,
            UNIQUE KEY uk_ventas_sync_key (sync_key),
            UNIQUE KEY uk_ventas_operation_key (operation_key),
            UNIQUE KEY uk_ventas_legacy_fingerprint (legacy_fingerprint),
            KEY ix_ventas_sucursal_fecha (sucursal_id, fecha),
            KEY idx_ventas_session (sucursal_id, session_id, tipo)
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS detalle_ventas (
            id BIGINT AUTO_INCREMENT PRIMARY KEY,
            venta_id BIGINT NOT NULL,
            sector VARCHAR(20) NOT NULL DEFAULT 'GENERAL',
            producto_id BIGINT NULL,
            presentacion_id BIGINT NULL,
            producto VARCHAR(180) NOT NULL,
            presentacion VARCHAR(120) NOT NULL,
            cantidad DECIMAL(14,4) NOT NULL DEFAULT 0,
            precio_unitario DECIMAL(12,2) NOT NULL DEFAULT 0,
            subtotal DECIMAL(12,2) NOT NULL DEFAULT 0,
            line_key VARCHAR(64) NULL,
            consumption_key VARCHAR(180) NULL,
            UNIQUE KEY uk_detalle_line_key (line_key),
            UNIQUE KEY uk_detalle_consumption_key (consumption_key),
            KEY ix_detalle_venta (venta_id)
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS reservas (
            id BIGINT AUTO_INCREMENT PRIMARY KEY,
            sucursal_id INT NOT NULL,
            mesa_id INT NOT NULL,
            cliente VARCHAR(180) NOT NULL,
            celular VARCHAR(40) NOT NULL DEFAULT '',
            fecha_reserva DATETIME NOT NULL,
            minutos INT NOT NULL DEFAULT 60,
            estado VARCHAR(30) NOT NULL DEFAULT 'ACTIVA',
            cajero VARCHAR(100) NOT NULL DEFAULT '',
            sync_key VARCHAR(180) NOT NULL,
            UNIQUE KEY uk_reservas_sync (sync_key),
            KEY ix_reservas_sucursal_fecha (sucursal_id, fecha_reserva)
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS propinas (
            id BIGINT AUTO_INCREMENT PRIMARY KEY,
            sucursal_id INT NOT NULL,
            mesa_id INT NULL,
            mesera VARCHAR(150) NOT NULL,
            cajero VARCHAR(100) NOT NULL,
            fecha DATETIME NOT NULL,
            monto DECIMAL(12,2) NOT NULL DEFAULT 0,
            sync_key VARCHAR(180) NOT NULL,
            UNIQUE KEY uk_propinas_sync (sync_key),
            KEY ix_propinas_sucursal_fecha (sucursal_id, fecha)
        );
        """
    };

    foreach (string sql in statements)
    {
        await using var cmd = new MySqlCommand(sql, con);
        await cmd.ExecuteNonQueryAsync();
    }

    await using var seed = new MySqlCommand("""
        INSERT IGNORE INTO sucursales (id, nombre, direccion, estado) VALUES
        (1, 'EL BRUJO', '', 'ACTIVO'),
        (2, 'EL BRUJO PREMIU', '', 'ACTIVO');
        UPDATE sucursales SET nombre='EL BRUJO', estado='ACTIVO' WHERE id=1;
        UPDATE sucursales SET nombre='EL BRUJO PREMIU', estado='ACTIVO' WHERE id=2;
    """, con);
    await seed.ExecuteNonQueryAsync();
}

static async Task EnsureUserManagementTables(MySqlConnection con)
{
    await using (var alterClave = new MySqlCommand("ALTER TABLE usuarios MODIFY COLUMN clave VARCHAR(255) NOT NULL;", con))
    {
        try { await alterClave.ExecuteNonQueryAsync(); } catch { }
    }

    await using (var cmd = new MySqlCommand("ALTER TABLE usuarios ADD COLUMN nombre_completo VARCHAR(180) NULL;", con))
    {
        try { await cmd.ExecuteNonQueryAsync(); } catch { }
    }

    await using (var cmd = new MySqlCommand("ALTER TABLE usuarios ADD COLUMN caja_nombre VARCHAR(60) NULL;", con))
    {
        try { await cmd.ExecuteNonQueryAsync(); } catch { }
    }

    await using (var cmd = new MySqlCommand("ALTER TABLE usuarios ADD COLUMN turno VARCHAR(20) NOT NULL DEFAULT 'MAÑANA';", con))
    {
        try { await cmd.ExecuteNonQueryAsync(); } catch { }
    }
    await using (var cmd = new MySqlCommand("ALTER TABLE usuarios ADD COLUMN sector VARCHAR(20) NOT NULL DEFAULT 'GENERAL';", con))
    {
        try { await cmd.ExecuteNonQueryAsync(); } catch { }
    }

    await using (var seed = new MySqlCommand("""
        INSERT INTO usuarios (usuario, clave, rol, sucursal_id, estado, nombre_completo, caja_nombre, turno, sector)
        SELECT 'admin', 'brujopremiu2025', 'ADMINISTRADOR', 1, 'ACTIVO', 'Administrador', 'ADMIN', 'MAÑANA', 'GENERAL'
        WHERE NOT EXISTS (SELECT 1 FROM usuarios WHERE usuario = 'admin');

        -- EL BRUJO: 1 PC, 2 cajeros (día/noche), misma CAJA ÚNICA.
        INSERT INTO usuarios (usuario, clave, rol, sucursal_id, estado, nombre_completo, caja_nombre, turno, sector)
        SELECT 'brujo1', 'BrujoM2026', 'CAJERO', 1, 'ACTIVO', 'Cajero EL BRUJO Día', 'CAJA ÚNICA', 'MAÑANA', 'GENERAL'
        WHERE NOT EXISTS (SELECT 1 FROM usuarios WHERE usuario = 'brujo1');

        INSERT INTO usuarios (usuario, clave, rol, sucursal_id, estado, nombre_completo, caja_nombre, turno, sector)
        SELECT 'brujo2', 'BrujoN2026', 'CAJERO', 1, 'ACTIVO', 'Cajero EL BRUJO Noche', 'CAJA ÚNICA', 'NOCHE', 'GENERAL'
        WHERE NOT EXISTS (SELECT 1 FROM usuarios WHERE usuario = 'brujo2');

        -- EL BRUJO PREMIU: abajo usa premiu1/premiu2; arriba usa premium1/premium2.
        INSERT INTO usuarios (usuario, clave, rol, sucursal_id, estado, nombre_completo, caja_nombre, turno, sector)
        SELECT 'premiu1', 'brujom2026', 'CAJERO', 2, 'ACTIVO', 'Cajero PREMIU Abajo Día', 'CAJA ABAJO', 'MAÑANA', 'ABAJO'
        WHERE NOT EXISTS (SELECT 1 FROM usuarios WHERE usuario = 'premiu1');

        INSERT INTO usuarios (usuario, clave, rol, sucursal_id, estado, nombre_completo, caja_nombre, turno, sector)
        SELECT 'premium1', 'brujom2026', 'CAJERO', 2, 'ACTIVO', 'Cajero PREMIU Arriba Día', 'CAJA ARRIBA', 'MAÑANA', 'ARRIBA'
        WHERE NOT EXISTS (SELECT 1 FROM usuarios WHERE usuario = 'premium1');

        INSERT INTO usuarios (usuario, clave, rol, sucursal_id, estado, nombre_completo, caja_nombre, turno, sector)
        SELECT 'premiu2', 'brujom2026', 'CAJERO', 2, 'ACTIVO', 'Cajero PREMIU Abajo Noche', 'CAJA ABAJO', 'NOCHE', 'ABAJO'
        WHERE NOT EXISTS (SELECT 1 FROM usuarios WHERE usuario = 'premiu2');

        INSERT INTO usuarios (usuario, clave, rol, sucursal_id, estado, nombre_completo, caja_nombre, turno, sector)
        SELECT 'premium2', 'brujon2026', 'CAJERO', 2, 'ACTIVO', 'Cajero PREMIU Arriba Noche', 'CAJA ARRIBA', 'NOCHE', 'ARRIBA'
        WHERE NOT EXISTS (SELECT 1 FROM usuarios WHERE usuario = 'premium2');

        -- Usuarios viejos de caja: se conservan para historial, pero no pueden volver a iniciar sesión.
        UPDATE usuarios
        SET estado = 'INACTIVO'
        WHERE rol = 'CAJERO'
          AND usuario IN ('caja1','caja1_noche','caja2','caja2_noche','caja2_2','caja2_2_noche','brujo_manana','brujo_noche',
                          'premiu_arriba_manana','premiu_abajo_manana','premiu_arriba_noche','premiu_abajo_noche');

        INSERT INTO usuarios (usuario, clave, rol, sucursal_id, estado, nombre_completo, caja_nombre, turno, sector)
        SELECT 'ana_mesera', 'mesera123', 'MESERA', 1, 'ACTIVO', 'Ana Mesera', '', 'MAÑANA', 'GENERAL'
        WHERE NOT EXISTS (SELECT 1 FROM usuarios WHERE usuario = 'ana_mesera');

        INSERT INTO usuarios (usuario, clave, rol, sucursal_id, estado, nombre_completo, caja_nombre, turno, sector)
        SELECT 'rosa_mesera', 'mesera123', 'MESERA', 2, 'ACTIVO', 'Rosa Mesera', '', 'NOCHE', 'ARRIBA'
        WHERE NOT EXISTS (SELECT 1 FROM usuarios WHERE usuario = 'rosa_mesera');
    """, con))
    {
        try { await seed.ExecuteNonQueryAsync(); } catch { }
    }

    // V68: aplica una sola vez el juego de credenciales solicitado también a instalaciones
    // que ya tenían usuarios en Railway. El marcador se escribe al final para que una falla
    // parcial vuelva a intentar la migración en el siguiente arranque.
    await ApplyCredentialSetV68(con);

    await HashPlainUserPasswords(con);
}

static async Task ApplyCredentialSetV68(MySqlConnection con)
{
    await using (var create = new MySqlCommand("""
        CREATE TABLE IF NOT EXISTS configuracion_sistema (
            clave VARCHAR(100) PRIMARY KEY,
            valor_decimal DECIMAL(12,2) NULL,
            actualizado DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
        );
    """, con))
    {
        await create.ExecuteNonQueryAsync();
    }

    await using (var check = new MySqlCommand("SELECT valor_decimal FROM configuracion_sistema WHERE clave='CREDENCIALES_V68_APLICADAS' LIMIT 1;", con))
    {
        object? value = await check.ExecuteScalarAsync();
        if (value != null && value != DBNull.Value && Convert.ToDecimal(value) > 0)
            return;
    }

    var users = new (string usuario, string clave, string rol, int sucursalId, string nombre, string caja, string turno, string sector)[]
    {
        ("admin", "brujopremiu2025", "ADMINISTRADOR", 1, "Administrador", "ADMIN", "MAÑANA", "GENERAL"),
        ("brujo1", "BrujoM2026", "CAJERO", 1, "Cajero EL BRUJO Día", "CAJA ÚNICA", "MAÑANA", "GENERAL"),
        ("brujo2", "BrujoN2026", "CAJERO", 1, "Cajero EL BRUJO Noche", "CAJA ÚNICA", "NOCHE", "GENERAL"),
        ("premiu1", "brujom2026", "CAJERO", 2, "Cajero PREMIU Abajo Día", "CAJA ABAJO", "MAÑANA", "ABAJO"),
        ("premium1", "brujom2026", "CAJERO", 2, "Cajero PREMIU Arriba Día", "CAJA ARRIBA", "MAÑANA", "ARRIBA"),
        ("premiu2", "brujom2026", "CAJERO", 2, "Cajero PREMIU Abajo Noche", "CAJA ABAJO", "NOCHE", "ABAJO"),
        ("premium2", "brujon2026", "CAJERO", 2, "Cajero PREMIU Arriba Noche", "CAJA ARRIBA", "NOCHE", "ARRIBA")
    };

    foreach (var u in users)
    {
        await using var cmd = new MySqlCommand("""
            INSERT INTO usuarios (usuario, clave, rol, sucursal_id, estado, nombre_completo, caja_nombre, turno, sector)
            VALUES (@usuario, @clave, @rol, @sucursal_id, 'ACTIVO', @nombre, @caja, @turno, @sector)
            ON DUPLICATE KEY UPDATE
                clave=VALUES(clave),
                rol=VALUES(rol),
                sucursal_id=VALUES(sucursal_id),
                estado='ACTIVO',
                nombre_completo=VALUES(nombre_completo),
                caja_nombre=VALUES(caja_nombre),
                turno=VALUES(turno),
                sector=VALUES(sector);
        """, con);
        cmd.Parameters.AddWithValue("@usuario", u.usuario);
        cmd.Parameters.AddWithValue("@clave", PasswordHasher.Hash(u.clave));
        cmd.Parameters.AddWithValue("@rol", u.rol);
        cmd.Parameters.AddWithValue("@sucursal_id", u.sucursalId);
        cmd.Parameters.AddWithValue("@nombre", u.nombre);
        cmd.Parameters.AddWithValue("@caja", u.caja);
        cmd.Parameters.AddWithValue("@turno", u.turno);
        cmd.Parameters.AddWithValue("@sector", u.sector);
        await cmd.ExecuteNonQueryAsync();
    }

    await using (var disable = new MySqlCommand("""
        UPDATE usuarios SET estado='INACTIVO'
        WHERE rol='CAJERO' AND usuario IN (
            'caja1','caja1_noche','caja2','caja2_noche','caja2_2','caja2_2_noche','brujo_manana','brujo_noche',
            'premiu_arriba_manana','premiu_abajo_manana','premiu_arriba_noche','premiu_abajo_noche'
        );
    """, con))
    {
        await disable.ExecuteNonQueryAsync();
    }

    await using (var mark = new MySqlCommand("""
        INSERT INTO configuracion_sistema (clave, valor_decimal, actualizado)
        VALUES ('CREDENCIALES_V68_APLICADAS', 1.00, NOW())
        ON DUPLICATE KEY UPDATE valor_decimal=1.00, actualizado=NOW();
    """, con))
    {
        await mark.ExecuteNonQueryAsync();
    }
}

static string NormalizarRol(string? rol)
{
    string r = (rol ?? "").Trim().ToUpperInvariant();
    if (r.Contains("ADMIN")) return "ADMINISTRADOR";
    if (r.Contains("CAJ")) return "CAJERO";
    if (r.Contains("MESER")) return "MESERA";
    return string.IsNullOrWhiteSpace(r) ? "CAJERO" : r;
}

static string NormalizarTurno(string? turno)
{
    string t = (turno ?? "").Trim().ToUpperInvariant();
    if (t.Contains("NOCHE")) return "NOCHE";
    return "MAÑANA";
}

static string GetInstanceCode()
{
    string raw = (Environment.GetEnvironmentVariable("INSTANCE_CODE") ?? "COMPARTIDO").Trim();
    return string.IsNullOrWhiteSpace(raw) ? "COMPARTIDO" : raw.ToUpperInvariant();
}

static int GetInstanceSucursalId()
{
    string raw = (Environment.GetEnvironmentVariable("INSTANCE_SUCURSAL_ID") ?? "").Trim();
    return int.TryParse(raw, out int sid) && (sid == 1 || sid == 2) ? sid : 0;
}

static string GetInstanceSector()
{
    string raw = (Environment.GetEnvironmentVariable("INSTANCE_SECTOR") ?? "").Trim().ToUpperInvariant();
    if (raw == "ARRIBA" || raw == "ABAJO" || raw == "GENERAL") return raw;
    return "";
}

static bool IsInstanceLocked()
{
    int sid = GetInstanceSucursalId();
    string sec = GetInstanceSector();
    return (sid == 1 && sec == "GENERAL") || (sid == 2 && (sec == "ARRIBA" || sec == "ABAJO"));
}

static bool InstanceSheetsEnabled()
{
    if (!IsInstanceLocked()) return true;
    string raw = (Environment.GetEnvironmentVariable("INSTANCE_SHEETS_ENABLED") ?? "false").Trim();
    return raw.Equals("true", StringComparison.OrdinalIgnoreCase) || raw == "1" || raw.Equals("si", StringComparison.OrdinalIgnoreCase) || raw.Equals("sí", StringComparison.OrdinalIgnoreCase);
}

static bool IsPremiumSingleSectorInstance()
    => GetInstanceSucursalId() == 2 && (GetInstanceSector() == "ARRIBA" || GetInstanceSector() == "ABAJO");

static int ResolveInstanceSucursalId(int requested)
{
    int fixedSid = GetInstanceSucursalId();
    if (fixedSid == 1 || fixedSid == 2) return fixedSid;
    return requested == 2 ? 2 : 1;
}

static string NormalizeRequestedSectorWithoutInstance(int sucursalId, string? sector, string? cajaNombre = null)
{
    if (sucursalId != 2) return "GENERAL";
    string raw = ((sector ?? "") + " " + (cajaNombre ?? "")).Trim().ToUpperInvariant();
    if (raw.Contains("ABAJO") || raw.Contains("BAJO") || raw.Contains("CAJA 2")) return "ABAJO";
    return "ARRIBA";
}

static bool InstanceRequestMatches(int requestedSucursalId, string? requestedSector, string? cajaNombre = null)
{
    if (!IsInstanceLocked()) return true;
    int fixedSid = GetInstanceSucursalId();
    int reqSid = requestedSucursalId == 2 ? 2 : 1;
    if (reqSid != fixedSid) return false;
    if (fixedSid == 1) return true;
    if (string.IsNullOrWhiteSpace(requestedSector) && string.IsNullOrWhiteSpace(cajaNombre)) return true;
    return NormalizeRequestedSectorWithoutInstance(reqSid, requestedSector, cajaNombre) == GetInstanceSector();
}

static string InstanceMismatchMessage(int requestedSucursalId, string? requestedSector)
    => $"Operación bloqueada: esta API es {GetInstanceCode()} (sucursal {GetInstanceSucursalId()}, sector {GetInstanceSector()}) y recibió sucursal {requestedSucursalId}, sector {requestedSector ?? ""}.";

static string NormalizarSector(int sucursalId, string? sector, string? cajaNombre = null)
{
    int sid = ResolveInstanceSucursalId(sucursalId);
    if (IsInstanceLocked()) return GetInstanceSector();
    return NormalizeRequestedSectorWithoutInstance(sid, sector, cajaNombre);
}

static string NormalizarSectorProducto(int sucursalId, string? sector = null)
{
    int sid = ResolveInstanceSucursalId(sucursalId);
    if (IsInstanceLocked()) return GetInstanceSector();
    if (sid != 2) return "GENERAL";
    string raw=(sector ?? "").Trim().ToUpperInvariant();
    if (string.IsNullOrWhiteSpace(raw) || raw=="GENERAL" || raw.Contains("COMPARTIDO")) return "ABAJO";
    return NormalizeRequestedSectorWithoutInstance(sid, sector);
}

static async Task EnsurePremiumSharedCatalogAsync(MySqlConnection con)
{
    if (IsPremiumSingleSectorInstance()) return;
    // V70: migración UNA sola vez. Dos cajas de PREMIU comparten el mismo producto físico.
    // Si existían copias ARRIBA/ABAJO, NO se suman los stocks para evitar inflación;
    // se conserva la mayor cantidad observada y una sola ficha activa por nombre.
    await using (var create = new MySqlCommand("""
        CREATE TABLE IF NOT EXISTS migraciones_sistema (
            clave VARCHAR(120) PRIMARY KEY,
            aplicado_en TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
        );
    """, con)) await create.ExecuteNonQueryAsync();

    await using (var check = new MySqlCommand("SELECT COUNT(*) FROM migraciones_sistema WHERE clave='PREMIU_CATALOGO_COMPARTIDO_V70';", con))
    {
        if (Convert.ToInt32(await check.ExecuteScalarAsync() ?? 0) > 0) return;
    }

    await using var tx = await con.BeginTransactionAsync();
    try
    {
        var rows = new List<(long id, string nombre, decimal stock, decimal minimo, string estado)>();
        await using (var cmd = new MySqlCommand("SELECT id,nombre,stock_actual,stock_minimo,estado FROM productos WHERE sucursal_id=2 ORDER BY id FOR UPDATE;", con, tx))
        await using (var rd = await cmd.ExecuteReaderAsync())
        {
            while (await rd.ReadAsync())
                rows.Add((rd.GetInt64(0), rd.IsDBNull(1) ? "" : rd.GetString(1), rd.IsDBNull(2) ? 0m : rd.GetDecimal(2), rd.IsDBNull(3) ? 0m : rd.GetDecimal(3), rd.IsDBNull(4) ? "ACTIVO" : rd.GetString(4)));
        }

        foreach (var g in rows.GroupBy(x => (x.nombre ?? "").Trim().ToUpperInvariant()).Where(g => !string.IsNullOrWhiteSpace(g.Key)))
        {
            var list = g.OrderBy(x => x.id).ToList();
            long canonicalId = list.FirstOrDefault(x => x.estado.Equals("ACTIVO", StringComparison.OrdinalIgnoreCase)).id;
            if (canonicalId <= 0) canonicalId = list[0].id;
            decimal stock = list.Max(x => Math.Max(0m, x.stock));
            decimal minimo = list.Max(x => Math.Max(0m, x.minimo));

            await using (var up = new MySqlCommand("UPDATE productos SET sector='GENERAL', stock_actual=@stock, stock_minimo=@minimo, estado='ACTIVO' WHERE id=@id;", con, tx))
            {
                up.Parameters.AddWithValue("@stock", stock);
                up.Parameters.AddWithValue("@minimo", minimo);
                up.Parameters.AddWithValue("@id", canonicalId);
                await up.ExecuteNonQueryAsync();
            }

            foreach (var duplicate in list.Where(x => x.id != canonicalId))
            {
                await using (var copy = new MySqlCommand("""
                    INSERT INTO presentaciones (producto_id,nombre,cantidad_base,precio_venta,estado)
                    SELECT @canonical, p.nombre, p.cantidad_base, p.precio_venta, p.estado
                    FROM presentaciones p
                    WHERE p.producto_id=@duplicate
                      AND NOT EXISTS (SELECT 1 FROM presentaciones c WHERE c.producto_id=@canonical AND LOWER(TRIM(c.nombre))=LOWER(TRIM(p.nombre)));
                """, con, tx))
                {
                    copy.Parameters.AddWithValue("@canonical", canonicalId);
                    copy.Parameters.AddWithValue("@duplicate", duplicate.id);
                    await copy.ExecuteNonQueryAsync();
                }

                await using var off = new MySqlCommand("UPDATE productos SET sector='GENERAL', estado='INACTIVO' WHERE id=@id;", con, tx);
                off.Parameters.AddWithValue("@id", duplicate.id);
                await off.ExecuteNonQueryAsync();
            }
        }

        await using (var allGeneral = new MySqlCommand("UPDATE productos SET sector='GENERAL' WHERE sucursal_id=2;", con, tx))
            await allGeneral.ExecuteNonQueryAsync();
        await using (var mark = new MySqlCommand("INSERT INTO migraciones_sistema(clave) VALUES('PREMIU_CATALOGO_COMPARTIDO_V70');", con, tx))
            await mark.ExecuteNonQueryAsync();
        await tx.CommitAsync();
    }
    catch
    {
        await tx.RollbackAsync();
        throw;
    }
}



static async Task MirrorPremiumCatalogAsync(MySqlConnection con, MySqlTransaction tx, long sourceId, string sourceSector, string? previousName = null)
{
    if (IsPremiumSingleSectorInstance()) return;
    string sector = NormalizarSectorProducto(2, sourceSector);
    string other = sector == "ARRIBA" ? "ABAJO" : "ARRIBA";
    string nombre = "";
    await using (var q = new MySqlCommand("SELECT nombre FROM productos WHERE id=@id AND sucursal_id=2 LIMIT 1;", con, tx))
    {
        q.Parameters.AddWithValue("@id", sourceId);
        nombre = Convert.ToString(await q.ExecuteScalarAsync()) ?? "";
    }
    if (string.IsNullOrWhiteSpace(nombre)) return;

    long otherId = 0;
    await using (var q = new MySqlCommand("""
        SELECT id FROM productos
        WHERE sucursal_id=2 AND sector=@sector
          AND (LOWER(TRIM(nombre))=LOWER(TRIM(@nombre)) OR (@anterior<>'' AND LOWER(TRIM(nombre))=LOWER(TRIM(@anterior))))
        ORDER BY CASE WHEN LOWER(TRIM(nombre))=LOWER(TRIM(@nombre)) THEN 0 ELSE 1 END,
                 CASE WHEN estado='ACTIVO' THEN 0 ELSE 1 END,id LIMIT 1;
    """, con, tx))
    {
        q.Parameters.AddWithValue("@sector", other);
        q.Parameters.AddWithValue("@nombre", nombre);
        q.Parameters.AddWithValue("@anterior", previousName ?? "");
        object? found = await q.ExecuteScalarAsync();
        if (found != null) otherId = Convert.ToInt64(found);
    }
    if (otherId <= 0)
    {
        await using var clone = new MySqlCommand("""
            INSERT INTO productos
            (sucursal_id,sector,nombre,categoria,tipo_entrada,unidad_base,unidades_por_entrada,precio_compra,
             stock_actual,stock_minimo,estado,genera_comision,tipo_comision,valor_comision,sin_limite_stock,rendimiento_vaso)
            SELECT 2,@other,nombre,categoria,tipo_entrada,unidad_base,unidades_por_entrada,precio_compra,
                   0,stock_minimo,estado,genera_comision,tipo_comision,valor_comision,sin_limite_stock,rendimiento_vaso
            FROM productos WHERE id=@src;
            SELECT LAST_INSERT_ID();
        """, con, tx);
        clone.Parameters.AddWithValue("@other", other);
        clone.Parameters.AddWithValue("@src", sourceId);
        otherId = Convert.ToInt64(await clone.ExecuteScalarAsync());
    }
    else
    {
        await using var upd = new MySqlCommand("""
            UPDATE productos dst
            INNER JOIN productos src ON src.id=@src
            SET dst.nombre=src.nombre,dst.categoria=src.categoria,dst.tipo_entrada=src.tipo_entrada,
                dst.unidad_base=src.unidad_base,dst.unidades_por_entrada=src.unidades_por_entrada,
                dst.precio_compra=src.precio_compra,dst.stock_minimo=src.stock_minimo,dst.estado=src.estado,
                dst.genera_comision=src.genera_comision,dst.tipo_comision=src.tipo_comision,
                dst.valor_comision=src.valor_comision,dst.sin_limite_stock=src.sin_limite_stock,
                dst.rendimiento_vaso=src.rendimiento_vaso
            WHERE dst.id=@dst;
        """, con, tx);
        upd.Parameters.AddWithValue("@src", sourceId);
        upd.Parameters.AddWithValue("@dst", otherId);
        await upd.ExecuteNonQueryAsync();
    }

    await using var pres = new MySqlCommand("""
        INSERT INTO presentaciones(producto_id,nombre,cantidad_base,precio_venta,estado)
        SELECT @dst,nombre,cantidad_base,precio_venta,estado
        FROM presentaciones WHERE producto_id=@src
        ON DUPLICATE KEY UPDATE cantidad_base=VALUES(cantidad_base),precio_venta=VALUES(precio_venta),estado=VALUES(estado);
    """, con, tx);
    pres.Parameters.AddWithValue("@dst", otherId);
    pres.Parameters.AddWithValue("@src", sourceId);
    await pres.ExecuteNonQueryAsync();
}

static async Task EnsurePremiumSectorPairsAsync(MySqlConnection con)
{
    if (IsPremiumSingleSectorInstance()) return;
    // V88: un catálogo lógico, dos existencias físicas. También repara nombres de sector
    // heredados (GENERAL, COMPARTIDO, CAJA ARRIBA/ABAJO) antes de crear la ficha hermana.
    // La normalización conserva la cantidad en su sector de origen conservador y la ficha
    // faltante SIEMPRE nace con stock 0, evitando cualquier inflación.
    await using var tx = await con.BeginTransactionAsync();
    try
    {
        await using (var normalize = new MySqlCommand("""
            UPDATE productos
            SET sector = CASE
                WHEN UPPER(TRIM(COALESCE(sector,''))) LIKE '%ARRIBA%' THEN 'ARRIBA'
                WHEN UPPER(TRIM(COALESCE(sector,''))) LIKE '%ABAJO%' OR UPPER(TRIM(COALESCE(sector,''))) LIKE '%BAJO%' THEN 'ABAJO'
                WHEN UPPER(TRIM(COALESCE(sector,''))) IN ('GENERAL','COMPARTIDO','') THEN 'ABAJO'
                ELSE sector
            END
            WHERE sucursal_id=2 AND estado='ACTIVO';
        """, con, tx)) await normalize.ExecuteNonQueryAsync();

        var activos = new List<(long id, string nombre, string sector)>();
        await using (var q = new MySqlCommand("SELECT id,nombre,sector FROM productos WHERE sucursal_id=2 AND estado='ACTIVO' AND sector IN ('ARRIBA','ABAJO') ORDER BY id FOR UPDATE;", con, tx))
        await using (var rd = await q.ExecuteReaderAsync())
        {
            while (await rd.ReadAsync())
                activos.Add((rd.GetInt64(0), rd.IsDBNull(1) ? "" : rd.GetString(1), rd.IsDBNull(2) ? "ABAJO" : rd.GetString(2)));
        }

        foreach (var g in activos.GroupBy(x => (x.nombre ?? "").Trim().ToUpperInvariant()).Where(g => !string.IsNullOrWhiteSpace(g.Key)).ToList())
        {
            foreach (string wanted in new[] { "ARRIBA", "ABAJO" })
            {
                if (g.Any(x => string.Equals(x.sector, wanted, StringComparison.OrdinalIgnoreCase))) continue;
                var src = g.First();
                await using var clone = new MySqlCommand("""
                    INSERT INTO productos
                    (sucursal_id,sector,nombre,categoria,tipo_entrada,unidad_base,unidades_por_entrada,precio_compra,
                     stock_actual,stock_minimo,estado,genera_comision,tipo_comision,valor_comision,sin_limite_stock,rendimiento_vaso)
                    SELECT sucursal_id,@sector,nombre,categoria,tipo_entrada,unidad_base,unidades_por_entrada,precio_compra,
                           0,stock_minimo,estado,genera_comision,tipo_comision,valor_comision,sin_limite_stock,rendimiento_vaso
                    FROM productos WHERE id=@src;
                    SELECT LAST_INSERT_ID();
                """, con, tx);
                clone.Parameters.AddWithValue("@sector", wanted);
                clone.Parameters.AddWithValue("@src", src.id);
                long dst = Convert.ToInt64(await clone.ExecuteScalarAsync());

                await using var copyPres = new MySqlCommand("""
                    INSERT INTO presentaciones(producto_id,nombre,cantidad_base,precio_venta,estado)
                    SELECT @dst,nombre,cantidad_base,precio_venta,estado FROM presentaciones WHERE producto_id=@src
                    ON DUPLICATE KEY UPDATE cantidad_base=VALUES(cantidad_base),precio_venta=VALUES(precio_venta),estado=VALUES(estado);
                """, con, tx);
                copyPres.Parameters.AddWithValue("@dst", dst);
                copyPres.Parameters.AddWithValue("@src", src.id);
                await copyPres.ExecuteNonQueryAsync();
            }
        }
        await tx.CommitAsync();
    }
    catch
    {
        await tx.RollbackAsync();
        throw;
    }
}

static async Task EnsurePremiumSectorStockAsync(MySqlConnection con)
{
    if (IsPremiumSingleSectorInstance()) return;
    // V76: un solo catálogo lógico para PREMIU, pero existencias físicas separadas ARRIBA/ABAJO.
    // Migración conservadora: el stock compartido existente se conserva completo en ABAJO y ARRIBA inicia en 0.
    // Así nunca se duplica inventario durante la migración. Luego el administrador puede repartirlo/transferirlo.
    await using (var create = new MySqlCommand("""
        CREATE TABLE IF NOT EXISTS migraciones_sistema (
            clave VARCHAR(120) PRIMARY KEY,
            aplicado_en TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
        );
    """, con)) await create.ExecuteNonQueryAsync();

    await using (var check = new MySqlCommand("SELECT COUNT(*) FROM migraciones_sistema WHERE clave='PREMIU_STOCK_SECTOR_V76';", con))
    {
        if (Convert.ToInt32(await check.ExecuteScalarAsync() ?? 0) > 0)
        {
            // V83: aunque la migración ya exista, reparar cualquier producto nuevo/antiguo
            // que tenga ficha en un solo sector. La ficha hermana se crea con stock 0,
            // por lo que nunca se infla el inventario existente.
            await EnsurePremiumSectorPairsAsync(con);
            return;
        }
    }

    await using var tx = await con.BeginTransactionAsync();
    try
    {
        var generales = new List<(long id, string nombre)>();
        await using (var q = new MySqlCommand("SELECT id,nombre FROM productos WHERE sucursal_id=2 AND estado='ACTIVO' AND UPPER(COALESCE(sector,'GENERAL'))='GENERAL' ORDER BY id FOR UPDATE;", con, tx))
        await using (var rd = await q.ExecuteReaderAsync())
        {
            while (await rd.ReadAsync()) generales.Add((rd.GetInt64(0), rd.IsDBNull(1) ? "" : rd.GetString(1)));
        }

        foreach (var item in generales)
        {
            await using (var abajo = new MySqlCommand("UPDATE productos SET sector='ABAJO' WHERE id=@id;", con, tx))
            {
                abajo.Parameters.AddWithValue("@id", item.id);
                await abajo.ExecuteNonQueryAsync();
            }

            long arribaId = 0;
            await using (var find = new MySqlCommand("SELECT id FROM productos WHERE sucursal_id=2 AND sector='ARRIBA' AND estado='ACTIVO' AND LOWER(TRIM(nombre))=LOWER(TRIM(@n)) ORDER BY id LIMIT 1;", con, tx))
            {
                find.Parameters.AddWithValue("@n", item.nombre);
                object? found = await find.ExecuteScalarAsync();
                if (found != null) arribaId = Convert.ToInt64(found);
            }
            if (arribaId <= 0)
            {
                await using var clone = new MySqlCommand("""
                    INSERT INTO productos
                    (sucursal_id,sector,nombre,categoria,tipo_entrada,unidad_base,unidades_por_entrada,precio_compra,
                     stock_actual,stock_minimo,estado,genera_comision,tipo_comision,valor_comision,sin_limite_stock,rendimiento_vaso)
                    SELECT sucursal_id,'ARRIBA',nombre,categoria,tipo_entrada,unidad_base,unidades_por_entrada,precio_compra,
                           0,stock_minimo,estado,genera_comision,tipo_comision,valor_comision,sin_limite_stock,rendimiento_vaso
                    FROM productos WHERE id=@src;
                    SELECT LAST_INSERT_ID();
                """, con, tx);
                clone.Parameters.AddWithValue("@src", item.id);
                arribaId = Convert.ToInt64(await clone.ExecuteScalarAsync());
                await using var copyPres = new MySqlCommand("""
                    INSERT INTO presentaciones(producto_id,nombre,cantidad_base,precio_venta,estado)
                    SELECT @dst,nombre,cantidad_base,precio_venta,estado FROM presentaciones WHERE producto_id=@src;
                """, con, tx);
                copyPres.Parameters.AddWithValue("@dst", arribaId);
                copyPres.Parameters.AddWithValue("@src", item.id);
                await copyPres.ExecuteNonQueryAsync();
            }
        }

        // Si alguna instalación ya tenía un sector creado manualmente, garantiza que exista la pareja.
        var activos = new List<(long id, string nombre, string sector)>();
        await using (var q = new MySqlCommand("SELECT id,nombre,sector FROM productos WHERE sucursal_id=2 AND estado='ACTIVO' AND sector IN ('ARRIBA','ABAJO') ORDER BY id;", con, tx))
        await using (var rd = await q.ExecuteReaderAsync())
        {
            while (await rd.ReadAsync()) activos.Add((rd.GetInt64(0), rd.IsDBNull(1) ? "" : rd.GetString(1), rd.IsDBNull(2) ? "ABAJO" : rd.GetString(2)));
        }
        foreach (var g in activos.GroupBy(x => (x.nombre ?? "").Trim().ToUpperInvariant()).Where(g => !string.IsNullOrWhiteSpace(g.Key)).ToList())
        {
            foreach (string wanted in new[] { "ARRIBA", "ABAJO" })
            {
                if (g.Any(x => string.Equals(x.sector, wanted, StringComparison.OrdinalIgnoreCase))) continue;
                var src = g.First();
                await using var clone = new MySqlCommand("""
                    INSERT INTO productos
                    (sucursal_id,sector,nombre,categoria,tipo_entrada,unidad_base,unidades_por_entrada,precio_compra,
                     stock_actual,stock_minimo,estado,genera_comision,tipo_comision,valor_comision,sin_limite_stock,rendimiento_vaso)
                    SELECT sucursal_id,@sector,nombre,categoria,tipo_entrada,unidad_base,unidades_por_entrada,precio_compra,
                           0,stock_minimo,estado,genera_comision,tipo_comision,valor_comision,sin_limite_stock,rendimiento_vaso
                    FROM productos WHERE id=@src;
                    SELECT LAST_INSERT_ID();
                """, con, tx);
                clone.Parameters.AddWithValue("@sector", wanted);
                clone.Parameters.AddWithValue("@src", src.id);
                long dst = Convert.ToInt64(await clone.ExecuteScalarAsync());
                await using var copyPres = new MySqlCommand("""
                    INSERT INTO presentaciones(producto_id,nombre,cantidad_base,precio_venta,estado)
                    SELECT @dst,nombre,cantidad_base,precio_venta,estado FROM presentaciones WHERE producto_id=@src;
                """, con, tx);
                copyPres.Parameters.AddWithValue("@dst", dst);
                copyPres.Parameters.AddWithValue("@src", src.id);
                await copyPres.ExecuteNonQueryAsync();
            }
        }

        await using (var mark = new MySqlCommand("INSERT INTO migraciones_sistema(clave) VALUES('PREMIU_STOCK_SECTOR_V76');", con, tx))
            await mark.ExecuteNonQueryAsync();
        await tx.CommitAsync();
        await EnsurePremiumSectorPairsAsync(con);
    }
    catch
    {
        try { await tx.RollbackAsync(); } catch { }
        throw;
    }
}


static async Task EnsureInitialStockLedgerV76Async(MySqlConnection con)
{
    await using (var create = new MySqlCommand("""
        CREATE TABLE IF NOT EXISTS movimientos_inventario_admin (
            id BIGINT AUTO_INCREMENT PRIMARY KEY,
            operation_key VARCHAR(100) NOT NULL,
            sucursal_id INT NOT NULL,
            sector VARCHAR(20) NOT NULL DEFAULT 'GENERAL',
            producto_id BIGINT NOT NULL,
            delta DECIMAL(14,4) NOT NULL,
            motivo VARCHAR(200) NULL,
            fecha DATETIME NOT NULL,
            UNIQUE KEY uk_mov_inv_admin_operation (operation_key)
        );
    """, con)) await create.ExecuteNonQueryAsync();
    await using (var createMig = new MySqlCommand("CREATE TABLE IF NOT EXISTS migraciones_sistema (clave VARCHAR(120) PRIMARY KEY, aplicado_en TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP);", con)) await createMig.ExecuteNonQueryAsync();
    await using (var check = new MySqlCommand("SELECT COUNT(*) FROM migraciones_sistema WHERE clave='STOCK_LEDGER_INITIAL_V76';", con))
        if (Convert.ToInt32(await check.ExecuteScalarAsync() ?? 0) > 0) return;
    await using var tx = await con.BeginTransactionAsync();
    try
    {
        await using var ins = new MySqlCommand("""
            INSERT IGNORE INTO movimientos_inventario_admin(operation_key,sucursal_id,sector,producto_id,delta,motivo,fecha)
            SELECT CONCAT('INIT-V76-',id), sucursal_id, sector, id, GREATEST(stock_actual,0), 'STOCK INICIAL AL ACTIVAR CONTROL POR SECTOR', NOW()
            FROM productos
            WHERE estado='ACTIVO' AND GREATEST(stock_actual,0)>0;
        """, con, tx);
        await ins.ExecuteNonQueryAsync();
        await using var mark = new MySqlCommand("INSERT INTO migraciones_sistema(clave) VALUES('STOCK_LEDGER_INITIAL_V76');", con, tx);
        await mark.ExecuteNonQueryAsync();
        await tx.CommitAsync();
    }
    catch { await tx.RollbackAsync(); throw; }
}

static async Task EnsureSectorLayoutAsync(MySqlConnection con)
{
    // V70: ARRIBA/ABAJO identifica cajas/usuarios y movimientos comerciales.
    // El catálogo e inventario físico se comparte por sucursal y usa sector GENERAL.
    string[] alters =
    {
        "ALTER TABLE usuarios ADD COLUMN sector VARCHAR(20) NOT NULL DEFAULT 'GENERAL';",
        "ALTER TABLE productos ADD COLUMN sector VARCHAR(20) NOT NULL DEFAULT 'GENERAL';",
        "ALTER TABLE detalle_ventas ADD COLUMN sector VARCHAR(20) NOT NULL DEFAULT 'GENERAL';",
        "ALTER TABLE pedidos_movil ADD COLUMN sector VARCHAR(20) NOT NULL DEFAULT 'GENERAL';",
        "ALTER TABLE detalle_pedidos_movil ADD COLUMN sector VARCHAR(20) NOT NULL DEFAULT 'GENERAL';",
        "ALTER TABLE reportes_productos_movil ADD COLUMN sector VARCHAR(20) NOT NULL DEFAULT 'GENERAL';",
        "ALTER TABLE comisiones_meseras ADD COLUMN sector VARCHAR(20) NOT NULL DEFAULT 'GENERAL';"
    };
    foreach (string sql in alters)
    {
        try { await new MySqlCommand(sql, con).ExecuteNonQueryAsync(); } catch { }
    }

    // Índice legado por sector se conserva por compatibilidad; V70 opera productos como catálogo compartido.
    try { await new MySqlCommand("ALTER TABLE productos DROP INDEX uk_productos_sucursal_nombre;", con).ExecuteNonQueryAsync(); } catch { }
    try { await new MySqlCommand("CREATE INDEX ix_productos_sucursal_sector_nombre ON productos(sucursal_id, sector, nombre);", con).ExecuteNonQueryAsync(); } catch { }

    await new MySqlCommand("UPDATE usuarios SET sector='GENERAL' WHERE sucursal_id=1;", con).ExecuteNonQueryAsync();
    await new MySqlCommand("UPDATE usuarios SET sector=CASE WHEN UPPER(COALESCE(caja_nombre,'')) LIKE '%ABAJO%' OR UPPER(COALESCE(caja_nombre,''))='CAJA 2' THEN 'ABAJO' ELSE 'ARRIBA' END WHERE sucursal_id=2 AND (sector IS NULL OR TRIM(sector)='' OR UPPER(sector)='GENERAL');", con).ExecuteNonQueryAsync();
    try { await new MySqlCommand("UPDATE detalle_ventas d INNER JOIN productos p ON p.id=d.producto_id SET d.sector=p.sector WHERE d.sector IS NULL OR TRIM(d.sector)='' OR (UPPER(d.sector)='GENERAL' AND p.sucursal_id=2);", con).ExecuteNonQueryAsync(); } catch { }
    try { await new MySqlCommand("UPDATE pedidos_movil p LEFT JOIN usuarios u ON u.usuario=p.mesera_usuario SET p.sector=CASE WHEN p.sucursal_id=1 THEN 'GENERAL' ELSE COALESCE(NULLIF(u.sector,''),'ARRIBA') END WHERE p.sector IS NULL OR TRIM(p.sector)='' OR (UPPER(p.sector)='GENERAL' AND p.sucursal_id=2);", con).ExecuteNonQueryAsync(); } catch { }
    try { await new MySqlCommand("UPDATE detalle_pedidos_movil d INNER JOIN pedidos_movil p ON p.id=d.pedido_id SET d.sector=p.sector WHERE d.sector IS NULL OR TRIM(d.sector)='' OR (UPPER(d.sector)='GENERAL' AND p.sucursal_id=2);", con).ExecuteNonQueryAsync(); } catch { }
    try { await new MySqlCommand("UPDATE reportes_productos_movil r LEFT JOIN usuarios u ON u.usuario=r.usuario SET r.sector=CASE WHEN r.sucursal_id=1 THEN 'GENERAL' ELSE COALESCE(NULLIF(u.sector,''),'ARRIBA') END WHERE r.sector IS NULL OR TRIM(r.sector)='' OR (UPPER(r.sector)='GENERAL' AND r.sucursal_id=2);", con).ExecuteNonQueryAsync(); } catch { }
    try { await new MySqlCommand("UPDATE comisiones_meseras c INNER JOIN pedidos_movil p ON p.id=c.pedido_id SET c.sector=p.sector WHERE c.sector IS NULL OR TRIM(c.sector)='' OR (UPPER(c.sector)='GENERAL' AND c.sucursal_id=2);", con).ExecuteNonQueryAsync(); } catch { }
}

static string HoraATurno(DateTime fecha)
    => fecha.Hour >= 8 && fecha.Hour < 20 ? "MAÑANA" : "NOCHE";

static string NormalizarCategoriaProducto(string? categoria, string? nombre)
{
    string c = (categoria ?? "").Trim().ToUpperInvariant();
    string n = (nombre ?? "").Trim().ToUpperInvariant();

    if (c.Contains("CENICER") || c.Contains("CINCER") || n.Contains("CENICER") || n.Contains("CINCERO")) return "Ceniceros";
    if (c.Contains("ACCESORIO") || n == "ENCENDEDOR" || n == "COPAS DE VINO" || n == "VASO TEQUILERO" || n == "VASOS CERVECEROS" || n == "VASOS DE SODA" || n == "VASOS DE WISKI" || n == "VASOS DE WHISKY" || n == "VASO DE WISKI" || n == "VASO DE WISKIE" || n == "VASO DE WHISKY") return "Accesorios";
    if (c.Contains("SERVIDOS EN VASO") || c.Contains("LO QUE SE SIRVE EN VASO") || (n.StartsWith("VASO ") && n != "VASO TEQUILERO")) return "Servidos en vaso";
    if (c == "COCAS" || c.Contains("COCA MACHUCADA") || n.StartsWith("COCA EL BRUJO") || n.StartsWith("COCA AMAIRE") || n.StartsWith("COCA BICO") || n.StartsWith("COCA MARACUYA") || n.StartsWith("COCA MEDUSA") || n.StartsWith("COCA RED") || n.StartsWith("COCA SANDIA") || n.StartsWith("COCA YOG")) return "Cocas";
    if (c.Contains("COMBO") || c.Contains("PROMO") || n.Contains("COMBO") || n.StartsWith("PROMO ")) return "Combos / Promos";
    if (c == "AGUA" || n.Contains("AGUA") || n.StartsWith("SANTE ")) return "Agua";
    if (c.Contains("ENERGIZANTE") || n.Contains("RED BULL") || n.Contains("CICLON") || n.Contains("POWER") || n == "BLACK") return "Energizantes";
    if (c.Contains("SODA") || n.StartsWith("SODA ") || n.Contains("SPRITE") || n.Contains("FANTA")) return "Sodas";
    if (c.Contains("CERVEZA") || c == "CERVEZAS" || n.StartsWith("CERVEZA ") || n.Contains("PACEÑA") || n.Contains("CONTI") || n.Contains("CORONA") || n.Contains("SKOL") || n.Contains("SKUL") || n.Contains("AMSTEL")) return "Cervezas";
    if (c.Contains("CIGARRO") || n.Contains("CIGARRO") || n.Contains("CAMEL") || n.Contains("BOHEM") || n.Contains("BOHEN") || n.Contains("HILLS")) return "Cigarros";
    if (c.Contains("SNACK") || c.Contains("PIQUEO") || n.Contains("NACHO") || n.Contains("PAPA") || n.Contains("PIZON") || n.Contains("PINZON") || n.Contains("PLATANITO") || n.Contains("TAKIS") || n.Contains("MIX NAX")) return "Snacks y piqueos";
    // V71: TRAGO DULCE se mantiene en Tragos / Botellas; TRAGO tiene prioridad sobre DULCE.
    if (c.Contains("TRAGO") || c.Contains("BOTELLA") || n.Contains("TRAGO DULCE") || n.StartsWith("TRAGO ") || n.Contains("RON") || n.Contains("FERNET") || n.Contains("GIN") || n.Contains("TEQUILA") || n.Contains("WHIKY") || n.Contains("WHISK") || n.Contains("WISK") || n.Contains("VINO") || n.Contains("AMARULA") || n.Contains("FLOR DE CAÑA") || n.Contains("FOUR LOCO") || n.Contains("FLOW") || n.Contains("HAVANA") || n.Contains("HABANA") || n.Contains("ICE 51") || n.Contains("NOCHE ICE") || n.Contains("OLD")) return "Tragos / Botellas";
    if (c.Contains("DULCE") || c.Contains("GOLOSINA") || n.Contains("CHICLE") || n.Contains("CLORETS") || n.Contains("BELDEN") || n.Contains("ARCOR") || n.Contains("HALLS") || n.Contains("MABEL") || n.Contains("GROSO") || n.Contains("MINT") || n.Contains("CHUPETE") || n.Contains("EUCALIPTO") || n.Contains("BICO SABORES")) return "Dulces y golosinas";
    return "Otros";
}

static async Task<(decimal normal, decimal promoLunes, decimal privada, decimal promoPrivada, bool promoActiva)> EnsureTablePricingAsync(MySqlConnection con)
{
    await using (var create = new MySqlCommand("""
        CREATE TABLE IF NOT EXISTS configuracion_sistema (
            clave VARCHAR(100) PRIMARY KEY,
            valor_decimal DECIMAL(12,2) NULL,
            actualizado DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
        );
    """, con))
    {
        await create.ExecuteNonQueryAsync();
    }

    // V54: valores iniciales solicitados. El lunes la Caja aplica la promo al INICIAR
    // la sesión y guarda esa tarifa en PricePerHourUsed / tarifa_hora.
    string[] seedSql =
    {
        "INSERT IGNORE INTO configuracion_sistema (clave, valor_decimal, actualizado) VALUES ('TARIFA_MESA_HORA', 20.00, NOW());",
        "INSERT IGNORE INTO configuracion_sistema (clave, valor_decimal, actualizado) VALUES ('TARIFA_MESA_NORMAL', 20.00, NOW());",
        "INSERT IGNORE INTO configuracion_sistema (clave, valor_decimal, actualizado) VALUES ('TARIFA_MESA_PROMO_LUNES', 10.00, NOW());",
        "INSERT IGNORE INTO configuracion_sistema (clave, valor_decimal, actualizado) VALUES ('TARIFA_MESA_PRIVADA', 40.00, NOW());",
        "INSERT IGNORE INTO configuracion_sistema (clave, valor_decimal, actualizado) VALUES ('TARIFA_MESA_PROMO_PRIVADA', 20.00, NOW());",
        "INSERT IGNORE INTO configuracion_sistema (clave, valor_decimal, actualizado) VALUES ('PROMO_LUNES_ACTIVA', 1.00, NOW());"
    };
    foreach (string sql in seedSql)
    {
        await using var seed = new MySqlCommand(sql, con);
        await seed.ExecuteNonQueryAsync();
    }

    // V54: migración de precio UNA sola vez. Cumple la regla solicitada:
    // lunes Bs 10/h y resto de días Bs 20/h. Después el administrador puede editar
    // normal, promo y privada sin que el reinicio vuelva a sobrescribirlos.
    bool pricingV54Applied = false;
    await using (var checkMigration = new MySqlCommand("SELECT valor_decimal FROM configuracion_sistema WHERE clave='TARIFA_V54_APLICADA' LIMIT 1;", con))
    {
        object? value = await checkMigration.ExecuteScalarAsync();
        pricingV54Applied = value != null && Convert.ToDecimal(value) > 0;
    }
    if (!pricingV54Applied)
    {
        await using var migratePricing = new MySqlCommand("""
            INSERT INTO configuracion_sistema (clave, valor_decimal, actualizado) VALUES ('TARIFA_MESA_HORA', 20.00, NOW())
            ON DUPLICATE KEY UPDATE valor_decimal=20.00, actualizado=NOW();
            INSERT INTO configuracion_sistema (clave, valor_decimal, actualizado) VALUES ('TARIFA_MESA_NORMAL', 20.00, NOW())
            ON DUPLICATE KEY UPDATE valor_decimal=20.00, actualizado=NOW();
            INSERT INTO configuracion_sistema (clave, valor_decimal, actualizado) VALUES ('TARIFA_MESA_PROMO_LUNES', 10.00, NOW())
            ON DUPLICATE KEY UPDATE valor_decimal=10.00, actualizado=NOW();
            INSERT INTO configuracion_sistema (clave, valor_decimal, actualizado) VALUES ('TARIFA_MESA_PRIVADA', 40.00, NOW())
            ON DUPLICATE KEY UPDATE valor_decimal=40.00, actualizado=NOW();
            INSERT INTO configuracion_sistema (clave, valor_decimal, actualizado) VALUES ('TARIFA_MESA_PROMO_PRIVADA', 20.00, NOW())
            ON DUPLICATE KEY UPDATE valor_decimal=VALUES(valor_decimal), actualizado=NOW();
            INSERT INTO configuracion_sistema (clave, valor_decimal, actualizado) VALUES ('PROMO_LUNES_ACTIVA', 1.00, NOW())
            ON DUPLICATE KEY UPDATE valor_decimal=1.00, actualizado=NOW();
            INSERT INTO configuracion_sistema (clave, valor_decimal, actualizado) VALUES ('TARIFA_V54_APLICADA', 1.00, NOW())
            ON DUPLICATE KEY UPDATE valor_decimal=1.00, actualizado=NOW();
        """, con);
        await migratePricing.ExecuteNonQueryAsync();
    }

    // V65: corregir una sola vez instalaciones anteriores donde la privada quedó en Bs 20/h.
    bool private40Applied = false;
    await using (var checkPrivate40 = new MySqlCommand("SELECT valor_decimal FROM configuracion_sistema WHERE clave='TARIFA_PRIVADA_40_APLICADA' LIMIT 1;", con))
    {
        object? value = await checkPrivate40.ExecuteScalarAsync();
        private40Applied = value != null && Convert.ToDecimal(value) > 0;
    }
    if (!private40Applied)
    {
        await using var migratePrivate40 = new MySqlCommand("""
            UPDATE configuracion_sistema SET valor_decimal=40.00, actualizado=NOW()
            WHERE clave='TARIFA_MESA_PRIVADA' AND (valor_decimal IS NULL OR valor_decimal=20.00);
            INSERT INTO configuracion_sistema (clave, valor_decimal, actualizado) VALUES ('TARIFA_PRIVADA_40_APLICADA', 1.00, NOW())
            ON DUPLICATE KEY UPDATE valor_decimal=1.00, actualizado=NOW();
        """, con);
        await migratePrivate40.ExecuteNonQueryAsync();
    }

    // V76: aplicar UNA sola vez la promo solicitada por el propietario:
    // mesa NORMAL los lunes a Bs 10/h (2 h = Bs 20). La privada permanece Bs 40/h.
    // Después de esta migración el administrador puede editar/desactivar la promo sin que
    // un reinicio de Railway vuelva a sobrescribir su decisión.
    bool mondayPromoV76Applied = false;
    await using (var checkPromo76 = new MySqlCommand("SELECT valor_decimal FROM configuracion_sistema WHERE clave='PROMO_LUNES_V76_APLICADA' LIMIT 1;", con))
    {
        object? value = await checkPromo76.ExecuteScalarAsync();
        mondayPromoV76Applied = value != null && Convert.ToDecimal(value) > 0;
    }
    if (!mondayPromoV76Applied)
    {
        await using var migratePromo76 = new MySqlCommand("""
            INSERT INTO configuracion_sistema (clave, valor_decimal, actualizado) VALUES ('TARIFA_MESA_PROMO_LUNES', 10.00, NOW())
            ON DUPLICATE KEY UPDATE valor_decimal=10.00, actualizado=NOW();
            INSERT INTO configuracion_sistema (clave, valor_decimal, actualizado) VALUES ('PROMO_LUNES_ACTIVA', 1.00, NOW())
            ON DUPLICATE KEY UPDATE valor_decimal=1.00, actualizado=NOW();
            INSERT INTO configuracion_sistema (clave, valor_decimal, actualizado) VALUES ('PROMO_LUNES_V76_APLICADA', 1.00, NOW())
            ON DUPLICATE KEY UPDATE valor_decimal=1.00, actualizado=NOW();
        """, con);
        await migratePromo76.ExecuteNonQueryAsync();
    }

    decimal normal = 20m, promo = 10m, privada = 40m, promoPrivada = 20m;
    bool promoActiva = true;
    await using (var get = new MySqlCommand("""
        SELECT clave, valor_decimal
        FROM configuracion_sistema
        WHERE clave IN ('TARIFA_MESA_HORA','TARIFA_MESA_NORMAL','TARIFA_MESA_PROMO_LUNES','TARIFA_MESA_PRIVADA','TARIFA_MESA_PROMO_PRIVADA','PROMO_LUNES_ACTIVA');
    """, con))
    await using (var rd = await get.ExecuteReaderAsync())
    {
        decimal? legacy = null;
        while (await rd.ReadAsync())
        {
            string key = rd.IsDBNull(0) ? "" : rd.GetString(0);
            decimal value = rd.IsDBNull(1) ? 0m : rd.GetDecimal(1);
            switch (key)
            {
                case "TARIFA_MESA_HORA": legacy = value; break;
                case "TARIFA_MESA_NORMAL": if (value > 0) normal = value; break;
                case "TARIFA_MESA_PROMO_LUNES": if (value > 0) promo = value; break;
                case "TARIFA_MESA_PRIVADA": if (value > 0) privada = value; break;
                case "TARIFA_MESA_PROMO_PRIVADA": if (value > 0) promoPrivada = value; break;
                case "PROMO_LUNES_ACTIVA": promoActiva = value > 0; break;
            }
        }
        // Una instalación antigua solo puede tener TARIFA_MESA_HORA. Si NORMAL sigue en su
        // valor semilla pero la tarifa antigua era válida, la conservamos para no sorprender al local.
        if (legacy.HasValue && legacy.Value > 0 && normal == 20m && legacy.Value != 30m)
            normal = legacy.Value;
    }

    if (normal <= 0) normal = 20m;
    if (promo <= 0) promo = 10m;
    if (privada <= 0) privada = 40m;
    if (promoPrivada <= 0) promoPrivada = 20m;
    normal = Math.Round(normal, 2);
    promo = Math.Round(promo, 2);
    privada = Math.Round(privada, 2);
    promoPrivada = Math.Round(promoPrivada, 2);

    // TARIFA_MESA_HORA queda espejada a NORMAL para clientes anteriores.
    await using (var save = new MySqlCommand("""
        INSERT INTO configuracion_sistema (clave, valor_decimal, actualizado) VALUES ('TARIFA_MESA_HORA', @normal, NOW())
        ON DUPLICATE KEY UPDATE valor_decimal=VALUES(valor_decimal), actualizado=NOW();
        INSERT INTO configuracion_sistema (clave, valor_decimal, actualizado) VALUES ('TARIFA_MESA_NORMAL', @normal, NOW())
        ON DUPLICATE KEY UPDATE valor_decimal=VALUES(valor_decimal), actualizado=NOW();
    """, con))
    {
        save.Parameters.AddWithValue("@normal", normal);
        await save.ExecuteNonQueryAsync();
    }

    // precio_hora en mesas es precio BASE. Nunca se escribe aquí la promo del lunes,
    // evitando que el martes quede accidentalmente la tarifa promocional.
    await using (var sync = new MySqlCommand("""
        UPDATE mesas
        SET precio_hora = CASE
            WHEN UPPER(TRIM(COALESCE(tipo_mesa,'NORMAL')))='PRIVADA' THEN @privada
            ELSE @normal
        END;
    """, con))
    {
        sync.Parameters.AddWithValue("@normal", normal);
        sync.Parameters.AddWithValue("@privada", privada);
        try { await sync.ExecuteNonQueryAsync(); } catch { }
    }

    return (normal, promo, privada, promoPrivada, promoActiva);
}

static int ParsePromoDate(string? value)
{
    if (DateTime.TryParse(value, out DateTime d))
        return d.Year * 10000 + d.Month * 100 + d.Day;
    return 0;
}

static string PromoDateText(int yyyymmdd)
{
    if (yyyymmdd <= 0) return "";
    int y = yyyymmdd / 10000;
    int m = (yyyymmdd / 100) % 100;
    int d = yyyymmdd % 100;
    try { return new DateTime(y, m, d).ToString("yyyy-MM-dd"); } catch { return ""; }
}

static async Task<(int mondayBranch, int mondaySector, bool tempEnabled, int tempStart, int tempEnd, int tempBranch, int tempSector)> EnsureTablePromotionScopeAsync(MySqlConnection con)
{
    // Usa la misma tabla numérica de configuración para no cambiar ni migrar ventas/inventario.
    string[] seeds =
    {
        "INSERT IGNORE INTO configuracion_sistema(clave,valor_decimal,actualizado) VALUES('PROMO_LUNES_SUCURSAL',0,NOW());",
        "INSERT IGNORE INTO configuracion_sistema(clave,valor_decimal,actualizado) VALUES('PROMO_LUNES_SECTOR',0,NOW());",
        "INSERT IGNORE INTO configuracion_sistema(clave,valor_decimal,actualizado) VALUES('PROMO_TEMPORAL_ACTIVA',0,NOW());",
        "INSERT IGNORE INTO configuracion_sistema(clave,valor_decimal,actualizado) VALUES('PROMO_TEMPORAL_DESDE',0,NOW());",
        "INSERT IGNORE INTO configuracion_sistema(clave,valor_decimal,actualizado) VALUES('PROMO_TEMPORAL_HASTA',0,NOW());",
        "INSERT IGNORE INTO configuracion_sistema(clave,valor_decimal,actualizado) VALUES('PROMO_TEMPORAL_SUCURSAL',0,NOW());",
        "INSERT IGNORE INTO configuracion_sistema(clave,valor_decimal,actualizado) VALUES('PROMO_TEMPORAL_SECTOR',0,NOW());"
    };
    foreach (string sql in seeds) await new MySqlCommand(sql, con).ExecuteNonQueryAsync();

    int mondayBranch=0, mondaySector=0, tempStart=0, tempEnd=0, tempBranch=0, tempSector=0;
    bool tempEnabled=false;
    await using var cmd = new MySqlCommand("""
        SELECT clave,valor_decimal FROM configuracion_sistema
        WHERE clave IN ('PROMO_LUNES_SUCURSAL','PROMO_LUNES_SECTOR','PROMO_TEMPORAL_ACTIVA',
                        'PROMO_TEMPORAL_DESDE','PROMO_TEMPORAL_HASTA','PROMO_TEMPORAL_SUCURSAL','PROMO_TEMPORAL_SECTOR');
    """, con);
    await using var rd = await cmd.ExecuteReaderAsync();
    while (await rd.ReadAsync())
    {
        string k = rd.IsDBNull(0) ? "" : rd.GetString(0);
        int v = rd.IsDBNull(1) ? 0 : (int)Math.Round(rd.GetDecimal(1));
        switch(k)
        {
            case "PROMO_LUNES_SUCURSAL": mondayBranch=Math.Clamp(v,0,2); break;
            case "PROMO_LUNES_SECTOR": mondaySector=Math.Clamp(v,0,2); break;
            case "PROMO_TEMPORAL_ACTIVA": tempEnabled=v>0; break;
            case "PROMO_TEMPORAL_DESDE": tempStart=v; break;
            case "PROMO_TEMPORAL_HASTA": tempEnd=v; break;
            case "PROMO_TEMPORAL_SUCURSAL": tempBranch=Math.Clamp(v,0,2); break;
            case "PROMO_TEMPORAL_SECTOR": tempSector=Math.Clamp(v,0,2); break;
        }
    }
    return (mondayBranch,mondaySector,tempEnabled,tempStart,tempEnd,tempBranch,tempSector);
}

static async Task<decimal> EnsureGlobalTableRateAsync(MySqlConnection con)
{
    var pricing = await EnsureTablePricingAsync(con);
    return pricing.normal;
}


static async Task<(int brujo, int premiumArriba, int premiumAbajo, int privadaPremiumArriba)> EnsureTableLayoutSettingsAsync(MySqlConnection con)
{
    await EnsureTablePricingAsync(con); // asegura configuracion_sistema
    string[] seed =
    {
        "INSERT IGNORE INTO configuracion_sistema(clave,valor_decimal,actualizado) VALUES('MESAS_EL_BRUJO_GENERAL',8,NOW());",
        "INSERT IGNORE INTO configuracion_sistema(clave,valor_decimal,actualizado) VALUES('MESAS_PREMIU_ARRIBA',5,NOW());",
        "INSERT IGNORE INTO configuracion_sistema(clave,valor_decimal,actualizado) VALUES('MESAS_PREMIU_ABAJO',10,NOW());",
        "INSERT IGNORE INTO configuracion_sistema(clave,valor_decimal,actualizado) VALUES('MESA_PRIVADA_PREMIU_ARRIBA',5,NOW());"
    };
    foreach (string sql in seed) await new MySqlCommand(sql, con).ExecuteNonQueryAsync();
    int brujo=8, arriba=5, abajo=10, privada=5;
    await using var q = new MySqlCommand("SELECT clave,valor_decimal FROM configuracion_sistema WHERE clave IN ('MESAS_EL_BRUJO_GENERAL','MESAS_PREMIU_ARRIBA','MESAS_PREMIU_ABAJO','MESA_PRIVADA_PREMIU_ARRIBA');", con);
    await using var rd = await q.ExecuteReaderAsync();
    while (await rd.ReadAsync())
    {
        string k=rd.GetString(0); int v=Math.Clamp((int)Math.Round(rd.IsDBNull(1)?0m:rd.GetDecimal(1)),1,29);
        if(k=="MESAS_EL_BRUJO_GENERAL") brujo=v;
        else if(k=="MESAS_PREMIU_ARRIBA") arriba=v;
        else if(k=="MESAS_PREMIU_ABAJO") abajo=v;
        else if(k=="MESA_PRIVADA_PREMIU_ARRIBA") privada=v;
    }
    return (brujo,arriba,abajo,privada);
}

static async Task EnsureAppMeseraTables(MySqlConnection con)
{
    // Las columnas sector se agregan después de que existan las tablas base.

    await using (var alter1 = new MySqlCommand("ALTER TABLE productos ADD COLUMN genera_comision TINYINT(1) NOT NULL DEFAULT 0;", con))
    {
        try { await alter1.ExecuteNonQueryAsync(); } catch { }
    }

    await using (var alter2 = new MySqlCommand("ALTER TABLE productos ADD COLUMN tipo_comision VARCHAR(30) NOT NULL DEFAULT 'NINGUNA';", con))
    {
        try { await alter2.ExecuteNonQueryAsync(); } catch { }
    }

    await using (var alter3 = new MySqlCommand("ALTER TABLE productos ADD COLUMN valor_comision DECIMAL(10,2) NOT NULL DEFAULT 0;", con))
    {
        try { await alter3.ExecuteNonQueryAsync(); } catch { }
    }

    await using (var alter4 = new MySqlCommand("ALTER TABLE productos ADD COLUMN sin_limite_stock TINYINT(1) NOT NULL DEFAULT 0;", con))
    {
        try { await alter4.ExecuteNonQueryAsync(); } catch { }
    }

    await using (var alter5 = new MySqlCommand("ALTER TABLE productos ADD COLUMN tipo_entrada VARCHAR(60) NOT NULL DEFAULT 'PAQUETE';", con))
    {
        try { await alter5.ExecuteNonQueryAsync(); } catch { }
    }

    await using (var alter6 = new MySqlCommand("ALTER TABLE productos ADD COLUMN unidades_por_entrada INT NOT NULL DEFAULT 1;", con))
    {
        try { await alter6.ExecuteNonQueryAsync(); } catch { }
    }

    await using (var alter7 = new MySqlCommand("ALTER TABLE productos ADD COLUMN precio_compra DECIMAL(10,2) NOT NULL DEFAULT 0;", con))
    {
        try { await alter7.ExecuteNonQueryAsync(); } catch { }
    }
    await using (var alter8 = new MySqlCommand("ALTER TABLE productos ADD COLUMN rendimiento_vaso INT NOT NULL DEFAULT 10;", con))
    {
        try { await alter8.ExecuteNonQueryAsync(); } catch { }
    }

    // Migración de nombres de categoría solicitados para el módulo Productos / Stock.
    await using (var catMig = new MySqlCommand("""
        UPDATE productos SET categoria = 'Cocas' WHERE categoria = 'Coca machucada';
        UPDATE productos SET categoria = 'Otros' WHERE categoria IN ('Varios', 'Otros / Extras');
        UPDATE productos SET categoria = 'Accesorios' WHERE categoria = 'Vasos/Accesorios';
        UPDATE productos SET categoria = 'Ceniceros'
        WHERE UPPER(nombre) LIKE '%CENICER%' OR UPPER(nombre) LIKE '%CINCERO%';
        UPDATE productos SET categoria = 'Accesorios'
        WHERE UPPER(nombre) IN ('ENCENDEDOR','COPAS DE VINO','VASO TEQUILERO','VASOS CERVECEROS','VASOS DE SODA','VASOS DE WISKI','VASOS DE WHISKY','VASO DE WISKI','VASO DE WISKIE','VASO DE WHISKY');
    """, con))
    {
        try { await catMig.ExecuteNonQueryAsync(); } catch { }
    }

    await using (var cmd = new MySqlCommand("""
        CREATE TABLE IF NOT EXISTS pedidos_movil (
            id BIGINT AUTO_INCREMENT PRIMARY KEY,
            sucursal_id INT NOT NULL,
            sector VARCHAR(20) NOT NULL DEFAULT 'GENERAL',
            mesa_id INT NOT NULL,
            mesa VARCHAR(100) NOT NULL,
            mesera_usuario VARCHAR(100) NOT NULL,
            mesera_nombre VARCHAR(150) NOT NULL,
            cajero_usuario VARCHAR(100) NULL,
            fecha DATETIME NOT NULL,
            fecha_respuesta DATETIME NULL,
            estado VARCHAR(30) NOT NULL DEFAULT 'PENDIENTE',
            total DECIMAL(10,2) NOT NULL DEFAULT 0,
            observacion VARCHAR(250) NULL,
            sync_key VARCHAR(180) NOT NULL,
            UNIQUE KEY uk_pedidos_movil_sync (sync_key),
            INDEX idx_pedidos_movil_sucursal_estado (sucursal_id, estado),
            INDEX idx_pedidos_movil_mesera (mesera_usuario)
        );
    """, con))
    {
        await cmd.ExecuteNonQueryAsync();
    }

    await using (var cmd = new MySqlCommand("""
        CREATE TABLE IF NOT EXISTS detalle_pedidos_movil (
            id BIGINT AUTO_INCREMENT PRIMARY KEY,
            pedido_id BIGINT NOT NULL,
            sector VARCHAR(20) NOT NULL DEFAULT 'GENERAL',
            producto_id BIGINT NOT NULL,
            presentacion_id BIGINT NOT NULL,
            producto VARCHAR(180) NOT NULL,
            presentacion VARCHAR(120) NOT NULL,
            cantidad DECIMAL(10,2) NOT NULL DEFAULT 0,
            precio_unitario DECIMAL(10,2) NOT NULL DEFAULT 0,
            subtotal DECIMAL(10,2) NOT NULL DEFAULT 0,
            genera_comision TINYINT(1) NOT NULL DEFAULT 0,
            tipo_comision VARCHAR(30) NOT NULL DEFAULT 'NINGUNA',
            valor_comision DECIMAL(10,2) NOT NULL DEFAULT 0,
            comision_calculada DECIMAL(10,2) NOT NULL DEFAULT 0,
            INDEX idx_detalle_pedidos_movil_pedido (pedido_id)
        );
    """, con))
    {
        await cmd.ExecuteNonQueryAsync();
    }

    await using (var cmd = new MySqlCommand("""
        CREATE TABLE IF NOT EXISTS comisiones_meseras (
            id BIGINT AUTO_INCREMENT PRIMARY KEY,
            pedido_id BIGINT NOT NULL,
            sucursal_id INT NOT NULL,
            sector VARCHAR(20) NOT NULL DEFAULT 'GENERAL',
            mesa_id INT NOT NULL,
            mesera_usuario VARCHAR(100) NOT NULL,
            mesera_nombre VARCHAR(150) NOT NULL,
            fecha DATETIME NOT NULL,
            producto VARCHAR(180) NOT NULL,
            cantidad DECIMAL(10,2) NOT NULL DEFAULT 0,
            venta_total DECIMAL(10,2) NOT NULL DEFAULT 0,
            comision_total DECIMAL(10,2) NOT NULL DEFAULT 0,
            estado VARCHAR(30) NOT NULL DEFAULT 'PENDIENTE_PAGO',
            UNIQUE KEY uk_comision_pedido (pedido_id, producto),
            INDEX idx_comisiones_meseras_fecha (fecha),
            INDEX idx_comisiones_meseras_mesera (mesera_usuario)
        );
    """, con))
    {
        await cmd.ExecuteNonQueryAsync();
    }

    await using (var cmd = new MySqlCommand("""
        CREATE TABLE IF NOT EXISTS reportes_productos_movil (
            id BIGINT AUTO_INCREMENT PRIMARY KEY,
            sucursal_id INT NOT NULL,
            sector VARCHAR(20) NOT NULL DEFAULT 'GENERAL',
            turno VARCHAR(30) NOT NULL DEFAULT 'MAÑANA',
            usuario VARCHAR(100) NOT NULL,
            nombre VARCHAR(150) NOT NULL,
            fecha DATETIME NOT NULL,
            producto_id BIGINT NULL,
            presentacion_id BIGINT NULL,
            producto VARCHAR(180) NOT NULL,
            presentacion VARCHAR(120) NULL,
            cantidad DECIMAL(10,2) NOT NULL DEFAULT 0,
            precio_unitario DECIMAL(10,2) NOT NULL DEFAULT 0,
            costo_perdido DECIMAL(10,2) NOT NULL DEFAULT 0,
            motivo VARCHAR(80) NOT NULL,
            observacion VARCHAR(250) NULL,
            sync_key VARCHAR(180) NOT NULL,
            UNIQUE KEY uk_reporte_producto_sync (sync_key),
            INDEX idx_reporte_producto_fecha (fecha),
            INDEX idx_reporte_producto_sucursal (sucursal_id)
        );
    """, con))
    {
        await cmd.ExecuteNonQueryAsync();
    }

    await using (var seed = new MySqlCommand("""
        INSERT INTO usuarios (usuario, clave, rol, sucursal_id, estado)
        SELECT 'ana_mesera', 'mesera123', 'MESERA', 1, 'ACTIVO'
        WHERE NOT EXISTS (SELECT 1 FROM usuarios WHERE usuario = 'ana_mesera');

        INSERT INTO usuarios (usuario, clave, rol, sucursal_id, estado)
        SELECT 'rosa_mesera', 'mesera123', 'MESERA', 2, 'ACTIVO'
        WHERE NOT EXISTS (SELECT 1 FROM usuarios WHERE usuario = 'rosa_mesera');
    """, con))
    {
        try { await seed.ExecuteNonQueryAsync(); } catch { }
    }
}


static async Task EnsureShiftCloseTables(MySqlConnection con)
{
    await using var cmd = new MySqlCommand("""
        CREATE TABLE IF NOT EXISTS cierres_turno (
            id BIGINT AUTO_INCREMENT PRIMARY KEY,
            sucursal_id INT NOT NULL,
            sucursal VARCHAR(120) NOT NULL,
            cajero_usuario VARCHAR(100) NOT NULL,
            cajero_nombre VARCHAR(150) NOT NULL,
            caja VARCHAR(100) NULL,
            turno VARCHAR(30) NOT NULL,
            inicio DATETIME NOT NULL,
            fin DATETIME NOT NULL,
            hora_entrada DATETIME NOT NULL,
            fecha_cierre DATETIME NOT NULL,
            transacciones_total INT NOT NULL DEFAULT 0,
            transacciones_efectivo INT NOT NULL DEFAULT 0,
            transacciones_qr INT NOT NULL DEFAULT 0,
            transacciones_tarjeta INT NOT NULL DEFAULT 0,
            transacciones_transferencia INT NOT NULL DEFAULT 0,
            efectivo DECIMAL(12,2) NOT NULL DEFAULT 0,
            qr DECIMAL(12,2) NOT NULL DEFAULT 0,
            tarjeta DECIMAL(12,2) NOT NULL DEFAULT 0,
            transferencia DECIMAL(12,2) NOT NULL DEFAULT 0,
            sin_metodo DECIMAL(12,2) NOT NULL DEFAULT 0,
            productos_total DECIMAL(12,2) NOT NULL DEFAULT 0,
            mesas_total DECIMAL(12,2) NOT NULL DEFAULT 0,
            minutos_jugados INT NOT NULL DEFAULT 0,
            propinas_total DECIMAL(12,2) NOT NULL DEFAULT 0,
            cortesias_valor DECIMAL(12,2) NOT NULL DEFAULT 0,
            comisiones_total DECIMAL(12,2) NOT NULL DEFAULT 0,
            gastos_total DECIMAL(12,2) NOT NULL DEFAULT 0,
            perdidas_total DECIMAL(12,2) NOT NULL DEFAULT 0,
            total_generado DECIMAL(12,2) NOT NULL DEFAULT 0,
            neto_turno DECIMAL(12,2) NOT NULL DEFAULT 0,
            observaciones TEXT NULL,
            detalle_json LONGTEXT NOT NULL,
            estado VARCHAR(20) NOT NULL DEFAULT 'CERRADO',
            reapertura_count INT NOT NULL DEFAULT 0,
            ultima_reapertura DATETIME NULL,
            reabierto_por VARCHAR(100) NULL,
            motivo_reapertura VARCHAR(250) NULL,
            cierre_key VARCHAR(190) NULL,
            sync_key VARCHAR(220) NOT NULL,
            UNIQUE KEY uk_cierre_turno_natural (cierre_key),
            UNIQUE KEY uk_cierre_turno_sync (sync_key),
            INDEX idx_cierre_turno_fecha (fecha_cierre),
            INDEX idx_cierre_turno_sucursal (sucursal_id),
            INDEX idx_cierre_turno_cajero (cajero_usuario)
        );
    """, con);
    await cmd.ExecuteNonQueryAsync();

    // V89: columnas de reapertura administrativa. No se borra historial.
    try { await new MySqlCommand("ALTER TABLE cierres_turno ADD COLUMN estado VARCHAR(20) NOT NULL DEFAULT 'CERRADO' AFTER detalle_json;", con).ExecuteNonQueryAsync(); } catch { }
    try { await new MySqlCommand("ALTER TABLE cierres_turno ADD COLUMN reapertura_count INT NOT NULL DEFAULT 0 AFTER estado;", con).ExecuteNonQueryAsync(); } catch { }
    try { await new MySqlCommand("ALTER TABLE cierres_turno ADD COLUMN ultima_reapertura DATETIME NULL AFTER reapertura_count;", con).ExecuteNonQueryAsync(); } catch { }
    try { await new MySqlCommand("ALTER TABLE cierres_turno ADD COLUMN reabierto_por VARCHAR(100) NULL AFTER ultima_reapertura;", con).ExecuteNonQueryAsync(); } catch { }
    try { await new MySqlCommand("ALTER TABLE cierres_turno ADD COLUMN motivo_reapertura VARCHAR(250) NULL AFTER reabierto_por;", con).ExecuteNonQueryAsync(); } catch { }

    // V86: migración sin borrar historial. Si ya había cierres duplicados, solo el primer
    // registro de cada identidad natural recibe cierre_key; los duplicados históricos quedan
    // intactos pero ya no se vuelven a crear y los reportes los filtran.
    try { await new MySqlCommand("ALTER TABLE cierres_turno ADD COLUMN cierre_key VARCHAR(190) NULL AFTER detalle_json;", con).ExecuteNonQueryAsync(); } catch { }
    try
    {
        await using var backfill = new MySqlCommand("""
            UPDATE cierres_turno c
            INNER JOIN (
                SELECT MIN(id) AS keep_id
                FROM cierres_turno
                GROUP BY sucursal_id, UPPER(TRIM(cajero_usuario)), UPPER(TRIM(COALESCE(caja,''))), UPPER(TRIM(turno)), inicio
            ) k ON k.keep_id=c.id
            SET c.cierre_key = CONCAT('NAT-', LOWER(SHA2(CONCAT_WS('|',
                c.sucursal_id, UPPER(TRIM(c.cajero_usuario)), UPPER(TRIM(COALESCE(c.caja,''))),
                UPPER(TRIM(c.turno)), DATE_FORMAT(c.inicio,'%Y%m%d%H%i%s')),256)))
            WHERE c.cierre_key IS NULL OR TRIM(c.cierre_key)='';
        """, con);
        await backfill.ExecuteNonQueryAsync();
    }
    catch { }
    try { await new MySqlCommand("CREATE UNIQUE INDEX uk_cierre_turno_natural ON cierres_turno(cierre_key);", con).ExecuteNonQueryAsync(); } catch { }
}

static async Task EnsureOfficialBranchAndTableLayout(MySqlConnection con)
{
    // V49: configuración oficial. No elimina registros históricos ni modifica ventas/inventario.
    try
    {
        await using var rename = new MySqlCommand("""
            UPDATE sucursales SET nombre='EL BRUJO' WHERE id=1;
            UPDATE sucursales SET nombre='EL BRUJO PREMIU' WHERE id=2;
        """, con);
        await rename.ExecuteNonQueryAsync();
    }
    catch { }

    decimal rate = await EnsureGlobalTableRateAsync(con);
    try { await new MySqlCommand("ALTER TABLE mesas ADD COLUMN tipo_mesa VARCHAR(30) NOT NULL DEFAULT 'NORMAL';", con).ExecuteNonQueryAsync(); } catch { }

    foreach (var config in new[] { (SucursalId: 1, Operational: 8), (SucursalId: 2, Operational: 29) })
    {
        for (int i = 1; i <= 29; i++)
        {
            await using var seed = new MySqlCommand("""
                INSERT INTO mesas (sucursal_id, nombre, precio_hora, estado)
                SELECT @sucursal_id, @nombre, @precio, @estado
                WHERE NOT EXISTS (
                    SELECT 1 FROM mesas WHERE sucursal_id=@sucursal_id AND LOWER(TRIM(nombre))=LOWER(TRIM(@nombre))
                );
            """, con);
            seed.Parameters.AddWithValue("@sucursal_id", config.SucursalId);
            seed.Parameters.AddWithValue("@nombre", "Mesa " + i);
            seed.Parameters.AddWithValue("@precio", rate);
            seed.Parameters.AddWithValue("@estado", i <= config.Operational ? "LIBRE" : "INACTIVA");
            try { await seed.ExecuteNonQueryAsync(); } catch { }
        }
    }

    // Mesas operativas oficiales quedan activas si una configuración vieja las dejó inactivas.
    for (int i = 1; i <= 8; i++)
    {
        await using var c = new MySqlCommand("UPDATE mesas SET estado=CASE WHEN UPPER(COALESCE(estado,''))='INACTIVA' THEN 'LIBRE' ELSE estado END WHERE sucursal_id=1 AND nombre=@nombre;", con);
        c.Parameters.AddWithValue("@nombre", "Mesa " + i);
        try { await c.ExecuteNonQueryAsync(); } catch { }
    }
    for (int i = 1; i <= 29; i++)
    {
        await using var c = new MySqlCommand("UPDATE mesas SET estado=CASE WHEN UPPER(COALESCE(estado,''))='INACTIVA' THEN 'LIBRE' ELSE estado END WHERE sucursal_id=2 AND nombre=@nombre;", con);
        c.Parameters.AddWithValue("@nombre", "Mesa " + i);
        try { await c.ExecuteNonQueryAsync(); } catch { }
    }

    // EL BRUJO: extras 9..29 se mantienen en la base pero fuera de servicio cuando están libres.
    for (int i = 9; i <= 29; i++)
    {
        await using var off = new MySqlCommand("""
            UPDATE mesas m
            LEFT JOIN mesa_estados me ON me.sucursal_id=m.sucursal_id AND me.mesa_id=m.id
            SET m.estado='INACTIVA'
            WHERE m.sucursal_id=1 AND m.nombre=@nombre
              AND (me.id IS NULL OR UPPER(COALESCE(me.estado,'LIBRE')) IN ('LIBRE','INACTIVA'));
        """, con);
        off.Parameters.AddWithValue("@nombre", "Mesa " + i);
        try { await off.ExecuteNonQueryAsync(); } catch { }
    }

    try
    {
        await using var types = new MySqlCommand("""
            UPDATE mesas SET tipo_mesa='NORMAL' WHERE sucursal_id=1 AND nombre IN ('Mesa 1','Mesa 2','Mesa 3','Mesa 4','Mesa 5','Mesa 6','Mesa 7');
            UPDATE mesas SET tipo_mesa='PRIVADA' WHERE sucursal_id=1 AND nombre='Mesa 8';
        """, con);
        await types.ExecuteNonQueryAsync();
    }
    catch { }

    // Reaplica precios base después de fijar los tipos oficiales.
    await EnsureTablePricingAsync(con);
}

static async Task EnsureMesasEnVivoTables(MySqlConnection con)
{
    await using (var cmd = new MySqlCommand("""
        CREATE TABLE IF NOT EXISTS mesa_estados (
            id BIGINT AUTO_INCREMENT PRIMARY KEY,
            sucursal_id INT NOT NULL,
            sector VARCHAR(20) NOT NULL DEFAULT 'GENERAL',
            mesa_id INT NOT NULL,
            mesa VARCHAR(100) NOT NULL,
            estado VARCHAR(50) NOT NULL,
            cajero VARCHAR(100) NULL,
            inicio DATETIME NULL,
            fin_programado DATETIME NULL,
            minutos INT NOT NULL DEFAULT 0,
            tarifa_hora DECIMAL(10,2) NOT NULL DEFAULT 20.00,
            total_mesa DECIMAL(10,2) NOT NULL DEFAULT 0,
            total_consumo DECIMAL(10,2) NOT NULL DEFAULT 0,
            total_general DECIMAL(10,2) NOT NULL DEFAULT 0,
            cliente_reserva VARCHAR(150) NULL,
            actualizado DATETIME NOT NULL,
            sync_key VARCHAR(180) NOT NULL,
            UNIQUE KEY uk_mesa_estado_sector (sucursal_id, sector, mesa_id),
            UNIQUE KEY uk_mesa_estado_sync_key (sync_key)
        );
    """, con)) await cmd.ExecuteNonQueryAsync();

    try { await new MySqlCommand("ALTER TABLE mesa_estados ADD COLUMN sector VARCHAR(20) NOT NULL DEFAULT 'GENERAL' AFTER sucursal_id;", con).ExecuteNonQueryAsync(); } catch { }
    try { await new MySqlCommand("ALTER TABLE mesa_estados ADD COLUMN tarifa_hora DECIMAL(10,2) NOT NULL DEFAULT 20.00 AFTER minutos;", con).ExecuteNonQueryAsync(); } catch { }
    // V158/V74: se elimina la identidad antigua sucursal+mesa, porque Mesa 1 ARRIBA y Mesa 1 ABAJO son físicas distintas.
    try { await new MySqlCommand("ALTER TABLE mesa_estados DROP INDEX uk_mesa_estado;", con).ExecuteNonQueryAsync(); } catch { }
    try { await new MySqlCommand("ALTER TABLE mesa_estados DROP INDEX uk_mesa_estado_sucursal_mesa;", con).ExecuteNonQueryAsync(); } catch { }
    try { await new MySqlCommand("ALTER TABLE mesa_estados ADD UNIQUE KEY uk_mesa_estado_sector (sucursal_id, sector, mesa_id);", con).ExecuteNonQueryAsync(); } catch { }
    try { await new MySqlCommand("ALTER TABLE mesa_estados ADD UNIQUE KEY uk_mesa_estado_sync_key (sync_key);", con).ExecuteNonQueryAsync(); } catch { }

    await using (var cmd = new MySqlCommand("""
        CREATE TABLE IF NOT EXISTS mesa_consumos_vivos (
            id BIGINT AUTO_INCREMENT PRIMARY KEY,
            sucursal_id INT NOT NULL,
            mesa_id INT NOT NULL,
            sector VARCHAR(20) NOT NULL DEFAULT 'GENERAL',
            producto VARCHAR(180) NOT NULL,
            presentacion VARCHAR(120) NULL,
            cantidad DECIMAL(10,2) NOT NULL DEFAULT 0,
            precio_unitario DECIMAL(10,2) NOT NULL DEFAULT 0,
            subtotal DECIMAL(10,2) NOT NULL DEFAULT 0,
            mobile_order_id BIGINT NOT NULL DEFAULT 0,
            stock_already_discounted_online TINYINT(1) NOT NULL DEFAULT 0,
            consumption_key VARCHAR(180) NULL,
            actualizado DATETIME NOT NULL,
            INDEX idx_mesa_consumos_vivos (sucursal_id, sector, mesa_id)
        );
    """, con)) await cmd.ExecuteNonQueryAsync();

    try { await new MySqlCommand("ALTER TABLE mesa_consumos_vivos ADD COLUMN mobile_order_id BIGINT NOT NULL DEFAULT 0;", con).ExecuteNonQueryAsync(); } catch { }
    try { await new MySqlCommand("ALTER TABLE mesa_consumos_vivos ADD COLUMN sector VARCHAR(20) NOT NULL DEFAULT 'GENERAL';", con).ExecuteNonQueryAsync(); } catch { }
    try { await new MySqlCommand("ALTER TABLE mesa_consumos_vivos ADD COLUMN stock_already_discounted_online TINYINT(1) NOT NULL DEFAULT 0;", con).ExecuteNonQueryAsync(); } catch { }
    try { await new MySqlCommand("ALTER TABLE mesa_consumos_vivos ADD COLUMN consumption_key VARCHAR(180) NULL;", con).ExecuteNonQueryAsync(); } catch { }
    try { await new MySqlCommand("ALTER TABLE mesa_consumos_vivos ADD INDEX idx_mesa_consumos_sector (sucursal_id, sector, mesa_id);", con).ExecuteNonQueryAsync(); } catch { }
    try { await new MySqlCommand("ALTER TABLE mesa_consumos_vivos ADD INDEX idx_mesa_consumption_key (consumption_key);", con).ExecuteNonQueryAsync(); } catch { }

    // Los estados antiguos de PREMIU no indicaban si eran ARRIBA o ABAJO y ya causaron cruces de reloj.
    // Son datos transitorios, no ventas: se limpian una sola vez de forma segura por quedar en GENERAL.
    try { await new MySqlCommand("DELETE FROM mesa_consumos_vivos WHERE sucursal_id=2 AND UPPER(COALESCE(sector,'GENERAL'))='GENERAL';", con).ExecuteNonQueryAsync(); } catch { }
    try { await new MySqlCommand("DELETE FROM mesa_estados WHERE sucursal_id=2 AND UPPER(COALESCE(sector,'GENERAL'))='GENERAL';", con).ExecuteNonQueryAsync(); } catch { }
    try { await new MySqlCommand("UPDATE mesa_estados SET sector='GENERAL' WHERE sucursal_id=1;", con).ExecuteNonQueryAsync(); } catch { }
    try { await new MySqlCommand("UPDATE mesa_consumos_vivos SET sector='GENERAL' WHERE sucursal_id=1;", con).ExecuteNonQueryAsync(); } catch { }
}

static class PasswordHasher
{
    const int Iterations = 100000;
    const int SaltSize = 16;
    const int KeySize = 32;
    const string Prefix = "PBKDF2$";

    public static bool IsHashed(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) && value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static string Hash(string password)
    {
        password ??= "";
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, Iterations, HashAlgorithmName.SHA256);
        byte[] key = pbkdf2.GetBytes(KeySize);
        return Prefix + Iterations + "$" + Convert.ToBase64String(salt) + "$" + Convert.ToBase64String(key);
    }

    public static bool Verify(string password, string stored)
    {
        password ??= "";
        stored ??= "";

        if (!IsHashed(stored))
            return stored == password;

        string[] parts = stored.Split('$');
        if (parts.Length != 4) return false;
        if (!int.TryParse(parts[1], out int iterations)) return false;

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch
        {
            return false;
        }

        using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, iterations, HashAlgorithmName.SHA256);
        byte[] actual = pbkdf2.GetBytes(expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}


public sealed class Db
{
    private readonly string _connectionString;

    public Db(IConfiguration configuration)
    {
        _connectionString = BuildConnectionString(configuration);
    }

    public async Task<MySqlConnection> OpenAsync()
    {
        var con = new MySqlConnection(_connectionString);
        await con.OpenAsync();
        return con;
    }

    public async Task<List<Dictionary<string, object?>>> QueryAsync(
        MySqlConnection con,
        string sql,
        Dictionary<string, object?>? parameters = null)
    {
        await using var cmd = new MySqlCommand(sql, con);

        if (parameters != null)
        {
            foreach (var p in parameters)
            {
                cmd.Parameters.AddWithValue(p.Key, p.Value ?? DBNull.Value);
            }
        }

        var rows = new List<Dictionary<string, object?>>();
        await using var rd = await cmd.ExecuteReaderAsync();

        while (await rd.ReadAsync())
        {
            var item = new Dictionary<string, object?>();
            for (int i = 0; i < rd.FieldCount; i++)
            {
                item[rd.GetName(i)] = rd.IsDBNull(i) ? null : rd.GetValue(i);
            }
            rows.Add(item);
        }

        return rows;
    }

    private static string BuildConnectionString(IConfiguration configuration)
    {
        string? fullUrl = Environment.GetEnvironmentVariable("MYSQL_URL");

        if (!string.IsNullOrWhiteSpace(fullUrl))
        {
            var uri = new Uri(fullUrl);
            string[] mysqlUserInfo = uri.UserInfo.Split(':', 2);
            string mysqlUserFromUrl = Uri.UnescapeDataString(mysqlUserInfo[0]);
            string mysqlPasswordFromUrl = mysqlUserInfo.Length > 1 ? Uri.UnescapeDataString(mysqlUserInfo[1]) : "";
            string mysqlDatabaseFromUrl = uri.AbsolutePath.TrimStart('/');

            return $"Server={uri.Host};Port={uri.Port};Database={mysqlDatabaseFromUrl};Uid={mysqlUserFromUrl};Pwd={mysqlPasswordFromUrl};SslMode=Preferred;";
        }

        string mysqlHost = Environment.GetEnvironmentVariable("MYSQLHOST")
            ?? configuration["MYSQLHOST"]
            ?? "localhost";

        string mysqlPort = Environment.GetEnvironmentVariable("MYSQLPORT")
            ?? configuration["MYSQLPORT"]
            ?? "3306";

        string mysqlDatabaseName = Environment.GetEnvironmentVariable("MYSQLDATABASE")
            ?? Environment.GetEnvironmentVariable("MYSQL_DATABASE")
            ?? configuration["MYSQLDATABASE"]
            ?? configuration["MYSQL_DATABASE"]
            ?? "railway";

        string mysqlUserName = Environment.GetEnvironmentVariable("MYSQLUSER")
            ?? configuration["MYSQLUSER"]
            ?? "root";

        string mysqlPasswordValue = Environment.GetEnvironmentVariable("MYSQLPASSWORD")
            ?? configuration["MYSQLPASSWORD"]
            ?? "";

        return $"Server={mysqlHost};Port={mysqlPort};Database={mysqlDatabaseName};Uid={mysqlUserName};Pwd={mysqlPasswordValue};SslMode=Preferred;";
    }
}


public sealed class SheetsReporter
{
    private readonly string _sheetId;
    private readonly string _credentialsJson;
    private readonly string _serviceAccountEmail;
    private readonly string _configurationError;

    public SheetsReporter()
    {
        string rawSheetId = Environment.GetEnvironmentVariable("GOOGLE_SHEET_ID") ?? "";
        _sheetId = NormalizeSpreadsheetId(rawSheetId);

        string rawCredentials = Environment.GetEnvironmentVariable("GOOGLE_CREDENTIALS_JSON") ?? "";
        string credentialsBase64 = Environment.GetEnvironmentVariable("GOOGLE_CREDENTIALS_JSON_BASE64") ?? "";
        _credentialsJson = NormalizeCredentials(rawCredentials, credentialsBase64);

        (_serviceAccountEmail, _configurationError) = ReadCredentialMetadata(_credentialsJson);
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_sheetId) &&
        !string.IsNullOrWhiteSpace(_credentialsJson) &&
        string.IsNullOrWhiteSpace(_configurationError);

    public string SpreadsheetId => string.IsNullOrWhiteSpace(_sheetId) ? "(sin configurar)" : _sheetId;
    public string ServiceAccountEmail => string.IsNullOrWhiteSpace(_serviceAccountEmail) ? "(no detectado)" : _serviceAccountEmail;

    public async Task<SheetsConnectionTest> TestConnectionAsync()
    {
        if (string.IsNullOrWhiteSpace(_sheetId))
        {
            return new SheetsConnectionTest(false, false, "(sin configurar)", "", ServiceAccountEmail,
                "Falta GOOGLE_SHEET_ID en Railway. Puedes pegar solo el ID o la URL completa de Google Sheets.");
        }

        if (string.IsNullOrWhiteSpace(_credentialsJson))
        {
            return new SheetsConnectionTest(false, false, _sheetId, "", ServiceAccountEmail,
                "Falta GOOGLE_CREDENTIALS_JSON en Railway. También se admite GOOGLE_CREDENTIALS_JSON_BASE64.");
        }

        if (!string.IsNullOrWhiteSpace(_configurationError))
        {
            return new SheetsConnectionTest(false, false, _sheetId, "", ServiceAccountEmail,
                "Las credenciales de Google no son JSON válidas: " + _configurationError);
        }

        try
        {
            var service = CreateService();
            var request = service.Spreadsheets.Get(_sheetId);
            request.Fields = "spreadsheetId,properties.title";
            var spreadsheet = await request.ExecuteAsync();
            string title = spreadsheet.Properties?.Title ?? "";

            return new SheetsConnectionTest(true, true, _sheetId, title, ServiceAccountEmail,
                "Conexión REAL correcta. La cuenta de servicio tiene acceso al archivo y la API puede leer/escribir Google Sheets.");
        }
        catch (Exception ex)
        {
            string raw = ex.Message ?? ex.GetType().Name;
            string friendly;
            if (raw.Contains("403") || raw.Contains("permission", StringComparison.OrdinalIgnoreCase) || raw.Contains("forbidden", StringComparison.OrdinalIgnoreCase))
                friendly = "Google rechazó el permiso. Comparte el archivo de Google Sheets con " + ServiceAccountEmail + " como EDITOR.";
            else if (raw.Contains("404") || raw.Contains("not found", StringComparison.OrdinalIgnoreCase))
                friendly = "Google no encontró el archivo. Revisa GOOGLE_SHEET_ID y confirma que el Sheet esté compartido con " + ServiceAccountEmail + ".";
            else if (raw.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase) || raw.Contains("credential", StringComparison.OrdinalIgnoreCase) || raw.Contains("private key", StringComparison.OrdinalIgnoreCase))
                friendly = "La credencial de la cuenta de servicio no es válida. Revisa GOOGLE_CREDENTIALS_JSON en Railway.";
            else
                friendly = "No se pudo conectar realmente con Google Sheets: " + raw;

            return new SheetsConnectionTest(false, true, _sheetId, "", ServiceAccountEmail, friendly);
        }
    }

    private static string NormalizeSpreadsheetId(string raw)
    {
        raw = (raw ?? "").Trim().Trim('"', '\'');
        if (string.IsNullOrWhiteSpace(raw)) return "";

        const string marker = "/spreadsheets/d/";
        int p = raw.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (p >= 0)
        {
            string rest = raw[(p + marker.Length)..];
            int end = rest.IndexOfAny(new[] { '/', '?', '#', '&' });
            return (end >= 0 ? rest[..end] : rest).Trim();
        }

        int cut = raw.IndexOfAny(new[] { '?', '#', '&' });
        if (cut >= 0) raw = raw[..cut];
        return raw.Trim().Trim('/');
    }

    private static string NormalizeCredentials(string rawJson, string rawBase64)
    {
        string value = (rawJson ?? "").Trim();
        if (string.IsNullOrWhiteSpace(value) && !string.IsNullOrWhiteSpace(rawBase64))
        {
            try
            {
                value = Encoding.UTF8.GetString(Convert.FromBase64String(rawBase64.Trim()));
            }
            catch
            {
                return rawBase64.Trim(); // TestConnectionAsync devolverá un diagnóstico claro.
            }
        }

        // Algunas interfaces guardan el JSON completo entre comillas como una cadena JSON.
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            try
            {
                string? decoded = JsonSerializer.Deserialize<string>(value);
                if (!string.IsNullOrWhiteSpace(decoded)) value = decoded;
            }
            catch { }
        }

        return value.Trim();
    }

    private static (string Email, string Error) ReadCredentialMetadata(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return ("", "");
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string email = root.TryGetProperty("client_email", out var e) ? (e.GetString() ?? "") : "";
            string type = root.TryGetProperty("type", out var t) ? (t.GetString() ?? "") : "";
            if (!string.Equals(type, "service_account", StringComparison.OrdinalIgnoreCase))
                return (email, "el campo type debe ser service_account");
            if (string.IsNullOrWhiteSpace(email))
                return ("", "falta client_email");
            if (!root.TryGetProperty("private_key", out var pk) || string.IsNullOrWhiteSpace(pk.GetString()))
                return (email, "falta private_key");
            return (email, "");
        }
        catch (Exception ex)
        {
            return ("", ex.Message);
        }
    }

    public async Task<string> SyncFromDatabaseAsync(Db db)
    {
        if (!IsConfigured)
            return "Google Sheets no configurado.";

        SheetsService service = CreateService();

        // V53: reportes totalmente separados por sucursal.
        // No existe una hoja financiera consolidada: EL BRUJO y EL BRUJO PREMIU
        // se publican en pestañas distintas para que sus ventas, cierres, inventario
        // y comisiones nunca se mezclen en Google Sheets.
        // Las hojas *_RESUMEN se conservan con fórmulas y NO se sobrescriben.
        await EnsureSheetsAsync(service, new[]
        {
            "EL_BRUJO_CIERRES",
            "EL_BRUJO_VENTAS",
            "EL_BRUJO_DETALLE_VENTAS",
            "EL_BRUJO_PRODUCTOS",
            "EL_BRUJO_INVENTARIO",
            "EL_BRUJO_MESERAS",
            "EL_BRUJO_PREMIU_CIERRES",
            "EL_BRUJO_PREMIU_VENTAS",
            "EL_BRUJO_PREMIU_DETALLE_VENTAS",
            "EL_BRUJO_PREMIU_PRODUCTOS",
            "EL_BRUJO_PREMIU_INVENTARIO",
            "EL_BRUJO_PREMIU_MESERAS"
        });

        await using var con = await db.OpenAsync();
        // V61: el esquema de protección de ventas se garantiza al arrancar la API
        // y en los endpoints contables. SheetsReporter es una clase y no puede invocar
        // directamente una función local declarada en el top-level de Program.cs (CS8801).
        // Aquí solo leemos las vistas/tablas ya inicializadas, evitando duplicar migraciones.

        var syncSummary = new List<string>();

        foreach (int sucursalId in new[] { 1, 2 })
        {
            string prefix = sucursalId == 2 ? "EL_BRUJO_PREMIU" : "EL_BRUJO";
            var args = new Dictionary<string, object?> { ["@sucursal_id"] = sucursalId };

            // Una venta = una fila. La identidad fuerte de ventas (sync_key/operation_key)
            // ya impide que un reintento vuelva a crear el mismo cobro.
            List<List<object>> ventas = new()
            {
                new() { "id_venta", "fecha_turno", "turno", "fecha", "hora", "cajero", "tipo", "metodo_pago", "efectivo", "qr", "transferencia", "total", "caja", "sector" }
            };
            ventas.AddRange((await db.QueryAsync(con, """
                SELECT v.id,
                       CASE
                           WHEN UPPER(COALESCE(NULLIF(v.turno,''), CASE WHEN TIME(v.fecha) >= '08:00:00' AND TIME(v.fecha) < '20:00:00' THEN 'MAÑANA' ELSE 'NOCHE' END)) = 'NOCHE'
                                AND TIME(v.fecha) < '20:00:00'
                               THEN DATE(DATE_SUB(v.fecha, INTERVAL 1 DAY))
                           ELSE DATE(v.fecha)
                       END AS fecha_turno,
                       COALESCE(NULLIF(v.turno,''), CASE
                           WHEN TIME(v.fecha) >= '08:00:00' AND TIME(v.fecha) < '20:00:00' THEN 'MAÑANA'
                           ELSE 'NOCHE'
                       END) AS turno,
                       DATE(v.fecha) AS fecha, TIME(v.fecha) AS hora,
                       v.cajero, v.tipo, v.metodo_pago,
                       COALESCE(v.efectivo, 0) AS efectivo,
                       COALESCE(v.qr, 0) AS qr,
                       CASE WHEN UPPER(v.metodo_pago)='TRANSFERENCIA' THEN v.total ELSE 0 END AS transferencia,
                       v.total,
                       CASE
                           WHEN v.sucursal_id = 1 THEN 'CAJA ÚNICA'
                           WHEN UPPER(COALESCE(NULLIF(v.caja_nombre,''), u.caja_nombre, '')) IN ('CAJA 1','CAJA ARRIBA') THEN 'CAJA ARRIBA'
                           WHEN UPPER(COALESCE(NULLIF(v.caja_nombre,''), u.caja_nombre, '')) IN ('CAJA 2','CAJA ABAJO') THEN 'CAJA ABAJO'
                           ELSE COALESCE(NULLIF(v.caja_nombre,''), NULLIF(u.caja_nombre,''), 'SIN CAJA')
                       END AS caja,
                       CASE
                           WHEN v.sucursal_id = 1 THEN 'GENERAL'
                           WHEN UPPER(COALESCE(NULLIF(v.caja_nombre,''), u.caja_nombre, '')) IN ('CAJA 2','CAJA ABAJO') THEN 'ABAJO'
                           ELSE COALESCE(NULLIF(u.sector,''), 'ARRIBA')
                       END AS sector
                FROM ventas_canonicas v
                LEFT JOIN usuarios u ON u.usuario = v.cajero AND u.sucursal_id = v.sucursal_id
                WHERE v.sucursal_id = @sucursal_id
                ORDER BY v.fecha DESC, v.id DESC;
            """, args)).Select(r => new List<object>
            {
                Val(r, "id"), DateOnlyText(r, "fecha_turno"), Text(r, "turno"), DateOnlyText(r, "fecha"), Text(r, "hora"),
                Text(r, "cajero"), Text(r, "tipo"), Text(r, "metodo_pago"), Val(r, "efectivo"), Val(r, "qr"), Val(r, "transferencia"), Val(r, "total"), Text(r, "caja"), Text(r, "sector")
            }));

            // V72: detalle auditable de ventas. Una fila por producto, sin inflar totales.
            // Los importes de pago y el total de venta se escriben solo en la primera línea
            // de cada venta; así SUM(efectivo/qr/transferencia/total_venta) sigue cuadrando.
            // Si la venta es solo tiempo de mesa, LEFT JOIN genera igualmente una fila.
            bool hasCobrosMesa = await TableExistsAsync(con, "cobros_mesa");
            string detalleVentasSql = hasCobrosMesa ? """
                SELECT z.id, z.fecha_turno, z.turno, z.fecha, z.hora, z.cajero, z.caja, z.sector,
                       z.tipo, z.mesa, z.mesera, z.tiempo_mesa,
                       z.producto, z.presentacion, z.cantidad, z.precio_unitario, z.subtotal_producto,
                       CASE WHEN z.linea_venta = 1 THEN z.costo_tiempo_base ELSE 0 END AS costo_tiempo,
                       CASE WHEN z.linea_venta = 1 THEN z.ajuste_redondeo_base ELSE 0 END AS ajuste_redondeo,
                       z.subtotal_producto
                         + CASE WHEN z.linea_venta = 1 THEN z.costo_tiempo_base ELSE 0 END
                         + CASE WHEN z.linea_venta = 1 THEN z.ajuste_redondeo_base ELSE 0 END AS total_linea,
                       z.metodo_pago,
                       CASE WHEN z.linea_venta = 1 THEN z.efectivo ELSE 0 END AS efectivo,
                       CASE WHEN z.linea_venta = 1 THEN z.qr ELSE 0 END AS qr,
                       CASE WHEN z.linea_venta = 1 THEN z.transferencia ELSE 0 END AS transferencia,
                       CASE WHEN z.linea_venta = 1 THEN z.total ELSE 0 END AS total_venta,
                       z.sync_key
                FROM (
                    SELECT v.id,
                           CASE
                               WHEN UPPER(COALESCE(NULLIF(v.turno,''), CASE WHEN TIME(v.fecha) >= '08:00:00' AND TIME(v.fecha) < '20:00:00' THEN 'MAÑANA' ELSE 'NOCHE' END)) = 'NOCHE'
                                    AND TIME(v.fecha) < '20:00:00'
                                   THEN DATE(DATE_SUB(v.fecha, INTERVAL 1 DAY))
                               ELSE DATE(v.fecha)
                           END AS fecha_turno,
                           COALESCE(NULLIF(v.turno,''), CASE
                               WHEN TIME(v.fecha) >= '08:00:00' AND TIME(v.fecha) < '20:00:00' THEN 'MAÑANA'
                               ELSE 'NOCHE'
                           END) AS turno,
                           DATE(v.fecha) AS fecha,
                           TIME(v.fecha) AS hora,
                           v.cajero,
                           CASE
                               WHEN v.sucursal_id = 1 THEN 'CAJA ÚNICA'
                               WHEN UPPER(COALESCE(NULLIF(v.caja_nombre,''), u.caja_nombre, '')) IN ('CAJA 1','CAJA ARRIBA') THEN 'CAJA ARRIBA'
                               WHEN UPPER(COALESCE(NULLIF(v.caja_nombre,''), u.caja_nombre, '')) IN ('CAJA 2','CAJA ABAJO') THEN 'CAJA ABAJO'
                               ELSE COALESCE(NULLIF(v.caja_nombre,''), NULLIF(u.caja_nombre,''), 'SIN CAJA')
                           END AS caja,
                           CASE
                               WHEN v.sucursal_id = 1 THEN 'GENERAL'
                               WHEN UPPER(COALESCE(NULLIF(d.sector,''), NULLIF(v.caja_nombre,''), u.caja_nombre, '')) IN ('ABAJO','CAJA 2','CAJA ABAJO') THEN 'ABAJO'
                               ELSE COALESCE(NULLIF(d.sector,''), NULLIF(u.sector,''), 'ARRIBA')
                           END AS sector,
                           v.tipo,
                           CASE
                               WHEN COALESCE(cm.mesa,'') <> '' THEN cm.mesa
                               WHEN UPPER(COALESCE(v.tipo,'')) = 'DIRECTA' THEN 'BAR / VENTA DIRECTA'
                               WHEN COALESCE(v.session_id,0) > 0 THEN CONCAT('SESIÓN ', v.session_id)
                               ELSE ''
                           END AS mesa,
                           COALESCE(cm.mesera,'') AS mesera,
                           COALESCE(cm.tiempo,'') AS tiempo_mesa,
                           COALESCE(d.producto,'') AS producto,
                           COALESCE(d.presentacion,'') AS presentacion,
                           COALESCE(d.cantidad,0) AS cantidad,
                           COALESCE(d.precio_unitario,0) AS precio_unitario,
                           COALESCE(d.subtotal,0) AS subtotal_producto,
                           CASE WHEN UPPER(COALESCE(v.tipo,''))='MESA'
                                THEN GREATEST(COALESCE(cm.total_mesa,
                                     v.total - SUM(COALESCE(d.subtotal,0)) OVER (PARTITION BY v.id)), 0)
                                ELSE 0 END AS costo_tiempo_base,
                           v.total
                             - SUM(COALESCE(d.subtotal,0)) OVER (PARTITION BY v.id)
                             - CASE WHEN UPPER(COALESCE(v.tipo,''))='MESA'
                                  THEN GREATEST(COALESCE(cm.total_mesa,
                                       v.total - SUM(COALESCE(d.subtotal,0)) OVER (PARTITION BY v.id)), 0)
                                  ELSE 0 END AS ajuste_redondeo_base,
                           v.metodo_pago,
                           COALESCE(v.efectivo,0) AS efectivo,
                           COALESCE(v.qr,0) AS qr,
                           CASE WHEN UPPER(COALESCE(v.metodo_pago,''))='TRANSFERENCIA' THEN v.total ELSE 0 END AS transferencia,
                           v.total,
                           COALESCE(v.sync_key,'') AS sync_key,
                           d.id AS detalle_id,
                           ROW_NUMBER() OVER (PARTITION BY v.id ORDER BY COALESCE(d.id,0)) AS linea_venta
                    FROM ventas_canonicas v
                    LEFT JOIN detalle_ventas_canonico d ON d.venta_id = v.id
                    LEFT JOIN usuarios u ON u.usuario = v.cajero AND u.sucursal_id = v.sucursal_id
                    LEFT JOIN cobros_mesa cm ON cm.sucursal_id = v.sucursal_id
                        AND cm.session_id = v.session_id
                        AND UPPER(TRIM(COALESCE(cm.caja_nombre,''))) = UPPER(TRIM(COALESCE(v.caja_nombre,'')))
                    WHERE v.sucursal_id = @sucursal_id
                ) z
                ORDER BY z.fecha DESC, z.hora DESC, z.id DESC, COALESCE(z.detalle_id,0);
            """ : """
                SELECT z.id, z.fecha_turno, z.turno, z.fecha, z.hora, z.cajero, z.caja, z.sector,
                       z.tipo, z.mesa, z.mesera, z.tiempo_mesa,
                       z.producto, z.presentacion, z.cantidad, z.precio_unitario, z.subtotal_producto,
                       CASE WHEN z.linea_venta = 1 THEN z.costo_tiempo_base ELSE 0 END AS costo_tiempo,
                       CASE WHEN z.linea_venta = 1 THEN z.ajuste_redondeo_base ELSE 0 END AS ajuste_redondeo,
                       z.subtotal_producto
                         + CASE WHEN z.linea_venta = 1 THEN z.costo_tiempo_base ELSE 0 END
                         + CASE WHEN z.linea_venta = 1 THEN z.ajuste_redondeo_base ELSE 0 END AS total_linea,
                       z.metodo_pago,
                       CASE WHEN z.linea_venta = 1 THEN z.efectivo ELSE 0 END AS efectivo,
                       CASE WHEN z.linea_venta = 1 THEN z.qr ELSE 0 END AS qr,
                       CASE WHEN z.linea_venta = 1 THEN z.transferencia ELSE 0 END AS transferencia,
                       CASE WHEN z.linea_venta = 1 THEN z.total ELSE 0 END AS total_venta,
                       z.sync_key
                FROM (
                    SELECT v.id,
                           CASE
                               WHEN UPPER(COALESCE(NULLIF(v.turno,''), CASE WHEN TIME(v.fecha) >= '08:00:00' AND TIME(v.fecha) < '20:00:00' THEN 'MAÑANA' ELSE 'NOCHE' END)) = 'NOCHE'
                                    AND TIME(v.fecha) < '20:00:00'
                                   THEN DATE(DATE_SUB(v.fecha, INTERVAL 1 DAY))
                               ELSE DATE(v.fecha)
                           END AS fecha_turno,
                           COALESCE(NULLIF(v.turno,''), CASE
                               WHEN TIME(v.fecha) >= '08:00:00' AND TIME(v.fecha) < '20:00:00' THEN 'MAÑANA'
                               ELSE 'NOCHE'
                           END) AS turno,
                           DATE(v.fecha) AS fecha,
                           TIME(v.fecha) AS hora,
                           v.cajero,
                           CASE
                               WHEN v.sucursal_id = 1 THEN 'CAJA ÚNICA'
                               WHEN UPPER(COALESCE(NULLIF(v.caja_nombre,''), u.caja_nombre, '')) IN ('CAJA 1','CAJA ARRIBA') THEN 'CAJA ARRIBA'
                               WHEN UPPER(COALESCE(NULLIF(v.caja_nombre,''), u.caja_nombre, '')) IN ('CAJA 2','CAJA ABAJO') THEN 'CAJA ABAJO'
                               ELSE COALESCE(NULLIF(v.caja_nombre,''), NULLIF(u.caja_nombre,''), 'SIN CAJA')
                           END AS caja,
                           CASE
                               WHEN v.sucursal_id = 1 THEN 'GENERAL'
                               WHEN UPPER(COALESCE(NULLIF(d.sector,''), NULLIF(v.caja_nombre,''), u.caja_nombre, '')) IN ('ABAJO','CAJA 2','CAJA ABAJO') THEN 'ABAJO'
                               ELSE COALESCE(NULLIF(d.sector,''), NULLIF(u.sector,''), 'ARRIBA')
                           END AS sector,
                           v.tipo,
                           CASE
                               WHEN UPPER(COALESCE(v.tipo,'')) = 'DIRECTA' THEN 'BAR / VENTA DIRECTA'
                               WHEN COALESCE(v.session_id,0) > 0 THEN CONCAT('SESIÓN ', v.session_id)
                               ELSE ''
                           END AS mesa,
                           '' AS mesera,
                           '' AS tiempo_mesa,
                           COALESCE(d.producto,'') AS producto,
                           COALESCE(d.presentacion,'') AS presentacion,
                           COALESCE(d.cantidad,0) AS cantidad,
                           COALESCE(d.precio_unitario,0) AS precio_unitario,
                           COALESCE(d.subtotal,0) AS subtotal_producto,
                           GREATEST(CASE WHEN UPPER(COALESCE(v.tipo,''))='MESA'
                               THEN v.total - SUM(COALESCE(d.subtotal,0)) OVER (PARTITION BY v.id)
                               ELSE 0 END, 0) AS costo_tiempo_base,
                           v.total
                             - SUM(COALESCE(d.subtotal,0)) OVER (PARTITION BY v.id)
                             - GREATEST(CASE WHEN UPPER(COALESCE(v.tipo,''))='MESA'
                                 THEN v.total - SUM(COALESCE(d.subtotal,0)) OVER (PARTITION BY v.id)
                                 ELSE 0 END, 0) AS ajuste_redondeo_base,
                           v.metodo_pago,
                           COALESCE(v.efectivo,0) AS efectivo,
                           COALESCE(v.qr,0) AS qr,
                           CASE WHEN UPPER(COALESCE(v.metodo_pago,''))='TRANSFERENCIA' THEN v.total ELSE 0 END AS transferencia,
                           v.total,
                           COALESCE(v.sync_key,'') AS sync_key,
                           d.id AS detalle_id,
                           ROW_NUMBER() OVER (PARTITION BY v.id ORDER BY COALESCE(d.id,0)) AS linea_venta
                    FROM ventas_canonicas v
                    LEFT JOIN detalle_ventas_canonico d ON d.venta_id = v.id
                    LEFT JOIN usuarios u ON u.usuario = v.cajero AND u.sucursal_id = v.sucursal_id
                    WHERE v.sucursal_id = @sucursal_id
                ) z
                ORDER BY z.fecha DESC, z.hora DESC, z.id DESC, COALESCE(z.detalle_id,0);
            """;

            List<List<object>> detalleVentas = new()
            {
                new()
                {
                    "id_venta", "fecha_turno", "turno", "fecha", "hora", "cajero", "caja", "sector", "tipo",
                    "mesa", "mesera", "tiempo_mesa", "producto", "presentacion", "cantidad", "precio_unitario",
                    "subtotal_producto", "costo_tiempo", "ajuste_redondeo", "total_linea", "metodo_pago",
                    "efectivo", "qr", "transferencia", "total_venta", "sync_key"
                }
            };

            detalleVentas.AddRange((await db.QueryAsync(con, detalleVentasSql, args)).Select(r => new List<object>
            {
                Val(r, "id"), DateOnlyText(r, "fecha_turno"), Text(r, "turno"), DateOnlyText(r, "fecha"), Text(r, "hora"),
                Text(r, "cajero"), Text(r, "caja"), Text(r, "sector"), Text(r, "tipo"), Text(r, "mesa"), Text(r, "mesera"),
                Text(r, "tiempo_mesa"), Text(r, "producto"), Text(r, "presentacion"), Val(r, "cantidad"), Val(r, "precio_unitario"),
                Val(r, "subtotal_producto"), Val(r, "costo_tiempo"), Val(r, "ajuste_redondeo"), Val(r, "total_linea"), Text(r, "metodo_pago"),
                Val(r, "efectivo"), Val(r, "qr"), Val(r, "transferencia"), Val(r, "total_venta"), Text(r, "sync_key")
            }));

            // V53: PRODUCTOS es un resumen diario. El mismo producto/presentación aparece
            // una sola vez por día, aunque se haya vendido en muchas mesas o tickets.
            // Esto evita que la hoja visualmente repita el catálogo y que alguien sume
            // filas duplicadas del reporte. Los importes salen de detalle_ventas de ventas
            // ya protegidas contra reintentos.
            List<List<object>> productos = new()
            {
                new() { "fecha_turno", "turno", "producto", "presentacion", "cantidad_total", "total_vendido", "operaciones", "sector" }
            };
            productos.AddRange((await db.QueryAsync(con, """
                SELECT
                       CASE
                           WHEN UPPER(COALESCE(NULLIF(v.turno,''), CASE WHEN TIME(v.fecha) >= '08:00:00' AND TIME(v.fecha) < '20:00:00' THEN 'MAÑANA' ELSE 'NOCHE' END)) = 'NOCHE'
                                AND TIME(v.fecha) < '20:00:00'
                               THEN DATE(DATE_SUB(v.fecha, INTERVAL 1 DAY))
                           ELSE DATE(v.fecha)
                       END AS fecha_turno,
                       COALESCE(NULLIF(v.turno,''), CASE
                           WHEN TIME(v.fecha) >= '08:00:00' AND TIME(v.fecha) < '20:00:00' THEN 'MAÑANA'
                           ELSE 'NOCHE'
                       END) AS turno,
                       COALESCE(NULLIF(d.sector,''), CASE WHEN v.sucursal_id=2 THEN 'ARRIBA' ELSE 'GENERAL' END) AS sector,
                       MIN(TRIM(d.producto)) AS producto,
                       MIN(TRIM(d.presentacion)) AS presentacion,
                       SUM(d.cantidad) AS cantidad_total,
                       SUM(d.subtotal) AS total_vendido,
                       COUNT(DISTINCT v.id) AS operaciones
                FROM detalle_ventas_canonico d
                INNER JOIN ventas_canonicas v ON v.id = d.venta_id
                WHERE v.sucursal_id = @sucursal_id
                GROUP BY fecha_turno, turno, sector, LOWER(TRIM(d.producto)), LOWER(TRIM(d.presentacion))
                ORDER BY fecha_turno DESC, turno, sector, producto, presentacion;
            """, args)).Select(r => new List<object>
            {
                DateOnlyText(r, "fecha_turno"), Text(r, "turno"), Text(r, "producto"), Text(r, "presentacion"),
                Val(r, "cantidad_total"), Val(r, "total_vendido"), Val(r, "operaciones"), Text(r, "sector")
            }));

            // V53: si quedaron productos repetidos de versiones antiguas, el reporte toma
            // solo el registro canónico (menor ID) por sucursal + nombre normalizado.
            // No suma registros duplicados de catálogo, evitando inflar el inventario visible.
            List<List<object>> inventario = new()
            {
                new() { "id_producto", "producto", "categoria", "cantidad_actual", "cantidad_minima", "unidad", "estado", "sector" }
            };
            inventario.AddRange((await db.QueryAsync(con, """
                SELECT p.id, p.sector, p.nombre, p.categoria,
                       GREATEST(p.stock_actual, 0) AS cantidad_actual,
                       p.stock_minimo, p.unidad_base,
                       CASE WHEN p.sin_limite_stock = 1 THEN 'SIN LIMITE'
                            WHEN GREATEST(p.stock_actual, 0) <= p.stock_minimo THEN 'BAJO'
                            ELSE 'OK' END AS estado
                FROM productos p
                INNER JOIN (
                    SELECT sucursal_id, sector, LOWER(TRIM(nombre)) AS nombre_norm, MIN(id) AS id_canonico
                    FROM productos
                    WHERE estado = 'ACTIVO'
                    GROUP BY sucursal_id, sector, LOWER(TRIM(nombre))
                ) canon ON canon.id_canonico = p.id
                WHERE p.sucursal_id = @sucursal_id
                ORDER BY p.sector, p.nombre;
            """, args)).Select(r => new List<object>
            {
                Val(r, "id"), Text(r, "nombre"), Text(r, "categoria"), Val(r, "cantidad_actual"),
                Val(r, "stock_minimo"), Text(r, "unidad_base"), Text(r, "estado"), Text(r, "sector")
            }));

            List<List<object>> meseras = new()
            {
                new()
                {
                    "fecha_turno", "turno", "usuario_mesera", "mesera", "pedidos_con_comision",
                    "cantidad_productos", "total_vendido_generador", "total_comision", "estado", "sector"
                }
            };

            if (await TableExistsAsync(con, "comisiones_meseras"))
            {
                meseras.AddRange((await db.QueryAsync(con, """
                    SELECT CASE
                               WHEN TIME(c.fecha) >= '08:00:00' AND TIME(c.fecha) < '20:00:00' THEN DATE(c.fecha)
                               WHEN TIME(c.fecha) >= '20:00:00' THEN DATE(c.fecha)
                               ELSE DATE(DATE_SUB(c.fecha, INTERVAL 1 DAY))
                           END AS fecha_turno,
                           CASE
                               WHEN TIME(c.fecha) >= '08:00:00' AND TIME(c.fecha) < '20:00:00' THEN 'MAÑANA'
                               ELSE 'NOCHE'
                           END AS turno,
                           c.mesera_usuario,
                           COALESCE(NULLIF(c.mesera_nombre, ''), NULLIF(u.nombre_completo, ''), c.mesera_usuario) AS mesera_nombre,
                           COALESCE(NULLIF(c.sector,''), CASE WHEN c.sucursal_id=2 THEN COALESCE(NULLIF(u.sector,''),'ARRIBA') ELSE 'GENERAL' END) AS sector,
                           COUNT(DISTINCT c.pedido_id) AS pedidos_con_comision,
                           SUM(c.cantidad) AS cantidad_productos,
                           SUM(c.venta_total) AS total_vendido_generador,
                           SUM(c.comision_total) AS total_comision,
                           CASE
                               WHEN SUM(CASE WHEN UPPER(COALESCE(c.estado, 'PENDIENTE_PAGO')) = 'PENDIENTE_PAGO' THEN 1 ELSE 0 END) > 0
                               THEN 'PENDIENTE_PAGO'
                               ELSE 'REGISTRADA'
                           END AS estado
                    FROM comisiones_meseras c
                    LEFT JOIN usuarios u
                           ON u.usuario = c.mesera_usuario
                          AND u.sucursal_id = c.sucursal_id
                    WHERE c.sucursal_id = @sucursal_id
                    GROUP BY fecha_turno, turno, sector, c.mesera_usuario,
                             COALESCE(NULLIF(c.mesera_nombre, ''), NULLIF(u.nombre_completo, ''), c.mesera_usuario)
                    ORDER BY fecha_turno DESC, turno, mesera_nombre;
                """, args)).Select(r => new List<object>
                {
                    DateOnlyText(r, "fecha_turno"), Text(r, "turno"), Text(r, "mesera_usuario"), Text(r, "mesera_nombre"),
                    Val(r, "pedidos_con_comision"), Val(r, "cantidad_productos"),
                    Val(r, "total_vendido_generador"), Val(r, "total_comision"), Text(r, "estado"), Text(r, "sector")
                }));
            }

            List<List<object>> cierres = new()
            {
                new()
                {
                    "fecha", "turno", "cajero", "transacciones",
                    "efectivo", "qr", "transferencia", "productos", "mesas", "propinas", "comisiones_meseras",
                    "gastos", "perdidas", "total_generado", "neto_turno", "observaciones", "caja", "sector"
                }
            };

            if (await TableExistsAsync(con, "cierres_turno"))
            {
                cierres.AddRange((await db.QueryAsync(con, """
                    SELECT DATE(c.fecha_cierre) AS fecha,
                           c.turno,
                           CASE WHEN COALESCE(c.cajero_nombre, '') <> '' THEN c.cajero_nombre ELSE c.cajero_usuario END AS cajero,
                           CASE WHEN EXISTS (SELECT 1 FROM ventas_canonicas vx WHERE vx.sucursal_id=c.sucursal_id AND vx.cajero=c.cajero_usuario AND vx.fecha>=c.inicio AND vx.fecha<c.fin)
                                THEN (SELECT COUNT(*) FROM ventas_canonicas vx WHERE vx.sucursal_id=c.sucursal_id AND vx.cajero=c.cajero_usuario AND vx.fecha>=c.inicio AND vx.fecha<c.fin)
                                ELSE c.transacciones_total END AS transacciones_total,
                           CASE WHEN EXISTS (SELECT 1 FROM ventas_canonicas vx WHERE vx.sucursal_id=c.sucursal_id AND vx.cajero=c.cajero_usuario AND vx.fecha>=c.inicio AND vx.fecha<c.fin)
                                THEN (SELECT COALESCE(SUM(vx.efectivo),0) FROM ventas_canonicas vx WHERE vx.sucursal_id=c.sucursal_id AND vx.cajero=c.cajero_usuario AND vx.fecha>=c.inicio AND vx.fecha<c.fin)
                                ELSE c.efectivo END AS efectivo,
                           CASE WHEN EXISTS (SELECT 1 FROM ventas_canonicas vx WHERE vx.sucursal_id=c.sucursal_id AND vx.cajero=c.cajero_usuario AND vx.fecha>=c.inicio AND vx.fecha<c.fin)
                                THEN (SELECT COALESCE(SUM(vx.qr),0) FROM ventas_canonicas vx WHERE vx.sucursal_id=c.sucursal_id AND vx.cajero=c.cajero_usuario AND vx.fecha>=c.inicio AND vx.fecha<c.fin)
                                ELSE c.qr END AS qr,
                           CASE WHEN EXISTS (SELECT 1 FROM ventas_canonicas vx WHERE vx.sucursal_id=c.sucursal_id AND vx.cajero=c.cajero_usuario AND vx.fecha>=c.inicio AND vx.fecha<c.fin)
                                THEN (SELECT COALESCE(SUM(CASE WHEN UPPER(vx.metodo_pago)='TRANSFERENCIA' THEN vx.total ELSE 0 END),0) FROM ventas_canonicas vx WHERE vx.sucursal_id=c.sucursal_id AND vx.cajero=c.cajero_usuario AND vx.fecha>=c.inicio AND vx.fecha<c.fin)
                                ELSE c.transferencia END AS transferencia,
                           CASE WHEN EXISTS (SELECT 1 FROM ventas_canonicas vx WHERE vx.sucursal_id=c.sucursal_id AND vx.cajero=c.cajero_usuario AND vx.fecha>=c.inicio AND vx.fecha<c.fin)
                                THEN (SELECT COALESCE(SUM(CASE
                                           WHEN UPPER(vx.tipo) IN ('DIRECTA','CONSUMO_MESA') THEN vx.total
                                           WHEN UPPER(vx.tipo)='MESA' THEN LEAST(vx.total, COALESCE(dt.detalle_total,0))
                                           ELSE 0 END),0)
                                      FROM ventas_canonicas vx
                                      LEFT JOIN (SELECT venta_id, SUM(subtotal) AS detalle_total FROM detalle_ventas_canonico GROUP BY venta_id) dt ON dt.venta_id=vx.id
                                      WHERE vx.sucursal_id=c.sucursal_id AND vx.cajero=c.cajero_usuario AND vx.fecha>=c.inicio AND vx.fecha<c.fin)
                                ELSE c.productos_total END AS productos_total,
                           CASE WHEN EXISTS (SELECT 1 FROM ventas_canonicas vx WHERE vx.sucursal_id=c.sucursal_id AND vx.cajero=c.cajero_usuario AND vx.fecha>=c.inicio AND vx.fecha<c.fin)
                                THEN (SELECT COALESCE(SUM(CASE
                                           WHEN UPPER(vx.tipo)='MESA' THEN GREATEST(vx.total - LEAST(vx.total, COALESCE(dt.detalle_total,0)),0)
                                           ELSE 0 END),0)
                                      FROM ventas_canonicas vx
                                      LEFT JOIN (SELECT venta_id, SUM(subtotal) AS detalle_total FROM detalle_ventas_canonico GROUP BY venta_id) dt ON dt.venta_id=vx.id
                                      WHERE vx.sucursal_id=c.sucursal_id AND vx.cajero=c.cajero_usuario AND vx.fecha>=c.inicio AND vx.fecha<c.fin)
                                ELSE c.mesas_total END AS mesas_total,
                           c.propinas_total, c.comisiones_total,
                           c.gastos_total, c.perdidas_total,
                           CASE WHEN EXISTS (SELECT 1 FROM ventas_canonicas vx WHERE vx.sucursal_id=c.sucursal_id AND vx.cajero=c.cajero_usuario AND vx.fecha>=c.inicio AND vx.fecha<c.fin)
                                THEN (SELECT COALESCE(SUM(vx.total),0) FROM ventas_canonicas vx WHERE vx.sucursal_id=c.sucursal_id AND vx.cajero=c.cajero_usuario AND vx.fecha>=c.inicio AND vx.fecha<c.fin)
                                ELSE c.total_generado END AS total_generado,
                           CASE WHEN EXISTS (SELECT 1 FROM ventas_canonicas vx WHERE vx.sucursal_id=c.sucursal_id AND vx.cajero=c.cajero_usuario AND vx.fecha>=c.inicio AND vx.fecha<c.fin)
                                THEN (SELECT COALESCE(SUM(vx.total),0) FROM ventas_canonicas vx WHERE vx.sucursal_id=c.sucursal_id AND vx.cajero=c.cajero_usuario AND vx.fecha>=c.inicio AND vx.fecha<c.fin) - c.gastos_total
                                ELSE c.neto_turno END AS neto_turno,
                           c.observaciones,
                           CASE
                               WHEN c.sucursal_id = 1 THEN 'CAJA ÚNICA'
                               WHEN UPPER(COALESCE(c.caja, '')) IN ('CAJA 1','CAJA ARRIBA') THEN 'CAJA ARRIBA'
                               WHEN UPPER(COALESCE(c.caja, '')) IN ('CAJA 2','CAJA ABAJO') THEN 'CAJA ABAJO'
                               ELSE COALESCE(NULLIF(c.caja,''), 'SIN CAJA')
                           END AS caja_reporte,
                           CASE
                               WHEN c.sucursal_id=1 THEN 'GENERAL'
                               WHEN UPPER(COALESCE(c.caja,'')) IN ('CAJA 2','CAJA ABAJO') THEN 'ABAJO'
                               ELSE 'ARRIBA'
                           END AS sector
                    FROM cierres_turno c
                    WHERE c.sucursal_id = @sucursal_id
                    ORDER BY c.fecha_cierre DESC, c.id DESC;
                """, args)).Select(r => new List<object>
                {
                    DateOnlyText(r, "fecha"), Text(r, "turno"), Text(r, "cajero"), Val(r, "transacciones_total"),
                    Val(r, "efectivo"), Val(r, "qr"), Val(r, "transferencia"), Val(r, "productos_total"), Val(r, "mesas_total"),
                    Val(r, "propinas_total"), Val(r, "comisiones_total"), Val(r, "gastos_total"), Val(r, "perdidas_total"),
                    Val(r, "total_generado"), Val(r, "neto_turno"), Text(r, "observaciones"), Text(r, "caja_reporte"), Text(r, "sector")
                }));
            }

            await ReplaceSheetAsync(service, prefix + "_CIERRES", cierres);
            await ReplaceSheetAsync(service, prefix + "_VENTAS", ventas);
            await ReplaceSheetAsync(service, prefix + "_DETALLE_VENTAS", detalleVentas);
            await ReplaceSheetAsync(service, prefix + "_PRODUCTOS", productos);
            await ReplaceSheetAsync(service, prefix + "_INVENTARIO", inventario);
            await ReplaceSheetAsync(service, prefix + "_MESERAS", meseras);

            syncSummary.Add(prefix + ": ventas " + Math.Max(0, ventas.Count - 1) +
                            ", detalle " + Math.Max(0, detalleVentas.Count - 1) +
                            ", cierres " + Math.Max(0, cierres.Count - 1) +
                            ", productos " + Math.Max(0, productos.Count - 1) +
                            ", inventario " + Math.Max(0, inventario.Count - 1) +
                            ", meseras " + Math.Max(0, meseras.Count - 1));
        }

        return "Google Sheets actualizado. " + string.Join(" | ", syncSummary);
    }

    private SheetsService CreateService()
    {
        GoogleCredential credential = GoogleCredential
            .FromJson(_credentialsJson)
            .CreateScoped(SheetsService.Scope.Spreadsheets);

        return new SheetsService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Billar El Brujo API"
        });
    }

    private async Task EnsureSheetsAsync(SheetsService service, IEnumerable<string> names)
    {
        var spreadsheet = await service.Spreadsheets.Get(_sheetId).ExecuteAsync();
        var existing = spreadsheet.Sheets
            .Select(s => s.Properties.Title)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var requests = new List<Google.Apis.Sheets.v4.Data.Request>();

        foreach (string name in names)
        {
            if (!existing.Contains(name))
            {
                requests.Add(new Google.Apis.Sheets.v4.Data.Request
                {
                    AddSheet = new Google.Apis.Sheets.v4.Data.AddSheetRequest
                    {
                        Properties = new Google.Apis.Sheets.v4.Data.SheetProperties
                        {
                            Title = name
                        }
                    }
                });
            }
        }

        if (requests.Count == 0) return;

        var batch = new Google.Apis.Sheets.v4.Data.BatchUpdateSpreadsheetRequest
        {
            Requests = requests
        };

        await service.Spreadsheets.BatchUpdate(batch, _sheetId).ExecuteAsync();
    }

    private async Task ReplaceSheetAsync(SheetsService service, string sheetName, List<List<object>> values)
    {
        string range = "'" + sheetName.Replace("'", "''") + "'!A1:Z5000";

        await service.Spreadsheets.Values.Clear(
            new Google.Apis.Sheets.v4.Data.ClearValuesRequest(),
            _sheetId,
            range
        ).ExecuteAsync();

        var valueRange = new Google.Apis.Sheets.v4.Data.ValueRange
        {
            Values = values.Select(r => (IList<object>)r).ToList()
        };

        var update = service.Spreadsheets.Values.Update(
            valueRange,
            _sheetId,
            "'" + sheetName.Replace("'", "''") + "'!A1"
        );
        update.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED;
        await update.ExecuteAsync();
    }

    private static async Task<bool> TableExistsAsync(MySqlConnection con, string tableName)
    {
        const string sql = """
            SELECT COUNT(*)
            FROM information_schema.tables
            WHERE table_schema = DATABASE()
              AND table_name = @table_name;
        """;

        await using var cmd = new MySqlCommand(sql, con);
        cmd.Parameters.AddWithValue("@table_name", tableName);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync() ?? 0) > 0;
    }

    private static object Val(Dictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out object? value) || value == null) return "";
        return value;
    }

    private static decimal DecimalValue(Dictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out object? value) || value == null) return 0m;
        try { return Convert.ToDecimal(value); }
        catch { return 0m; }
    }

    private static string Text(Dictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out object? value) || value == null) return "";
        return Convert.ToString(value) ?? "";
    }

    private static string DateOnlyText(Dictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out object? value) || value == null) return "";
        if (value is DateTime dt) return dt.ToString("yyyy-MM-dd");
        return Convert.ToString(value) ?? "";
    }
}


public sealed record TableLayoutRequest(
    int SucursalId,
    string? Sector,
    int CantidadMesas,
    int MesaPrivadaNumero = 0
);

public sealed record AdminInventoryQueryRequest(
    string Usuario,
    string Clave,
    int SucursalId
);

public sealed record AdminInventoryAdjustRequest(
    string Usuario,
    string Clave,
    int SucursalId,
    string Producto,
    long? ProductoId,
    string? Sector,
    decimal Cantidad,
    string? Motivo,
    string? OperationKey
);

public sealed record AdminInventoryTransferRequest(
    string Usuario,
    string Clave,
    string Producto,
    string SectorOrigen,
    string SectorDestino,
    decimal Cantidad
);

public sealed record TableRateRequest(
    decimal PrecioHora = 0m,
    decimal PrecioNormal = 0m,
    decimal PrecioPromoLunes = 10m,
    decimal PrecioPrivada = 40m,
    decimal PrecioPromoPrivada = 20m,
    bool PromoLunesActiva = true,
    int PromoLunesSucursal = 0,
    int PromoLunesSector = 0,
    bool PromoTemporalActiva = false,
    string? PromoTemporalDesde = null,
    string? PromoTemporalHasta = null,
    int PromoTemporalSucursal = 0,
    int PromoTemporalSector = 0
);

public record MesaConsumoVivoRequest(
    string? Producto,
    string? Presentacion,
    decimal Cantidad,
    decimal PrecioUnitario,
    decimal Subtotal,
    int MobileOrderId,
    bool StockAlreadyDiscountedOnline,
    string? ConsumptionKey = null,
    string? Sector = null
);

public record MesaEstadoRequest(
    int SucursalId,
    int MesaId,
    string? Sector,
    string? Mesa,
    string? Estado,
    string? Cajero,
    DateTime? Inicio,
    DateTime? FinProgramado,
    int Minutos,
    decimal TarifaHora,
    decimal TotalMesa,
    decimal TotalConsumo,
    decimal TotalGeneral,
    string? ClienteReserva,
    string? SyncKey,
    List<MesaConsumoVivoRequest>? Detalle
);

public record ProductReportRequest(
    int SucursalId,
    string Turno,
    string Usuario,
    string Nombre,
    long ProductoId,
    long PresentacionId,
    string Producto,
    string Presentacion,
    decimal Cantidad,
    decimal PrecioUnitario,
    string Motivo,
    string Observacion,
    string SyncKey,
    string? Sector = null
);

public record ProductCommissionRequest(
    int SucursalId,
    string Nombre,
    bool GeneraComision,
    string TipoComision,
    decimal ValorComision,
    string? Sector = null
);

public sealed record AdminProductPresentationRequest(
    long? PresentacionId,
    string Nombre,
    int CantidadBase,
    decimal PrecioVenta,
    string Estado
);


public sealed record AdminGlassStateRequest(
    int SucursalId,
    string? Sector,
    long ProductoId,
    int Rendimiento,
    int ServiciosRestantes
);

public sealed record AdminStockAdjustmentRequest(
    string OperationKey,
    long? ProductoId,
    int SucursalId,
    string? Sector,
    string Nombre,
    int Delta,
    string? Motivo
);

public sealed record AdminProductRequest(
    long? ProductoId,
    int SucursalId,
    string Nombre,
    string Categoria,
    string TipoEntrada,
    string UnidadBase,
    int UnidadesPorEntrada,
    decimal PrecioCompra,
    decimal StockActual,
    decimal StockMinimo,
    bool SinLimiteStock,
    bool GeneraComision,
    string TipoComision,
    decimal ValorComision,
    int RendimientoVaso,
    string Estado,
    List<AdminProductPresentationRequest>? Presentaciones,
    string? Sector = null
);


public sealed record ShiftCloseRequest(
    int SucursalId,
    string? Sucursal,
    string? CajeroUsuario,
    string? CajeroNombre,
    string? Caja,
    string? Turno,
    DateTime Inicio,
    DateTime Fin,
    DateTime HoraEntrada,
    DateTime FechaCierre,
    int TransaccionesTotal,
    int TransaccionesEfectivo,
    int TransaccionesQr,
    int TransaccionesTarjeta,
    int TransaccionesTransferencia,
    decimal Efectivo,
    decimal Qr,
    decimal Tarjeta,
    decimal Transferencia,
    decimal SinMetodo,
    decimal ProductosTotal,
    decimal MesasTotal,
    int MinutosJugados,
    decimal PropinasTotal,
    decimal CortesiasValor,
    decimal ComisionesTotal,
    decimal GastosTotal,
    decimal PerdidasTotal,
    decimal TotalGenerado,
    decimal NetoTurno,
    string? Observaciones,
    string? DetalleJson,
    string? SyncKey
);

public sealed record ReopenShiftRequest(
    long CierreId,
    int SucursalId,
    string? Caja,
    string? Turno,
    string? CajeroUsuario,
    DateTime Inicio,
    string? AdminUsuario,
    string? Motivo
);

public sealed record AdminReportQueryRequest(
    string Usuario,
    string Clave,
    int SucursalId,
    string? Sector,
    string? Turno,
    DateTime? Desde,
    DateTime? Hasta
);

public record LoginRequest(string Usuario, string Clave);

public record ProductoRequest(
    int SucursalId,
    string Nombre,
    string Categoria,
    string UnidadBase,
    decimal StockActual,
    decimal StockMinimo
);

public record VentaDetalleRequest(
    int ProductoId,
    int PresentacionId,
    string Producto,
    string Presentacion,
    decimal Cantidad,
    decimal CantidadBase,
    decimal PrecioUnitario,
    decimal Subtotal,
    bool StockAlreadyDiscountedOnline,
    string? ConsumptionKey = null,
    string? Sector = null
);

public record VentaRequest(
    int SucursalId,
    string Cajero,
    DateTime Fecha,
    string Tipo,
    string MetodoPago,
    decimal Efectivo,
    decimal Qr,
    decimal Total,
    string? SyncKey,
    string? OperationKey,
    List<VentaDetalleRequest> Detalle,
    int? SessionId = null,
    int? ClientVersion = null,
    string? CajaNombre = null,
    string? Turno = null
);

public record CobroMesaRequest(
    int SucursalId,
    int? SessionId,
    int? MesaId,
    string? Mesa,
    string? CajaNombre,
    string? Sector,
    string? Cajero,
    string? Mesera,
    DateTime Fecha,
    string? Tiempo,
    decimal TotalMesa,
    decimal TotalConsumo,
    decimal TotalCobrado,
    string? MetodoPago,
    string? SyncKey
);

public record ReservaRequest(
    int SucursalId,
    int MesaId,
    string Cliente,
    string? Celular,
    DateTime FechaReserva,
    int Minutos,
    string Estado,
    string? Cajero,
    string? SyncKey
);

public record PropinaRequest(
    int SucursalId,
    int? MesaId,
    string Mesera,
    string Cajero,
    DateTime Fecha,
    decimal Monto,
    string? SyncKey
);


public sealed record AppPedidoMovilRequest(
    int SucursalId,
    int MesaId,
    string Mesa,
    string MeseraUsuario,
    string MeseraNombre,
    long ProductoId,
    long PresentacionId,
    string Producto,
    string Presentacion,
    decimal Cantidad,
    decimal PrecioUnitario,
    bool GeneraComision,
    string TipoComision,
    decimal ValorComision,
    string? Observacion,
    string? SyncKey
);

public sealed record PedidoEstadoRequest(
    string Estado,
    string? CajeroUsuario
);


public record AdminUserRequest(
    string Usuario,
    string Clave,
    string NombreCompleto,
    string Rol,
    int SucursalId,
    string CajaNombre,
    string Turno,
    string Estado,
    string? Sector = null
);

public record UserEstadoRequest(string Estado);

// V66: inventario real para combos/promociones.
// Los combos no usan un stock ficticio propio: descuentan los productos físicos disponibles
// en la misma sucursal y sector. En PREMIU el catálogo es común, pero ARRIBA/ABAJO tienen existencias separadas.
public static class CompositeInventoryDb
{
    public sealed class StockProduct
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public string Category { get; set; } = "";
        public decimal Stock { get; set; }
        public bool Unlimited { get; set; }
        public int GlassYield { get; set; } = 10;
        public string RecipeDetail { get; set; } = "";
    }

    public sealed class StockPart
    {
        public long ProductId { get; set; }
        public string ProductName { get; set; } = "";
        public decimal Units { get; set; }
    }

    static string N(string? value)
        => (value ?? "").Trim().ToUpperInvariant()
            .Replace("Á", "A").Replace("É", "E").Replace("Í", "I").Replace("Ó", "O").Replace("Ú", "U").Replace("Ñ", "N");

    public static bool IsServedGlassName(string? productName, string? category = null)
    {
        string n = N(productName);
        string c = N(category);
        if (c.Contains("SERVIDOS EN VASO") || c.Contains("SERVIDO EN VASO")) return true;
        if (n.StartsWith("PROMO VASO") && n.Contains("FERNET")) return true;
        return n == "VASO FERNET" || n.StartsWith("VASO DE FERNET") ||
               n == "VASO FLOR DE CANA" || n == "VASO RUM/RON ABUELO" || n == "VASO RON ABUELO" || n == "VASO DE RON" ||
               n == "VASO TEQUILA" || n == "VASO VINO" || n == "VASO VINO TINTO" ||
               n == "VASO WHISKY" || n == "VASO WISKIE" || n == "VASO DE WISKIE" || n == "VASO DE WHISKY" ||
               n == "VASO CHUFLAY";
    }

    public static bool IsComposite(string? productName, string? category = null)
    {
        string n = N(productName);
        string c = N(category);
        if (IsServedGlassName(productName, category)) return true;
        // V79: cualquier producto categorizado/nombre COMBO o PROMO es compuesto.
        return n.StartsWith("COMBO ") || n.StartsWith("PROMO ") ||
               c == "COMBO" || c == "PROMO" || c == "PROMOCION" || c.Contains("COMBOS / PROMOS");
    }

    static decimal Qty(StockProduct? p) => p == null ? 0M : (p.Unlimited ? 1000000000M : Math.Max(0M, p.Stock));

    static StockProduct? FindOne(List<StockProduct> all, params string[] aliases)
    {
        foreach (string alias in aliases)
        {
            string a = N(alias);
            StockProduct? exact = all.FirstOrDefault(x => N(x.Name) == a);
            if (exact != null) return exact;
        }
        foreach (string alias in aliases)
        {
            string a = N(alias);
            StockProduct? contains = all.FirstOrDefault(x => N(x.Name).Contains(a));
            if (contains != null) return contains;
        }
        return null;
    }

    static List<StockProduct> Sodas(List<StockProduct> all)
        => all.Where(x =>
                !N(x.Name).Contains("PEQUE") &&
                (N(x.Category).Contains("SODA") || N(x.Name).StartsWith("SODA ")) &&
                (N(x.Name).Contains("COCA") || N(x.Name).Contains("FANTA") || N(x.Name).Contains("SPRITE")))
            .OrderByDescending(Qty).ThenBy(x => x.Name).ToList();

    sealed class RecipeNeed
    {
        public decimal Units { get; set; } = 1M;
        public string Label { get; set; } = "";
        public bool SodaPool { get; set; }
    }

    static List<RecipeNeed> ParseRecipe(string? detail)
    {
        var result = new List<RecipeNeed>();
        string raw = (detail ?? "").Trim();
        if (string.IsNullOrWhiteSpace(raw)) return result;
        foreach (string piece in Regex.Split(raw, @"\s*(?:\+|;|\r?\n)\s*"))
        {
            string part = piece.Trim();
            if (string.IsNullOrWhiteSpace(part)) continue;
            Match m = Regex.Match(part, @"^\s*(\d+(?:[\.,]\d+)?)\s*[xX×]\s*(.+?)\s*$");
            if (!m.Success) continue;
            decimal units = decimal.TryParse(m.Groups[1].Value.Replace(',', '.'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out decimal q) ? Math.Max(1M, q) : 1M;
            string label = m.Groups[2].Value.Trim();
            if (string.IsNullOrWhiteSpace(label)) continue;
            string nl = N(label);
            bool sodaPool = nl.Contains("SODA") &&
                (nl.Contains("COCA") || nl.Contains("FANTA") || nl.Contains("SPRITE") || nl == "SODA" || nl.Contains("GASEOSA"));
            result.Add(new RecipeNeed { Units = units, Label = label, SodaPool = sodaPool });
        }
        return result;
    }

    static StockProduct? FindRecipeProduct(List<StockProduct> all, string label)
    {
        string target = N(label);
        StockProduct? exact = all.FirstOrDefault(x => N(x.Name) == target);
        if (exact != null) return exact;
        StockProduct? contains = all.FirstOrDefault(x => N(x.Name).Contains(target) || target.Contains(N(x.Name)));
        if (contains != null) return contains;
        string[] words = target.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 3 && w is not "SODA" and not "CERVEZA" and not "BOTELLA" and not "LITROS" and not "LITRO")
            .ToArray();
        if (words.Length == 0) return null;
        return all.Select(x => new { Product = x, Score = words.Count(w => N(x.Name).Contains(w)) })
            .Where(x => x.Score > 0).OrderByDescending(x => x.Score).ThenBy(x => x.Product.Name)
            .Select(x => x.Product).FirstOrDefault();
    }

    static bool TryRecipeAvailability(List<StockProduct> all, StockProduct parent, out decimal available)
    {
        available = 0M;
        List<RecipeNeed> recipe = ParseRecipe(parent.RecipeDetail);
        if (recipe.Count == 0) return false;
        decimal max = decimal.MaxValue;
        foreach (RecipeNeed need in recipe)
        {
            decimal possible;
            if (need.SodaPool)
            {
                decimal total = Sodas(all).Sum(Qty);
                possible = Math.Floor(total / Math.Max(1M, need.Units));
            }
            else
            {
                StockProduct? component = FindRecipeProduct(all.Where(x => x.Id != parent.Id && !IsComposite(x.Name, x.Category)).ToList(), need.Label);
                if (component == null) { available = 0M; return true; }
                possible = Math.Floor(Qty(component) / Math.Max(1M, need.Units));
            }
            max = Math.Min(max, possible);
        }
        available = max == decimal.MaxValue ? 0M : Math.Max(0M, max);
        return true;
    }

    static bool TryRecipeDescribe(List<StockProduct> all, StockProduct parent, out string description)
    {
        description = "";
        List<RecipeNeed> recipe = ParseRecipe(parent.RecipeDetail);
        if (recipe.Count == 0) return false;
        var parts = new List<string>();
        List<StockProduct> physical = all.Where(x => x.Id != parent.Id && !IsComposite(x.Name, x.Category)).ToList();
        foreach (RecipeNeed need in recipe)
        {
            if (need.SodaPool)
            {
                string sodas = string.Join(", ", Sodas(physical).Where(x => Qty(x) > 0).Select(x => x.Name));
                parts.Add(need.Units.ToString("0.##") + "x soda disponible: " + (string.IsNullOrWhiteSpace(sodas) ? "AGOTADA" : sodas));
            }
            else
            {
                StockProduct? component = FindRecipeProduct(physical, need.Label);
                parts.Add(need.Units.ToString("0.##") + "x " + (component?.Name ?? (need.Label + " (NO ENCONTRADO)")));
            }
        }
        description = string.Join(" + ", parts);
        return true;
    }

    static bool TryAllocateRecipe(List<StockProduct> all, StockProduct parent, decimal saleQty, out List<StockPart> parts, out string error)
    {
        parts = new List<StockPart>();
        error = "";
        List<RecipeNeed> recipe = ParseRecipe(parent.RecipeDetail);
        if (recipe.Count == 0) return false;
        List<StockProduct> physical = all.Where(x => x.Id != parent.Id && !IsComposite(x.Name, x.Category)).ToList();

        foreach (RecipeNeed need in recipe)
        {
            decimal required = Math.Max(1M, need.Units) * Math.Max(1M, saleQty);
            if (need.SodaPool)
            {
                decimal remain = required;
                foreach (StockProduct soda in Sodas(physical))
                {
                    if (remain <= 0) break;
                    decimal take = Math.Min(remain, soda.Unlimited ? remain : Qty(soda));
                    if (take > 0) { parts.Add(new StockPart { ProductId = soda.Id, ProductName = soda.Name, Units = take }); remain -= take; }
                }
                if (remain > 0) { error = "No hay soda suficiente para armar " + parent.Name + "."; return true; }
            }
            else
            {
                StockProduct? component = FindRecipeProduct(physical, need.Label);
                if (component == null) { error = "No se encontró el componente '" + need.Label + "' en el catálogo actual."; return true; }
                parts.Add(new StockPart { ProductId = component.Id, ProductName = component.Name, Units = required });
            }
        }

        parts = parts.GroupBy(x => x.ProductId)
            .Select(g => new StockPart { ProductId = g.Key, ProductName = g.First().ProductName, Units = g.Sum(x => x.Units) })
            .ToList();
        foreach (StockPart part in parts)
        {
            StockProduct? p = all.FirstOrDefault(x => x.Id == part.ProductId);
            if (p == null) { error = "Producto componente no encontrado."; return true; }
            if (!p.Unlimited && p.Stock < part.Units)
            {
                error = "Inventario insuficiente: " + p.Name + ". Disponible: " + p.Stock + ", requerido: " + part.Units + ".";
                return true;
            }
        }
        return true;
    }

    static int BeerUnits(string productName)
    {
        string n = N(productName);
        return n.Contains("X 3") || n.EndsWith(" X3") ? 3 : 5;
    }

    static string NormalizarSectorProductoInterno(int sucursalId, string? sector)
    {
        if (sucursalId != 2) return "GENERAL";
        string raw = (sector ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(raw) || raw == "GENERAL" || raw.Contains("COMPARTIDO")) return "ABAJO";
        if (raw.Contains("ABAJO") || raw.Contains("BAJO") || raw.Contains("CAJA 2")) return "ABAJO";
        return "ARRIBA";
    }

    public static async Task<List<StockProduct>> LoadSnapshotAsync(MySqlConnection con, int sucursalId, string sector, MySqlTransaction? tx = null, bool forUpdate = false)
    {
        string sql = """
            SELECT id, nombre, categoria, stock_actual, COALESCE(sin_limite_stock,0) AS sin_limite_stock,
                   COALESCE(rendimiento_vaso,10) AS rendimiento_vaso,
                   COALESCE((SELECT pr.nombre FROM presentaciones pr WHERE pr.producto_id=productos.id AND pr.estado='ACTIVO' ORDER BY pr.id LIMIT 1),'') AS recipe_detail
            FROM productos
            WHERE sucursal_id=@sucursal_id AND sector=@sector AND estado='ACTIVO'
            ORDER BY id
            """ + (forUpdate ? " FOR UPDATE;" : ";");
        await using var cmd = tx == null ? new MySqlCommand(sql, con) : new MySqlCommand(sql, con, tx);
        cmd.Parameters.AddWithValue("@sucursal_id", sucursalId);
        cmd.Parameters.AddWithValue("@sector", NormalizarSectorProductoInterno(sucursalId, sector));
        List<StockProduct> result = new();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
        {
            result.Add(new StockProduct
            {
                Id = rd.GetInt64(0),
                Name = rd.IsDBNull(1) ? "" : rd.GetString(1),
                Category = rd.IsDBNull(2) ? "" : rd.GetString(2),
                Stock = rd.IsDBNull(3) ? 0M : rd.GetDecimal(3),
                Unlimited = !rd.IsDBNull(4) && rd.GetInt32(4) == 1,
                GlassYield = rd.IsDBNull(5) ? 10 : Math.Max(1, rd.GetInt32(5)),
                RecipeDetail = rd.IsDBNull(6) ? "" : rd.GetString(6)
            });
        }
        return result;
    }

    static bool TryGlassBase(List<StockProduct> all, string servedName, out StockProduct? baseProduct)
    {
        // Solo una botella física puede ser origen de un vaso servido.
        // Evita que "VASO FERNET" termine encontrándose a sí mismo si falta FERNET.
        List<StockProduct> physical = all.Where(x => !IsComposite(x.Name, x.Category)).ToList();
        string n = N(servedName);
        baseProduct = null;
        if (n.Contains("FERNET")) baseProduct = FindOne(physical, "FERNET");
        else if (n.Contains("FLOR DE CANA")) baseProduct = FindOne(physical, "RON FLOR DE CAÑA", "FLOR DE CAÑA");
        else if (n.Contains("RON ABUELO") || n.Contains("RUM/RON ABUELO") || n == "VASO DE RON") baseProduct = FindOne(physical, "RON ABUELO");
        else if (n.Contains("TEQUILA")) baseProduct = FindOne(physical, "TEQUILA JOSE CUERVO", "TEQUILA");
        else if (n.Contains("WHISKY") || n.Contains("WISKIE")) baseProduct = FindOne(physical, "QUISQUE BLACK LABEL", "WHIKY BLACK LABEL", "BLACK LABEL");
        else if (n.Contains("VINO TINTO")) baseProduct = FindOne(physical, "VINO TINTO");
        else if (n.Contains("VINO")) baseProduct = FindOne(physical, "VINO BLANCO");
        else if (n.Contains("CHUFLAY")) baseProduct = FindOne(physical, "FLOW CHUFLAY", "CHUFLAY");
        return baseProduct != null;
    }

    public static decimal AvailableSales(List<StockProduct> all, string productName)
    {
        StockProduct? own = FindOne(all, productName);
        if (IsServedGlassName(productName, own?.Category))
        {
            if (!TryGlassBase(all, productName, out StockProduct? b) || b == null) return 0M;
            return b.Unlimited ? 1000000000M : Math.Floor(Qty(b) * Math.Max(1, b.GlassYield));
        }
        if (!IsComposite(productName, own?.Category))
            return Qty(own);

        if (own != null && TryRecipeAvailability(all, own, out decimal recipeAvailable)) return recipeAvailable;

        string n = N(productName);
        if (n.StartsWith("COMBO "))
        {
            if (n.Contains("GIN"))
                return Math.Floor(Math.Min(Qty(FindOne(all, "GIN ROSADO", "GIN")), Qty(FindOne(all, "SANTE GRANDE"))));

            StockProduct? baseProduct = n.Contains("FERNET") ? FindOne(all, "FERNET") :
                n.Contains("FLOR DE CANA") ? FindOne(all, "RON FLOR DE CAÑA", "FLOR DE CAÑA") :
                n.Contains("HABANA") || n.Contains("HAVANA") ? FindOne(all, "RON HABANA 7 AÑOS", "HABANA 7", "HAVANA") :
                n.Contains("RON ABUELO") ? FindOne(all, "RON ABUELO") : null;
            decimal sodaTotal = Sodas(all).Sum(Qty);
            return Math.Floor(Math.Min(Qty(baseProduct), sodaTotal));
        }

        StockProduct? beer = n.Contains("AMSTEL") ? FindOne(all, "CERVEZA AMSTEL", "AMSTEL") :
            n.Contains("CONTI") ? FindOne(all, "CERVEZA CONTI", "CONTI") :
            n.Contains("CORONA") ? FindOne(all, "CERVEZA CORONA", "CORONA") :
            n.Contains("PACENA") ? FindOne(all, "CERVEZA PACEÑA", "PACEÑA", "PACENA") : null;
        return beer == null ? 0M : Math.Floor(Qty(beer) / BeerUnits(productName));
    }

    public static async Task<decimal> AvailableSalesAsync(MySqlConnection con, int sucursalId, string sector, string productName)
    {
        List<StockProduct> all = await LoadSnapshotAsync(con, sucursalId, sector);
        if (!IsServedGlassName(productName)) return AvailableSales(all, productName);
        if (!TryGlassBase(all, productName, out StockProduct? b) || b == null) return 0M;
        if (b.Unlimited) return 1000000000M;
        int remaining = 0;
        await using (var q = new MySqlCommand("SELECT servicios_restantes FROM vaso_control WHERE sucursal_id=@s AND sector=@sector AND producto_id=@p LIMIT 1;", con))
        {
            q.Parameters.AddWithValue("@s", sucursalId);
            q.Parameters.AddWithValue("@sector", NormalizarSectorProductoInterno(sucursalId, sector));
            q.Parameters.AddWithValue("@p", b.Id);
            object? v = await q.ExecuteScalarAsync();
            if (v != null) remaining = Math.Max(0, Convert.ToInt32(v));
        }
        int yield = Math.Max(1, b.GlassYield);
        remaining = Math.Clamp(remaining, 0, Math.Max(0, yield - 1));
        return remaining + Math.Floor(Qty(b) * yield);
    }

    public static string DescribeAvailable(List<StockProduct> all, string productName)
    {
        string n = N(productName);
        StockProduct? own = FindOne(all, productName);
        if (IsServedGlassName(productName, own?.Category))
        {
            if (!TryGlassBase(all, productName, out StockProduct? b) || b == null) return "Falta configurar la botella de origen";
            return b.Name + " · " + Math.Max(1, b.GlassYield) + " vasos por botella";
        }
        if (!IsComposite(productName, own?.Category)) return productName;
        if (own != null && TryRecipeDescribe(all, own, out string recipeDescription)) return recipeDescription;
        if (n.StartsWith("COMBO "))
        {
            if (n.Contains("GIN")) return "GIN ROSADO + SANTE GRANDE";
            StockProduct? baseProduct = n.Contains("FERNET") ? FindOne(all, "FERNET") :
                n.Contains("FLOR DE CANA") ? FindOne(all, "RON FLOR DE CAÑA", "FLOR DE CAÑA") :
                n.Contains("HABANA") || n.Contains("HAVANA") ? FindOne(all, "RON HABANA 7 AÑOS", "HABANA 7", "HAVANA") :
                n.Contains("RON ABUELO") ? FindOne(all, "RON ABUELO") : null;
            string soda = string.Join(", ", Sodas(all).Where(x => Qty(x) > 0).Select(x => x.Name));
            return (baseProduct?.Name ?? "Producto base") + " + soda disponible: " + (string.IsNullOrWhiteSpace(soda) ? "AGOTADA" : soda);
        }
        StockProduct? beer = n.Contains("AMSTEL") ? FindOne(all, "CERVEZA AMSTEL", "AMSTEL") :
            n.Contains("CONTI") ? FindOne(all, "CERVEZA CONTI", "CONTI") :
            n.Contains("CORONA") ? FindOne(all, "CERVEZA CORONA", "CORONA") :
            n.Contains("PACENA") ? FindOne(all, "CERVEZA PACEÑA", "PACEÑA", "PACENA") : null;
        return beer == null ? productName : BeerUnits(productName) + " x " + beer.Name;
    }

    static bool TryAllocate(List<StockProduct> all, string productName, decimal saleQty, out List<StockPart> parts, out string error)
    {
        var allocatedParts = new List<StockPart>();
        parts = allocatedParts;
        error = "";
        saleQty = Math.Max(1M, saleQty);
        string n = N(productName);

        StockProduct? parent = FindOne(all, productName);
        if (parent != null && IsComposite(productName, parent.Category) && !IsServedGlassName(productName, parent.Category))
        {
            bool handled = TryAllocateRecipe(all, parent, saleQty, out List<StockPart> recipeParts, out error);
            if (handled)
            {
                parts = recipeParts;
                return string.IsNullOrWhiteSpace(error);
            }
        }

        void Need(StockProduct? p, decimal units, string label)
        {
            if (p == null) throw new InvalidOperationException("No está configurado en el catálogo: " + label + ".");
            allocatedParts.Add(new StockPart { ProductId = p.Id, ProductName = p.Name, Units = units });
        }

        try
        {
            if (n.StartsWith("COMBO "))
            {
                if (n.Contains("GIN"))
                {
                    Need(FindOne(all, "GIN ROSADO", "GIN"), saleQty, "GIN ROSADO");
                    Need(FindOne(all, "SANTE GRANDE"), saleQty, "SANTE GRANDE");
                }
                else
                {
                    StockProduct? baseProduct = n.Contains("FERNET") ? FindOne(all, "FERNET") :
                        n.Contains("FLOR DE CANA") ? FindOne(all, "RON FLOR DE CAÑA", "FLOR DE CAÑA") :
                        n.Contains("HABANA") || n.Contains("HAVANA") ? FindOne(all, "RON HABANA 7 AÑOS", "HABANA 7", "HAVANA") :
                        n.Contains("RON ABUELO") ? FindOne(all, "RON ABUELO") : null;
                    Need(baseProduct, saleQty, "producto base del combo");
                    decimal remain = saleQty;
                    foreach (StockProduct soda in Sodas(all))
                    {
                        if (remain <= 0) break;
                        decimal available = soda.Unlimited ? remain : Qty(soda);
                        decimal take = Math.Min(remain, available);
                        if (take > 0)
                        {
                            allocatedParts.Add(new StockPart { ProductId = soda.Id, ProductName = soda.Name, Units = take });
                            remain -= take;
                        }
                    }
                    if (remain > 0) throw new InvalidOperationException("No hay soda suficiente (Coca-Cola, Fanta o Sprite) para armar el combo.");
                }
            }
            else
            {
                StockProduct? beer = n.Contains("AMSTEL") ? FindOne(all, "CERVEZA AMSTEL", "AMSTEL") :
                    n.Contains("CONTI") ? FindOne(all, "CERVEZA CONTI", "CONTI") :
                    n.Contains("CORONA") ? FindOne(all, "CERVEZA CORONA", "CORONA") :
                    n.Contains("PACENA") ? FindOne(all, "CERVEZA PACEÑA", "PACEÑA", "PACENA") : null;
                Need(beer, BeerUnits(productName) * saleQty, "cerveza de la promoción");
            }
        }
        catch (Exception ex) { error = ex.Message; return false; }

        foreach (StockPart part in allocatedParts)
        {
            StockProduct? p = all.FirstOrDefault(x => x.Id == part.ProductId);
            if (p == null) { error = "Producto componente no encontrado."; return false; }
            if (!p.Unlimited && p.Stock < part.Units)
            {
                error = "Inventario insuficiente: " + p.Name + ". Disponible: " + p.Stock + ", requerido: " + part.Units + ".";
                return false;
            }
        }
        return true;
    }

    static async Task<(bool Ok, string Error, string Description, List<StockPart> Parts)> ReserveServedGlassAsync(
        MySqlConnection con, MySqlTransaction tx, int sucursalId, string sector, string productName, decimal saleQty)
    {
        List<StockProduct> all = await LoadSnapshotAsync(con, sucursalId, sector, tx, true);
        if (!TryGlassBase(all, productName, out StockProduct? baseProduct) || baseProduct == null)
            return (false, "No está configurada la botella de origen para " + productName + ".", "", new());
        if (baseProduct.Unlimited)
            return (true, "", saleQty + " vaso(s) de " + baseProduct.Name + " (sin límite)", new());

        string sectorNorm = NormalizarSectorProductoInterno(sucursalId, sector);
        int yield = Math.Max(1, baseProduct.GlassYield);
        int requested = Math.Max(1, (int)Math.Ceiling(saleQty));

        await using (var ensure = new MySqlCommand("""
            INSERT IGNORE INTO vaso_control (sucursal_id, sector, producto_id, rendimiento, servicios_restantes)
            VALUES (@s,@sector,@p,@r,0);
        """, con, tx))
        {
            ensure.Parameters.AddWithValue("@s", sucursalId);
            ensure.Parameters.AddWithValue("@sector", sectorNorm);
            ensure.Parameters.AddWithValue("@p", baseProduct.Id);
            ensure.Parameters.AddWithValue("@r", yield);
            await ensure.ExecuteNonQueryAsync();
        }

        int remaining;
        await using (var q = new MySqlCommand("SELECT servicios_restantes FROM vaso_control WHERE sucursal_id=@s AND sector=@sector AND producto_id=@p FOR UPDATE;", con, tx))
        {
            q.Parameters.AddWithValue("@s", sucursalId);
            q.Parameters.AddWithValue("@sector", sectorNorm);
            q.Parameters.AddWithValue("@p", baseProduct.Id);
            remaining = Convert.ToInt32(await q.ExecuteScalarAsync() ?? 0);
        }
        remaining = Math.Clamp(remaining, 0, Math.Max(0, yield - 1));

        int available = remaining + (int)Math.Floor(Math.Max(0M, baseProduct.Stock)) * yield;
        if (available < requested)
            return (false, "Inventario insuficiente: " + baseProduct.Name + ". Vasos disponibles: " + available + ".", "", new());

        int need = requested;
        int fromOpen = Math.Min(Math.Max(0, remaining), need);
        remaining -= fromOpen;
        need -= fromOpen;
        int bottlesOpened = need <= 0 ? 0 : (int)Math.Ceiling(need / (double)yield);

        if (bottlesOpened > 0)
        {
            await using var upd = new MySqlCommand("""
                UPDATE productos
                SET stock_actual = stock_actual - @bottles
                WHERE id=@id AND sucursal_id=@sucursal_id AND sector=@sector
                  AND estado='ACTIVO' AND COALESCE(sin_limite_stock,0)=0
                  AND stock_actual >= @bottles;
            """, con, tx);
            upd.Parameters.AddWithValue("@bottles", bottlesOpened);
            upd.Parameters.AddWithValue("@id", baseProduct.Id);
            upd.Parameters.AddWithValue("@sucursal_id", sucursalId);
            upd.Parameters.AddWithValue("@sector", sectorNorm);
            if (await upd.ExecuteNonQueryAsync() != 1)
                return (false, "El inventario cambió mientras se servía el vaso. Actualiza e inténtalo nuevamente.", "", new());
            remaining = bottlesOpened * yield - need;
        }

        await using (var upState = new MySqlCommand("""
            UPDATE vaso_control
            SET rendimiento=@r, servicios_restantes=@rest
            WHERE sucursal_id=@s AND sector=@sector AND producto_id=@p;
        """, con, tx))
        {
            upState.Parameters.AddWithValue("@r", yield);
            upState.Parameters.AddWithValue("@rest", Math.Max(0, remaining));
            upState.Parameters.AddWithValue("@s", sucursalId);
            upState.Parameters.AddWithValue("@sector", sectorNorm);
            upState.Parameters.AddWithValue("@p", baseProduct.Id);
            await upState.ExecuteNonQueryAsync();
        }

        List<StockPart> parts = bottlesOpened > 0
            ? new List<StockPart> { new StockPart { ProductId = baseProduct.Id, ProductName = baseProduct.Name, Units = bottlesOpened } }
            : new List<StockPart>();
        string desc = requested + " vaso(s) de " + baseProduct.Name + " · " + yield + " vasos/botella · quedan " + Math.Max(0, remaining) + " en botella abierta";
        return (true, "", desc, parts);
    }

    public static async Task<(bool Ok, string Error, string Description, List<StockPart> Parts)> ReserveAsync(
        MySqlConnection con, MySqlTransaction tx, int sucursalId, string sector, string productName, decimal saleQty)
    {
        if (IsServedGlassName(productName))
            return await ReserveServedGlassAsync(con, tx, sucursalId, sector, productName, saleQty);

        List<StockProduct> all = await LoadSnapshotAsync(con, sucursalId, sector, tx, true);
        if (!TryAllocate(all, productName, saleQty, out List<StockPart> parts, out string error))
            return (false, error, "", parts);

        foreach (StockPart part in parts)
        {
            StockProduct? p = all.FirstOrDefault(x => x.Id == part.ProductId);
            if (p == null || p.Unlimited) continue;
            await using var upd = new MySqlCommand("""
                UPDATE productos
                SET stock_actual = stock_actual - @units
                WHERE id=@id AND sucursal_id=@sucursal_id AND sector=@sector
                  AND estado='ACTIVO' AND COALESCE(sin_limite_stock,0)=0
                  AND stock_actual >= @units;
            """, con, tx);
            upd.Parameters.AddWithValue("@units", part.Units);
            upd.Parameters.AddWithValue("@id", part.ProductId);
            upd.Parameters.AddWithValue("@sucursal_id", sucursalId);
            upd.Parameters.AddWithValue("@sector", NormalizarSectorProductoInterno(sucursalId, sector));
            if (await upd.ExecuteNonQueryAsync() != 1)
                return (false, "El inventario cambió mientras se registraba la venta. Actualiza el catálogo e inténtalo nuevamente.", "", parts);
        }

        string description = string.Join(" + ", parts.Select(x => x.Units + " x " + x.ProductName));
        return (true, "", description, parts);
    }
}


public sealed record SheetsConnectionTest(
    bool Ok,
    bool Configured,
    string SpreadsheetId,
    string SpreadsheetTitle,
    string ServiceAccountEmail,
    string Message);
