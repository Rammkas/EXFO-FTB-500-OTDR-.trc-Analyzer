# ExfoTrcAnalyzer

Command-line tool that parses **EXFO FTB-500 reflectogram files (`.trc`)** and
produces QC (acceptance test) verdicts for fibers, including CSV reports.

It was built for a typical fiber acceptance bench:

```
OTDR port -> fixed launcher connector -> buffer spool (~1 km)
          -> QC connector -> fiber under test
```

Each measurement is taken at two wavelengths (1310 nm and 1550 nm). The tool
splits the trace into sections and events, excludes the buffer spool, computes
length / loss / specific attenuation of the fiber under test, checks configurable
thresholds and writes PASS/FAIL verdicts with reasons.

## Features

- **No dependencies** — pure .NET 8, no NuGet packages.
- Reverse-engineered `.trc` reader: zlib chunk scanning + node-tree parsing
  (format details are documented in `ExfoTrcAnalyzer/TrcParser.cs`).
- 1310/1550 sections and events matched **by coordinate**, not by index, so an
  extra/missing event on one wavelength does not shift the pairs.
- Configurable thresholds via `otdr_config.txt` (auto-created as a commented
  template on first run).
- QC input-connector check (optional, `check_qc_connector`).
- Warnings when the `has_buffer_spool` setting contradicts the actual trace.
- `--dump-keys` diagnostic mode to inspect the raw EXFO field names of any file.
- `--map` mode: ASCII schematic of the measured link with event markers,
  raw trace plots (decoded `RawSamples` curve with events overlaid) and
  detail tables.
- `--file-map full|short` mode: structural map of the raw `.trc` file (record
  headers, zlib chunk ranges, node tree; `short` also exports the tree to JSON).
- `--raw-trace` mode: export of the point-by-point trace curves from the
  `RawSamples` blobs to CSV (Int16 samples, 1024 counts per dB).
- `--excel-trace` mode: chart-friendly binned CSV export (one row per meter,
  level column per wavelength) — charts in Excel or any other tool are built
  straight from the columns.
- Running with no parameters prints the full flag reference.
- 37 regression tests, including GOLD tests against real reference files.

## Build & run

Requires the .NET SDK 8.0 or newer.

```bash
dotnet build
dotnet run --project ExfoTrcAnalyzer -- path/to/folder_with_trc_files
# or a single file:
dotnet run --project ExfoTrcAnalyzer -- path/to/file.trc
# with options (flags go before the input path):
dotnet run --project ExfoTrcAnalyzer -- --source_output on path/to/file.trc
```

### Command-line flags

| Flag | Default | Meaning |
|---|---|---|
| *(positional)* | — | Folder with `.trc` files, or a single `.trc` file path. With no path at all the program prints the flag reference and exits |
| `--source_output on\|off` | `off` | Where the CSV reports are written: `off` = the application folder, `on` = the folder containing the `.trc` files |
| `--dump-keys <file.trc>` | — | Format diagnostics mode (see below) |
| `--map <file.trc \| folder>` | — | ASCII link map mode (see below) |
| `--file-map <full\|short> <file.trc \| folder>` | — | Structural map of the raw `.trc` file itself (see below) |
| `--raw-trace <file.trc \| folder>` | — | Export the point-by-point trace curves to `raw_trace_<name>.csv` (see below) |
| `--excel-trace <file.trc \| folder>` | — | Chart-friendly export `trace_plot_<name>.csv` — binned, one level column per wavelength (see below) |
| `--help`, `-h`, `/?` | — | Print the full flag reference (with descriptions and examples) |

Running the program **with no parameters** (or with `--help`) prints the full
flag reference: every flag, what it does, and usage examples.

Flags go before the input path (the parser also tolerates them after it, but
the documented convention is flags first).

CSV reports are written to the folder chosen by `--source_output`
(`off` = application folder, `on` = folder with the `.trc` files):

| File | Contents |
|---|---|
| `summary_fibers.csv` | one row per fiber: length / loss / attenuation at 1310 and 1550 nm, QC connector loss, verdict, rejection reasons, warnings |
| `sections_detail.csv` | per section: role (buffer / fiber under test), boundaries, data of both wavelengths, verdict |
| `events_detail.csv` | per point event: bench role (port / QC connector / splice / end), losses and reflections |

CSV conventions: `;` separator, comma as decimal separator, UTF-8 with BOM
(Excel-friendly).

### Diagnostics

```bash
dotnet run --project ExfoTrcAnalyzer -- --dump-keys file.trc
```

Prints every `EventTable` key and trace key with values — useful when a new
`.trc` variant from different EXFO firmware shows up.

### Link map

```bash
dotnet run --project ExfoTrcAnalyzer -- --map file.trc   # or a folder of files
```

Prints a scaled ASCII schematic of the measured path built from the same
analysis that produces the QC verdict:

