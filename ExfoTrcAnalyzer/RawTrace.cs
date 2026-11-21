// ==============================================================================
// RawTrace.cs — export of the raw reflectogram CURVE (point-by-point signal
// values) from the RawSamples BinaryRef blobs into a CSV table.
//
// FORMAT FACTS (verified on the эталонные .trc files, e.g. 9076.trc):
//   * each TraceN container holds one BinaryRef child named RawSamples;
//   * the blob is located in the UNPACKED stream right after the node header:
//     data starts at head + NodeHeaderSize and spans ValLen bytes;
//   * samples are Int16 little-endian, ValLen is even and ValLen/2 = sample count
//     (9076.trc: 125,364 B = 62,682 samples per wavelength);
//   * the value scale is ScaleFactor counts per dB (tree field ScaleFactor =
//     1024). Verified: with 1024 counts/dB the decoded trace slopes match the
//     EventTable/GOLD attenuations (0.32 dB/km at 1310, 0.18 dB/km at 1550);
//   * the sample axis is TIME-uniform: sample i corresponds to
//     t_i = RangeStart + i * SamplingPeriod seconds (SamplingPeriod = 3.125e-9 s,
//     i.e. 320 MSa/s). Meters: distance_i = t_i * Range / (RangeEnd - RangeStart).
//     Cross-checked against EventTable positions: the QC-connector reflection
//     peak lands at 1014 m (EventTable 1010.9 m) and the fiber-end peak at
//     8348 m (EventTable 8349.3 m) — the small difference is normal: EXFO
//     refines event positions beyond the raw sample grid.
//
// Blob -> trace association is done by locating each RawSamples node inside
// the child table of its parent TraceN container, then reading the sibling
// scalar nodes (Wavelength, Range, SamplingPeriod, RangeStart, RangeEnd,
// ScaleFactor) of that container.
//
// This module only exports data; it never changes parsing or verdicts.
//
// RUN:  exfo_trc_analyzer.exe --raw-trace <file.trc | folder>
//       (CSV written like the other reports: --source_output chooses the folder)
// ==============================================================================
using System.Globalization;

namespace ExfoTrcAnalyzer;

public static class RawTrace
{
    // Samples are Int16 little-endian (verified fact, see file header)
    private const int BytesPerSample = 2;

    // Fallback dB scale when the ScaleFactor field is absent from the tree
    // (1024 counts per dB is the verified value in checked EXFO files)
    private const int DefaultCountsPerDb = 1024;

    // RawSamples blobs smaller than this are treated as not-a-trace (guards
    // against bogus node headers; real traces have tens of thousands of samples)
    private const int MinTraceBytes = 1024;

    public sealed class TraceCurve
    {
        public string TraceName = "";
        public int WavelengthNm;                 // 0 = unknown
        public short[] Samples = Array.Empty<short>();
        public int CountsPerDb = DefaultCountsPerDb;
        public double SamplingPeriodS;           // 0 = unknown
        public double RangeStartS;
        public double RangeEndS;
        public double RangeM;                    // 0 = unknown

        public int SampleCount => Samples.Length;

        public double TimeS(int i) => RangeStartS + i * SamplingPeriodS;

        // Meters along the fiber; NaN when the trace lacks Range/SamplingPeriod fields
        public double DistanceM(int i)
        {
            if (RangeM <= 0 || SamplingPeriodS <= 0 || RangeEndS <= RangeStartS)
                return double.NaN;
            return TimeS(i) * RangeM / (RangeEndS - RangeStartS);
        }

        public double LevelDb(int i) => Samples[i] / (double)CountsPerDb;
    }

