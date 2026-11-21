// ==============================================================================
// Config.cs — QC bench settings and rejection thresholds (PASS/FAIL).
//
// WHAT IT DOES: holds the default values and can override them from an
// otdr_config.txt file. File format: "parameter = value"; lines starting with
// # ; // are comments; numbers accept both '.' and ',' as decimal separator;
// bool accepts True/False/1/0/yes/no/да/нет etc. The file is looked for first in
// the folder with the .trc files, then next to the EXE. If the file is missing —
// built-in defaults are used and a template file (ConfigTemplate) is created in
// the .trc folder for manual editing in Notepad.
//
// PARAMETER MEANING (defaults match a bench with a ~1 km buffer and SMF fiber):
//   has_buffer_spool       — the first trace section (~1 km) is the bench buffer
//                            spool and is not part of the fiber under test.
//                            MEASURING WITHOUT A BUFFER (fiber plugged directly
//                            into the fixed launcher): must be False — then the
//                            whole trace counts as the fiber under test and the
//                            QC-connector columns stay empty. If left True while
//                            measuring a direct 800..1200 m fiber, that fiber is
//                            silently treated as a buffer and excluded from the
//                            check (report: length 0 m, PASS on empty data). The
//                            Analyzer warns about such mismatches; the trace data
//                            itself cannot reveal "bufferlessness" — this is the
//                            setting's responsibility alone.
//   buffer_min_m/max_m     — buffer length window used to recognize it (800..1200
//                            m; the actual buffers in the reference files are ~1010 m);
//   atten_1310_max         — attenuation limit for the fiber under test at 1310 nm,
//                            dB/km (0.36; typical measured values on references 0.32-0.33);
//   atten_1550_max         — same for 1550 nm, dB/km (0.22; measured ~0.18);
//   splice_loss_max        — loss limit for internal splices, dB (0.30);
//   check_qc_connector     — whether to check the QC input connector loss (default
//                            False: only reported; when True it affects the verdict);
//   qc_connector_loss_max  — QC connector loss threshold, dB (0.50; values measured
//                            on the references are 0.13..0.54 — align this threshold
//                            with your product spec!);
//   match_tolerance_m      — tolerance for matching same-position events/sections
//                            between 1310 and 1550 nm, meters (5.0).
//
// Config is a static class (the application is single-threaded). ResetDefaults()
// exists so tests do not inherit values from a previous test.
// ==============================================================================
using System.Globalization;
using System.Text;

namespace ExfoTrcAnalyzer;

// ==============================================================================
// QC BENCH SETTINGS AND REJECTION THRESHOLDS (PASS / FAIL)
// Overridden by otdr_config.txt (in the .trc folder or next to the EXE)
// ==============================================================================
public static class Config
{
    public static bool HasBufferSpool = true;       // True = the first section (~1 km) is the buffer spool
    public static double BufferMinM = 800.0;        // min buffer spool length (m)
    public static double BufferMaxM = 1200.0;       // max buffer spool length (m)
    public static double Atten1310Max = 0.36;       // max attenuation of the fiber under test at 1310 nm (dB/km)
    public static double Atten1550Max = 0.22;       // max attenuation of the fiber under test at 1550 nm (dB/km)
    public static double SpliceLossMax = 0.30;      // max loss of internal splices inside the coil (dB)
    public static bool CheckQcConnector = false;    // whether a bad QC input connector rejects the fiber
    public static double QcConnectorLossMax = 0.50; // max QC input connector loss (dB), see check_qc_connector
    public static double MatchToleranceM = 5.0;     // tolerance for matching 1310/1550 events/sections (m)

    public const string ConfigFileName = "otdr_config.txt";

    public const string ConfigTemplate =
        "# ================================================================================\r\n" +
        "# EXFO REFLECTOGRAM ANALYZER (.trc) SETTINGS — CONFIG BLOCK ANALOGUE\r\n" +
        "# The file is read by the program automatically. Format: parameter = value.\r\n" +
        "# Lines starting with # are comments. Values can be edited in Notepad.\r\n" +
        "# Deleting this file falls back to the built-in defaults.\r\n" +
        "# ================================================================================\r\n" +
        "\r\n" +
        "# Is the first section (~1 km) a buffer spool? (True / False)\r\n" +
        "# Set False when measuring a fiber WITHOUT a buffer spool.\r\n" +
        "has_buffer_spool = True\r\n" +
        "\r\n" +
        "# Min and max buffer spool length (m)\r\n" +
        "buffer_min_m = 800.0\r\n" +
        "buffer_max_m = 1200.0\r\n" +
        "\r\n" +
        "# Max attenuation of the fiber under test (dB/km)\r\n" +
        "atten_1310_max = 0.36\r\n" +
        "atten_1550_max = 0.22\r\n" +
        "\r\n" +
        "# Max loss of internal splices inside the coil (dB)\r\n" +
        "splice_loss_max = 0.30\r\n" +
        "\r\n" +
        "# Reject a bad QC input connector (Event 2)? (True / False)\r\n" +
        "check_qc_connector = False\r\n" +
        "\r\n" +
        "# Max QC input connector loss (dB), active when check_qc_connector = True\r\n" +
        "qc_connector_loss_max = 0.50\r\n" +
        "\r\n" +
        "# Tolerance for matching 1310/1550 nm events and sections by coordinate (m)\r\n" +
        "match_tolerance_m = 5.0\r\n";