```
=== Link map: fiber '9076' | verdict: PASS ===
scale: 1 character ≈ 87.9 m

0.0         1010.6
|           |                                                                                |
▐▒▒▒▒▒▒▒▒▒▒▒●══════════════════════════════════════════════════════════════════════════════════◆
   1011 m                                          7337 m
PORT       QC

legend: ▐ port  ● QC connector  × splice  ◆ end of fiber | ▒ buffer spool  ═ fiber under test
```

Under the schematic, `--map` also draws a raw trace plot per wavelength — the
decoded `RawSamples` curve (dB level vs distance) as an area chart with the
analyzed events overlaid as markers on the curve. Downsampling is
peak-preserving (max dB per character column), so narrow reflection spikes stay
visible:

```
Trace plot [1310 nm]: level dB vs distance, 30085 samples in window (max per column)
  28.5 dB |▐         ●                                                                        ¦
       dB |░         ░                                                                        ¦
       dB |░         ░                                                                        ¦
       dB |░         ░                                                                        ◆   ← end reflection
  18.5 dB |░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░     ░
   8.5 dB |░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░
                    1|        2|        3|        4|       5|        6|        7|        8|        9|
           ▐0        ●1011                                                                    ◆8347
```

Files without readable `RawSamples` blobs simply get the map without plots.

followed by tables of all sections and events (loss / reflection / attenuation
for both wavelengths) and the fiber-under-test totals with FAIL/WARNING reasons.

### File map

```bash
dotnet run --project ExfoTrcAnalyzer -- --file-map full file.trc   # or a folder of files
dotnet run --project ExfoTrcAnalyzer -- --file-map short file.trc
```

Maps the raw `.trc` file itself, in two variants:

**`full`** — detailed console map, three layers:

1. **Raw file layout** — the observed EXFO container structure: record headers
   with the ASCII magic `AppReg Format Ex  ` (their int32 fields are printed),
   zlib chunks with exact byte ranges (sizes cross-verified by the Adler-32
   checksum), and 4-byte int32 size prefixes between chunks; service gaps are
   shown with a hex preview and extracted text.
2. **Unpacked stream** — which chunk produced which byte range of the stream
   the analyzer parses (identical to `TrcParser.DecompressTrc` behavior) and
   where the root node `OtdrFile` lives.
3. **Node tree** — every container expanded with child count, offset and source
   chunk; scalar children grouped by type per parent; BinaryRef blob nodes
   (`CLSID`, `RawSamples`) with offsets and sizes; node type totals at the end
   (e.g. `Container 39, Int32 120, BinaryRef 40, Double 362, Utf16String 79`).

**`short`** — compact "address → stores what" map: every raw region annotated
with the node-tree content it carries, plus a section explaining where the
point-by-point trace values live (`RawSamples` BinaryRef blobs, ~125 KB per
wavelength — the analyzer itself reads the computed EventTable Double nodes
instead). The node tree is not printed; instead the complete tree (every node
with offset, type, size and parsed value) is saved to **`<name>.nodetree.json`**
(the folder follows `--source_output`, like the CSV reports).

### Raw trace export

```bash
dotnet run --project ExfoTrcAnalyzer -- --raw-trace file.trc   # or a folder of files
```

Exports the point-by-point reflectogram curve (the `RawSamples` blobs) to
**`raw_trace_<name>.csv`** — one row per sample: `trace_name;wavelength_nm;
sample_index;time_s;distance_m;raw_int16;level_db`. The encoding was decoded
from the reference files: Int16 little-endian samples, **1024 counts per dB**
(the `ScaleFactor` field), uniform *time* axis — sample `i` corresponds to
`RangeStart + i × SamplingPeriod` seconds, converted to meters via the trace's
`Range`. Cross-checked: the QC-connector reflection peak lands at ~1014 m
(EventTable: 1010.9 m), the fiber-end peak at ~8348 m (EventTable: 8349.3 m),
and the decoded slopes match the GOLD attenuations (0.32 dB/km at 1310 nm,
0.18 dB/km at 1550 nm). The CSV folder follows `--source_output`, like the other
reports.

### Chart-friendly CSV export

```bash
dotnet run --project ExfoTrcAnalyzer -- --excel-trace file.trc   # or a folder of files
```

Writes **`trace_plot_<name>.csv`** per `.trc` — one row per 1 m bin:

```
distance_m;level_db_1310nm;level_db_1550nm
0,000;25,485;26,283
1,000;28,212;28,157
```

Binning is peak-preserving (max dB per bin), so reflection spikes survive
downsampling; select the columns in Excel (or any plotting tool) and insert a
chart in one step. Plain CSV keeps the data open and easy for third-party
programs to consume. Output folder follows `--source_output`, like the other
reports.

## Configuration (`otdr_config.txt`)

The file is looked for in the folder with the `.trc` files, then next to the
executable. If missing, a commented template is created automatically. Format:
`parameter = value`, `#` comments, numbers with `.` or `,`.