    // --------------------------------------------------------------------------
    // Read all RawSamples curves of a .trc file
    // --------------------------------------------------------------------------
    public static List<TraceCurve> ReadCurves(string filepath)
    {
        byte[] merged = TrcParser.DecompressTrc(filepath);
        var curves = new List<TraceCurve>();

        foreach (var head in FindRawSampleNodes(merged))
        {
            int parent = FindParentContainer(merged, head.Offset);
            var scalars = parent >= 0 ? ReadChildScalars(merged, parent) : new Dictionary<string, TrcParser.NodeHeaderInfo>();

            var curve = new TraceCurve
            {
                TraceName = parent >= 0 ? PathOf(parent, merged) : "unknown",
                WavelengthNm = WavelengthNm(merged, scalars),
                CountsPerDb = (int)(GetDouble(merged, scalars, "ScaleFactor") ?? DefaultCountsPerDb),
                SamplingPeriodS = GetDouble(merged, scalars, "SamplingPeriod") ?? 0,
                RangeStartS = GetDouble(merged, scalars, "RangeStart") ?? 0,
                RangeEndS = GetDouble(merged, scalars, "RangeEnd") ?? 0,
                RangeM = GetDouble(merged, scalars, "Range") ?? 0,
            };

            int dataStart = head.Offset + TrcParser.NodeHeaderSize;
            int count = Math.Min(head.ValLen, merged.Length - dataStart) / BytesPerSample;
            curve.Samples = new short[count];
            for (int i = 0; i < count; i++)
                curve.Samples[i] = BitConverter.ToInt16(merged, dataStart + i * BytesPerSample);
            curves.Add(curve);
        }
        return curves;
    }

    // --------------------------------------------------------------------------
    // Build CSV rows for all curves (house style: Csv.SaveCsv — ';' separator,
    // comma decimal separator, UTF-8 BOM)
    // --------------------------------------------------------------------------
    public static void SaveCsv(string csvFilepath, List<TraceCurve> curves)
    {
        var headers = new[] { "trace_name", "wavelength_nm", "sample_index", "time_s", "distance_m", "raw_int16", "level_db" };
        var rows = new List<string[]>();

        foreach (var c in curves)
            for (int i = 0; i < c.SampleCount; i++)
                rows.Add(new[]
                {
                    c.TraceName,
                    c.WavelengthNm > 0 ? c.WavelengthNm.ToString(CultureInfo.InvariantCulture) : "",
                    i.ToString(CultureInfo.InvariantCulture),
                    Csv.Fmt(c.SamplingPeriodS > 0 ? c.TimeS(i) : null, 12),
                    Csv.Fmt(double.IsNaN(c.DistanceM(i)) ? null : c.DistanceM(i), 3),
                    c.Samples[i].ToString(CultureInfo.InvariantCulture),
                    Csv.Fmt(c.LevelDb(i), 3),
                });

        Csv.SaveCsv(csvFilepath, headers, rows);
    }

    // --------------------------------------------------------------------------
    // Blob discovery
    // --------------------------------------------------------------------------
    // Find BinaryRef nodes named "RawSamples": the name string is located in the
    // stream, then a node header whose nameOffset field points at it is searched
    // (same needle technique as TrcParser.FindRootTree).
    private static List<TrcParser.NodeHeaderInfo> FindRawSampleNodes(byte[] merged)
    {
        var found = new List<TrcParser.NodeHeaderInfo>();
        int nameSearch = 0;
        while (nameSearch < merged.Length)
        {
            int rel = merged.AsSpan(nameSearch).IndexOf("RawSamples\0"u8);
            if (rel < 0)
                break;
            int namePos = nameSearch + rel;

            byte[] needle = BitConverter.GetBytes(namePos);
            int needleSearch = 0;
            while (needleSearch < merged.Length)
            {
                int r = merged.AsSpan(needleSearch).IndexOf(needle);
                if (r < 0)
                    break;
                int pos = needleSearch + r;
                var head = TrcParser.ReadNodeHeader(merged, pos);
                if (head?.Name == "RawSamples" &&
                    head.Type == (int)TrcParser.TrcNodeType.BinaryRef &&
                    head.ValLen >= MinTraceBytes &&
                    head.Offset + TrcParser.NodeHeaderSize + MinTraceBytes <= merged.Length)
                    found.Add(head);
                needleSearch = pos + 1;
            }
            nameSearch = namePos + 1;
        }
        return found;
    }

