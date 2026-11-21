// ==============================================================================
// Csv.cs — formatting and writing of CSV result tables.
//
// OUTPUT CONVENTIONS (Excel-friendly):
// - field separator is ';' (the default for many non-English Excel locales);
// - decimal separator inside numbers is a comma (formatted with InvariantCulture
//   then '.' replaced by ',');
// - encoding is UTF-8 with BOM so that Excel/Notepad show headers and rejection
//   reasons correctly;
// - fields containing ';' '"' or a line break are wrapped in quotes, inner quotes
//   are doubled (standard CSV escaping);
// - an empty numeric value (null) is written as an empty field, not "0" or
//   "NaN" — missing data in a reflectogram must not look like zero.
// ==============================================================================
using System.Globalization;
using System.Text;

namespace ExfoTrcAnalyzer;

// ==============================================================================
// CSV formatting and saving (';' separator, comma as decimal separator,
// UTF-8 with BOM — for correct opening in Excel)
// ==============================================================================
public static class Csv
{
    public static string Fmt(double? val, int digits = 3)
    {
        if (val == null)
            return "";
        return val.Value.ToString("F" + digits, CultureInfo.InvariantCulture).Replace('.', ',');
    }

    public static string CsvField(string field)
    {
        if (field.Contains(';') || field.Contains('"') || field.Contains('\n') || field.Contains('\r'))
            return "\"" + field.Replace("\"", "\"\"") + "\"";
        return field;
    }

    public static void SaveCsv(string filename, string[] headers, List<string[]> rows)
    {
        using var fs = File.Create(filename);
        using var w = new StreamWriter(fs, new UTF8Encoding(true)); // utf-8-sig (with BOM)
        w.WriteLine(string.Join(";", headers.Select(CsvField)));
        foreach (var row in rows)
            w.WriteLine(string.Join(";", row.Select(CsvField)));
        w.Flush();
    }
}
