# InvoiceProcessor

ASP.NET Core 8 web application for automated invoice processing, ERP catalog matching, and UiPath robot handoff.

## Stack

- **Framework:** ASP.NET Core 8 (Razor Pages + REST API)
- **Database:** SQLite via EF Core (`./data/invoice-processor.db`, auto-created on startup)
- **PDF Extraction:** PdfPig (coordinate-based text extraction)
- **Email:** MailKit (IMAP polling, currently using folder ingestor)
- **Robot Integration:** UiPath Orchestrator (optional), REST API for job queue
- **Hosting:** Runs as Windows Service (`UseWindowsService()`) or console app

## Project Structure

```
InvoiceProcessor.Web/
├── Background/              DispatcherWorker (hosted service, orchestrates processing)
├── Contracts/               CanonicalModels (invoice, line, payload, request records)
├── Controllers/
│   ├── RobotController.cs   Robot job queue API (/robot/jobs)
│   └── UiController.cs      UI + catalog API (/ui/*)
├── Data/                    AppDbContext + EF Migrations
├── Enums/                   DocumentStatus, DocumentType, PostingJobStatus
├── Models/                  Entity models (8 tables)
├── Pages/
│   ├── Config/              Supplier management, catalog import, re-match
│   ├── Inbox/               Document list, edit, preview
│   ├── Stats/               Processing statistics
│   └── Status/              Robot job tracking
├── Services/
│   ├── Email/               IMAP + folder ingestion
│   ├── Extraction/          PDF pipeline, classifiers, supplier extractors
│   ├── Matching/            Token-based fuzzy matching engine
│   ├── Robot/               Posting job service, UiPath orchestrator client
│   └── Storage/             File storage service
└── appsettings.json         Configuration
```

## Database Schema (8 tables)

| Table | Purpose |
|-------|---------|
| Documents | Ingested invoice/material PDFs with supplier, status, extracted header |
| Suppliers | Vendor master with Name, ErpName (ERP display name), VatNo, AliasesJson |
| ExtractArtifacts | Raw + canonical JSON per document |
| InvoiceLines | Line items with matched catalog item, confidence, reason |
| CatalogItems | ERP item master (ErpItemCode = CODOBIECT, unique index) |
| SupplierItemMappings | Learned vendor code -> catalog item per supplier |
| PostingJobs | Robot job queue (queued, claimed, success, failed) |
| AuditEvents | Event log per document |

## Deployment

### Publish (single-file, self-contained)

```bash
dotnet publish InvoiceProcessor.Web -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o ./publish
```

Output: `InvoiceProcessor.Web.exe` (~111MB) + `appsettings.json` + `e_sqlite3.dll` + `aspnetcorev2_inprocess.dll`. No .NET runtime needed on target.

### Install on target machine

1. Create folder (e.g. `C:\InvoiceProcessor`)
2. Copy all files from `publish/`
3. Edit `appsettings.json` — set `SourceFolder` to the PDF input path
4. Run as console: `InvoiceProcessor.Web.exe`
5. Or install as Windows Service (requires admin):
```cmd
sc create InvoiceProcessor binPath="C:\InvoiceProcessor\InvoiceProcessor.Web.exe --contentRoot C:\InvoiceProcessor" start=auto
sc start InvoiceProcessor
```
6. To uninstall: `sc stop InvoiceProcessor && sc delete InvoiceProcessor`

### Configuration (appsettings.json)

Key settings:
- `ConnectionStrings.Default` — SQLite path (default: `./data/invoice-processor.db`)
- `App.Storage.SourceFolder` — where the app picks up PDF files (e.g. `C:/Users/bogdan.fusea/Desktop/input`)
- `App.Storage.InboxRoot` / `StoreRoot` — relative paths, auto-created
- `App.Orchestrator.Enabled` — UiPath integration (default: false, robot polls directly)

**Database**: auto-created with all migrations on startup. Delete `invoice-processor.db` for a clean reset (suppliers and catalog will be lost).

### Access

Default: `http://localhost:5000`. For network access: add `"Urls": "http://0.0.0.0:5000"` to appsettings.json.

## Processing Pipeline

