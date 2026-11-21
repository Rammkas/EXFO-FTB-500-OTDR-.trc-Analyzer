// ==============================================================================
// Analyzer.cs — semantic analysis of reflectograms: turns a .trc tree into a
// QC (acceptance-test) verdict.
//
// MEASUREMENT BENCH (the setup this logic was written for):
//   OTDR port -> fixed launcher connector -> buffer spool (~1 km)
//   -> QC connector (the fiber under test is plugged in here) -> fiber under test
// Each measurement is taken at two wavelengths: 1310 nm and 1550 nm.
//
// HOW EXFO DATA LOOKS (confirmed on reference files, see --dump-keys):
// - A Trace contains an EventTable with keys Event0, Event1, ... (NUMBERED FROM
//   ZERO); entries alternate between point events (have Type/Reflectance fields)
//   and linear sections LMA (Position/Length/Loss/CursorBPosition, no Type).
// - Example of a real trace (9076.trc, 1310 nm):
//     Event0 = port connector (Position=0, Loss=NaN->null)
//     Event1 = LMA of the buffer spool (0..1010.93 m, Loss=0.328 dB)
//     Event2 = QC connector (Loss=0.541 dB)  <- checked by check_qc_connector
//     Event3 = LMA of the fiber under test (1010.93..8349.28 m, Loss=2.364 dB)
//     Event4 = end of fiber
// - IMPORTANT ATTENUATION SEMANTICS: EXFO does NOT store a measured dB/km
//   coefficient in the EventTable. FiberSectionCharacteristics holds TEMPLATE
//   SETTINGS (TypicalFiberAttenuation); SpansLoss/SpansLength is a total over all
//   spans including connectors. However, EXFO attributes connector losses to point
//   events, and LMAs lie BETWEEN events, so section attenuation = Loss/Length is a
//   correct estimate. If some other firmware exposes an explicit attenuation field
//   (AttenuationKeys below), it is used instead of the computed one.
//
// VERDICT LOGIC:
// 1) 1310 and 1550 sections are matched BY START COORDINATE (not by index) — an
//    extra/missing section on one wavelength does not shift the pairs.
// 2) The first section whose length falls in the buffer_min_m..buffer_max_m window
//    is tagged BUFFER and excluded from the fiber-under-test (DUT) length/loss.
// 3) DUT sections: attenuation of each wavelength is compared with
//    aten_1310_max / aten_1550_max.
// 4) Point events get a role: port (position < FrontPortMaxPosM), end of fiber
//    (last event), QC connector (position == buffer end +- tolerance; its Loss is
//    checked when check_qc_connector=True), otherwise a splice (splice_loss_max
//    threshold).
// 5) The fiber is FAIL if any check fails; reasons are written to the CSV.
// 6) Measuring WITHOUT a buffer spool (fiber plugged directly into the fixed
//    launcher) is a supported mode: set has_buffer_spool=False, then every section
//    counts as the fiber under test and the QC-connector columns stay empty. The
//    trace itself does not reveal "bufferlessness" — when the setting contradicts
//    the trace the Analyzer adds Warnings (a separate CSV column):
//      - flag True but no buffer-like section -> "set has_buffer_spool=False";
//      - flag True and the whole trace swallowed as buffer (DUT length 0) ->
//        dangerous case: a 800..1200 m fiber under test was treated as a buffer,
//        verdict PASS on empty data — the warning catches it.
//    The opposite ambiguous case (measured WITH a buffer while
//    has_buffer_spool=False) is not covered by a warning: the buffer joins the DUT
//    and usually fails the attenuation limit on its own (SMF ~0.33 dB/km vs the
//    0.36 threshold).
// ==============================================================================
using System.Globalization;
using System.Text;

namespace ExfoTrcAnalyzer;

// ==============================================================================
// REFLECTOGRAM ANALYSIS: traces -> sections/events -> PASS/FAIL verdict
// ==============================================================================
public sealed class Section
{
    public double? StartM;
    public double? EndM;
    public double? LengthM;
    public double? LossDb;
    public double? AttenDbKm;
    public bool AttenFromExplicitField; // true = taken from an explicit tree field, false = computed as Loss/Length
}

public sealed class PointEvent
{
    public double? PosM;
    public double? LossDb;
    public double? ReflDb;
    public string Comment = "";
}

public sealed class TraceMetrics
{
    public int WavelengthNm;
    public List<Section> Sections = new();
    public List<PointEvent> Events = new();
}

