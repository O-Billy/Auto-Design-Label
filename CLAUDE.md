# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Auto Design Label turns a product master data (PMD) spec into print-ready packaging labels. The core
data structure is the LDM (Label Definition Model) — a normalized, declarative JSON representation of
label layouts. From an LDM the C# tool produces, per label:

- a **1:1-scale PDF** for artwork approval and dimension checks,
- **ZPL** (Zebra Printer Language) for direct thermal-printer output, and
- a **`.lab` file** (CodeSoft 6 / TEKLYNX) that keeps `{{TOKEN}}` fields live as CS6 Free Variables,
  so the current manual "merge real data in CS6 and print" workflow still applies — only the *design*
  step is automated.

An LDM can be authored by hand, or extracted automatically from a **PMD PDF** by `PmdExtractor`
(rule-based, fully offline, no AI — see `PmdExtractor.cs`).

A printability linter runs during PDF rendering and flags issues (barcode X-dimension too small, QR
quiet-zone/module size too small, text overflowing the label, overlapping elements) that only surface
once labels are printed at scale.

### Implementations

- **`AutoDesignLabel-csharp/`** — the primary work (`AutoDesignLabel.sln`). All four projects target
  `net8.0-windows10.0.19041.0` (Windows-only: CS6 COM, `FontInstaller`, `Windows.Media.Ocr`;
  `render_pdf.py` is the cross-platform reference).
  - **`AutoDesignLabel/`** — core library + CLI. All pipeline logic lives here (`PmdExtractor`,
    `PmdClassifier`, `ContentSpecExtractor`, `AutoLayoutEngine`, `Binder`, `PdfLabelRenderer`,
    `Linter`, `ZplEmitter`, `LabFileGenerator`, `PrinterClient`, `FontInstaller`, `WindowsMediaOcr`).
  - **`AutoDesignLabel.Web/`** — Blazor Server UI (`Pages/Index.razor`) wrapping the same library:
    upload PMD.pdf → auto-extract → preview 1:1 PDF per label → review PMD issues / printer specs →
    export `.lab` or ZPL. Internal LAN tool; no HTTPS redirect, SignalR max message size raised to
    50 MB for PDF uploads.
  - **`AutoDesignLabel.Wpf/`** — empty scaffold (WPF). Not implemented; does not currently build
    (`MSB4057 CheckForDuplicateItems` — a WindowsDesktop SDK target issue, pre-existing).
- **`render_pdf.py`** (repo root) — reference/demo implementation in Python (ReportLab). PDF + lint
  report only; no ZPL, no `.lab`, no PMD extraction. A second opinion when validating rendering logic,
  not where features land.

Comments and console output in the C# and Python source are in Vietnamese; keep that convention when
editing existing files. User-facing text in `Index.razor` is in English.

## Commands

All commands assume PowerShell from the repo root unless noted.

### C# CLI (primary)

```powershell
cd AutoDesignLabel-csharp/AutoDesignLabel
dotnet build
dotnet run -- <ldm.json | pmd.pdf> <data.json> <out-dir> [dpi]
# with bundled samples, using defaults:
dotnet run
```

First arg may be an `*.ldm.json` **or** a `*.pdf` — a `.pdf` is run through `PmdExtractor` first and the
generated LDM is written to `<out-dir>/<DocumentId>.ldm.json` (always review it before production use).
Defaults when args are omitted: `B01O023.01.ldm.json`, `sample-data.json`, `out/`, dpi `203`.

Per label, `out/` gets: `<DocumentId>-1to1.pdf`, `<Id>.template.zpl`, `<Id>.job.zpl`, and `<Id>.lab`
(only for labels with `requiresAutoDesign: true`).

No test suite. Verification = the linter report + manual inspection of the PDF/ZPL/.lab in `out/`.
**A non-zero exit (linter `HasError`) means a layout error — the run is not a success.** `.lab` export
failures are warnings only and do not change the exit code (PDF/ZPL already succeeded at that point).

### Sample inputs for testing