```
PDF file dropped in SourceFolder
  → FolderIngestor picks it up, stores in InboxRoot
  → PdfExtractionPipeline:
      1. Extract text via PdfPig
      2. Classify document (Invoice / MaterialsList / Unknown)
      3. Match supplier by VAT number, name, or aliases
         → Auto-creates supplier if new (detected from PDF)
      4. Extract invoice data (supplier-specific or generic parser)
      5. Match lines against ERP catalog (3-tier matching)
      6. Validate (line totals vs header totals)
      7. Set status: ReadyToPost or NeedsReview
  → User reviews in Inbox/Edit/Preview UI
  → Send to robot → PostingJob created (mappings learned)
  → Robot polls GET /robot/jobs/next
  → Robot completes POST /robot/jobs/{id}/complete (mappings confirmed)
```

## Matching Engine (3 tiers)

### Tier 1: Exact Vendor Code (confidence 1.0)
Looks up `SupplierItemMappings` table by vendor code for the document's supplier. Instant, deterministic.

### Tier 2: Token-Based Fuzzy Match (confidence 0.60-0.95)
- **Normalize**: lowercase, European decimals (`,` → `.`), strip noise (`COD.INTRAST.`, 8-digit customs codes, `ALUM.NATUR`)
- **Tokenize**: split both invoice description and catalog item name into token sets
- **Score**: `matched_tokens / max(invoice_tokens, catalog_tokens)`
- **Dimension bonus**: +0.10 if dimension patterns match (e.g. `7x14.2`), -0.20 penalty if dimensions exist but differ
- **Threshold**: 0.60 minimum to accept a match

Example:
```
Invoice:  "Coltar patrat (cam. 7x14,2) COD.INTRAST. 83024190"
Catalog:  "COLTAR PATRAT (CAM 7X14.2) ALUM.NATUR 1"
Tokens:   [coltar,patrat,cam,7x14.2] vs [coltar,patrat,cam,7x14.2,1]
Score:    4/5 = 0.80 + 0.10 (dimension bonus) = 0.90
```

### Tier 3: No Match (confidence 0.30)
Document set to NeedsReview for manual intervention.

### Learning (auto-save mappings)
Mappings (vendor code → catalog item per supplier) are saved when:
1. **During matching**: Tier 2 score >= 0.85 with a vendor code present
2. **User sends to robot**: all matched lines with vendor codes get saved
3. **Robot completes successfully**: mappings confirmed
4. **Manual match**: user picks catalog item via Edit page search modal

Once learned, that vendor code always hits Tier 1 (instant, confidence 1.0).

## Supplier Management

- **Auto-detection**: new suppliers auto-created from PDF data (name and/or VAT)
- **ErpName**: configured in Config page — maps detected PDF name → ERP display name
- **DisplayName**: `ErpName ?? Name` — shown everywhere in the UI
- **Aliases**: comma-separated alternate names for flexible matching
- **VatNo**: used as primary match key (most reliable)

### Cortizo Extractors (supplier-specific)
Two Cortizo entities currently handled by `CortizoInvoiceExtractor`:
- **Cortizo Slovakia** (VAT: SK2020065685) — EUR invoices, series S19/S29/S49
- **Cortizo Romania** (VAT: RO27268588) — RON invoices, series R21
- Coordinate-based extraction: supplier name at Y≈776, line items by column X-boundaries
- Invoice number pattern: `[A-Z]\d{2,3} / \d{6}`

## Catalog Management

### CSV Import (Config page)
- Auto-detects separator (tab `;` `,`)
- **Required columns**: `CODOBIECT` (unique key), `Denumire obiect` (item description)
- **Optional columns**: `UM` (unit of measure), `CotaTVA` (tax code)
- **Upsert logic**: existing items updated by CODOBIECT, new items added
- After import, click **"Re-asociaza toate documentele"** to re-run matching on all existing documents

### Re-match
The "Re-asociaza toate documentele" button in Config re-runs the matching engine on all documents in NeedsReview/Matched/Validated/ReadyToPost status using the current catalog. Use after importing or updating the catalog.

## Robot API

Base URL: `http://<host>:5000/robot/jobs`

| Method | Endpoint | Purpose |
|--------|----------|---------|
| `GET` | `/robot/jobs` | List jobs (`?status=Queued&limit=50`) |
| `GET` | `/robot/jobs/next` | Claim next queued job (returns 204 if none) |
| `GET` | `/robot/jobs/{id}` | Get specific job by ID |
| `PATCH` | `/robot/jobs/{id}` | Update job fields |
| `POST` | `/robot/jobs/{id}/complete` | Mark job as complete |

### Job Payload (returned by `/next` and `/{id}`)