    public static void ResetDefaults()
    {
        HasBufferSpool = true;
        BufferMinM = 800.0;
        BufferMaxM = 1200.0;
        Atten1310Max = 0.36;
        Atten1550Max = 0.22;
        SpliceLossMax = 0.30;
        CheckQcConnector = false;
        QcConnectorLossMax = 0.50;
        MatchToleranceM = 5.0;
    }

    // --------------------------------------------------------------------------
    // Reading settings from otdr_config.txt. Looked for in the folder with the
    // .trc files first, then next to the EXE. If the file is missing — defaults,
    // and a template file is created.
    // --------------------------------------------------------------------------
    public static string? Load(string inputDir, Action<string>? log = null)
    {
        log ??= Console.WriteLine;

        string[] candidates =
        {
            Path.Combine(inputDir, ConfigFileName),
            Path.Combine(AppContext.BaseDirectory, ConfigFileName),
        };

        string? configPath = candidates.FirstOrDefault(File.Exists);
        if (configPath == null)
        {
            log($"[CONFIG] {ConfigFileName} not found — using built-in defaults.");
            TryCreateConfigTemplate(inputDir, log);
            return null;
        }

        int applied = 0;
        foreach (string rawLine in File.ReadLines(configPath))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';') || line.StartsWith("//"))
                continue;

            int sep = line.IndexOfAny(new[] { '=', ':' });
            if (sep < 0)
            {
                log($"[CONFIG] Skipped line (no '='): {line}");
                continue;
            }

            string key = line.Substring(0, sep).Trim().ToLowerInvariant();
            string val = line.Substring(sep + 1).Trim();

            switch (key)
            {
                case "has_buffer_spool":
                    ApplyBool(key, val, ref HasBufferSpool, ref applied, log);
                    break;
                case "buffer_min_m":
                    ApplyDouble(key, val, ref BufferMinM, ref applied, log);
                    break;
                case "buffer_max_m":
                    ApplyDouble(key, val, ref BufferMaxM, ref applied, log);
                    break;
                case "atten_1310_max":
                    ApplyDouble(key, val, ref Atten1310Max, ref applied, log);
                    break;
                case "atten_1550_max":
                    ApplyDouble(key, val, ref Atten1550Max, ref applied, log);
                    break;
                case "splice_loss_max":
                    ApplyDouble(key, val, ref SpliceLossMax, ref applied, log);
                    break;
                case "check_qc_connector":
                    ApplyBool(key, val, ref CheckQcConnector, ref applied, log);
                    break;
                case "qc_connector_loss_max":
                    ApplyDouble(key, val, ref QcConnectorLossMax, ref applied, log);
                    break;
                case "match_tolerance_m":
                    ApplyDouble(key, val, ref MatchToleranceM, ref applied, log);
                    break;
                default:
                    log($"[CONFIG] Unknown parameter: {key}");
                    break;
            }
        }

        log($"[CONFIG] Loaded '{Path.GetFileName(configPath)}' (parameters applied: {applied})");
        return configPath;
    }

    public static bool ParseBool(string val, out bool result)
    {
        switch (val.ToLowerInvariant())
        {
            case "true":
            case "1":
            case "yes":
            case "y":
            case "on":            
                result = true;
                return true;
            case "false":
            case "0":
            case "no":
            case "n":
            case "off":            
                result = false;
                return true;
            default:
                result = false;
                return false;
        }
    }

    public static bool ParseDouble(string val, out double result)
        => double.TryParse(val.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out result);

    private static void ApplyBool(string key, string val, ref bool target, ref int applied, Action<string> log)
    {
        if (ParseBool(val, out bool parsed))
        {
            target = parsed;
            applied++;
        }
        else
        {
            log($"[CONFIG] Invalid value '{val}' for {key} (expected True/False) — kept {target}.");
        }
    }

    private static void ApplyDouble(string key, string val, ref double target, ref int applied, Action<string> log)
    {
        if (ParseDouble(val, out double parsed))
        {
            target = parsed;
            applied++;
        }
        else
        {
            log($"[CONFIG] Invalid value '{val}' for {key} (expected a number) — kept {target}.");
        }
    }

    private static void TryCreateConfigTemplate(string inputDir, Action<string> log)
    {
        try
        {
            string target = Path.Combine(inputDir, ConfigFileName);
            File.WriteAllText(target, ConfigTemplate, new UTF8Encoding(true));
            log($"[CONFIG] Created settings file '{target}' — values can be edited in Notepad.");
        }
        catch
        {
            // if it cannot be written (e.g. no permissions) — run with defaults
        }
    }
}
