# InvoiceProcessor MVP (.NET 8)

## Architecture diagram (concise)

```text
IMAP Mailbox
   │ (poll every N sec)
   ▼
DispatcherWorker (HostedService)
   ├─ ImapEmailDispatcher -> save PDF to ./data + create Documents(RECEIVED)
   └─ PdfExtractionPipeline
        ├─ PDF text extraction (PdfPig)
        ├─ Rule-based classification (Invoice/Materials)
        ├─ Canonical parsing strategies
        ├─ Supplier resolution + line matching
        └─ Persist ExtractArtifacts + InvoiceLines + AuditEvents

Razor UI + REST API
   ├─ /Inbox (supplier grouping, selection, send job)
   ├─ /Status (job outcome tracking)
   └─ /ui/* endpoints for documents/jobs

Robot handoff
   ├─ POST /ui/posting-jobs -> PostingJobs(QUEUED)
   ├─ UiPath Orchestrator trigger (optional)
   └─ Robot pull API:
       GET /robot/jobs/next
       POST /robot/jobs/{id}/complete

Storage/Audit
   ├─ SQLite via EF Core
   ├─ PDF files in ./data/store
   └─ Extracted JSON/canonical/payload/result in DB + audit events
```

## Solution structure

- `InvoiceProcessor.sln`
- `InvoiceProcessor.Web`
  - `Program.cs` wiring (DI, EF, hosted worker, Razor Pages + controllers)
  - `Data/` (`AppDbContext`, migration)
  - `Models/` entities for all MVP tables + mappings
  - `Services/` dispatcher/extraction/matching/robot/storage
  - `Controllers/` UI API + robot contract endpoints
  - `Pages/Inbox` and `Pages/Status` (minimal operational UI)
  - `Contracts/` canonical payload contracts

## Robot endpoint contract (samples)

### GET `/robot/jobs/next` response

```json
{
  "postingJobId": "d4fae3e6-3f09-4f95-9f71-2c0f1ab763e0",
  "documentId": "ec22ab4f-2ad8-4bf8-95ad-271f1e5d5ce7",
  "correlationId": "e2a3f635a9bf4afbbfc888c771180b5c",
  "invoice": {
    "supplier": "ABC Sp. z o.o.",
    "invoiceNo": "FV/12/2026",
    "invoiceDate": "2026-02-20",
    "currency": "PLN",
    "netTotal": 1000.00,
    "vatTotal": 230.00,
    "grossTotal": 1230.00,
    "lines": [],
    "metadata": { "confidence": 0.9, "strategy": "GenericInvoiceTableStrategy", "notes": null }
  },
  "lines": [
    {
      "lineNo": 1,
      "description": "Material XYZ",
      "qty": 2,
      "uom": "szt",
      "amount": 500,
      "erpItemCode": "MAT-0001",
      "confidence": 1.0,
      "reason": "exact-vendor-code"
    }
  ]
}
```

### POST `/robot/jobs/{id}/complete` request

```json
{
  "result": "SUCCESS",
  "erpDocNo": "WM/2026/000123",
  "errorCategory": null,
  "errorMessage": null,
  "resultJson": "{\"screenshots\":[\"/logs/run123/1.png\"]}"
}
```

## TODO (next iterations)

1. OCR path for scanned PDFs (Tesseract or Azure OCR adapter).
2. Better table parsing (row segmentation, multi-line rows, tax buckets).
3. Improved supplier + item matching with embeddings and language normalization.
4. Admin UI for suppliers, aliases, mapping rules, and confidence thresholds.
5. Queue abstraction (e.g., Hangfire/RabbitMQ) for robust retry orchestration.
6. Stronger idempotency policy (compound unique constraints + semantic dedupe service).
7. Live status push to UI (SignalR) + richer robot artifact browser.