```json
{
  "PostingJobId": "guid",
  "DocumentId": "guid",
  "CorrelationId": "string",
  "Invoice": {
    "Supplier": "Cortizo Slovakia, a.s.",
    "InvoiceNo": "S29/000871",
    "InvoiceDate": "2025-11-25",
    "Currency": "EUR",
    "NetTotal": 60.00,
    "GrossTotal": 60.00,
    "Lines": [{ "DescriptionRaw": "raw PDF text...", ... }]
  },
  "Lines": [
    {
      "LineNo": 1,
      "Description": "Coltar patrat (cam. 7x14,2) COD.INTRAST. 83024190",
      "Qty": 125.0,
      "Uom": "UD",
      "Amount": 60.00,
      "ErpItemCode": "6113",
      "ErpItemName": "COLTAR PATRAT CAM 7X14.2",
      "Confidence": 1.0,
      "Reason": "exact-vendor-code"
    }
  ]
}
```

**Important**: Use `item("Lines")` for matched ERP data (ErpItemCode, ErpItemName), not `item("Invoice")("Lines")` which contains raw PDF extraction.

### UiPath Integration Example

```vb
Dim item As JObject = JObject.Parse(requestJson)

' Invoice header
Dim supplier As String = item("Invoice")("Supplier").ToString()
Dim invoiceNo As String = item("Invoice")("InvoiceNo").ToString()
Dim currency As String = item("Invoice")("Currency").ToString()

' Matched lines with ERP item codes
Dim lines As JArray = CType(item("Lines"), JArray)
For Each line As JObject In lines
    Dim erpCode As String = line("ErpItemCode").ToString()     ' CODOBIECT
    Dim erpName As String = line("ErpItemName").ToString()     ' ERP description
    Dim desc As String = line("Description").ToString()        ' PDF description
    Dim qty As String = line("Qty").ToString()
    Dim amount As String = line("Amount").ToString()
Next
```

### Complete callback

```json
POST /robot/jobs/{id}/complete
{
  "Result": "SUCCESS",
  "ErpDocNo": "WM/2026/000123",
  "ErrorCategory": null,
  "ErrorMessage": null,
  "ResultJson": null
}
```

`Result` values: `SUCCESS`, `FAILED`, `PARTIAL`

## UI Pages

| Page | URL | Purpose |
|------|-----|---------|
| Inbox | `/Inbox` | Documents grouped by supplier, **Mapare** column (e.g. 3/5), send to robot |
| Edit | `/Inbox/Edit/{id}` | Edit invoice fields, **manual catalog matching** via search modal per line |
| Preview | `/Inbox/Preview/{id}` | PDF + extracted data side-by-side, **Articol ERP** column, send to robot |
| Status | `/Status` | Robot job list with **Job ID** column, preview links, filters |
| Stats | `/Stats` | Processing statistics by supplier, status, success rate |
| Config | `/Config` | Supplier CRUD (Name/ErpName/VAT/Aliases), catalog CSV import, re-match all, clear inbox |

### Config page actions
- **Supplier management**: add/edit/delete suppliers, set ErpName, VAT, aliases
- **Catalog import**: upload CSV with CODOBIECT + Denumire obiect columns (upsert)
- **Re-match all**: re-run matching on existing documents after catalog update
- **Clear inbox**: delete all documents/jobs/artifacts (keeps suppliers and catalog)

## Other API Endpoints

| Method | Endpoint | Purpose |
|--------|----------|---------|
| `GET` | `/ui/suppliers` | List suppliers with document counts |
| `POST` | `/ui/suppliers` | Create supplier |
| `PUT` | `/ui/suppliers/{id}` | Update supplier |
| `DELETE` | `/ui/suppliers/{id}` | Deactivate supplier |
| `GET` | `/ui/catalog/search?q=coltar` | Search catalog (min 2 chars, top 15) |
| `POST` | `/ui/catalog/map` | Save manual vendor code → catalog item mapping |
| `GET` | `/ui/documents` | List documents (`?supplierId=&status=`) |
| `GET` | `/ui/documents/{id}/pdf` | Stream PDF file |
| `GET` | `/ui/documents/{id}/canonical` | Get canonical JSON |

## Known Issues & Future Work

1. OCR path for scanned PDFs (currently fails with NeedsOcr status)
2. Only one supplier-specific extractor (Cortizo) — generic parser is basic
3. Email/IMAP ingestion configured but not active (using folder ingestor)
4. UiPath Orchestrator integration configured but disabled (robot polls directly)
5. No pagination on large datasets
6. Consider additional CSV columns from ERP catalog if needed
7. Multiple VAT numbers per supplier not yet supported (each VAT = separate supplier)
