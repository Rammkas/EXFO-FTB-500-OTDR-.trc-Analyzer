// ==============================================================================
// Program.cs — entry point and CLI of the exfo_trc_analyzer utility.
//
// USAGE (flags go before the input path):
//   exfo_trc_analyzer.exe [folder_with_.trc]         — parse and write CSV reports
//   exfo_trc_analyzer.exe <file.trc>                 — parse a single file
//   exfo_trc_analyzer.exe --source_output on|off <path> — where CSV reports are written:
//       off (default): the application folder;
//       on:            the folder containing the .trc files.
//   exfo_trc_analyzer.exe --dump-keys <file.trc>     — format diagnostics: prints
//       all EventTable and trace keys with values (needed to confirm EXFO field
//       names when encountering a new .trc variant)
//   exfo_trc_analyzer.exe --map <file.trc | folder>  — ASCII link map: a scaled
//       schematic of the measured path with event markers and detail tables
//
// WORKFLOW:
//   1) Config.Load — settings from otdr_config.txt (or defaults + template creation);
//      IMPORTANT about has_buffer_spool: measuring WITHOUT a buffer spool (fiber
//      plugged directly into the fixed launcher) requires has_buffer_spool=False.
//      Leaving it True for such a measurement: a 800..1200 m fiber is treated as a
//      buffer and excluded from the check (a warning appears in the CSV, but the
//      verdict would be based on empty data).
//   2) iterate over *.trc in the folder (ordered by file name, Ordinal); for each —
//      Analyzer.AnalyzeFile (TrcParser -> tree -> sections/events -> verdict);
//   3) every file is processed in try/catch: a corrupt file does not stop the batch;
//   4) three CSVs are written to the folder chosen by --source_output
//      (off = application folder, on = folder with the .trc files; each write in
//      try/catch — a CSV locked open in Excel must not crash the finished results):
//      summary_fibers.csv  — one row per fiber: length/loss/attenuation at 1310 and
//                            1550, QC connector loss, verdict and reasons,
//                            warnings (e.g. has_buffer_spool contradicting the
//                            actual trace);
//      sections_detail.csv — per section: role (buffer/DUT), boundaries, data of
//                            both wavelengths, section verdict;
//      events_detail.csv   — per point event: role on the bench (port / QC
//                            connector / splice / end), losses and reflections of
//                            both wavelengths.
//
// The analysis itself lives in the Config/TrcParser/Analyzer/Csv modules — this
// file is only the CLI, console output and CSV row assembly.
// ==============================================================================
using System.Text;

namespace ExfoTrcAnalyzer;