public sealed class SectionPair
{
    public Section S13 = new();
    public Section S15 = new();
}

public sealed class EvPair
{
    public PointEvent? E13;
    public PointEvent? E15;
}

public sealed class SectionAnalysis
{
    public int Index;
    public string Role = "";
    public string Verdict = "PASS";
    public double? StartM;
    public double? EndM;
    public Section S13 = new();
    public Section S15 = new();
    public List<string> Reasons = new();
}

public sealed class EventAnalysis
{
    public int Index;
    public string Role = "";
    public string Verdict = "PASS";
    public double PosM;
    public double? L13, R13, L15, R15;
    public List<string> Reasons = new();
}

public sealed class FiberAnalysis
{
    public string FiberId = "";
    public List<SectionAnalysis> Sections = new();
    public List<EventAnalysis> Events = new();
    public double Len13, Loss13, Len15, Loss15;
    public double? Att13, Att15;
    public double? QcLoss13, QcLoss15;
    public string Verdict = "PASS";
    public List<string> Fails = new();
    public List<string> Warnings = new(); // warnings that do not affect the verdict
}

public static class Analyzer
{
    // An event at a coordinate < FrontPortMaxPosM is treated as the OTDR port —
    // the fixed launcher connector of the bench in front of the buffer spool.
    public const double FrontPortMaxPosM = 5.0;

    // Standard measurement wavelengths of the QC bench
    public const int Wavelength1310Nm = 1310;
    public const int Wavelength1550Nm = 1550;

