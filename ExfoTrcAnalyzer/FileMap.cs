// ==============================================================================
// FileMap.cs — structural map of the raw .trc FILE ITSELF (not the fiber link;
// the link schematic is in MapRenderer.cs).
//
// TWO MODES:
//   --file-map full  — detailed console map:
//        1) raw file layout: record headers (ASCII magic "AppReg Format Ex  "),
//           zlib chunk byte ranges, size prefixes and service gaps;
//        2) unpacked stream layout (chunk -> unpacked byte range);
//        3) full node tree outline (containers expanded, scalars grouped);
//        4) BinaryRef nodes (raw data blobs — CLSID / RawSamples) with sizes.
//   --file-map short — compact "address -> stores what" map:
//        1) every raw region annotated with the node-tree content it carries;
//        2) a section explaining where the trace sample values live
//           (RawSamples BinaryRef blobs);
//        3) the node tree is NOT printed — instead it is saved to a JSON file
//           "<name>.nodetree.json" next to the .trc file (all nodes with
//           offset, type, size and parsed values).
//
// Chunk end determination (cross-checked, both methods reported):
//   a) record header size field: an int32 located 4 bytes before the zlib
//      header declares the compressed size; accepted only when the Adler-32
//      checksum of the unpacked data is found exactly at the predicted end
//      (either just before or just after the predicted end byte);
//   b) fallback: scan for the big-endian Adler-32 trailer after the chunk start
//      (ZLibStream validates Adler-32 itself, so a successfully decompressed
//      chunk always ends with this trailer in the raw file).
//
// The layout facts here are the same ones documented and verified in
// TrcParser.cs; this module only presents them as a map. It never changes
// parsing behavior.
//
// RUN:  exfo_trc_analyzer.exe --file-map <full|short> <file.trc | folder>
// ==============================================================================
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace ExfoTrcAnalyzer;

public static class FileMap
{
    // ASCII magic observed at the start of every record header in checked .trc files
    private const string RecordMagicText = "AppReg Format Ex  ";
    private static readonly byte[] RecordMagic = "AppReg Format Ex  "u8.ToArray();

    // int32 fields observed inside a record header (relative to its magic start);
    // reported as-is, their exact EXFO meaning is not documented publicly
    private static readonly int[] HeaderInt32Fields = { 20, 24, 28, 32, 36 };

    // CLSID BinaryRef blobs are 16-byte GUID markers present in every container
    private const int ClsidGuidSize = 16;

    private sealed class Chunk
    {
        public int No;                    // 1-based scan order number
        public int RawStart;              // offset of the CMF byte in the raw file
        public int RawEnd;                // exclusive; 0 = could not be located
        public byte Cmf, Flg;
        public byte[] Data = Array.Empty<byte>();
        public bool SizeFromHeader;       // exact size confirmed via the header size field
        public bool Accepted;             // included into the merged stream (Data.Length > MinChunkLength)
        public int MergedStart = -1;      // position in the merged (unpacked) stream
        public int RawSize => RawEnd > 0 ? RawEnd - RawStart : 0;
    }

    // Results of the tree walk shared by all output sections
    private sealed class TreeInfo
    {
        public int RootOffset = -1;
        public StringBuilder OutlineSb = new();          // full-mode tree outline text
        public List<(int Offset, string Name)> ContainersInTreeOrder = new();
        public List<(NodeHead Head, string ParentPath)> BinaryRefs = new();
        public Dictionary<int, int> TypeCount = new();
        public HashSet<int> Visited = new();
        public List<Chunk> AcceptedChunks = new();
    }

    private sealed class NodeHead
    {
        public int Offset;
        public string Name = "";
        public int Type;
        public int ValLen;
    }

    // --------------------------------------------------------------------------
    // full mode (console only)
    // --------------------------------------------------------------------------
    public static string Render(string filepath)
    {
        var (raw, chunks, merged) = LoadFileAndChunks(filepath);
        TreeInfo info = WalkTree(merged, chunks);

        var sb = new StringBuilder();
        AppendHeader(sb, filepath, "full");
        AppendRawLayout(sb, raw, chunks);
        AppendMergedLayout(sb, chunks);
        AppendTree(sb, info);
        return sb.ToString();
    }

