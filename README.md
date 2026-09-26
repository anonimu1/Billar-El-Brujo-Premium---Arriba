# BILLAR EL BRUJO API V77

API central para Caja/Admin V159, App Mesera V19 y App Admin Android V3.

## Google Sheets
- Conexión real verificable con `/api/sheets/test`.
- Diagnóstico completo con `/api/sheets/debug`.
- Sincronización manual con `/api/sheets/sync`.
- Reportes separados para EL BRUJO y EL BRUJO PREMIU.
- Incluye `*_DETALLE_VENTAS` sin duplicar totales.
- Premium mantiene un único catálogo/inventario compartido entre caja ARRIBA y ABAJO.

## Variables Railway
- `GOOGLE_SHEET_ID`
- `GOOGLE_CREDENTIALS_JSON`
- opcional: `GOOGLE_CREDENTIALS_JSON_BASE64`

El archivo de Google Sheets debe compartirse como **Editor** con el `client_email` de la cuenta de servicio.