    // Prefix of EXFO event-table keys: "Event0", "Event1", ... (numbered from zero)
    public const string EventKeyPrefix = "Event";
    private static readonly System.Text.RegularExpressions.Regex EventKeyRegex =
        new($"^{EventKeyPrefix}\\d+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private const double MetersPerKm = 1000.0;     // meters per kilometer — for dB/km attenuation
    private const double NanometersPerMeter = 1e9; // EXFO stores wavelength in meters (1.31E-06)

    // --dump-keys output limits
    private const int TraceValuePreviewMaxLength = 400; // max preview length of one key's value
    private const int StringPreviewMaxLength = 40;      // max string length in a preview
    private const int PreviewMaxDepth = 2;              // max dictionary nesting depth in a preview

    // Explicit attenuation fields in the EXFO tree (checked before Loss/Length).
    // Exact names are confirmed with: exfo_trc_analyzer --dump-keys <file.trc>
    private static readonly string[] AttenuationKeys =
    {
        "Attenuation",
        "AttenuationCoefficient",
        "AttenuationDbPerKm",
        "AttenuationPerKm",
        "LMAAttenuation",
    };

    // --------------------------------------------------------------------------
    // Full file analysis: .trc -> list of fibers with verdicts
    // --------------------------------------------------------------------------
    public static List<FiberAnalysis> AnalyzeFile(string filepath)
    {
        byte[] stream = TrcParser.DecompressTrc(filepath);
        var tree = TrcParser.FindRootTree(stream);

        var fibers = TrcParser.AsDict(
            TrcParser.Get(tree, "OtdrData") is Dictionary<string, object?> otdr
                ? TrcParser.Get(otdr, "Fibers")
                : null);

        string baseName = Path.GetFileNameWithoutExtension(filepath);
        var result = new List<FiberAnalysis>();
        foreach (var fEntry in fibers)
        {
            if (fEntry.Value is Dictionary<string, object?> fObj)
                result.Add(AnalyzeFiber(fObj, baseName));
        }
        return result;
    }

    public static FiberAnalysis AnalyzeFiber(Dictionary<string, object?> fObj, string baseName)
    {
        var res = new FiberAnalysis();

        res.FiberId = TrcParser.ValueToString(TrcParser.Get(fObj, "Color"));
        if (string.IsNullOrEmpty(res.FiberId))
            res.FiberId = TrcParser.ValueToString(TrcParser.Get(fObj, "Identifier"));
        if (string.IsNullOrEmpty(res.FiberId))
            res.FiberId = baseName;

        var byWl = new Dictionary<int, TraceMetrics>();
        var traces = TrcParser.AsDict(TrcParser.Get(fObj, "Traces"));
        foreach (var tEntry in traces)
        {
            if (tEntry.Value is Dictionary<string, object?> tObj && tObj.ContainsKey("Wavelength"))
            {
                var m = ExtractTraceMetrics(tObj);
                byWl[m.WavelengthNm] = m;
            }
        }

        var t13 = byWl.TryGetValue(Wavelength1310Nm, out var m13) ? m13 : null;
        var t15 = byWl.TryGetValue(Wavelength1550Nm, out var m15) ? m15 : null;

        // 1310/1550 sections are matched by coordinate, not by index: an
        // extra/missing section on one wavelength does not shift the pairs.
        var pairs = MatchSectionsByPosition(
            t13?.Sections ?? new List<Section>(),
            t15?.Sections ?? new List<Section>(),
            Config.MatchToleranceM);

        double bufferEndM = 0.0;

        for (int idx = 0; idx < pairs.Count; idx++)
        {
            var p = pairs[idx];

            double len13 = p.S13.LengthM ?? 0.0;
            double len15 = p.S15.LengthM ?? 0.0;
            // the buffer window check uses the 1550 length; 0 means "no data at this wavelength"
            double checkLen = len15 > 0 ? len15 : len13;

            double? startM = p.S15.StartM ?? p.S13.StartM;
            // 0 in CursorBPosition = EXFO did not fix the section end — take it from the other wavelength
            double? endM = (p.S15.EndM == null || p.S15.EndM == 0) ? p.S13.EndM : p.S15.EndM;
            double? att13 = p.S13.AttenDbKm;
            double? att15 = p.S15.AttenDbKm;

            // only the first section of the trace (idx == 0) can be the buffer spool
            bool isBuffer =
                idx == 0 &&
                Config.HasBufferSpool &&
                Config.BufferMinM <= checkLen &&
                checkLen <= Config.BufferMaxM;

            var sa = new SectionAnalysis
            {
                Index = idx + 1,
                S13 = p.S13,
                S15 = p.S15,
                StartM = startM,
                EndM = endM,
            };
            var reasons = new List<string>();

            if (isBuffer)
            {
                sa.Role = "Buffer spool";
                // 0 in endM = "no end data" — treat the buffer as equal to the section length
                bufferEndM = (endM == null || endM == 0) ? checkLen : endM.Value;
                sa.Verdict = "BUFFER";
            }
            else
            {
                sa.Role = "Fiber under test";
                res.Len13 += len13;
                res.Len15 += len15;
                if (p.S13.LossDb != null)
                    res.Loss13 += p.S13.LossDb.Value;
                if (p.S15.LossDb != null)
                    res.Loss15 += p.S15.LossDb.Value;

                if (att13 != null && att13 > Config.Atten1310Max)
                    reasons.Add($"Attenuation 1310 > {Config.Atten1310Max} ({att13:F3})");
                if (att15 != null && att15 > Config.Atten1550Max)
                    reasons.Add($"Attenuation 1550 > {Config.Atten1550Max} ({att15:F3})");

                sa.Verdict = reasons.Count > 0 ? "FAIL" : "PASS";
                if (reasons.Count > 0)
                    res.Fails.Add($"Section {idx + 1}: {string.Join(", ", reasons)}");
            }

            sa.Reasons = reasons;
            res.Sections.Add(sa);
        }

        var matchedEv = MatchEventsByPosition(
            t13?.Events ?? new List<PointEvent>(),
            t15?.Events ?? new List<PointEvent>(),
            Config.MatchToleranceM);

        for (int evIdx = 0; evIdx < matchedEv.Count; evIdx++)
        {
            var (e13, e15) = (matchedEv[evIdx].E13, matchedEv[evIdx].E15);
            double posM = e15?.PosM ?? e13?.PosM ?? 0.0;
            double? l13 = e13?.LossDb, l15 = e15?.LossDb;
            double? r13 = e13?.ReflDb, r15 = e15?.ReflDb;

            var ea = new EventAnalysis
            {
                Index = evIdx + 1,
                PosM = posM,
                L13 = l13,
                R13 = r13,
                L15 = l15,
                R15 = r15,
            };
            var reasons = new List<string>();
            string role;

            if (evIdx == 0 && posM < FrontPortMaxPosM)
            {
                role = "OTDR port";
            }
            else if (evIdx == matchedEv.Count - 1)
            {
                role = "End of fiber";
            }
            // bufferEndM == 0 means "no buffer spool found" — there is nowhere to look for the QC connector
            else if (bufferEndM > 0 && Math.Abs(posM - bufferEndM) <= Config.MatchToleranceM)
            {
                role = "QC connector (input)";
                res.QcLoss13 = l13;
                res.QcLoss15 = l15;

                // Real QC input-connector check (only active when check_qc_connector=True)
                if (Config.CheckQcConnector)
                {
                    if (l13 != null && l13 > Config.QcConnectorLossMax)
                        reasons.Add($"QC connector loss 1310 ({l13:F3} > {Config.QcConnectorLossMax} dB)");
                    if (l15 != null && l15 > Config.QcConnectorLossMax)
                        reasons.Add($"QC connector loss 1550 ({l15:F3} > {Config.QcConnectorLossMax} dB)");
                }
            }
            else
            {
                role = "Splice / internal event";
                if (l13 != null && l13 > Config.SpliceLossMax)
                    reasons.Add($"Loss 1310 ({l13:F3} > {Config.SpliceLossMax} dB)");
                if (l15 != null && l15 > Config.SpliceLossMax)
                    reasons.Add($"Loss 1550 ({l15:F3} > {Config.SpliceLossMax} dB)");
            }

            ea.Role = role;
            ea.Verdict = reasons.Count > 0 ? "FAIL" : "PASS";
            if (reasons.Count > 0)
                res.Fails.Add($"{role} {posM:F1} m: {string.Join(", ", reasons)}");
            ea.Reasons = reasons;
            res.Events.Add(ea);
        }

        // Total specific attenuation of the DUT = total loss / total length in km
        res.Att13 = res.Len13 > 0 ? res.Loss13 / (res.Len13 / MetersPerKm) : null;
        res.Att15 = res.Len15 > 0 ? res.Loss15 / (res.Len15 / MetersPerKm) : null;

        // Insurance against a wrong has_buffer_spool setting (a measurement with a
        // buffer and one without it are indistinguishable from trace data alone —
        // only the bench configuration decides):
        if (Config.HasBufferSpool)
        {
            bool bufferFound = res.Sections.Any(s => s.Verdict == "BUFFER");
            if (!bufferFound)
                res.Warnings.Add(
                    "has_buffer_spool=True but no buffer-like section found — this may be a measurement " +
                    "without a buffer spool: set has_buffer_spool=False (otherwise the buffer, if present, " +
                    "would be counted as part of the fiber under test).");
            else if (res.Len13 <= 0 && res.Len15 <= 0)
                res.Warnings.Add(
                    "all sections are tagged as buffer — fiber-under-test length is 0 " +
                    "(the fiber under test may have been mistaken for a buffer): check has_buffer_spool.");
        }

        res.Verdict = res.Fails.Count > 0 ? "FAIL" : "PASS";
        return res;
    }

    // --------------------------------------------------------------------------
    // Splitting linear fiber sections and point events
    // --------------------------------------------------------------------------
    public static TraceMetrics ExtractTraceMetrics(Dictionary<string, object?> traceObj)
    {
        // EXFO stores wavelength in meters (1.31E-06); 0 = wavelength not specified
        double wlM = TrcParser.AsDouble(TrcParser.Get(traceObj, "Wavelength")) ?? 0.0;
        int wlNm = wlM != 0 ? (int)Math.Round(wlM * NanometersPerMeter) : 0;

        var evTable = TrcParser.AsDict(TrcParser.Get(traceObj, "EventTable"));
        var eventKeys = evTable.Keys
            .Where(k => EventKeyRegex.IsMatch(k))
            .OrderBy(k => long.Parse(k.AsSpan(EventKeyPrefix.Length))) // "Event" -> the number at the end of the key
            .ToList();

        var sections = new List<Section>();
        var pointEvents = new List<PointEvent>();

        foreach (string key in eventKeys)
        {
            var evObj = evTable[key] as Dictionary<string, object?>;
            if (evObj == null)
                continue;

            bool isPointEvent =
                evObj.ContainsKey("Reflectance") ||
                evObj.ContainsKey("PeakReflectionToRbs") ||
                evObj.ContainsKey("Type");

            double lengthM = TrcParser.AsDouble(TrcParser.Get(evObj, "Length")) ?? 0.0;

            // linear section (LMA): not a point event and has a non-zero length
            if (!isPointEvent && lengthM > 0)
            {
                double? lossDb = TrcParser.AsDouble(TrcParser.Get(evObj, "Loss"));
                double posStart = TrcParser.AsDouble(TrcParser.Get(evObj, "Position")) ?? 0.0;
                double? posEnd = evObj.ContainsKey("CursorBPosition")
                    ? TrcParser.AsDouble(TrcParser.Get(evObj, "CursorBPosition"))
                    : posStart + lengthM;

                // Attenuation comes from an explicit tree field when present;
                // otherwise it is computed as Loss/Length (an estimate that includes
                // losses at section boundaries — be careful with short sections).
                double? attenDbKm = null;
                bool explicitAtten = false;
                foreach (string k in AttenuationKeys)
                {
                    var a = TrcParser.AsDouble(TrcParser.Get(evObj, k));
                    if (a != null)
                    {
                        attenDbKm = a;
                        explicitAtten = true;
                        break;
                    }
                }
                if (!explicitAtten && lossDb != null)
                    attenDbKm = lossDb.Value / (lengthM / MetersPerKm);

                sections.Add(new Section
                {
                    StartM = posStart,
                    EndM = posEnd,
                    LengthM = lengthM,
                    LossDb = lossDb,
                    AttenDbKm = attenDbKm,
                    AttenFromExplicitField = explicitAtten,
                });
            }
            else
            {
                pointEvents.Add(new PointEvent
                {
                    PosM = TrcParser.AsDouble(TrcParser.Get(evObj, "Position")) ?? 0.0,
                    LossDb = TrcParser.AsDouble(TrcParser.Get(evObj, "Loss")),
                    ReflDb = TrcParser.AsDouble(TrcParser.Get(evObj, "Reflectance")),
                    Comment = TrcParser.Get(evObj, "Comment") as string ?? "",
                });
            }
        }

        return new TraceMetrics
        {
            WavelengthNm = wlNm,
            Sections = sections,
            Events = pointEvents,
        };
    }

    // --------------------------------------------------------------------------
    // Matching 1310/1550 sections by start coordinate
    // --------------------------------------------------------------------------
    public static List<SectionPair> MatchSectionsByPosition(
        List<Section> sec1310, List<Section> sec1550, double toleranceM)
    {
        var pairs = new List<SectionPair>();
        var used1310 = new HashSet<int>();

        foreach (var s15 in sec1550)
        {
            double start15 = s15.StartM ?? 0.0;
            int bestIdx = -1; // -1 = no matching section found
            double bestDiff = toleranceM;

            for (int idx = 0; idx < sec1310.Count; idx++)
            {
                if (used1310.Contains(idx))
                    continue;
                double diff = Math.Abs((sec1310[idx].StartM ?? 0.0) - start15);
                if (diff <= bestDiff)
                {
                    bestDiff = diff;
                    bestIdx = idx;
                }
            }

            if (bestIdx >= 0)
            {
                used1310.Add(bestIdx);
                pairs.Add(new SectionPair { S13 = sec1310[bestIdx], S15 = s15 });
            }
            else
            {
                pairs.Add(new SectionPair { S13 = new Section(), S15 = s15 });
            }
        }

        for (int idx = 0; idx < sec1310.Count; idx++)
        {
            if (!used1310.Contains(idx))
                pairs.Add(new SectionPair { S13 = sec1310[idx], S15 = new Section() });
        }

        pairs = pairs.OrderBy(p => PairStart(p)).ToList();
        return pairs;

        static double PairStart(SectionPair p)
            => Math.Min(p.S13.StartM ?? double.MaxValue, p.S15.StartM ?? double.MaxValue);
    }

    // --------------------------------------------------------------------------
    // Matching 1310 and 1550 nm events by their coordinate
    // --------------------------------------------------------------------------
    public static List<EvPair> MatchEventsByPosition(
        List<PointEvent> evList1310, List<PointEvent> evList1550, double toleranceM)
    {
        var matched = new List<EvPair>();
        var used1310 = new HashSet<int>();

        foreach (var e15 in evList1550)
        {
            double pos15 = e15.PosM ?? 0.0;
            int bestIdx = -1;
            double bestDiff = toleranceM;

            for (int idx = 0; idx < evList1310.Count; idx++)
            {
                if (used1310.Contains(idx))
                    continue;
                double diff = Math.Abs((evList1310[idx].PosM ?? 0.0) - pos15);
                if (diff <= bestDiff)
                {
                    bestDiff = diff;
                    bestIdx = idx;
                }
            }

            if (bestIdx >= 0)
            {
                used1310.Add(bestIdx);
                matched.Add(new EvPair { E13 = evList1310[bestIdx], E15 = e15 });
            }
            else
            {
                matched.Add(new EvPair { E13 = null, E15 = e15 });
            }
        }

        for (int idx = 0; idx < evList1310.Count; idx++)
        {
            if (!used1310.Contains(idx))
                matched.Add(new EvPair { E13 = evList1310[idx], E15 = null });
        }

        matched = matched
            .OrderBy(p => p.E15?.PosM ?? p.E13?.PosM ?? 0.0)
            .ToList();

        return matched;
    }

    // --------------------------------------------------------------------------
    // Format diagnostics: list of EventTable keys with values (--dump-keys).
    // Lets you confirm the real attenuation field names in EXFO .trc files.
    // --------------------------------------------------------------------------
    public static string DumpTrc(string filepath)
    {
        byte[] stream = TrcParser.DecompressTrc(filepath);
        var tree = TrcParser.FindRootTree(stream);

        var sb = new StringBuilder();
        sb.AppendLine($"File: {Path.GetFileName(filepath)} (decompressed {stream.Length} bytes)");
        sb.AppendLine($"OtdrFile root: {string.Join(", ", tree.Keys)}");

        var fibers = TrcParser.AsDict(
            TrcParser.Get(tree, "OtdrData") is Dictionary<string, object?> otdr
                ? TrcParser.Get(otdr, "Fibers")
                : null);

        foreach (var fEntry in fibers)
        {
            if (fEntry.Value is not Dictionary<string, object?> fObj)
                continue;

            sb.AppendLine();
            sb.AppendLine($"=== Fiber '{fEntry.Key}' Color='{TrcParser.ValueToString(TrcParser.Get(fObj, "Color"))}' ===");
            sb.AppendLine($"  Fiber keys: {string.Join(", ", fObj.Keys)}");

            var traces = TrcParser.AsDict(TrcParser.Get(fObj, "Traces"));
            foreach (var tEntry in traces)
            {
                if (tEntry.Value is not Dictionary<string, object?> tObj)
                    continue;

                double wlM = TrcParser.AsDouble(TrcParser.Get(tObj, "Wavelength")) ?? 0.0;
                sb.AppendLine($"  --- Trace '{tEntry.Key}' (Wavelength={wlM:G6} -> {(int)Math.Round(wlM * NanometersPerMeter)} nm) ---");
                sb.AppendLine($"      Trace keys: {string.Join(", ", tObj.Keys)}");

                var evTable = TrcParser.AsDict(TrcParser.Get(tObj, "EventTable"));
                var eventKeys = evTable.Keys
                    .Where(k => EventKeyRegex.IsMatch(k))
                    .OrderBy(k => long.Parse(k.AsSpan(EventKeyPrefix.Length)))
                    .ToList();

                foreach (string key in eventKeys)
                {
                    if (evTable[key] is not Dictionary<string, object?> eObj)
                        continue;
                    sb.Append($"      {key}: ");
                    foreach (var kv in eObj)
                        sb.Append($"{kv.Key}={PreviewValue(kv.Value)}; ");
                    sb.AppendLine();
                }

                // scalar/service trace keys (without the huge sample arrays)
                foreach (var kv in tObj)
                {
                    if (kv.Key is "EventTable" or "RawSamples" or "AcquisitionDatas" or "ModuleInformation" or "SoftwareContext")
                        continue;
                    sb.AppendLine($"      [{kv.Key}] = {Truncate(PreviewValue(kv.Value), TraceValuePreviewMaxLength)}");
                }
            }
        }

        return sb.ToString();
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";

    private static string PreviewValue(object? v, int depth = 0)
    {
        if (v is Dictionary<string, object?> d)
        {
            if (depth >= PreviewMaxDepth)
                return "{{...}}";
            return "{" + string.Join("; ", d.Select(kv => $"{kv.Key}={PreviewValue(kv.Value, depth + 1)}")) + "}";
        }
        return v switch
        {
            double dv => dv.ToString("G6", CultureInfo.InvariantCulture),
            int iv => iv.ToString(CultureInfo.InvariantCulture),
            bool bv => bv ? "True" : "False",
            string sv => "\"" + (sv.Length > StringPreviewMaxLength ? sv[..StringPreviewMaxLength] + "…" : sv) + "\"",
            null => "null",
            _ => v.ToString() ?? "",
        };
    }
}