    // --------------------------------------------------------------------------
    // short mode: compact map + node tree saved to JSON
    // --------------------------------------------------------------------------
    public static string RenderShort(string filepath)
    {
        var (raw, chunks, merged) = LoadFileAndChunks(filepath);
        TreeInfo info = WalkTree(merged, chunks);

        var sb = new StringBuilder();
        AppendHeader(sb, filepath, "short");
        AppendContentsLayout(sb, raw, chunks, info);
        AppendTraceValuesSection(sb, info);

        if (info.RootOffset >= 0)
        {
            string jsonPath = SaveTreeJson(filepath, raw.Length, merged.Length, chunks, info);
            sb.AppendLine($"node tree saved to JSON: {jsonPath}");
        }
        else
        {
            sb.AppendLine("(root node OtdrFile not found — tree JSON not created)");
        }
        return sb.ToString();
    }

    private static void AppendHeader(StringBuilder sb, string filepath, string mode)
    {
        sb.AppendLine($"=== File map ({mode}): {Path.GetFileName(filepath)} ===");
        sb.AppendLine($"file size: {Num(new FileInfo(filepath).Length)} bytes");
        sb.AppendLine();
    }

    // --------------------------------------------------------------------------
    // Load + chunk scan (shared by both modes)
    // --------------------------------------------------------------------------
    private static (byte[] raw, List<Chunk> chunks, byte[] merged) LoadFileAndChunks(string filepath)
    {
        byte[] raw = File.ReadAllBytes(filepath);
        var chunks = ScanChunks(raw);

        // merged stream — built exactly the way TrcParser.DecompressTrc builds it
        int mergedLen = 0;
        foreach (var c in chunks)
            if (c.Accepted)
            {
                c.MergedStart = mergedLen;
                mergedLen += c.Data.Length;
            }
        byte[] merged = new byte[mergedLen];
        foreach (var c in chunks)
            if (c.Accepted)
                Buffer.BlockCopy(c.Data, 0, merged, c.MergedStart, c.Data.Length);

        return (raw, chunks, merged);
    }

    // --------------------------------------------------------------------------
    // Chunk scanning: same zlib-candidate logic as TrcParser.DecompressTrc,
    // but every successfully decompressed chunk is recorded with its raw range.
    // --------------------------------------------------------------------------
    private static List<Chunk> ScanChunks(byte[] raw)
    {
        var chunks = new List<Chunk>();
        int consumedUntil = 0; // skip candidates already inside a resolved chunk

        for (int offset = 0; offset + 1 < raw.Length; offset++)
        {
            if (offset < consumedUntil)
                continue;
            if (!TrcParser.IsZlibStreamStart(raw[offset], raw[offset + 1]))
                continue;

            try
            {
                using var inMs = new MemoryStream(raw, offset, raw.Length - offset, writable: false);
                using var zs = new ZLibStream(inMs, CompressionMode.Decompress);
                using var chunkMs = new MemoryStream();
                zs.CopyTo(chunkMs);
                byte[] data = chunkMs.ToArray();

                var c = new Chunk
                {
                    No = chunks.Count + 1,
                    RawStart = offset,
                    Cmf = raw[offset],
                    Flg = raw[offset + 1],
                    Data = data,
                    Accepted = data.Length > TrcParser.MinChunkLength,
                };
                ResolveChunkEnd(raw, c);
                chunks.Add(c);

                if (c.RawEnd > c.RawStart)
                {
                    consumedUntil = c.RawEnd;
                    offset = c.RawEnd - 1; // continue after this chunk
                }
            }
            catch
            {
                // candidate inside random binary data — not a valid zlib stream
            }
        }
        return chunks;
    }

    // Locate the exact end of the zlib stream in the raw file
    private static void ResolveChunkEnd(byte[] raw, Chunk c)
    {
        uint adler = Adler32(c.Data);

        // (a) size field declared 4 bytes before the zlib header
        if (c.RawStart >= 4)
        {
            int declared = BitConverter.ToInt32(raw, c.RawStart - 4);
            if (declared >= 6)
            {
                int predicted = c.RawStart + declared;
                // trailer included in the declared size
                if (predicted >= 4 && predicted <= raw.Length && ReadBe32(raw, predicted - 4) == adler)
                {
                    c.RawEnd = predicted;
                    c.SizeFromHeader = true;
                    return;
                }
                // trailer just after the declared size
                if (predicted + 4 <= raw.Length && ReadBe32(raw, predicted) == adler)
                {
                    c.RawEnd = predicted + 4;
                    c.SizeFromHeader = true;
                    return;
                }
            }
        }

        // (b) scan for the big-endian Adler-32 trailer.
        // Upper bound: deflate worst case is ~1.032*N + 5 compressed bytes.
        int limit = Math.Min(raw.Length - 4, c.RawStart + c.Data.Length + c.Data.Length / 16 + 4096);
        for (int p = Math.Max(c.RawStart + 2, 0); p <= limit; p++)
        {
            if (ReadBe32(raw, p) == adler)
            {
                c.RawEnd = p + 4;
                return;
            }
        }
    }