- `AutoDesignLabel-csharp/AutoDesignLabel/B01O023.01.ldm.json` + `sample-data.json` — the bundled
  hand-authored LDM (CLI defaults; also copied to each project's `bin/`).
- Repo-root PDFs `B01O023.01-LBL-00.pdf`, `B01G017.03_LBL-00XX_*.pdf` — vector-path PMDs (real
  text/vector mockups) for exercising `PmdExtractor`.
- `SE PMD/AP955*_Label_Approval.pdf` (Schneider) + `SE PMD/AP9559-sample-data.json` — content-only
  path (PowerPoint→PDF raster mockups) for `ContentSpecExtractor` / `AutoLayoutEngine`.

### Build notes

- Not a git repository — there is no version history; don't run git commands.
- Build/run per project (`cd` into it, `dotnet build`/`dotnet run`). A solution-wide
  `dotnet build AutoDesignLabel.sln` fails on the empty `AutoDesignLabel.Wpf` project (`MSB4057`,
  pre-existing) — that failure is expected and does not affect the other two projects.

### C# Web UI

```powershell
cd AutoDesignLabel-csharp/AutoDesignLabel.Web
dotnet run
```

### Python (reference)

```bash
pip install reportlab
python render_pdf.py <ldm.json> <data.json> <out-prefix>
```
Produces `<out-prefix>-1to1.pdf`, `<out-prefix>-proof.pdf` (A4 proof sheet with rulers/annotations for
QC), and `<out-prefix>-lint.json`.

## Architecture

### The LDM (Label Definition Model) — `Ldm.cs`

`*.ldm.json` is the single source of truth for a label document. It is *not* free-form artwork — it's a
declarative model (hand-authored, or `PmdExtractor` output), with unresolved ambiguities tracked in
`openIssues` (each has a `severity`: `blocker` issues are printed at startup and — in the Web UI —
hard-block export until resolved with the PM; `major`/`minor` are informational). A document has one or
more `labels`, each with physical dimensions (`widthMm`/`heightMm`), material, `quantity`, a
`requiresAutoDesign` flag, and a flat list of `elements` positioned in mm from the top-left origin.

`requiresAutoDesign` (default `true`): false when the PMD defines no per-label "Label Font Style & Size"
table — the sign of a pre-printed static decal (Screw VOID, safety seal) that is out of scope for
auto-design. The Web UI and `.lab` export skip these labels.

Element `type`s: `text`, `line`, `barcode128`, `qr`, `image`, and `repeat` (a template block
instantiated N times for repeating rows, e.g. per-unit RSN/MAC lines on a carton label — see
`y0`/`stepY`/`max`/`template`). `repeat` expansion also injects `i`, `RSN_i`, `MAC_i` per row and reads
`UNIT_COUNT` from the data (clamped to `max`). Both C# (`Binder.Expand`) and Python (`expand()`) must
stay in sync on how `repeat` unrolls — it's the trickiest binding logic.

`{{TOKEN}}` placeholders in text/data fields are resolved against a separate data JSON (e.g.
`sample-data.json`) at render time. The `fields` block documents each token's source system (MES, ERP,
SerialService, const, system) and validation `pattern` for reference — renderers don't enforce patterns.

### PMD → LDM — two paths, chosen by `PmdClassifier.cs`

A PDF input is classified by running `PmdExtractor` and checking whether it produced any label with
elements. If yes → **vector path** (use that result). If no (parse error, no `<n>. Name 000.00000.000`
headers, or labels found but no coordinates) → **content-only path**. `PmdClassifier.Classify` returns
the reason string and, for the vector case, the already-extracted `LdmDocument` (no double extraction).

**Vector path — `PmdExtractor.cs`.** For PMDs whose mockups are drawn with **real text/vector** (not
raster), so exact coordinates come straight out of the PDF via PdfPig. Roughly: parse Quantity/Label
Size/Material text → find the vector rectangle whose aspect ratio matches the real mm size (mockup
frame → px/mm scale + origin) → group words inside into text lines with PdfPig's actual point sizes →
group fill rectangles by Y-band (one band = barcode128, stacked = QR) → cross-reference the "Font
Style & Size" table and sample values to assign medium/light fonts and swap literals for `{{TOKEN}}`.
Unrecognized things go to `openIssues`. `FieldAliasesByLabel` is tuned per vendor/PMD.

**Content-only path — `ContentSpecExtractor.cs` → `AutoLayoutEngine.cs`.** For PMDs prepared in
PowerPoint and "Save As PDF": the mockup is a **raster image**, no coordinates. Such a spec only
constrains label size + the required field list — not font, not barcode type, not position — so a
missing coordinate is *by design*, not a document error. `ContentSpecExtractor` reads only the PDF
**text layer** (real PPT text boxes): slide title → label id + product (`ParseProduct` strips a known
"…LABEL/Packing Label/…" suffix so hyphenated part numbers survive), `LABEL SIZE: W x H MM` → mm
dimensions, callout phrases → a `ContentSpec` field list (`ContentSpec.cs`: token, caption, render
kind, sample value, confidence). It matches callouts across 1–3 consecutive lines because PPT text
boxes wrap ("KIT assembly part" / "number"). `AutoLayoutEngine.BuildDocument` turns a `ContentSpec`
into an `LdmDocument` with `LabelDef.LayoutSource = Auto`: per archetype it places elements with
**default** font (`Helvetica` → system Arial via the resolver, JioType fallback) and Code128, running
the layout math itself (fixed per-row height budget via `EmitBarcodeBlock`; self-managed quiet zone
≥ 1 mm; text auto-shrunk to fit width; HRI always below the bars). Archetypes: `simple-id` (small
ID/accessory — caption+value / barcode), `packing` (barcode blocks with separate HRI + text-field
lines), `lit-kit` (header + serial barcode + kit table **baked as static text rows** from `data`
`KIT_COUNT`/`KIT_PN{i}`/`KIT_SN{i}` — no `repeat` element, since CS6 can't bind many variables into
one table cell); anything else → one centered note element + `openIssue`. Part numbers are baked from
the slide title (with an `openIssue` to re-verify).

