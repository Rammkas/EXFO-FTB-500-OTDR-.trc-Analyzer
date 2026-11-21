// ==============================================================================
// MapRenderer.cs — ASCII "map" of the analyzed fiber link.
//
// WHAT IT SHOWS: a scaled horizontal schematic of the measured path
// (OTDR port -> buffer spool -> QC connector -> fiber under test -> end):
//   - a ruler row with meter coordinates and tick marks;
//   - sections drawn as filled segments (buffer and fiber-under-test use
//     different fill characters);
//   - point events drawn as role markers (port / QC connector / splice / end);
//   - a length row under each section and short event names under markers;
//   - detail tables below the schematic with all section and event data for
//     both wavelengths (loss, reflection, attenuation) and DUT totals.
//
// The map is built from FiberAnalysis produced by Analyzer, so it shows exactly
// what the QC verdict logic sees (roles and BUFFER/DUT split are already there).
//
// RUN:  exfo_trc_analyzer.exe --map <file.trc | folder>
// ==============================================================================
using System.Globalization;
using System.Text;

namespace ExfoTrcAnalyzer;

public static class MapRenderer
{
    public const int MapWidth = 96; // schematic width in characters

    // fills for section roles
    private const char FillBuffer = '▒';
    private const char FillFiber = '═';
    private const char FillUnknown = '─';

    // markers for point-event roles
    private const char MarkPort = '▐';
    private const char MarkConnector = '●';
    private const char MarkSplice = '×';
    private const char MarkEnd = '◆';
    private const char MarkDefault = '|';

    // exact role strings produced by Analyzer (kept in sync with Analyzer.cs)
    private const string RoleBuffer = "Buffer spool";
    private const string RoleFiber = "Fiber under test";

    public static string Render(FiberAnalysis f)
    {
        double maxPos = 0.0;
        foreach (var s in f.Sections)
        {
            maxPos = Math.Max(maxPos, s.StartM ?? 0.0);
            maxPos = Math.Max(maxPos, s.EndM ?? 0.0);
        }
        foreach (var e in f.Events)
            maxPos = Math.Max(maxPos, e.PosM);

        var sb = new StringBuilder();
        sb.AppendLine($"=== Link map: fiber '{f.FiberId}' | verdict: {f.Verdict} ===");

        if (maxPos <= 0)
        {
            sb.AppendLine("(no trace positions — nothing to map)");
            return sb.ToString();
        }

        sb.AppendLine($"scale: 1 character ≈ {maxPos / (MapWidth - 1):F1} m");
        sb.AppendLine();

        int Col(double pos) => (int)Math.Round(pos / maxPos * (MapWidth - 1));

        // ---- ruler: coordinate labels + tick row ----
        var tickRow = new char[MapWidth];
        Array.Fill(tickRow, ' ');
        foreach (var s in f.Sections)
        {
            if (s.StartM != null) tickRow[Col(s.StartM.Value)] = '|';
            if (s.EndM != null) tickRow[Col(s.EndM.Value)] = '|';
        }
        foreach (var e in f.Events) tickRow[Col(e.PosM)] = '|';
        tickRow[MapWidth - 1] = '|';

        var labelRow = new char[MapWidth];
        Array.Fill(labelRow, ' ');
        int cursor = 0;
        foreach (var s in f.Sections)
        {
            if (s.StartM == null)
                continue;
            string label = $"{s.StartM.Value:F1}";
            int start = Math.Max(Col(s.StartM.Value), cursor);
            if (start + label.Length >= MapWidth)
                continue; // no room — the tick mark is kept
            label.CopyTo(0, labelRow, start, label.Length);
            cursor = start + label.Length + 1;
        }

        // ---- map row: section fills, then event markers over them ----
        var mapRow = new char[MapWidth];
        Array.Fill(mapRow, ' ');
        foreach (var s in f.Sections)
        {
            if (s.StartM == null || s.EndM == null)
                continue;
            int c1 = Col(s.StartM.Value), c2 = Col(s.EndM.Value);
            if (c2 < c1)
                (c1, c2) = (c2, c1);
            char fill = s.Verdict == "BUFFER" ? FillBuffer
                      : s.Role == RoleFiber ? FillFiber
                      : FillUnknown;
            for (int c = c1; c <= c2; c++)
                mapRow[c] = fill;
        }
        foreach (var e in f.Events)
            mapRow[Col(e.PosM)] = MarkerFor(e.Role);

        // ---- lengths row: section length centered over its segment ----
        var lenRow = new char[MapWidth];
        Array.Fill(lenRow, ' ');
        foreach (var s in f.Sections)
        {
            if (s.StartM == null || s.EndM == null)
                continue;
            int c1 = Col(s.StartM.Value), c2 = Col(s.EndM.Value);
            int w = c2 - c1 + 1;
            double len = s.S15.LengthM ?? s.S13.LengthM ?? 0.0;
            string label = $"{len:F0} m";
            if (w < label.Length + 2)
                continue; // segment too narrow for a label — details are in the table
            int start = c1 + (w - label.Length) / 2;
            label.CopyTo(0, lenRow, start, label.Length);
        }

        // ---- event names row (short, collision-shifted) ----
        var evRow = new char[MapWidth];
        Array.Fill(evRow, ' ');
        int lastEnd = -1;
        foreach (var e in f.Events)
        {
            string name = ShortEventName(e);
            int c = Col(e.PosM);
            int start = Math.Max(Math.Min(c - name.Length / 2, MapWidth - name.Length), lastEnd + 1);
            if (start + name.Length >= MapWidth)
                continue; // no room — the marker on the map row is enough
            name.CopyTo(0, evRow, start, name.Length);
            lastEnd = start + name.Length - 1;
        }

        sb.AppendLine(new string(labelRow));
        sb.AppendLine(new string(tickRow));
        sb.AppendLine(new string(mapRow));
        sb.AppendLine(new string(lenRow));
        sb.AppendLine(new string(evRow));
        sb.AppendLine();
        sb.AppendLine($"legend: {MarkPort} port  {MarkConnector} QC connector  {MarkSplice} splice  {MarkEnd} end of fiber | {FillBuffer} buffer spool  {FillFiber} fiber under test");

        AppendSectionTable(sb, f);
        AppendEventTable(sb, f);
        AppendTotals(sb, f);
        return sb.ToString();
    }