    // --------------------------------------------------------------------------
    // Tree walk (shared): outline text for full mode + content/refs collections
    // --------------------------------------------------------------------------
    private static TreeInfo WalkTree(byte[] merged, List<Chunk> chunks)
    {
        var info = new TreeInfo();
        info.AcceptedChunks.AddRange(chunks.Where(c => c.Accepted));
        if (merged.Length == 0)
            return info;

        info.RootOffset = FindRootOffset(merged);
        if (info.RootOffset < 0)
            return info;

        Outline(merged, info.RootOffset, 0, "OtdrFile", info);
        return info;
    }

    private static void Outline(byte[] buf, int offset, int depth, string path, TreeInfo info)
    {
        var h = ReadHead(buf, offset);
        if (h == null || !info.Visited.Add(offset) || depth > TrcParser.MaxTreeDepth)
            return;

        info.TypeCount[h.Type] = info.TypeCount.GetValueOrDefault(h.Type) + 1;
        string indent = new string(' ', depth * 2);

        if (h.Type != (int)TrcParser.TrcNodeType.Container)
        {
            info.OutlineSb.AppendLine($"{indent}{h.Name}  [{TypeName(h.Type)}]  @{Hex(h.Offset)}");
            return;
        }

        info.OutlineSb.AppendLine($"{indent}{h.Name}  [Container, children {childCountOf(buf, h)}]  @{Hex(h.Offset)} {ChunkOf(h.Offset, info.AcceptedChunks)}");
        info.ContainersInTreeOrder.Add((h.Offset, h.Name));

        int valStart = h.Offset + TrcParser.NodeHeaderSize;
        int childCount = childCountOf(buf, h);

        var scalars = new List<NodeHead>();
        var subContainers = new List<int>();
        for (int i = 0; i < childCount; i++)
        {
            int cOff = BitConverter.ToInt32(buf, valStart + i * TrcParser.ChildRefSize);
            if (cOff == TrcParser.NoChildRef)
                continue;
            var ch = ReadHead(buf, cOff);
            if (ch == null)
                continue;
            if (ch.Type == (int)TrcParser.TrcNodeType.Container)
                subContainers.Add(cOff);
            else
            {
                scalars.Add(ch);
                if (ch.Type == (int)TrcParser.TrcNodeType.BinaryRef)
                    info.BinaryRefs.Add((ch, path));
            }
        }

        if (scalars.Count > 0)
        {
            foreach (var s in scalars)
            {
                if (info.Visited.Add(s.Offset))
                    info.TypeCount[s.Type] = info.TypeCount.GetValueOrDefault(s.Type) + 1;
            }
            info.OutlineSb.AppendLine($"{indent}  scalars: {GroupScalars(scalars)}");
        }

        foreach (int s in subContainers)
        {
            var sh = ReadHead(buf, s);
            Outline(buf, s, depth + 1, sh != null ? path + " > " + sh.Name : path, info);
        }
    }

    private static int childCountOf(byte[] buf, NodeHead h)
    {
        int valStart = h.Offset + TrcParser.NodeHeaderSize;
        return Math.Max(0, Math.Min(h.ValLen, buf.Length - valStart)) / TrcParser.ChildRefSize;
    }

    private static string GroupScalars(List<NodeHead> scalars)
    {
        var groups = new List<string>();
        foreach (var g in scalars.GroupBy(s => s.Type).OrderBy(g => g.Key))
        {
            string names = string.Join(", ", g.Select(s => s.Name));
            groups.Add($"{TypeName(g.Key)} x{g.Count()}: {names}");
        }
        return string.Join(" | ", groups);
    }

