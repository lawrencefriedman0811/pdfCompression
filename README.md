# PdfCompress

A .NET 8 command-line tool that reduces PDF file sizes by pruning unused PDF resources and deduplicating redundant **Form XObjects** — common sources of bloat in PDFs exported from form-heavy systems (e.g. Kofax, DocuWare, SAP).

## How it works

PDFs from form-processing systems often contain hundreds of identical or empty invisible Form XObjects embedded across every page, plus unused font resources that keep large embedded font programs reachable. This tool performs three optimization passes over each PDF:

1. **Pass 1 — Collect** — Scans every page and decodes each Form XObject's stream. Empty forms (`q Q /Tx BMC EMC`) are flagged. Non-empty forms are fingerprinted using SHA-256 of their decoded bytes **plus** their semantic dictionary entries (`/BBox`, `/Matrix`, `/Resources`, `/Group`, etc.) to ensure visually different XObjects are never collapsed.

2. **Pass 2 — Rewrite** — Replaces all references to empty forms with a single shared canonical empty, and replaces duplicate forms with a reference to the first occurrence. Content stream references remain valid — nothing is deleted.

3. **Pass 3 — Font resource pruning** — Tokenizes page, form, and annotation appearance content streams to find the font resource names actually selected with `Tf`. Unused `/Font` resource entries are removed so unreachable font dictionaries, descriptors, and embedded font-file streams are left out of the rewritten output. Unsafe scopes, such as unparseable content or Type 3 font resources, are left untouched.

The output is saved with maximum stream compression and full object-stream compression (iText 7's `BEST_COMPRESSION` + `SetFullCompressionMode`).

## Project structure

```
src/
├── PdfCompress.Core/               Class library — all compression logic
│   ├── Models/
│   │   └── CompressionResult.cs    Result record per file (sizes, error, reduction %)
│   └── Services/
│       ├── PdfOptimizer.cs         Two-pass Form XObject optimizer (iText 7)
│       └── PasswordMapReader.cs    Reads per-file passwords from an Excel spreadsheet
└── PdfCompress.Cli/                Console app — argument parsing and output formatting
    └── Program.cs
```

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8)

## Usage

```powershell
dotnet run --project src/PdfCompress.Cli -- --input <dir> --output <dir> [options]
```

### Options

| Option | Required | Description |
|--------|----------|-------------|
| `--input <dir>` | ✅ | Directory containing input PDFs |
| `--output <dir>` | ✅ | Directory to write compressed PDFs (created if missing) |
| `--passwords <file.xlsx>` | | Excel file mapping filenames to passwords (see below) |
| `--parallelism <n>` | | Max parallel files (default: CPU core count) |
| `--limit <n>` | | Process only the first N files (useful for testing) |
| `--format text\|json` | | Output format — `text` table (default) or `json` |
| `--overwrite` | | Allow `--output` to be the same directory as `--input` |
| `--password-only` | | Apply passwords from `--passwords` without running compression optimization |

### Examples

```powershell
# Basic run
dotnet run --project src/PdfCompress.Cli -- --input old --output new

# With passwords, 4 parallel workers, JSON output
dotnet run --project src/PdfCompress.Cli -- --input old --output new --passwords passwords.xlsx --parallelism 4 --format json

# Password-only pass (same Excel mapping; no compression optimization)
dotnet run --project src/PdfCompress.Cli -- --input old --output new --passwords passwords.xlsx --password-only

# Test on 3 files first
dotnet run --project src/PdfCompress.Cli -- --input old --output new --limit 3
```

### Password file format

Create an Excel file (`.xlsx`) with filename and password columns. The standard headers are:

| FileName | Password |
|----------|----------|
| Invoice_001.pdf | secret123 |
| Invoice_002 | abc456 |

- Existing partner-list workbooks are also supported with these headers:
  - `Custom File Name (No Special Characters)`
  - `Password (Optional)`
- The `.pdf` extension is optional in the `FileName` column.
- Matching is case-insensitive.
- If a PDF has no entry in the file, it is opened without a password. If that fails, the file is logged as an error and the batch continues.

## Output

```
FILE                                          OLD_BYTES     NEW_BYTES   SAVED_BYTES  REDUCTION
-------------------------------------------------------------------------------------------------------------
Invoice_001.pdf                               4,821,300     1,203,400     3,617,900      75.0%
Invoice_002.pdf                               5,104,800     1,289,100     3,815,700      74.8%
-------------------------------------------------------------------------------------------------------------
TOTAL                                         9,926,100     2,492,500     7,433,600      74.9%
```

Failures are listed below the table with their error message. The process exits with code `1` if any file failed.

## Error handling

- One corrupt or password-protected file does not abort the batch.
- Each file runs in its own iText 7 `PdfDocument` instance — no shared state between workers.
- Per-stream decode errors are caught and the stream is skipped (the file is still saved).

## Dependencies

| Package | Purpose |
|---------|---------|
| [itext7](https://www.nuget.org/packages/itext7) | Low-level PDF read/write and XObject access |
| [ClosedXML](https://www.nuget.org/packages/ClosedXML) | Read the password Excel file |
| [System.CommandLine](https://www.nuget.org/packages/System.CommandLine) | CLI argument parsing |