**Sample values — `IPmdImageReader` (`WindowsImageReader`).** Values aren't in the text layer; they
live in the raster. `WindowsImageReader` (offline, no NuGet — the reason all four csproj target
`net8.0-windows10.0.19041.0`) reads each slide image two ways, in order of trust:

1. **`Barcodes()` — ZXing decodes the label's barcodes** (`BarcodeReaderGeneric` + `RGBLuminanceSource`
   from `Windows.Graphics.Imaging` pixels). Checksummed → exact. `ContentSpecExtractor.FillFromImages`
   splits the ANSI MH10 **data identifier** (`1P`/`P` → part number, `S` → serial, `Q` → qty) so the
   field gets the bare value while `ContentField.DataIdentifier` is kept — `AutoLayoutEngine` then
   encodes `DI+value` in the Code128 but shows only `value` in the HRI, matching the approved artwork.
   A decoded barcode also **auto-corrects an `O`/`0` typo in the slide title** (`label.Product`).
2. **serial structure from the text layer** (`"0"-Factory code`, `"A"-…` callouts → `ParseSerialStructure`)
   — a per-position digit/letter template used to repair noisy OCR of the serial.
3. **cross-label** (`ResolveKitTable`, `ResolveSerialConsensus`): the LIT-KIT table is filled from the
   accessory ID labels' own (part number, serial); a barcode-exact serial prefix is propagated to
   labels whose serial only came from OCR.
4. **`Ocr()` — Windows.Media.Ocr** (optional; barcode decoding still works without the OCR language
   pack). Only used for what 1–3 miss (description, country, un-decoded barcodes), with a digit-confusion
   repair map + `Prefold` for junk glyphs. Low-confidence fills get `Confidence` ≤ 0.4 and a "must
   verify" `openIssue`.

`ContentField.Confidence` drives the review UI dots (green ≥ 0.9 barcode, amber ≥ 0.5, red < 0.5).
Every raw barcode/OCR line is stashed in `ContentLabel.OcrRawLines`. `AutoLayoutEngine.BuildDocument`
picks the highest-confidence sample for each shared token across all labels; `EffectiveSample` never
lets a low-confidence OCR value override a data-JSON value; `AutoLayoutEngine.EnsureRenderData` fills
any still-missing `{{TOKEN}}` with a visible `<TOKEN>` placeholder so the proof always renders.
Env: `ADL_SKIP_LAB=1` skips the CS6 `.lab` step; `ADL_OCR_DEBUG=1` dumps decoded barcodes + OCR lines.

`LayoutSource` (`Exact` | `Auto`) records where coordinates came from; it is distinct from
`RequiresAutoDesign` (in scope for auto-design at all?) and `LayoutConfidence` (a human note). The
review UI should badge `Auto` labels neutrally ("needs design review"), not as a severity.

### CLI pipeline — `Program.cs`

Fixed sequence:
1. Load data JSON. Load LDM directly, or for a `.pdf` input run `PmdClassifier` then the vector or
   content-only path (above); the generated LDM is written to `<out>/<DocumentId>.ldm.json`.
2. Print `blocker`-severity open issues.
3. Render PDF via `PdfLabelRenderer` — internally calls `Binder.Expand` to unroll `repeat`, reports
   lint findings into a shared `Linter` as it draws. Barcode/QR are drawn as **vector rectangles**
   (module matrices from `ZXing.Net`), sharp at any DPI.
4. Emit ZPL via `ZplEmitter`, two modes: `EmitStoredFormat` (`^DF`-stored template with `^FN`
   placeholders, loaded onto the printer once) and `EmitPrintJob` (short per-job `^XF` + `^FN` fill
   referencing that template — the steady-state print path). `^FD` escaping uses hex-with-`_` and only
   emits `^FH` when a char was actually escaped.