    // --------------------------------------------------------------------------
    // FULL mode sections
    // --------------------------------------------------------------------------
    private static void AppendRawLayout(StringBuilder sb, byte[] raw, List<Chunk> chunks)
    {
        sb.AppendLine("-- Raw file layout --");
        int pos = 0;
        bool sawSizePrefixGap = false;
        for (int i = 0; i < chunks.Count; i++)
        {
            var c = chunks[i];
            if (c.RawStart > pos)
                sawSizePrefixGap |= AppendGap(sb, raw, pos, c.RawStart, c);

            string status = c.SizeFromHeader ? "[size confirmed: size field + Adler-32]"
                           : c.RawEnd > 0 ? "[size located via Adler-32 scan]"
                                          : "[WARNING: chunk end NOT located]";
            sb.AppendLine($"0x{c.RawStart:X6}..0x{(c.RawEnd > 0 ? c.RawEnd : c.RawStart):X6}  {Num(c.RawSize),9} B  chunk #{c.No:D2}  zlib CMF=0x{c.Cmf:X2} FLG=0x{c.Flg:X2} (deflate, {FlgLevel(c.Flg)})  -> unpacked {Num(c.Data.Length),9} B  {status}");
            if (!c.Accepted)
                sb.AppendLine($"      (skipped by the analyzer: unpacked size <= {TrcParser.MinChunkLength} B)");

            pos = Math.Max(pos, c.RawEnd > 0 ? c.RawEnd : c.RawStart + 1);
        }
        if (pos < raw.Length)
            sawSizePrefixGap |= AppendGap(sb, raw, pos, raw.Length, null);
        if (sawSizePrefixGap)
            sb.AppendLine("  pattern: chunks after the first two are preceded by their 4-byte compressed size (int32)");
        sb.AppendLine();
    }

    // A gap between two chunks: describe it; when it is exactly 4 bytes and equals
    // the compressed size of the following chunk, label it as that size prefix and
    // return true (the hex preview is then omitted — the label already explains it).
    private static bool AppendGap(StringBuilder sb, byte[] raw, int start, int end, Chunk? nextChunk)
    {
        int len = end - start;
        string kind = "service data";
        bool isSizePrefix = false;

        int magicPos = IndexOfMagic(raw, start, end);
        if (magicPos >= 0)
            kind = $"record header \"{RecordMagicText}\"";
        else if (len == 4 && nextChunk != null && BitConverter.ToInt32(raw, start) == nextChunk.RawSize && nextChunk.RawSize > 0)
        {
            kind = $"int32 = compressed size of chunk #{nextChunk.No:D2}";
            isSizePrefix = true;
        }

        sb.AppendLine($"0x{start:X6}..0x{end:X6}  {Num(len),9} B  {kind}");

        if (magicPos >= 0)
        {
            var fields = new List<string>();
            foreach (int rel in HeaderInt32Fields)
            {
                int off = magicPos + rel;
                if (off + 4 <= end)
                    fields.Add($"int32@+{rel} = {Num(BitConverter.ToInt32(raw, off))}");
            }
            if (fields.Count > 0)
                sb.AppendLine($"      header fields: {string.Join("; ", fields)}");
        }

        // hex + ASCII preview — skipped for pure 4-byte size prefixes (self-explanatory)
        if (!isSizePrefix)
        {
            int previewLen = Math.Min(32, len);
            var hex = new StringBuilder();
            var text = new StringBuilder();
            for (int i = start; i < start + previewLen; i++)
            {
                hex.Append(raw[i].ToString("X2")).Append(' ');
                text.Append(raw[i] >= 32 && raw[i] < 127 ? (char)raw[i] : '.');
            }
            sb.AppendLine($"      preview: {hex}");
            sb.AppendLine($"             |{text}|");

            foreach (string s in ExtractPrintable(raw, start, end, minRun: 4, maxStrings: 4))
                sb.AppendLine($"      text found: \"{s}\"");
        }
        return isSizePrefix;
    }

    private static void AppendMergedLayout(StringBuilder sb, List<Chunk> chunks)
    {
        sb.AppendLine("-- Unpacked stream (what the analyzer parses) --");
        int mergedLen = 0;
        int accepted = 0, skipped = 0, skippedBytes = 0;
        foreach (var c in chunks)
        {
            if (c.Accepted)
            {
                accepted++;
                sb.AppendLine($"chunk #{c.No:D2}: raw 0x{c.RawStart:X6} -> unpacked {HexRange(c.MergedStart, c.MergedStart + c.Data.Length)} ({Num(c.Data.Length),9} B)");
                mergedLen += c.Data.Length;
            }
            else
            {
                skipped++;
                skippedBytes += c.Data.Length;
            }
        }
        sb.AppendLine($"total: {Num(mergedLen)} B from {accepted} chunks" +
                      (skipped > 0 ? $" ({skipped} chunk(s) skipped, {Num(skippedBytes)} B)" : ""));
        sb.AppendLine();
    }