    // Locate the container whose child table contains the given child offset:
    // the 4-byte child reference must sit at head + NodeHeaderSize + j*ChildRefSize
    // inside a valid Container node.
    private static int FindParentContainer(byte[] merged, int childOffset)
    {
        byte[] needle = BitConverter.GetBytes(childOffset);
        int search = 0;
        while (search < merged.Length)
        {
            int r = merged.AsSpan(search).IndexOf(needle);
            if (r < 0)
                break;
            int slot = search + r; // position of the child reference in some child table

            for (int j = 0; ; j++)
            {
                int head = slot - TrcParser.NodeHeaderSize - j * TrcParser.ChildRefSize;
                if (head <= 0)
                    break;
                var h = TrcParser.ReadNodeHeader(merged, head);
                if (h == null || h.Type != (int)TrcParser.TrcNodeType.Container)
                    continue;
                int valStart = head + TrcParser.NodeHeaderSize;
                int childCount = Math.Max(0, Math.Min(h.ValLen, merged.Length - valStart)) / TrcParser.ChildRefSize;
                if (j < childCount && BitConverter.ToInt32(merged, valStart + j * TrcParser.ChildRefSize) == childOffset)
                    return head;
            }
            search = slot + 1;
        }
        return -1;
    }

    // Scalar/child nodes of a container: name -> header info
    private static Dictionary<string, TrcParser.NodeHeaderInfo> ReadChildScalars(byte[] merged, int containerOffset)
    {
        var result = new Dictionary<string, TrcParser.NodeHeaderInfo>();
        var h = TrcParser.ReadNodeHeader(merged, containerOffset);
        if (h == null || h.Type != (int)TrcParser.TrcNodeType.Container)
            return result;

        int valStart = containerOffset + TrcParser.NodeHeaderSize;
        int childCount = Math.Max(0, Math.Min(h.ValLen, merged.Length - valStart)) / TrcParser.ChildRefSize;
        for (int i = 0; i < childCount; i++)
        {
            int cOff = BitConverter.ToInt32(merged, valStart + i * TrcParser.ChildRefSize);
            if (cOff == TrcParser.NoChildRef)
                continue;
            var ch = TrcParser.ReadNodeHeader(merged, cOff);
            if (ch != null && !result.ContainsKey(ch.Name))
                result[ch.Name] = ch;
        }
        return result;
    }

    // Container name (Trace0, Trace1, ...) — used as trace label
    private static string PathOf(int containerOffset, byte[] merged)
        => TrcParser.ReadNodeHeader(merged, containerOffset)?.Name ?? "unknown";

    private static int WavelengthNm(byte[] merged, Dictionary<string, TrcParser.NodeHeaderInfo> scalars)
    {
        double? w = GetDouble(merged, scalars, "Wavelength"); // stored in meters, e.g. 1.31e-6
        return w is null or <= 0 ? 0 : (int)Math.Round(w.Value * 1e9);
    }

    // Read a Double scalar child value (absent/corrupt -> null)
    private static double? GetDouble(byte[] merged, Dictionary<string, TrcParser.NodeHeaderInfo> scalars, string name)
    {
        if (!scalars.TryGetValue(name, out var ch) || ch.Type != (int)TrcParser.TrcNodeType.Double)
            return null;
        int valStart = ch.Offset + TrcParser.NodeHeaderSize;
        if (ch.ValLen != sizeof(double) || valStart + sizeof(double) > merged.Length)
            return null;
        double v = BitConverter.ToDouble(merged, valStart);
        return double.IsNaN(v) ? null : v; // EXFO encodes "no value" as NaN
    }
}
