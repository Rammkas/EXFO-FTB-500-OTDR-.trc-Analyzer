// ==============================================================================
// ExfoTrcAnalyzer.Tests\Program.cs — regression tests for the analyzer.
//
// RUN: dotnet run --project ExfoTrcAnalyzer.Tests
// (console application without external packages; exit code 0 = all tests passed)
//
// TEST GROUPS:
// - Config: bool/double parsing from otdr_config.txt, threshold application;
// - TrcParser: zlib signature (all 9 valid FLG values for CMF=0x78; EXFO writes
//   0xDA), unpacking several chunks through garbage padding, reading nodes of all
//   types, safeguards: tree cycles, odd-length strings, NaN, over-long names,
//   verified OtdrFile root despite a false 4-byte match;
// - Analyzer: matching 1310<->1550 sections/events by coordinate, PASS/FAIL
//   verdicts on attenuation/splices/QC connector (including check_qc_connector in
//   both states), explicit Attenuation field priority, buffer exclusion;
// - Csv: decimal comma, CSV escaping, UTF-8 BOM;
// - GOLD: parsing REAL reference .trc files with recorded expected lengths/losses/
//   attenuations/QC-connector losses and verdict. Golden values were captured from
//   correctly parsed traces and guard against silent verdict changes while the
//   parser evolves. Reference files are looked for in: the OTDR_TRC_DIR environment
//   variable, then .\reference_trc relative to the current directory and to the
//   test binary. If the files are not present — GOLD tests are skipped (SKIP), not
//   failed.
//
// Synthetic traces are built like the real bench: port(0) -> buffer(0..1010 m) ->
// QC connector(1010 m) -> DUT -> end.
// A separate group covers measuring WITHOUT a buffer spool: correct behavior with
// has_buffer_spool=False and Analyzer warnings when the setting contradicts the
// trace (including the dangerous case "DUT ~1 km treated as buffer, length 0, PASS").
// Binary trees for parser tests use the same layout (16-byte header) that TrcParser
// reads, reusing its TrcNodeType enum.
// ==============================================================================
using System.Text;
using ExfoTrcAnalyzer;

// ==============================================================================
// REGRESSION TESTS for ExfoTrcAnalyzer (no external packages).
//   dotnet run --project ExfoTrcAnalyzer.Tests
// "GOLD" group: checks against real reference .trc files (if they are missing,
// GOLD tests are skipped with a notice).
// ==============================================================================

internal static class TestRunner
{
    private static int Main()
    {
        var tests = new List<(string Name, Action Test)>
        {
            // --- Config ---
            ("Config.ParseBool variants", TestParseBool),
            ("Config.ParseDouble comma/dot", TestParseDouble),
            ("Config.Load applies parameters", TestConfigLoad),

            // --- TrcParser ---
            ("TrcParser.IsZlibStreamStart valid/invalid FLG", TestZlibHeader),
            ("TrcParser.DecompressTrc several chunks", TestDecompress),
            ("TrcParser.ReadNodeAt int/double/bool", TestReadNodeScalars),
            ("TrcParser.ReadNodeAt odd-length string", TestReadNodeOddString),
            ("TrcParser.ReadNodeAt NaN -> null", TestReadNodeNaN),
            ("TrcParser.ReadNodeAt over-long name rejected", TestReadNodeLongName),
            ("TrcParser.ReadNodeAt tree cycle protection", TestReadNodeCycle),
            ("TrcParser.FindRootTree accepts verified root", TestFindRoot),
            ("TrcParser.FindRootTree without OtdrFile -> exception", TestFindRootMissing),

            // --- Analyzer ---
            ("Analyzer.MatchSectionsByPosition with unequal lists", TestMatchSections),
            ("Analyzer.MatchEventsByPosition basic", TestMatchEvents),
            ("Analysis: good fiber -> PASS", TestFiberPass),
            ("Analysis: 1310 attenuation over limit -> FAIL", TestFiberAtten1310Fail),
            ("Analysis: 1550 attenuation over limit -> FAIL", TestFiberAtten1550Fail),
            ("Analysis: buffer spool excluded from DUT length", TestBufferExcluded),
            ("No buffer: has_buffer_spool=False — whole trace is DUT", TestNoBufferFlagOff),
            ("No buffer: flag True but no buffer — warning, length intact", TestNoBufferFlagOnWarn),
            ("Danger: DUT ~1 km swallowed as buffer — warning, length 0", TestDutSwallowedAsBuffer),
            ("Analysis: splice over threshold -> FAIL", TestSpliceFail),
            ("Analysis: QC connector, flag False -> PASS (report only)", TestQcFlagOff),
            ("Analysis: QC connector, flag True, over threshold -> FAIL", TestQcFlagOnFail),
            ("Analysis: QC connector, flag True, within limit -> PASS", TestQcFlagOnPass),
            ("Analysis: explicit Attenuation field has priority over Loss/Length", TestExplicitAttenuation),

            // --- Csv ---
            ("Csv.Fmt comma and null", TestCsvFmt),
            ("Csv.CsvField escaping", TestCsvField),
            ("Csv.SaveCsv writes file with BOM", TestCsvSave),

            // --- PlotExport ---
            ("PlotExport binning is peak-preserving", TestPlotExportBinning),

            // --- GOLD: reference .trc files ---
            ("GOLD 9076.trc", () => TestGolden("9076.trc", 7338.4, 2.364, 0.322, 7336.5, 1.346, 0.183, 0.541, 0.440)),
            ("GOLD 9077.trc", () => TestGolden("9077.trc", 7342.2, 2.406, 0.328, 7336.5, 1.365, 0.186, 0.152, 0.147)),
            ("GOLD 9084.trc", () => TestGolden("9084.trc", 7340.9, 2.427, 0.331, 7338.8, 1.353, 0.184, 0.259, 0.132)),
            ("GOLD 9085.trc", () => TestGolden("9085.trc", 7338.7, 2.432, 0.331, 7336.5, 1.372, 0.187, 0.480, 0.352)),
            ("GOLD 9076.trc raw trace curves (--raw-trace)", TestRawTraceGolden),
            ("GOLD 9076.trc map trace plots (--map)", TestMapTracePlotsGolden),
            ("GOLD 9076.trc Excel trace export (--excel-trace)", TestExcelTraceGolden),
        };

        int failed = 0;
        foreach (var (name, test) in tests)
        {
            Config.ResetDefaults();
            try
            {
                test();
                Console.WriteLine($"[PASS] {name}");
            }
            catch (Exception e)
            {
                failed++;
                Console.WriteLine($"[FAIL] {name}: {e.Message}");
            }
        }

        Console.WriteLine($"\nTotal: {tests.Count}, OK: {tests.Count - failed}, FAIL: {failed}");
        return failed == 0 ? 0 : 1;
    }