    private static void AppendTree(StringBuilder sb, TreeInfo info)
    {
        sb.AppendLine("-- Node tree --");
        if (info.RootOffset < 0)
        {
            sb.AppendLine("(root node OtdrFile not found — cannot map the tree)");
            sb.AppendLine();
            return;
        }

        sb.AppendLine($"root node \"OtdrFile\" at unpacked offset 0x{info.RootOffset:X6} in {ChunkOf(info.RootOffset, info.AcceptedChunks)}");
        sb.AppendLine();
        sb.AppendLine(info.OutlineSb.ToString().TrimEnd());

        AppendBinaryRefsSection(sb, info);

        int total = 0;
        foreach (var kv in info.TypeCount)
            total += kv.Value;
        var parts = new List<string>();
        foreach (var kv in info.TypeCount.OrderBy(k => k.Key))
            parts.Add($"{TypeName(kv.Key)} {kv.Value}");
        sb.AppendLine($"node types in tree: {string.Join(", ", parts)}  (total {total})");
        sb.AppendLine();
    }

    // BinaryRef nodes are raw data blobs (CLSID GUIDs and RawSamples) — their
    // contents are NOT parsed by the analyzer; only offsets and sizes are shown.
    // Groups of identical small blobs (16 B CLSID GUID markers) are summarized
    // in one line to keep the output human-readable.
    private static void AppendBinaryRefsSection(StringBuilder sb, TreeInfo info)
    {
        if (info.BinaryRefs.Count == 0)
            return;
        sb.AppendLine();
        sb.AppendLine($"BinaryRef nodes (raw blobs, contents not parsed): {info.BinaryRefs.Count}");
        foreach (var g in info.BinaryRefs.GroupBy(b => b.Head.Name).OrderBy(g => g.Key))
        {
            var items = g.ToList();
            if (items.Count > 1 && items.All(b => b.Head.ValLen == ClsidGuidSize))
            {
                sb.AppendLine($"  {g.Key} x{items.Count}: {ClsidGuidSize} B GUID marker in each container (not listed individually)");
                continue;
            }
            sb.AppendLine($"  {g.Key} x{items.Count}:");
            foreach (var (head, parentPath) in items)
                sb.AppendLine($"    @{Hex(head.Offset)} {ChunkOf(head.Offset, info.AcceptedChunks)}  value size {Num(head.ValLen)} B  <- {parentPath}");
        }
    }

    // --------------------------------------------------------------------------
    // SHORT mode: address -> stores what
    // --------------------------------------------------------------------------
    private static void AppendContentsLayout(StringBuilder sb, byte[] raw, List<Chunk> chunks, TreeInfo info)
    {
        sb.AppendLine("-- File contents by address --");
        int pos = 0;
        for (int i = 0; i < chunks.Count; i++)
        {
            var c = chunks[i];
            if (c.RawStart > pos)
                AppendGapShort(sb, raw, pos, c.RawStart, c);

            if (c.Accepted)
            {
                var contained = info.ContainersInTreeOrder
                    .Where(n => n.Offset >= c.MergedStart && n.Offset < c.MergedStart + c.Data.Length)
                    .Select(n => n.Name)
                    .ToList();
                string stores = contained.Count > 0
                    ? "stores: " + string.Join(", ", contained)
                    : "(no node headers start here — continuation of scalar values of previous containers)";
                sb.AppendLine($"raw 0x{c.RawStart:X6}..0x{(c.RawEnd > 0 ? c.RawEnd : c.RawStart):X6}  {Num(c.RawSize),9} B  chunk #{c.No:D2} -> unpacked {HexRange(c.MergedStart, c.MergedStart + c.Data.Length)}  {stores}");
            }
            else
            {
                sb.AppendLine($"raw 0x{c.RawStart:X6}..0x{(c.RawEnd > 0 ? c.RawEnd : c.RawStart):X6}  {Num(c.RawSize),9} B  chunk #{c.No:D2} -> unpacked {Num(c.Data.Length)} B  service block (skipped by the analyzer)");
            }

            pos = Math.Max(pos, c.RawEnd > 0 ? c.RawEnd : c.RawStart + 1);
        }
        if (pos < raw.Length)
            AppendGapShort(sb, raw, pos, raw.Length, null);
        sb.AppendLine();
    }