| Parameter | Default | Meaning |
|---|---|---|
| `has_buffer_spool` | `True` | First trace section (~1 km) is the bench buffer spool and is excluded from the fiber under test. **Set `False` when measuring a fiber without a buffer spool** (plugged directly into the fixed launcher). |
| `buffer_min_m` / `buffer_max_m` | `800` / `1200` | Length window used to recognize the buffer spool. |
| `atten_1310_max` | `0.36` | Max attenuation of the fiber under test at 1310 nm, dB/km. |
| `atten_1550_max` | `0.22` | Same at 1550 nm, dB/km. |
| `splice_loss_max` | `0.30` | Max loss of internal splices, dB. |
| `check_qc_connector` | `False` | Whether QC input connector loss affects the verdict (when `False` it is only reported). |
| `qc_connector_loss_max` | `0.50` | QC connector loss threshold, dB. Align with your product spec. |
| `match_tolerance_m` | `5.0` | Coordinate tolerance for matching 1310/1550 events and sections, m. |

> **Measuring without the buffer spool:** set `has_buffer_spool = False`. If you
> leave it `True` and measure a direct fiber 800–1200 m long, that fiber will be
> silently treated as a buffer and excluded from the check. The tool prints a
> warning in this case, but the setting is your responsibility — the trace data
> itself cannot reveal whether a buffer was used.

## Tests

```bash
dotnet run --project ExfoTrcAnalyzer.Tests
```

33 tests, no external packages. The **GOLD** group verifies parsing of real
reference `.trc` files against recorded golden values. Reference reflectograms
are measurement data and are **not** included in this repository. To run the
GOLD tests, put the `.trc` files in a `reference_trc` folder next to the test
binary or the current directory, or set the `OTDR_TRC_DIR` environment variable.
Without them the GOLD tests are skipped (SKIP), not failed.

## Building a standalone executable

Single-file publish, two variants:

```bash
# Self-contained: runs on any Windows x64 machine, no .NET installation needed
# (~65 MB)
dotnet publish ExfoTrcAnalyzer -c Release -r win-x64 --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -o publish/self-contained-win-x64

# Framework-dependent: tiny (~0.2 MB), requires the .NET 8 Desktop Runtime
# installed on the target machine
dotnet publish ExfoTrcAnalyzer -c Release -r win-x64 --self-contained false \
    -p:PublishSingleFile=true -o publish/framework-dependent-win-x64
```

The result is a single `exfo_trc_analyzer.exe`. Replace `win-x64` with another
RID (`linux-x64`, `win-arm64`, ...) for other platforms.

## Format notes (reverse-engineered)

- A `.trc` file is binary service data plus a sequence of zlib-compressed
  chunks. Each checked FTB-500 file contained exactly 9 chunks, all with
  zlib FLG=`0xDA` (compression level 9). Chunk detection uses the RFC 1950
  CMF/FLG validity rule (`(CMF*256 + FLG) % 31 == 0`, CMF=`0x78`).
- The decompressed stream is a node tree: 16-byte header
  `[nameOffset][dataType][valLen][selfOffset]` followed by the value; offsets
  are absolute; `dataType` values: Container, Int32, BinaryRef, Double,
  Utf16String, Bool8.
- The root node is found by locating the name `OtdrFile` and verifying each
  candidate header.
- The trace curve itself lives in a `RawSamples` BinaryRef blob inside each
  `TraceN` container (in the decompressed stream, right after the node header):
  Int16 little-endian samples, 1024 counts per dB (`ScaleFactor`), uniform time
  axis `RangeStart + i × SamplingPeriod` — decoded and exportable via
  `--raw-trace`.
- EXFO does **not** store a measured dB/km coefficient in the EventTable;
  section attenuation is computed as `Loss / Length` (EXFO attributes connector
  losses to point events, so this is a correct estimate). If an explicit
  attenuation field appears in some firmware, it takes priority automatically.

Full details are documented in the file headers of `TrcParser.cs` and
`Analyzer.cs`.

## Project layout

```
ExfoTrcAnalyzer/          main tool
  Program.cs              thin CLI (console output + CSV assembly)
  Config.cs               settings + otdr_config.txt loader
  TrcParser.cs            binary .trc format reader
  Analyzer.cs             sections/events extraction + PASS/FAIL verdicts
  Csv.cs                  CSV formatting (Excel-friendly)
  MapRenderer.cs          --map: ASCII link schematic + raw trace plots
  FileMap.cs              --file-map: raw-file structural map + tree JSON export
  RawTrace.cs             --raw-trace: point-by-point trace curve CSV export
  PlotExport.cs           --excel-trace: binned chart-friendly CSV export
ExfoTrcAnalyzer.Tests/    regression tests (37, incl. GOLD)
```

## Disclaimer

This is an independent reverse-engineering effort. The `.trc` format is not an
open specification; parsing was validated against a limited set of real
FTB-500 files. Verify results against your official test software before using
them for acceptance decisions. The tool is provided under the MIT license,
without warranty of any kind.
