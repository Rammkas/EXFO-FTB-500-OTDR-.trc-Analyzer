// ==============================================================================
// PlotExport.cs — chart-friendly export of the trace curves for Excel or any
// other plotting tool (CSV only — an open, dependency-free format).
//
// WHAT IT PRODUCES (one file per .trc):
//   trace_plot_<name>.csv — one row per 1 m bin:
//       distance_m;level_db_<wl1>;level_db_<wl2>
//   columns side by side so a chart can be inserted in Excel (or any tool)
//   directly from the columns, distance on the X axis. Binning is
//   peak-preserving: each bin shows the MAXIMUM dB level of its samples, so
//   narrow reflection spikes stay visible (same rule as the --map plots).
//
// Downsampling facts: samples are ~0.32 m apart (3.125 ns sampling period), so
// every 1 m bin always contains several samples; a bin beyond a curve's range
// gets an empty cell.
//
// The module only exports data; analysis and verdict behavior are unchanged.
//
// RUN:  exfo_trc_analyzer.exe --excel-trace <file.trc | folder>
//       (the file goes where the other reports go: --source_output picks the
//       folder)
// ==============================================================================
using System.Globalization;

namespace ExfoTrcAnalyzer;

public static class PlotExport
{
    // distance bin width in meters for the chart table (~0.32 m between raw
    // samples -> every bin gets several samples; 1 m keeps files ~20k rows)
    public const double PlotBinStepM = 1.0;

    public sealed class PlotData
    {
        public List<double> Distances = new();      // bin start (m), 0-based
        public List<(string SeriesName, double[] Levels)> Series = new();
    }

    // --------------------------------------------------------------------------
    // Build the binned table from raw curves (peak-preserving max per bin)
    // --------------------------------------------------------------------------
    public static PlotData BuildPlotData(List<RawTrace.TraceCurve> curves)
    {
        double maxDist = 0.0;
        foreach (var c in curves)
        {
            if (double.IsNaN(c.DistanceM(0)))
                continue; // no distance calibration — cannot join the common grid
            double d = c.DistanceM(c.SampleCount - 1);
            if (!double.IsNaN(d))
                maxDist = Math.Max(maxDist, d);
        }

        var data = new PlotData();
        int binCount = (int)Math.Ceiling(maxDist / PlotBinStepM);
        for (int b = 0; b < binCount; b++)
            data.Distances.Add(b * PlotBinStepM);

        foreach (var c in curves)
        {
            if (double.IsNaN(c.DistanceM(0)) || c.SampleCount == 0)
                continue;

            var levels = new double[binCount];
            Array.Fill(levels, double.NaN);

            int bin = 0;
            for (int i = 0; i < c.SampleCount && bin < binCount; i++)
            {
                double d = c.DistanceM(i);
                if (double.IsNaN(d))
                    continue;
                int b = Math.Min((int)(d / PlotBinStepM), binCount - 1);
                if (b < bin) // samples advance monotonically; never look back
                    continue;
                bin = b;
                double v = c.LevelDb(i);
                if (double.IsNaN(levels[bin]) || v > levels[bin])
                    levels[bin] = v; // peak-preserving: max dB in the bin
            }

            string name = c.WavelengthNm > 0 ? $"{c.WavelengthNm}nm" : c.TraceName;
            data.Series.Add((name, levels));
        }
        return data;
    }

    // --------------------------------------------------------------------------
    // Chart CSV: distance_m + one level column per wavelength
    // (Csv.SaveCsv house style: ';' separator, comma decimal separator,
    // UTF-8 BOM)
    // --------------------------------------------------------------------------
    public static void SaveCsv(string csvFilepath, PlotData data)
    {
        var headers = new List<string> { "distance_m" };
        foreach (var s in data.Series)
            headers.Add("level_db_" + s.SeriesName);

        var rows = new List<string[]>();
        for (int b = 0; b < data.Distances.Count; b++)
        {
            var row = new List<string>
            {
                Csv.Fmt(data.Distances[b], 3),
            };
            foreach (var s in data.Series)
                row.Add(double.IsNaN(s.Levels[b]) ? "" : Csv.Fmt(s.Levels[b], 3));
            rows.Add(row.ToArray());
        }
        Csv.SaveCsv(csvFilepath, headers.ToArray(), rows);
    }
}