    // Same gap classification as full mode, single line, no previews/fields
    private static void AppendGapShort(StringBuilder sb, byte[] raw, int start, int end, Chunk? nextChunk)
    {
        int len = end - start;
        string kind = "service data";

        int magicPos = IndexOfMagic(raw, start, end);
        if (magicPos >= 0)
            kind = $"record header \"{RecordMagicText}\"";
        else if (len == 4 && nextChunk != null && BitConverter.ToInt32(raw, start) == nextChunk.RawSize && nextChunk.RawSize > 0)
            kind = $"int32 = compressed size of chunk #{nextChunk.No:D2}";

        sb.AppendLine($"raw 0x{start:X6}..0x{end:X6}  {Num(len),9} B  {kind}");
    }

    private static void AppendTraceValuesSection(StringBuilder sb, TreeInfo info)
    {
        sb.AppendLine("-- Where the trace values live --");
        if (info.BinaryRefs.Count == 0)
        {
            sb.AppendLine("(no BinaryRef nodes found)");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("Point-by-point signal values (the trace curve itself) are stored in");
        sb.AppendLine("BinaryRef blobs named RawSamples. The analyzer does not decode these");
        sb.AppendLine("blobs — it reads the computed EventTable values (Position, Loss,");
        sb.AppendLine("Reflectance...) which are ordinary Double nodes in the tree.");
        foreach (var g in info.BinaryRefs.GroupBy(b => b.Head.Name).OrderBy(g => g.Key))
        {
            var items = g.ToList();
            if (items.Count > 1 && items.All(b => b.Head.ValLen == ClsidGuidSize))
            {
                sb.AppendLine($"  {g.Key} x{items.Count}: {ClsidGuidSize} B GUID marker in each container (not listed individually)");
                continue;
            }
            sb.AppendLine($"  {g.Key} x{items.Count}:");
            foreach (var (head, parentPath) in items)
                sb.AppendLine($"    @{Hex(head.Offset)} {ChunkOf(head.Offset, info.AcceptedChunks)}  declared blob size {Num(head.ValLen)} B  <- {parentPath}");
        }
        sb.AppendLine("  (each TraceN node holds one RawSamples blob; which wavelength a");
        sb.AppendLine("   trace has is in its Wavelength field — see the tree JSON)");
        sb.AppendLine();
    }

    // --------------------------------------------------------------------------
    // Node tree -> JSON file
    // --------------------------------------------------------------------------
    private static string SaveTreeJson(string filepath, long fileSize, long unpackedSize, List<Chunk> chunks, TreeInfo info)
    {
        byte[] merged = new byte[unpackedSize];
        foreach (var c in chunks)
            if (c.Accepted)
                Buffer.BlockCopy(c.Data, 0, merged, c.MergedStart, c.Data.Length);

        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine($"  {JsonLit("file")}: {JsonLit(Path.GetFileName(filepath))},");
        sb.AppendLine($"  {JsonLit("fileSizeBytes")}: {fileSize.ToString(CultureInfo.InvariantCulture)},");
        sb.AppendLine($"  {JsonLit("unpackedBytes")}: {unpackedSize.ToString(CultureInfo.InvariantCulture)},");
        sb.AppendLine($"  {JsonLit("rootOffset")}: {info.RootOffset.ToString(CultureInfo.InvariantCulture)},");
        sb.AppendLine($"  {JsonLit("tree")}:");
        sb.Append(JsonNode(merged, info.RootOffset, 2, info, new HashSet<int> { info.RootOffset }));
        sb.AppendLine();
        sb.AppendLine("}");

        string jsonPath = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(filepath)) ?? ".",
            Path.GetFileNameWithoutExtension(filepath) + ".nodetree.json");
        File.WriteAllText(jsonPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return jsonPath;
    }