    private static void Check(bool cond, string msg)
    {
        if (!cond)
            throw new Exception(msg);
    }

    private static void CheckClose(double? actual, double expected, double tol, string msg)
    {
        if (actual == null)
            throw new Exception($"{msg}: value missing");
        if (Math.Abs(actual.Value - expected) > tol)
            throw new Exception($"{msg}: expected {expected}±{tol}, got {actual}");
    }

    // ============================ Config ============================

    private static void TestParseBool()
    {
        Check(Config.ParseBool("True", out bool b) && b, "True");
        Check(Config.ParseBool("yes", out b) && b, "yes");
        Check(Config.ParseBool("0", out b) && !b, "0");
        Check(Config.ParseBool("off", out b) && !b, "off");
        Check(!Config.ParseBool("maybe-not", out _), "garbage must be rejected");
    }

    private static void TestParseDouble()
    {
        Check(Config.ParseDouble("0,75", out double d) && Math.Abs(d - 0.75) < 1e-9, "0,75");
        Check(Config.ParseDouble("0.75", out d) && Math.Abs(d - 0.75) < 1e-9, "0.75");
        Check(!Config.ParseDouble("abc", out _), "abc");
    }

    private static void TestConfigLoad()
    {
        string dir = Path.Combine(Path.GetTempPath(), "otdr_cfg_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, Config.ConfigFileName),
                "check_qc_connector = True\r\n" +
                "qc_connector_loss_max = 0,75\r\n" +
                "atten_1310_max = 0.40\r\n" +
                "unknown_param = 1\r\n");

            Config.Load(dir, _ => { }); // no console output

            Check(Config.CheckQcConnector, "check_qc_connector not applied");
            CheckClose(Config.QcConnectorLossMax, 0.75, 1e-9, "qc_connector_loss_max");
            CheckClose(Config.Atten1310Max, 0.40, 1e-9, "atten_1310_max");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ============================ TrcParser ============================

    private static byte[] ZlibCompress(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var zs = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            zs.Write(data);
        return ms.ToArray();
    }

    private static void TestDecompress()
    {
        byte[] p1 = Encoding.ASCII.GetBytes(new string('A', 100) + "|MARK1|" + new string('B', 100));
        byte[] p2 = Encoding.ASCII.GetBytes(new string('C', 100) + "|MARK2|" + new string('D', 100));

        var file = new List<byte>();
        file.AddRange(Enumerable.Repeat((byte)0x01, 32)); // 32-byte garbage prefix (0x01 is not a zlib start)
        file.AddRange(ZlibCompress(p1));
        file.AddRange(Enumerable.Repeat((byte)0x01, 32)); // 32-byte garbage separator
        file.AddRange(ZlibCompress(p2));

        string path = Path.Combine(Path.GetTempPath(), "otdr_dec_" + Guid.NewGuid().ToString("N") + ".trc");
        File.WriteAllBytes(path, file.ToArray());
        try
        {
            byte[] merged = TrcParser.DecompressTrc(path);
            int m1 = IndexOf(merged, "|MARK1|"u8);
            int m2 = IndexOf(merged, "|MARK2|"u8);
            Check(m1 >= 0, "MARK1 not found");
            Check(m2 >= 0, "MARK2 not found");
            Check(m1 < m2, "chunk order broken");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static int IndexOf(byte[] data, ReadOnlySpan<byte> pattern) => data.AsSpan().IndexOf(pattern);

    private static void TestZlibHeader()
    {
        // all nine valid FLG values for CMF=0x78
        foreach (byte flg in new byte[] { 0x01, 0x20, 0x3F, 0x5E, 0x7D, 0x9C, 0xBB, 0xDA, 0xF9 })
            Check(TrcParser.IsZlibStreamStart(0x78, flg), $"FLG 0x{flg:X2} should be valid");
        // invalid: fail the modulo-31 checksum
        foreach (byte flg in new byte[] { 0x00, 0x02, 0x9B, 0xDB, 0xFF })
            Check(!TrcParser.IsZlibStreamStart(0x78, flg), $"FLG 0x{flg:X2} should be rejected");
        // different CMF (not deflate/32K)
        Check(!TrcParser.IsZlibStreamStart(0x8C, 0xDA), "CMF != 0x78 should be rejected");
    }

    // Mini binary-tree builder: 16-byte node header —
    // [nameOffset:int32][dataType:int32][valLen:int32][reserved:int32], then the value.
    private sealed class ByteTree
    {
        public List<byte> Buf = new();

        public int AddName(string s)
        {
            int p = Buf.Count;
            Buf.AddRange(Encoding.ASCII.GetBytes(s));
            Buf.Add(0);
            return p;
        }

        public int AddNode(int nameOff, int type, byte[] value)
        {
            int p = Buf.Count;
            Buf.AddRange(BitConverter.GetBytes(nameOff));
            Buf.AddRange(BitConverter.GetBytes(type));
            Buf.AddRange(BitConverter.GetBytes(value.Length));
            Buf.AddRange(new byte[4]); // self-offset field (offset+16) — not checked by the parser
            Buf.AddRange(value);
            return p;
        }

        public int AddInt(int nameOff, int v) => AddNode(nameOff, (int)TrcParser.TrcNodeType.Int32, BitConverter.GetBytes(v));
        public int AddDouble(int nameOff, double v) => AddNode(nameOff, (int)TrcParser.TrcNodeType.Double, BitConverter.GetBytes(v));
        public int AddBool(int nameOff, bool v) => AddNode(nameOff, (int)TrcParser.TrcNodeType.Bool8, new[] { (byte)(v ? 1 : 0) });
        public int AddDict(int nameOff, int[] childOffsets)
            => AddNode(nameOff, (int)TrcParser.TrcNodeType.Container, childOffsets.SelectMany(o => BitConverter.GetBytes(o)).ToArray());
    }

    private static void TestReadNodeScalars()
    {
        var t = new ByteTree();
        int nInt = t.AddName("Num");
        int nDbl = t.AddName("Dbl");
        int nBool = t.AddName("Bl");
        int oInt = t.AddInt(nInt, 42);
        int oDbl = t.AddDouble(nDbl, 3.25);
        int oBool = t.AddBool(nBool, true);

        byte[] buf = t.Buf.ToArray();
        var (n1, v1) = TrcParser.ReadNodeAt(buf, oInt);
        var (n2, v2) = TrcParser.ReadNodeAt(buf, oDbl);
        var (n3, v3) = TrcParser.ReadNodeAt(buf, oBool);

        Check(n1 == "Num" && v1 is int i && i == 42, $"int: {n1}={v1}");
        Check(n2 == "Dbl" && v2 is double d && Math.Abs(d - 3.25) < 1e-12, $"double: {n2}={v2}");
        Check(n3 == "Bl" && v3 is bool b && b, $"bool: {n3}={v3}");
    }

    private static void TestReadNodeOddString()
    {
        var t = new ByteTree();
        int nameOff = t.AddName("Str");
        var val = new List<byte>(Encoding.Unicode.GetBytes("abc"));
        val.Add(0); // odd length (7 bytes) — must not crash the parser
        int nodeOff = t.AddNode(nameOff, (int)TrcParser.TrcNodeType.Utf16String, val.ToArray());

        byte[] buf = t.Buf.ToArray();
        var (n, v) = TrcParser.ReadNodeAt(buf, nodeOff); // previously: ArgumentException
        Check(n == "Str" && v as string == "abc", $"odd-length string: {n}={v}");
    }

    private static void TestReadNodeNaN()
    {
        var t = new ByteTree();
        int nameOff = t.AddName("Nan");
        int nodeOff = t.AddDouble(nameOff, double.NaN);
        var (n, v) = TrcParser.ReadNodeAt(t.Buf.ToArray(), nodeOff);
        Check(n == "Nan" && v == null, $"NaN must become null, got {v}");
    }

    private static void TestReadNodeLongName()
    {
        var t = new ByteTree();
        // 300 characters without a terminator — longer than TrcParser.MaxNameLength (256)
        int fakeNameOff = t.Buf.Count;
        t.Buf.AddRange(Enumerable.Repeat((byte)'x', 300));
        int nodeOff = t.AddNode(fakeNameOff, (int)TrcParser.TrcNodeType.Int32, BitConverter.GetBytes(7));

        var (n, v) = TrcParser.ReadNodeAt(t.Buf.ToArray(), nodeOff);
        Check(n == null && v == null, $"over-long name must be rejected, got name '{n}'");
    }

    private static void TestReadNodeCycle()
    {
        var t = new ByteTree();
        int nA = t.AddName("A");
        int nB = t.AddName("B");
        // B — dict with one reference slot (NoChildRef for now); then A -> B; then write B -> A (cycle)
        int oB = t.AddNode(nB, (int)TrcParser.TrcNodeType.Container, new byte[TrcParser.ChildRefSize]);
        int oA = t.AddDict(nA, new[] { oB });
        byte[] link = BitConverter.GetBytes(oA); // B now points back to A: A -> B -> A
        for (int i = 0; i < link.Length; i++)
            t.Buf[oB + TrcParser.NodeHeaderSize + i] = link[i];

        byte[] buf = t.Buf.ToArray();
        var (n, v) = TrcParser.ReadNodeAt(buf, oA); // previously: StackOverflow risk
        Check(n == "A" && v is Dictionary<string, object?>, "node A must parse");
        var dictA = (Dictionary<string, object?>)v!;
        Check(dictA.TryGetValue("B", out var vb) && vb is Dictionary<string, object?>, "node B must parse");
        var dictB = (Dictionary<string, object?>)vb!;
        Check(dictB.Count == 0, $"cyclic reference A<-B must be cut, B contains: {string.Join(",", dictB.Keys)}");
    }

    private static void TestFindRoot()
    {
        // Garbage prefix containing an INCIDENTAL match of the 4-byte reference to the
        // OtdrFile name. The tree is built WITH the prefix from the start: format
        // offsets are absolute.
        const int garbagePrefixLen = 64;  // size of the service "garbage" prefix
        const int fakeCandidateOff = 10;  // a fake root candidate written inside the prefix

        var t = new ByteTree();
        t.Buf.AddRange(Enumerable.Repeat((byte)0x01, garbagePrefixLen));

        int nRoot = t.AddName("OtdrFile");
        int nFibers = t.AddName("Fibers");
        int oFibers = t.AddDict(nFibers, Array.Empty<int>());
        int oRoot = t.AddDict(nRoot, new[] { oFibers });

        byte[] link = BitConverter.GetBytes(nRoot);
        for (int i = 0; i < link.Length; i++)
            t.Buf[fakeCandidateOff + i] = link[i]; // fake candidate earlier than the real header

        var tree = TrcParser.FindRootTree(t.Buf.ToArray());
        Check(tree.ContainsKey("Fibers"), $"verified root not found, keys: {string.Join(",", tree.Keys)}");
    }

    private static void TestFindRootMissing()
    {
        byte[] buf = Enumerable.Repeat((byte)0x01, 128).ToArray();
        try
        {
            TrcParser.FindRootTree(buf);
            throw new Exception("expected InvalidDataException");
        }
        catch (InvalidDataException) { /* expected */ }
    }

    // ============================ Analyzer ============================

    private static Dictionary<string, object?> SectionObj(double pos, double length, double loss) => new()
    {
        ["Position"] = pos,
        ["Length"] = length,
        ["Loss"] = loss,
        ["CursorAPosition"] = pos,
        ["CursorBPosition"] = pos + length,
    };

    private static Dictionary<string, object?> PointObj(double pos, double? loss, double refl) => new()
    {
        ["Position"] = pos,
        ["Type"] = 3, // EXFO data value: reflective event type (not a parser constant)
        ["Reflectance"] = refl,
        ["Loss"] = loss,
    };

    private static Dictionary<string, object?> TraceObj(double wl, Dictionary<string, object?> evTable) => new()
    {
        ["Wavelength"] = wl,
        ["EventTable"] = evTable,
    };

    // Typical bench trace: port(0) -> buffer(0..1010) -> QC connector(1010) -> DUT -> end
    private static Dictionary<string, object?> MakeFiber(double dutLen, double dutLoss13, double dutLoss15, double qcLoss)
    {
        var ev13 = new Dictionary<string, object?>
        {
            ["Event0"] = PointObj(0, null, -44.0),
            ["Event1"] = SectionObj(0, 1010, 0.33),
            ["Event2"] = PointObj(1010, qcLoss, -45.0),
            ["Event3"] = SectionObj(1010, dutLen, dutLoss13),
            ["Event4"] = PointObj(1010 + dutLen, null, -50.0),
        };
        var ev15 = new Dictionary<string, object?>
        {
            ["Event0"] = PointObj(0, null, -44.0),
            ["Event1"] = SectionObj(0, 1010, 0.19),
            ["Event2"] = PointObj(1010, qcLoss * 0.9, -45.0),
            ["Event3"] = SectionObj(1010, dutLen * 0.999, dutLoss15),
            ["Event4"] = PointObj(1010 + dutLen * 0.999, null, -50.0),
        };
        return new Dictionary<string, object?>
        {
            ["Color"] = "F1",
            ["Traces"] = new Dictionary<string, object?>
            {
                ["Trace0"] = TraceObj(1.31e-6, ev13),
                ["Trace1"] = TraceObj(1.55e-6, ev15),
            },
        };
    }

    private static void TestMatchSections()
    {
        var s13 = new List<Section>
        {
            new() { StartM = 0, LengthM = 1000 },
            new() { StartM = 1010, LengthM = 500 },
            new() { StartM = 1600, LengthM = 300 },
        };
        // 1550 is missing the middle section
        var s15 = new List<Section>
        {
            new() { StartM = 0, LengthM = 1000 },
            new() { StartM = 1602, LengthM = 300 },
        };

        var pairs = Analyzer.MatchSectionsByPosition(s13, s15, 5.0);
        Check(pairs.Count == 3, $"expected 3 pairs, got {pairs.Count}");
        CheckClose(pairs[0].S13.LengthM, 1000, 1e-9, "pair0 S13");
        CheckClose(pairs[0].S15.LengthM, 1000, 1e-9, "pair0 S15");
        Check(pairs[1].S15.LengthM == null, "missing 1550 section must stay empty");
        CheckClose(pairs[1].S13.LengthM, 500, 1e-9, "pair1 S13");
        CheckClose(pairs[2].S13.LengthM, 300, 1e-9, "pair2 S13 (matched by position, not by index)");
        CheckClose(pairs[2].S15.LengthM, 300, 1e-9, "pair2 S15");
    }

    private static void TestMatchEvents()
    {
        var e13 = new List<PointEvent> { new() { PosM = 0 }, new() { PosM = 1000.5 }, new() { PosM = 5000 } };
        var e15 = new List<PointEvent> { new() { PosM = 0 }, new() { PosM = 1002 }, new() { PosM = 9000 } };

        var pairs = Analyzer.MatchEventsByPosition(e13, e15, 5.0);
        Check(pairs.Count == 4, $"expected 4 pairs, got {pairs.Count}");
        Check(pairs[1].E13?.PosM == 1000.5 && pairs[1].E15?.PosM == 1002, "events 1000.5<->1002 must match");
        Check(pairs.Any(p => p.E13?.PosM == 5000 && p.E15 == null), "unpaired 1310 event must survive");
        Check(pairs.Any(p => p.E15?.PosM == 9000 && p.E13 == null), "unpaired 1550 event must survive");
    }

    private static void TestFiberPass()
    {
        var f = Analyzer.AnalyzeFiber(MakeFiber(7000, 1.4, 1.3, 0.2), "test"); // 0.2 dB/km
        Check(f.Verdict == "PASS", $"verdict: {f.Verdict}, reasons: {string.Join(" | ", f.Fails)}");
        CheckClose(f.Att13, 0.2, 0.01, "att13");
        Check(f.FiberId == "F1", "FiberId");
    }

    private static void TestFiberAtten1310Fail()
    {
        var f = Analyzer.AnalyzeFiber(MakeFiber(7000, 3.5, 1.3, 0.2), "test"); // 0.5 dB/km @1310
        Check(f.Verdict == "FAIL", "expected FAIL on 1310 attenuation");
        Check(f.Fails.Any(s => s.Contains("Attenuation 1310")), "reason not described: " + string.Join(" | ", f.Fails));
    }

    private static void TestFiberAtten1550Fail()
    {
        var f = Analyzer.AnalyzeFiber(MakeFiber(7000, 1.4, 2.1, 0.2), "test"); // 0.3 dB/km @1550
        Check(f.Verdict == "FAIL", "expected FAIL on 1550 attenuation");
        Check(f.Fails.Any(s => s.Contains("Attenuation 1550")), "reason not described: " + string.Join(" | ", f.Fails));
    }

    private static void TestBufferExcluded()
    {
        var f = Analyzer.AnalyzeFiber(MakeFiber(7000, 1.4, 1.3, 0.2), "test");
        Check(f.Sections.Count == 2, $"expected 2 sections, got {f.Sections.Count}");
        Check(f.Sections[0].Verdict == "BUFFER", "first section must be BUFFER");
        CheckClose(f.Len13, 7000, 1.0, "DUT length must not include the buffer");
    }

    // Trace of a measurement WITHOUT a buffer spool: port(0) -> DUT(0..len) -> end
    private static Dictionary<string, object?> MakeNoBufferFiber(double dutLen, double loss13, double loss15)
    {
        var ev13 = new Dictionary<string, object?>
        {
            ["Event0"] = PointObj(0, null, -44.0),
            ["Event1"] = SectionObj(0, dutLen, loss13),
            ["Event2"] = PointObj(dutLen, null, -50.0),
        };
        var ev15 = new Dictionary<string, object?>
        {
            ["Event0"] = PointObj(0, null, -44.0),
            ["Event1"] = SectionObj(0, dutLen * 0.999, loss15),
            ["Event2"] = PointObj(dutLen * 0.999, null, -50.0),
        };
        return new Dictionary<string, object?>
        {
            ["Color"] = "F1",
            ["Traces"] = new Dictionary<string, object?>
            {
                ["Trace0"] = TraceObj(1.31e-6, ev13),
                ["Trace1"] = TraceObj(1.55e-6, ev15),
            },
        };
    }

    private static void TestNoBufferFlagOff()
    {
        Config.HasBufferSpool = false;
        var f = Analyzer.AnalyzeFiber(MakeNoBufferFiber(7000, 1.4, 1.3), "test"); // 0.2 dB/km
        Check(f.Verdict == "PASS", $"verdict: {f.Verdict}: {string.Join(" | ", f.Fails)}");
        Check(f.Sections.Count == 1 && f.Sections[0].Role == "Fiber under test",
            "with has_buffer_spool=False the first section is the fiber under test");
        CheckClose(f.Len13, 7000, 1.0, "DUT length");
        Check(f.QcLoss13 == null && f.QcLoss15 == null, "QC connector is not determined without a buffer — columns stay empty");
        Check(f.Warnings.Count == 0, $"no warnings expected: {string.Join(" | ", f.Warnings)}");
    }

    private static void TestNoBufferFlagOnWarn()
    {
        Config.HasBufferSpool = true; // setting contradicts the trace
        var f = Analyzer.AnalyzeFiber(MakeNoBufferFiber(7000, 1.4, 1.3), "test"); // 7000 m is outside the buffer window
        Check(f.Sections.All(s => s.Verdict != "BUFFER"), "a 7000 m section must not be tagged as buffer");
        CheckClose(f.Len13, 7000, 1.0, "length still computed correctly");
        Check(f.Warnings.Any(w => w.Contains("no buffer-like section found")),
            "expected a warning about has_buffer_spool mismatch: " + string.Join(" | ", f.Warnings));
    }

    private static void TestDutSwallowedAsBuffer()
    {
        Config.HasBufferSpool = true;
        // fiber under test ~1000 m measured directly: falls into the 800..1200 m buffer window
        var f = Analyzer.AnalyzeFiber(MakeNoBufferFiber(1000, 0.33, 0.19), "test");
        Check(f.Sections[0].Verdict == "BUFFER", "danger scenario: the fiber is tagged as buffer (that is how the window rule works)");
        CheckClose(f.Len13, 0, 1e-9, "DUT length zeroed");
        Check(f.Warnings.Any(w => w.Contains("fiber-under-test length is 0")),
            "expected a warning about zero DUT length: " + string.Join(" | ", f.Warnings));
    }

    private static void TestSpliceFail()
    {
        var fiber = MakeFiber(7000, 1.4, 1.3, 0.2);
        // add a splice in the middle of the DUT (Event3 shifts)
        var traces = (Dictionary<string, object?>)fiber["Traces"]!;
        var ev13 = (Dictionary<string, object?>)((Dictionary<string, object?>)traces["Trace0"]!)["EventTable"]!;
        ev13["Event3"] = PointObj(4000, 0.5, -48.0); // 0.5 > 0.30
        ev13["Event4"] = SectionObj(1010, 7000, 1.4);
        ev13["Event5"] = PointObj(8010, null, -50.0);

        var f = Analyzer.AnalyzeFiber(fiber, "test");
        Check(f.Verdict == "FAIL", "expected FAIL on splice loss");
        Check(f.Fails.Any(s => s.Contains("Splice")), "reason not described: " + string.Join(" | ", f.Fails));
    }

    private static void TestQcFlagOff()
    {
        Config.CheckQcConnector = false;
        var f = Analyzer.AnalyzeFiber(MakeFiber(7000, 1.4, 1.3, 0.9), "test"); // QC 0.9 dB
        Check(f.Verdict == "PASS", $"with check_qc_connector=False the QC connector must not reject the fiber: {f.Verdict}");
        CheckClose(f.QcLoss13, 0.9, 1e-6, "QC loss must still be reported");
    }

    private static void TestQcFlagOnFail()
    {
        Config.CheckQcConnector = true;
        Config.QcConnectorLossMax = 0.5;
        var f = Analyzer.AnalyzeFiber(MakeFiber(7000, 1.4, 1.3, 0.9), "test");
        Check(f.Verdict == "FAIL", "expected FAIL on QC connector");
        Check(f.Fails.Any(s => s.Contains("QC connector loss")), "reason not described: " + string.Join(" | ", f.Fails));
    }

    private static void TestQcFlagOnPass()
    {
        Config.CheckQcConnector = true;
        Config.QcConnectorLossMax = 0.5;
        var f = Analyzer.AnalyzeFiber(MakeFiber(7000, 1.4, 1.3, 0.3), "test");
        Check(f.Verdict == "PASS", $"QC connector within limit, verdict: {f.Verdict}, reasons: {string.Join(" | ", f.Fails)}");
    }

    private static void TestExplicitAttenuation()
    {
        var fiber = MakeFiber(7000, 5.0, 1.3, 0.2); // Loss/Length = 0.714 -> would be FAIL
        var traces = (Dictionary<string, object?>)fiber["Traces"]!;
        var ev13 = (Dictionary<string, object?>)((Dictionary<string, object?>)traces["Trace0"]!)["EventTable"]!;
        ((Dictionary<string, object?>)ev13["Event3"]!)["Attenuation"] = 0.15; // explicit field -> PASS

        var f = Analyzer.AnalyzeFiber(fiber, "test");
        Check(f.Verdict == "PASS", $"explicit Attenuation=0.15 must take priority: {f.Verdict}, {string.Join(" | ", f.Fails)}");
        Check(f.Sections[1].S13.AttenFromExplicitField, "AttenFromExplicitField flag not set");
    }

    // ============================ Csv ============================

    private static void TestCsvFmt()
    {
        Check(Csv.Fmt(1.2345, 2) == "1,23", $"Fmt(1.2345,2) = {Csv.Fmt(1.2345, 2)}");
        Check(Csv.Fmt(null, 3) == "", "Fmt(null) must be an empty string");
    }

    private static void TestCsvField()
    {
        Check(Csv.CsvField("a;b") == "\"a;b\"", "semicolon must be escaped");
        Check(Csv.CsvField("say \"yes\"") == "\"say \"\"yes\"\"\"", "quotes must be doubled");
        Check(Csv.CsvField("plain") == "plain", "plain field stays unchanged");
    }

    private static void TestCsvSave()
    {
        string path = Path.Combine(Path.GetTempPath(), "otdr_csv_" + Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            Csv.SaveCsv(path, new[] { "A", "B" }, new List<string[]> { new[] { "1", "x;y" } });
            byte[] raw = File.ReadAllBytes(path);
            byte[] utf8Bom = { 0xEF, 0xBB, 0xBF }; // UTF-8 BOM signature
            Check(raw.Length >= utf8Bom.Length && raw[0] == utf8Bom[0] && raw[1] == utf8Bom[1] && raw[2] == utf8Bom[2], "no BOM");
            string text = File.ReadAllText(path);
            Check(text.Contains("\"x;y\""), "escaping in the file");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    // ============================ GOLD ============================

    // GOLD tolerances: length ±0.2 m, loss ±0.002 dB, attenuation ±0.001 dB/km
    // (the uncompressed .trc fields must reproduce exactly; the tolerance guards
    // against future changes in formatting/rounding)
    private const double GoldenLenTolM = 0.2;
    private const double GoldenLossTolDb = 0.002;
    private const double GoldenAttenTolDbKm = 0.001;

    // Where to find reference .trc files: OTDR_TRC_DIR environment variable first,
    // then .\reference_trc relative to the current directory and to the test binary.
    private static string? FindGoldenFile(string fileName)
    {
        var candidates = new List<string>();
        string? envDir = Environment.GetEnvironmentVariable("OTDR_TRC_DIR");
        if (!string.IsNullOrEmpty(envDir))
            candidates.Add(envDir);
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "reference_trc"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "reference_trc"));

        foreach (string dir in candidates)
        {
            string path = Path.Combine(dir, fileName);
            if (File.Exists(path))
                return path;
        }
        return null;
    }

    // GOLD for the --raw-trace export: RawSamples blob encoding verified on
    // 9076.trc — Int16 LE samples, 1024 counts per dB, uniform time axis
    // (see RawTrace.cs file header for the cross-checks)
    // BuildPlotData: each 1 m bin must hold the MAXIMUM dB of its samples
    // (narrow spikes survive downsampling), bins beyond the curve stay empty
    private static void TestPlotExportBinning()
    {
        // synthetic curve: samples every 0.25 m from 0 to 9.75 m, one spike
        var curve = new RawTrace.TraceCurve
        {
            TraceName = "Trace0",
            WavelengthNm = 1310,
            SamplingPeriodS = 1.0,   // fake calibration: 1 sample per unit time
            RangeStartS = 0,
            RangeEndS = 40,
            RangeM = 10,             // DistanceM(i) = i * 10 / 40 = i * 0.25
            CountsPerDb = 1000,
        };
        var samples = new short[40];
        for (int i = 0; i < 40; i++) samples[i] = 10_000; // 10 dB baseline
        samples[2 * 4 + 3] = 18_000; // 18 dB spike at 2.75 m -> bin 2
        curve.Samples = samples;

        var data = PlotExport.BuildPlotData(new List<RawTrace.TraceCurve> { curve });
        Check(data.Series.Count == 1, $"expected 1 series, got {data.Series.Count}");
        Check(data.Distances.Count == 10, $"expected 10 bins, got {data.Distances.Count}");
        CheckClose(data.Series[0].Levels[2], 18.0, 1e-9, "bin 2 must keep the spike max");
        CheckClose(data.Series[0].Levels[1], 10.0, 1e-9, "bin 1 baseline");
        CheckClose(data.Series[0].Levels[9], 10.0, 1e-9, "last bin (9.75 m sample)");

        // a curve ending early leaves later bins empty in the shared grid
        var shortCurve = new RawTrace.TraceCurve
        {
            TraceName = "Trace1",
            WavelengthNm = 1550,
            SamplingPeriodS = 1.0,
            RangeStartS = 0,
            RangeEndS = 40,
            RangeM = 10,
            CountsPerDb = 1000,
            Samples = new short[20], // up to 4.75 m only
        };
        for (int i = 0; i < 20; i++) shortCurve.Samples[i] = 12_000; // 12 dB

        var both = PlotExport.BuildPlotData(new List<RawTrace.TraceCurve> { curve, shortCurve });
        Check(both.Series.Count == 2 && both.Distances.Count == 10, "two series on a 10-bin grid");
        Check(double.IsNaN(both.Series[1].Levels[8]), "bins beyond the short curve must be empty");
        CheckClose(both.Series[1].Levels[4], 12.0, 1e-9, "short curve keeps its level");
    }

    // --map must draw one raw trace plot per wavelength and overlay the
    // analyzed event markers (plot rows use the same column grid as the ruler)
    // --excel-trace GOLD: binned chart CSV over the real reference file
    private static void TestExcelTraceGolden()
    {
        string? path = FindGoldenFile("9076.trc");
        if (path == null)
        {
            Console.WriteLine("  [SKIP] reference file not found (put .trc files in .\\reference_trc or set OTDR_TRC_DIR)");
            return;
        }

        var curves = RawTrace.ReadCurves(path);
        var data = PlotExport.BuildPlotData(curves);
        Check(data.Series.Count == 2, $"expected 2 series, got {data.Series.Count}");

        string dir = Path.Combine(Path.GetTempPath(), "otdr_plot_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string csvPath = Path.Combine(dir, "trace_plot_test.csv");
            PlotExport.SaveCsv(csvPath, data);

            // CSV: header + one row per bin, distance + 2 level columns
            string[] csvLines = File.ReadAllLines(csvPath);
            Check(csvLines.Length == data.Distances.Count + 1,
                  $"expected {data.Distances.Count + 1} CSV lines, got {csvLines.Length}");
            Check(csvLines[0].Contains("distance_m") && csvLines[0].Contains("level_db_1310nm")
                  && csvLines[0].Contains("level_db_1550nm"), "unexpected CSV header: " + csvLines[0]);

            // house style: ';' separator and comma decimal separator
            Check(csvLines[1].Split(';').Length == 3, "expected 3 columns in a data row");
            Check(csvLines[1].Contains(','), "numbers must use the comma decimal separator");

            // the QC connector reflection peak (raw-trace position ~1014 m,
            // GOLD: sample 3179 at 1014.45 m) must survive the 1 m binning:
            // its bin level is far above the neighboring fiber baseline
            int lo = (int)(1010.0 / PlotExport.PlotBinStepM);
            int hi = (int)(1020.0 / PlotExport.PlotBinStepM);
            double peakLevel = double.MinValue;
            for (int b = lo; b <= hi; b++)
                peakLevel = Math.Max(peakLevel, data.Series[0].Levels[b]);
            double baseline = data.Series[0].Levels[hi + 290];
            Check(peakLevel > baseline + 3.0,
                  $"QC peak must stay visible: {peakLevel:F2} dB vs baseline {baseline:F2} dB");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private static void TestMapTracePlotsGolden()
    {
        string? path = FindGoldenFile("9076.trc");
        if (path == null)
        {
            Console.WriteLine("  [SKIP] reference file not found (put .trc files in .\\reference_trc or set OTDR_TRC_DIR)");
            return;
        }

        var fibers = Analyzer.AnalyzeFile(path);
        var curves = RawTrace.ReadCurves(path);
        Check(fibers.Count == 1 && curves.Count == 2,
              $"expected 1 fiber and 2 curves, got {fibers.Count}/{curves.Count}");

        string map = MapRenderer.Render(fibers[0], curves);
        Check(map.Contains("Trace plot [1310 nm]"), "missing 1310 nm trace plot");
        Check(map.Contains("Trace plot [1550 nm]"), "missing 1550 nm trace plot");
        Check(map.Contains("plot legend"), "missing plot legend");

        // the QC event marker and the end marker must appear inside the plots
        // (the ruler/event rows carry the position labels)
        Check(map.Contains("●1011") || map.Contains("●1010"), "QC marker missing in plot rows");
        Check(map.Contains("◆8347") || map.Contains("◆8348"), "end marker missing in plot rows");

        // Render without curves (files lacking RawSamples) must stay valid
        string plain = MapRenderer.Render(fibers[0]);
        Check(!plain.Contains("Trace plot"), "plots must be absent when no curves are passed");
    }

    private static void TestRawTraceGolden()
    {
        string? path = FindGoldenFile("9076.trc");
        if (path == null)
        {
            Console.WriteLine("  [SKIP] reference file not found (put .trc files in .\\reference_trc or set OTDR_TRC_DIR)");
            return;
        }

        var curves = RawTrace.ReadCurves(path);
        Check(curves.Count == 2, $"expected 2 trace curves, got {curves.Count}");
        Check(curves[0].WavelengthNm == 1310 && curves[1].WavelengthNm == 1550,
              $"wavelengths must be 1310/1550, got {curves[0].WavelengthNm}/{curves[1].WavelengthNm}");
        Check(curves[0].SampleCount == 62682 && curves[1].SampleCount == 62682,
              $"sample counts must be 62682, got {curves[0].SampleCount}/{curves[1].SampleCount}");
        Check(curves[0].CountsPerDb == 1024, $"ScaleFactor must be 1024, got {curves[0].CountsPerDb}");

        // QC connector reflection peak (1310): sample 3179, raw 29219, ~1014 m
        Check(curves[0].Samples[3179] == 29219, $"QC peak raw must be 29219, got {curves[0].Samples[3179]}");
        CheckClose(curves[0].DistanceM(3179), 1014.451, 0.01, "QC peak distance (1310)");

        // fiber-end reflection peak (1310): sample 26165, raw 23068, ~8349 m
        Check(curves[0].Samples[26165] == 23068, $"end peak raw must be 23068, got {curves[0].Samples[26165]}");
        CheckClose(curves[0].DistanceM(26165), 8348.614, 0.01, "end peak distance (1310)");

        // decoded DUT slope must match the GOLD attenuation (0.322 / 0.183 dB/km)
        CheckClose(DutSlopeDbKm(curves[0]), 0.322, 0.01, "raw-trace slope 1310 vs GOLD 0.322");
        CheckClose(DutSlopeDbKm(curves[1]), 0.183, 0.01, "raw-trace slope 1550 vs GOLD 0.183");
    }

    // Mean level of a sample window, then dB/km slope between two windows
    private static double WindowLevel(RawTrace.TraceCurve c, int lo, int hi)
    {
        double s = 0;
        for (int i = lo; i < hi; i++) s += c.LevelDb(i);
        return s / (hi - lo);
    }

    private static double DutSlopeDbKm(RawTrace.TraceCurve c)
    {
        // windows well inside the DUT: after the QC connector, before the fiber end
        double l1 = WindowLevel(c, 3300, 4000), l2 = WindowLevel(c, 25500, 26000);
        double spanKm = (c.DistanceM(25500) + c.DistanceM(25999) - c.DistanceM(3300) - c.DistanceM(3999)) / 2000.0;
        return (l1 - l2) / spanKm;
    }

    private static void TestGolden(string fileName,
        double len13, double loss13, double att13,
        double len15, double loss15, double att15,
        double qc13, double qc15)
    {
        string? path = FindGoldenFile(fileName);
        if (path == null)
        {
            Console.WriteLine("  [SKIP] reference file not found (put .trc files in .\\reference_trc or set OTDR_TRC_DIR)");
            return;
        }

        var fibers = Analyzer.AnalyzeFile(path);
        Check(fibers.Count == 1, $"expected 1 fiber, got {fibers.Count}");
        var f = fibers[0];

        Check(f.Verdict == "PASS", $"verdict must be PASS, got {f.Verdict}: {string.Join(" | ", f.Fails)}");
        Check(f.Sections.Count >= 2, $"expected >=2 sections, got {f.Sections.Count}");
        Check(f.Sections[0].Verdict == "BUFFER", "first section must be the buffer");

        CheckClose(f.Len13, len13, GoldenLenTolM, "Length 1310");
        CheckClose(f.Loss13, loss13, GoldenLossTolDb, "Loss 1310");
        CheckClose(f.Att13, att13, GoldenAttenTolDbKm, "Attenuation 1310");
        CheckClose(f.Len15, len15, GoldenLenTolM, "Length 1550");
        CheckClose(f.Loss15, loss15, GoldenLossTolDb, "Loss 1550");
        CheckClose(f.Att15, att15, GoldenAttenTolDbKm, "Attenuation 1550");
        CheckClose(f.QcLoss13, qc13, GoldenLossTolDb, "QC connector 1310");
        CheckClose(f.QcLoss15, qc15, GoldenLossTolDb, "QC connector 1550");
    }
}