    private static char MarkerFor(string role) => role switch
    {
        _ when role.Contains("port") => MarkPort,
        _ when role.Contains("QC") => MarkConnector,
        _ when role.Contains("Splice") => MarkSplice,
        _ when role.Contains("End") => MarkEnd,
        _ => MarkDefault,
    };

    private static string ShortEventName(EventAnalysis e) => e.Role switch
    {
        _ when e.Role.Contains("port") => "PORT",
        _ when e.Role.Contains("QC") => "QC",
        _ when e.Role.Contains("Splice") => $"SP{e.Index}",
        _ when e.Role.Contains("End") => "END",
        _ => "?",
    };

    private static void AppendSectionTable(StringBuilder sb, FiberAnalysis f)
    {
        sb.AppendLine();
        sb.AppendLine("Sections:");
        foreach (var s in f.Sections)
        {
            double len13 = s.S13.LengthM ?? 0.0;
            double len15 = s.S15.LengthM ?? 0.0;
            sb.AppendLine(
                $"  #{s.Index} {s.Role} [{s.Verdict}] {Num(s.StartM, 1)} – {Num(s.EndM, 1)} m | " +
                $"1310: {Num(len13, 1)} m, loss {Num(s.S13.LossDb)}, att {Num(s.S13.AttenDbKm)} dB/km | " +
                $"1550: {Num(len15, 1)} m, loss {Num(s.S15.LossDb)}, att {Num(s.S15.AttenDbKm)} dB/km");
            foreach (string r in s.Reasons)
                sb.AppendLine($"      ! {r}");
        }
    }

    private static void AppendEventTable(StringBuilder sb, FiberAnalysis f)
    {
        sb.AppendLine();
        sb.AppendLine("Events:");
        foreach (var e in f.Events)
        {
            sb.AppendLine(
                $"  #{e.Index} {e.Role} @ {e.PosM:F1} m | " +
                $"1310: loss {Num(e.L13)}, refl {Num(e.R13, 1)} | " +
                $"1550: loss {Num(e.L15)}, refl {Num(e.R15, 1)}");
            foreach (string r in e.Reasons)
                sb.AppendLine($"      ! {r}");
        }
    }

    private static void AppendTotals(StringBuilder sb, FiberAnalysis f)
    {
        sb.AppendLine();
        sb.AppendLine(
            $"Fiber under test totals: 1310: {f.Len13:F1} m, {f.Loss13:F3} dB, {Num(f.Att13)} dB/km | " +
            $"1550: {f.Len15:F1} m, {f.Loss15:F3} dB, {Num(f.Att15)} dB/km");
        if (f.QcLoss13 != null || f.QcLoss15 != null)
            sb.AppendLine($"QC connector: 1310: {Num(f.QcLoss13)} dB | 1550: {Num(f.QcLoss15)} dB");
        foreach (string fail in f.Fails)
            sb.AppendLine($"FAIL: {fail}");
        foreach (string w in f.Warnings)
            sb.AppendLine($"WARNING: {w}");
    }

    private static string Num(double? v, int digits = 3)
        => v?.ToString($"F{digits}", CultureInfo.InvariantCulture) ?? "-";

    private static string Num(double v, int digits)
        => v.ToString($"F{digits}", CultureInfo.InvariantCulture);
}