    // JSON walk uses its own visited set (the console walk has already marked all nodes)
    private static string JsonNode(byte[] buf, int offset, int depth, TreeInfo info, HashSet<int> visited)
    {
        var h = ReadHead(buf, offset);
        if (h == null)
            return "null";

        string pad = new string(' ', depth * 2);
        string padIn = new string(' ', (depth + 1) * 2);
        var sb = new StringBuilder();
        sb.AppendLine($"{pad}{{");
        sb.AppendLine($"{padIn}{JsonLit("name")}: {JsonLit(h.Name)},");
        sb.AppendLine($"{padIn}{JsonLit("type")}: {JsonLit(TypeNameEn(h.Type))},");
        sb.AppendLine($"{padIn}{JsonLit("offset")}: {h.Offset.ToString(CultureInfo.InvariantCulture)},");
        sb.AppendLine($"{padIn}{JsonLit("chunk")}: {ChunkNoOf(h.Offset, info.AcceptedChunks).ToString(CultureInfo.InvariantCulture)},");
        sb.AppendLine($"{padIn}{JsonLit("valueSizeBytes")}: {h.ValLen.ToString(CultureInfo.InvariantCulture)},");

        if (h.Type == (int)TrcParser.TrcNodeType.Container)
        {
            int valStart = h.Offset + TrcParser.NodeHeaderSize;
            int childCount = childCountOf(buf, h);
            var children = new List<string>();
            for (int i = 0; i < childCount; i++)
            {
                int cOff = BitConverter.ToInt32(buf, valStart + i * TrcParser.ChildRefSize);
                if (cOff == TrcParser.NoChildRef)
                    continue;
                var ch = ReadHead(buf, cOff);
                if (ch == null || !visited.Add(cOff) || depth > TrcParser.MaxTreeDepth)
                    continue;
                children.Add(JsonNode(buf, cOff, depth + 1, info, visited));
            }
            sb.AppendLine($"{padIn}{JsonLit("children")}: [");
            sb.AppendLine(string.Join(",\n", children));
            sb.AppendLine($"{padIn}]");
        }
        else
        {
            sb.AppendLine($"{padIn}{JsonLit("value")}: {JsonValue(buf, h)}");
        }
        sb.Append($"{pad}}}");
        return sb.ToString();
    }

    // Parse a scalar node value for JSON output
    private static string JsonValue(byte[] buf, NodeHead h)
    {
        int valStart = h.Offset + TrcParser.NodeHeaderSize;
        if (valStart >= buf.Length)
            return "null";
        int actual = Math.Max(0, Math.Min(h.ValLen, buf.Length - valStart));

        if (h.Type == (int)TrcParser.TrcNodeType.Int32 && actual == sizeof(int))
            return BitConverter.ToInt32(buf, valStart).ToString(CultureInfo.InvariantCulture);

        if (h.Type == (int)TrcParser.TrcNodeType.Double && actual == sizeof(double))
        {
            double d = BitConverter.ToDouble(buf, valStart);
            // EXFO encodes "no value" as NaN — in JSON that is null
            return double.IsNaN(d) ? "null" : d.ToString("R", CultureInfo.InvariantCulture);
        }

        if (h.Type == (int)TrcParser.TrcNodeType.Utf16String)
        {
            int len = actual & ~1; // UTF-16: even length only
            string s = len == 0 ? "" : Encoding.Unicode.GetString(buf, valStart, len).TrimEnd('\0');
            return JsonLit(s);
        }

        if (h.Type == (int)TrcParser.TrcNodeType.Bool8 && actual == 1)
            return buf[valStart] != 0 ? "true" : "false";

        // BinaryRef and unknown types: raw blob, not decoded
        return "null";
    }