// ==============================================================================
// Thin CLI over the Config / TrcParser / Analyzer / Csv modules.
// Usage:
//   exfo_trc_analyzer.exe [folder_with_.trc | file.trc]
//   exfo_trc_analyzer.exe --dump-keys <file.trc>   — format diagnostics
// ==============================================================================
internal static class Program
{
    private static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        // --- option parsing: --source_output on|off (default off) ---
        bool sourceOutput = false;
        var positional = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--source_output", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length ||
                    (!args[i + 1].Equals("on", StringComparison.OrdinalIgnoreCase) &&
                     !args[i + 1].Equals("off", StringComparison.OrdinalIgnoreCase)))
                {
                    Console.WriteLine("[ERROR] --source_output requires a value: on or off");
                    return;
                }
                sourceOutput = args[i + 1].Equals("on", StringComparison.OrdinalIgnoreCase);
                i++; // consume the value
            }
            else
            {
                positional.Add(args[i]);
            }
        }

        // Windows console quirk: quoting a path that ends with a backslash — "C:\dir\" —
        // turns the trailing \" into an escaped quote, so the argument arrives with a
        // stray trailing '"'. A path cannot legitimately end with '"', so trim it.
        for (int i = 0; i < positional.Count; i++)
            positional[i] = positional[i].TrimEnd('"');

        if (positional.Count >= 1 && positional[0] == "--dump-keys")
        {
            if (positional.Count < 2)
            {
                Console.WriteLine("Usage: exfo_trc_analyzer.exe --dump-keys <file.trc>");
                return;
            }
            DumpKeys(positional[1]);
            return;
        }

        if (positional.Count >= 1 && positional[0] == "--map")
        {
            if (positional.Count < 2)
            {
                Console.WriteLine("Usage: exfo_trc_analyzer.exe --map <file.trc | folder>");
                return;
            }
            DumpMap(positional[1]);
            return;
        }

        string inputArg = positional.Count > 0 ? positional[0] : ".";

        // Accept either a folder or a direct path to a single .trc file.
        string inputDir;
        string? singleFile = null;
        if (File.Exists(inputArg))
        {
            if (!inputArg.EndsWith(".trc", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"[ERROR] The specified file is not a .trc file: {inputArg}");
                return;
            }
            singleFile = Path.GetFullPath(inputArg);
            inputDir = Path.GetDirectoryName(singleFile)!;
        }
        else if (Directory.Exists(inputArg))
        {
            inputDir = Path.GetFullPath(inputArg);
        }
        else
        {
            Console.WriteLine($"[ERROR] Path not found: {inputArg}");
            return;
        }

        Config.Load(inputDir);
        // --source_output: off (default) = write CSVs to the application folder;
        //                  on = write CSVs to the folder containing the .trc files.
        string outputDir = sourceOutput ? inputDir : AppContext.BaseDirectory;
        ProcessFolder(inputDir, singleFile, outputDir);
    }

    private static void DumpKeys(string filepath)
    {
        try
        {
            Console.WriteLine(Analyzer.DumpTrc(filepath));
        }
        catch (Exception e)
        {
            Console.WriteLine($"[ERROR] {filepath}: {e.Message}");
        }
    }

    // --------------------------------------------------------------------------
    // --map: ASCII schematic of the analyzed link (one file or a folder of files)
    // --------------------------------------------------------------------------
    private static void DumpMap(string path)
    {
        try
        {
            var files = new List<string>();
            if (Directory.Exists(path))
                files.AddRange(Directory.GetFiles(path, "*.trc").OrderBy(f => f, StringComparer.Ordinal));
            else if (File.Exists(path))
                files.Add(path);
            else
            {
                Console.WriteLine($"[ERROR] Path not found: {path}");
                return;
            }

            foreach (string file in files)
                foreach (var f in Analyzer.AnalyzeFile(file))
                    Console.WriteLine(MapRenderer.Render(f));
        }
        catch (Exception e)
        {
            Console.WriteLine($"[ERROR] {path}: {e.Message}");
        }
    }

    // --------------------------------------------------------------------------
    // Main folder processing (or a single explicitly given .trc file)
    // --------------------------------------------------------------------------
    private static void ProcessFolder(string inputDir, string? singleFile, string outputDir)
    {
        List<string> trcFiles;
        try
        {
            trcFiles = singleFile != null
                ? new List<string> { singleFile }
                : Directory.GetFiles(inputDir, "*.trc").OrderBy(f => f, StringComparer.Ordinal).ToList();
        }
        catch (Exception e)
        {
            Console.WriteLine($"[ERROR] Failed to enumerate .trc files in '{inputDir}': {e.Message}");
            return;
        }
        if (trcFiles.Count == 0)
        {
            Console.WriteLine($"No .trc files found in folder '{inputDir}'");
            return;
        }

        var summaryRows = new List<string[]>();
        var sectionRows = new List<string[]>();
        var eventRows = new List<string[]>();

        foreach (string filepath in trcFiles)
        {
            string filename = Path.GetFileName(filepath);
            try
            {
                var fibers = Analyzer.AnalyzeFile(filepath);
                if (fibers.Count == 0)
                    Console.WriteLine($"[WARN] {filename}: no fibers found in the file (OtdrData/Fibers).");

                foreach (var f in fibers)
                {
                    foreach (var s in f.Sections)
                        sectionRows.Add(new[]
                        {
                            filename, f.FiberId, s.Index.ToString(), s.Role,
                            Csv.Fmt(s.StartM, 1), Csv.Fmt(s.EndM, 1),
                            Csv.Fmt(s.S13.LengthM, 1), Csv.Fmt(s.S13.LossDb, 3), Csv.Fmt(s.S13.AttenDbKm, 3),
                            Csv.Fmt(s.S15.LengthM, 1), Csv.Fmt(s.S15.LossDb, 3), Csv.Fmt(s.S15.AttenDbKm, 3),
                            s.Verdict, string.Join("; ", s.Reasons),
                        });

                    foreach (var e in f.Events)
                        eventRows.Add(new[]
                        {
                            filename, f.FiberId, e.Index.ToString(), e.Role, Csv.Fmt(e.PosM, 1),
                            Csv.Fmt(e.L13, 3), Csv.Fmt(e.R13, 1), Csv.Fmt(e.L15, 3), Csv.Fmt(e.R15, 1),
                            e.Verdict, string.Join("; ", e.Reasons),
                        });

                    summaryRows.Add(new[]
                    {
                        filename, f.FiberId,
                        Csv.Fmt(f.Len13, 1), Csv.Fmt(f.Loss13, 3), Csv.Fmt(f.Att13, 3),
                        Csv.Fmt(f.Len15, 1), Csv.Fmt(f.Loss15, 3), Csv.Fmt(f.Att15, 3),
                        Csv.Fmt(f.QcLoss13, 3), Csv.Fmt(f.QcLoss15, 3),
                        f.Verdict, string.Join(" | ", f.Fails), string.Join(" | ", f.Warnings),
                    });

                    Console.WriteLine($"[OK] {filename} [{f.FiberId}] -> 1310: {f.Len13:F1} m | 1550: {f.Len15:F1} m | Verdict: {f.Verdict}");
                    foreach (string w in f.Warnings)
                        Console.WriteLine($"[WARN] {filename} [{f.FiberId}]: {w}");
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"[ERROR] {filename}: {e.Message}");
            }
        }

        TrySaveCsv(outputDir, "summary_fibers.csv", new[]
        {
            "File", "Fiber (ID)",
            "Length 1310 (m)", "Loss 1310 (dB)", "Attenuation 1310 (dB/km)",
            "Length 1550 (m)", "Loss 1550 (dB)", "Attenuation 1550 (dB/km)",
            "QC connector 1310 (dB)", "QC connector 1550 (dB)",
            "Verdict", "Rejection reasons", "Warnings",
        }, summaryRows);

        TrySaveCsv(outputDir, "sections_detail.csv", new[]
        {
            "File", "Fiber (ID)", "Section #", "Role", "Start (m)", "End (m)",
            "Length 1310 (m)", "Loss 1310 (dB)", "Attenuation 1310 (dB/km)",
            "Length 1550 (m)", "Loss 1550 (dB)", "Attenuation 1550 (dB/km)",
            "Verdict", "Reason",
        }, sectionRows);

        TrySaveCsv(outputDir, "events_detail.csv", new[]
        {
            "File", "Fiber (ID)", "Event #", "Bench role", "Position (m)",
            "Loss 1310 (dB)", "Reflection 1310 (dB)", "Loss 1550 (dB)",
            "Reflection 1550 (dB)", "Verdict", "Reason",
        }, eventRows);

        Console.WriteLine($"\nDone! Created tables in '{outputDir}': summary_fibers.csv, sections_detail.csv, events_detail.csv");
    }

    // CSV writing must not crash the program (e.g. when the file is open in Excel)
    private static void TrySaveCsv(string outputDir, string filename, string[] headers, List<string[]> rows)
    {
        try
        {
            Csv.SaveCsv(Path.Combine(outputDir, filename), headers, rows);
        }
        catch (Exception e)
        {
            Console.WriteLine($"[ERROR] Failed to write '{filename}': {e.Message}");
        }
    }
}