5. Print linter report; **exit non-zero if any row is `LintStatus.Error`** (warnings don't block).
6. For each `requiresAutoDesign` label, emit `.lab` via `LabFileGenerator`. Failures here are warnings
   and do **not** affect the exit code.

`PrinterClient.cs` sends ZPL over raw TCP 9100 (no driver/license) and can poll `~HS` status before a
large batch. Not wired into `Program.cs` — invoked separately when actually printing.

### `.lab` generation — `LabFileGenerator.cs`

Drives **CodeSoft 6 via COM automation** (the `Lppx2` type library) to build the document object by
object and `SaveAs`, rather than writing TEKLYNX's proprietary binary format. Windows-only, requires
CS6 installed. Key constraints baked into the code:

- CS6 internal unit is 1/1000 inch (mil): `units = round(mm * 1000 / 25.4)`.
- A **static `SemaphoreSlim`** serializes all of `Generate()` process-wide — CS6 automation hangs / breaks
  layout when multiple `Application` instances run concurrently (the Web app can receive parallel export
  requests).
- `{{TOKEN}}` fields become **real CS6 Free Variables** (kept live, not baked). Exceptions that *are*
  baked to static sample text: QR payloads (XML composite of many tokens) and `repeat`-expanded rows —
  CS6 can't bind multiple variables into one Barcode value. Mixed static+token text is split into
  separate Text objects (`AddMixedContentAsSeparateTexts`).
- View mode is set to show sample *values* (like the PDF review step), not variable names.

### Fonts — `FontInstaller.cs`, `JioTypeFontResolver.cs`, `Font/`

The PMD specifies **JioType Light/Medium**. Six JioType `.ttf` variants live in `Font/` and are embedded
into the `AutoDesignLabel` assembly (`<EmbeddedResource>` in the csproj).

- `JioTypeFontResolver` feeds the embedded Light/Medium faces to PdfSharpCore (medium = `XFontStyle.Bold`
  by convention). Installed once as `GlobalFontSettings.FontResolver` in `PdfLabelRenderer`'s static ctor.
- `FontInstaller` installs **all** JioType variants as real per-user Windows fonts (Win10 1809+ HKCU
  method, no admin) if missing. Idempotent, never throws. Needed because CS6 only knows fonts by name and
  silently substitutes a cached fallback if the real font isn't installed — and the fallback's metrics
  shift with any CS6 UI interaction, breaking `.lab` layouts. Registry writes go through direct P/Invoke
  (`advapi32`) because the build environment has no network to add `Microsoft.Win32.Registry`.

### Web UI specifics — `AutoDesignLabel.Web/`

Blazor Server. `Index.razor` renders each label's PDF server-side into a `data:` URL shown in an
`<iframe>`; the exact `zoom=` is computed from mm→px (not CSS `transform:scale`) so the PDF rasterizes
sharp — see the long comment on `BaseScale`. Generated `.lab`/`.zpl`/`.ldm.json` files are written to a
server temp path and handed to the browser via a short-lived GUID token (`FileDownloads.Pending` +
`GET /download-file/{token}`), the standard Blazor Server download pattern.

Upload runs `PmdClassifier`. Vector → straight to the label view. Content-only → a **spec review
editor** (`ShowSpecEditor`): editable per-label size/archetype and a field table (caption / token /
kind / sample value), with the OCR raw lines shown as hints; "Generate auto-layout" then calls
`AutoLayoutEngine.BuildDocument`. `Auto` labels get a neutral blue "Auto-layout" pill (not a severity),
the Element Properties tab's sample values are editable (re-renders on change via `UpdateSample`), and
a "confirm auto-layout" checkbox (`NeedsLayoutApproval`) gates both export buttons. "Download LDM"
saves the current `LdmDocument`; "← Edit content spec" returns to the editor.

### Linter rules — `Linter.cs` (mirrored in `render_pdf.py`)

- **Code128**: X-dimension (mm per narrow module) must be ≥ `MinXDimMm` (0.19 mm) — evaluated for both
  203 and 300 dpi targets (same template prints on either).
  - *Fix for X-dim too small:* increase the barcode's target width, or shorten the encoded value. The
    DPI/bar-width math is fixed and not a lever.
- **QR**: module size must be ≥ `MinQrModuleMm` (0.25 mm), accounting for the fixed 4-module quiet zone
  each side (`n + 8`, `n` = QR's own module count).
  - *Fix for QR module too small:* increase target size, or shorten/simplify the payload (fewer bytes →
    fewer modules at a given ECC level).
- **Text overflow** (`LintStatus.Error`): rendered string's bounding box falls outside the label width.
- **Collisions** (`LintStatus.Error`): two element bounding boxes overlapping > 0.3 mm on both axes,
  *except* text-vs-text (adjacent baselines routinely intersect and aren't real defects).

Note: barcode/QR findings are `Warning` (don't block exit); text-overflow and collision are `Error`.

When changing element positions/sizes in an LDM, re-run the renderer and check the lint report / exit
code rather than eyeballing coordinates — the thresholds are DPI- and print-hardware-derived.