    private static string JsonLit(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                        sb.Append($"\\u{(int)c:X4}");
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    // --------------------------------------------------------------------------
    // Low-level helpers
    // --------------------------------------------------------------------------
    // Read the 16-byte node header and decode the name (same rules as TrcParser)
    private static NodeHead? ReadHead(byte[] buf, int offset)
    {
        if (offset <= 0 || offset + TrcParser.NodeHeaderSize > buf.Length)
            return null;

        int nameOffset = BitConverter.ToInt32(buf, offset);
        int type = BitConverter.ToInt32(buf, offset + 4);
        int valLen = BitConverter.ToInt32(buf, offset + 8);

        if (nameOffset < 0 || nameOffset >= buf.Length)
            return null;

        int window = Math.Min(TrcParser.MaxNameLength, buf.Length - nameOffset);
        int nullPos = Array.FindIndex(buf, nameOffset, window, b => b == 0);
        if (nullPos == -1)
            return null;

        return new NodeHead
        {
            Offset = offset,
            Name = TrcParser.DecodeAscii(buf, nameOffset, nullPos - nameOffset),
            Type = type,
            ValLen = valLen,
        };
    }

    // Same search as TrcParser.FindRootTree, but returns the verified root offset
    private static int FindRootOffset(byte[] stream)
    {
        int nameSearch = 0;
        while (nameSearch < stream.Length)
        {
            int relNamePos = stream.AsSpan(nameSearch).IndexOf("OtdrFile\0"u8);
            if (relNamePos < 0)
                break;
            int namePos = nameSearch + relNamePos;

            byte[] needle = BitConverter.GetBytes(namePos);
            int needleSearch = 0;
            while (needleSearch < stream.Length)
            {
                int relPos = stream.AsSpan(needleSearch).IndexOf(needle);
                if (relPos < 0)
                    break;
                int pos = needleSearch + relPos;

                var head = ReadHead(stream, pos);
                var (_, treeObj) = TrcParser.ReadNodeAt(stream, pos);
                if (head?.Name == "OtdrFile" && treeObj is Dictionary<string, object?>)
                    return pos;

                needleSearch = pos + 1;
            }
            nameSearch = namePos + 1;
        }
        return -1;
    }

    private static uint Adler32(byte[] data)
    {
        const uint Mod = 65521; // largest prime < 2^16 (Adler-32 modulus)
        uint a = 1, b = 0;
        foreach (byte x in data)
        {
            a = (a + x) % Mod;
            b = (b + a) % Mod;
        }
        return (b << 16) | a;
    }

    private static uint ReadBe32(byte[] b, int p)
        => p >= 0 && p + 4 <= b.Length
            ? ((uint)b[p] << 24) | ((uint)b[p + 1] << 16) | ((uint)b[p + 2] << 8) | b[p + 3]
            : 0;

    private static int IndexOfMagic(byte[] raw, int start, int end)
    {
        var span = raw.AsSpan(start, end - start);
        int rel = span.IndexOf(RecordMagic);
        return rel >= 0 ? start + rel : -1;
    }

    // Printable ASCII runs (for describing service gaps)
    private static IEnumerable<string> ExtractPrintable(byte[] raw, int start, int end, int minRun, int maxStrings)
    {
        var results = new List<string>();
        var current = new StringBuilder();
        for (int i = start; i < end && results.Count < maxStrings; i++)
        {
            byte b = raw[i];
            if (b >= 32 && b < 127)
                current.Append((char)b);
            else
            {
                if (current.Length >= minRun)
                    results.Add(current.ToString());
                current.Clear();
            }
        }
        if (current.Length >= minRun && results.Count < maxStrings)
            results.Add(current.ToString());
        return results;
    }

    private static string ChunkOf(int mergedOffset, List<Chunk> accepted)
    {
        foreach (var c in accepted)
            if (mergedOffset >= c.MergedStart && mergedOffset < c.MergedStart + c.Data.Length)
                return $"chunk #{c.No:D2}";
        return "(offset outside unpacked regions)";
    }

    private static int ChunkNoOf(int mergedOffset, List<Chunk> accepted)
    {
        foreach (var c in accepted)
            if (mergedOffset >= c.MergedStart && mergedOffset < c.MergedStart + c.Data.Length)
                return c.No;
        return 0; // 0 = outside unpacked regions
    }

    private static string HexRange(int start, int end) => $"0x{start:X6}..0x{end:X6}";
    private static string Hex(int v) => $"0x{v:X6}";
    private static string Num(long v) => v.ToString("N0", CultureInfo.InvariantCulture);

    // Localized type name for console output
    private static string TypeName(int type) => type switch
    {
        (int)TrcParser.TrcNodeType.Container => "Container",
        (int)TrcParser.TrcNodeType.Int32 => "Int32",
        (int)TrcParser.TrcNodeType.BinaryRef => "BinaryRef",
        (int)TrcParser.TrcNodeType.Double => "Double",
        (int)TrcParser.TrcNodeType.Utf16String => "Utf16String",
        (int)TrcParser.TrcNodeType.Bool8 => "Bool8",
        _ => $"unknown(type={type})",
    };

    // Unlocalized type name for the JSON file (stable for external tools)
    private static string TypeNameEn(int type) => type switch
    {
        (int)TrcParser.TrcNodeType.Container => "container",
        (int)TrcParser.TrcNodeType.Int32 => "int32",
        (int)TrcParser.TrcNodeType.BinaryRef => "binaryref",
        (int)TrcParser.TrcNodeType.Double => "double",
        (int)TrcParser.TrcNodeType.Utf16String => "utf16string",
        (int)TrcParser.TrcNodeType.Bool8 => "bool8",
        _ => $"unknown_{type}",
    };

    // Compression level bits of FLG for CMF=0x78: FLEVEL = FLG >> 6
    // (0=fastest .. 3=maximal); the checked EXFO files use FLG=0xDA -> FLEVEL=3
    private static string FlgLevel(byte flg) => (flg >> 6) switch
    {
        0 => "fastest",
        1 => "fast",
        2 => "default",
        _ => "maximal",
    };
}
