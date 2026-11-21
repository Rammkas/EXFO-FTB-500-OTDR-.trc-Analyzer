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
- 33 regression tests, including GOLD tests against real reference files.

## Build & run

Requires the .NET SDK 8.0 or newer.

```bash
dotnet build
dotnet run --project ExfoTrcAnalyzer -- path/to/folder_with_trc_files
# or a single file:
dotnet run --project ExfoTrcAnalyzer -- path/to/file.trc
```

### Command-line flags

| Flag | Default | Meaning |
|---|---|---|
| *(positional)* | current folder | Folder with `.trc` files, or a single `.trc` file path |
| `--source_output on\|off` | `off` | Where the CSV reports are written: `off` = the application folder, `on` = the folder containing the `.trc` files |
| `--dump-keys <file.trc>` | — | Format diagnostics mode (see below) |

Flags may appear in any order relative to the input path.

CSV reports are written to the current folder:

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
ExfoTrcAnalyzer.Tests/    regression tests (33, incl. GOLD)
```

## Disclaimer

This is an independent reverse-engineering effort. The `.trc` format is not an
open specification; parsing was validated against a limited set of real
FTB-500 files. Verify results against your official test software before using
them for acceptance decisions. The tool is provided under the MIT license,
without warranty of any kind.
