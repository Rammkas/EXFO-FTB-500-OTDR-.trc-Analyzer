// ==============================================================================
// TrcParser.cs — low-level parser for the binary EXFO .trc format.
//
// .TRC FORMAT (reverse-engineered, confirmed on 4 reference FTB-500 files):
//
// 1) The file is a "mess": binary service data plus a sequence of zlib-compressed
//    chunks. Parsing: byte-by-byte scan for a zlib header (CMF/FLG), an attempted
//    decompression at each candidate, successfully decompressed chunks are
//    concatenated into a single unpacked stream.
//    zlib (RFC 1950): CMF=0x78 (deflate, 32K window), FLG chosen so that
//    (CMF*256+FLG) % 31 == 0. All checked EXFO FTB-500 .trc files contain exactly
//    9 chunks, all with FLG=0xDA (compression level 9, maximum).
//    IMPORTANT: .NET ZLibStream reads the underlying stream with a read-ahead
//    buffer, so its Position is NOT the end of the zlib stream — you cannot
//    "skip past" a decompressed chunk using Position (verified: on .NET 8 this
//    silently drops every chunk after the first one).
//
// 2) The unpacked stream is a tree of nodes. Each node: 16-byte header
//    [nameOffset:int32][dataType:int32][valLen:int32][selfOffset:int32] followed
//    by the value. selfOffset (field [12..16)) == offset+16 for all 640 nodes
//    examined; it is not used during parsing. All tree offsets are ABSOLUTE
//    (relative to the start of the unpacked stream).
//
// 3) dataType (confirmed by counting nodes in local_sample.trc):
//    0=Container (39 nodes: table of int32 child references), 1=Int32 (120),
//    2=BinaryRef (40: 16-byte CLSID GUID, RawSamples — skipped),
//    3=Double (362; NaN means "no value", e.g. Loss of the input connector),
//    4=Utf16String (79), 5=Bool8 (0 nodes — kept from the original Python parser).
//
// 4) Root discovery: find the ASCII string "OtdrFile\0" in the stream, then look
//    for its absolute 4-byte offset — that is the nameOffset field of the root
//    node header. The first match may be a coincidence inside binary data, so
//    every candidate is verified: the node must parse and its name must be
//    "OtdrFile".
//
// SAFEGUARDS: node names capped at 256 bytes; tree traversal uses a visited set
// and a depth limit of 200 (cyclic references in a corrupted file cannot cause
// StackOverflow); odd-length UTF-16 strings are truncated; negative valLen is
// rejected.
// ==============================================================================
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace ExfoTrcAnalyzer;

// ==============================================================================
// EXFO .trc format parsing:
//   1) unpacking zlib chunks;
//   2) reading the node tree by absolute offsets in the unpacked stream.
// Safeguards: bounded node names, cycle/depth-checked traversal, root node
// verified by its "OtdrFile" name.
// ==============================================================================
public static class TrcParser
{
    public const int MaxNameLength = 256;  // max node name length (guards against bogus offsets)
    public const int MaxTreeDepth = 200;   // max tree nesting depth (guards against cycles)
    public const int MinChunkLength = 50;  // ignore unpacked chunks that are too small

    // zlib (RFC 1950): each stream starts with a 2-byte CMF/FLG header.
    // CMF=0x78 — deflate method, 32K window. FLG is chosen so that
    // (CMF*256 + FLG) % 31 == 0 (checksum bits + compression level).
    // For CMF=0x78 the valid FLG values are: 0x01, 0x20, 0x3F, 0x5E, 0x7D, 0x9C,
    // 0xBB, 0xDA, 0xF9. All checked EXFO FTB-500 .trc files use FLG=0xDA (level 9).
    // This check is only a fast pre-filter before attempting decompression.
    public const byte ZlibCmf = 0x78;

    public static bool IsZlibStreamStart(byte cmf, byte flg)
        => cmf == ZlibCmf && ((cmf << 8 | flg) % 31) == 0;

    // Node header in the EXFO .trc tree — 16 bytes (empirically confirmed on a real
    // file: 640 nodes, identical layout for all):
    //   [0..4)   — absolute offset of the node name (ASCII string, 0x00 terminated);
    //   [4..8)   — data type (see TrcNodeType);
    //   [8..12)  — value length in bytes;
    //   [12..16) — absolute offset of the value area (always == offset + 16;
    //              not used during parsing).
    // The value itself starts right after the header, at offset + NodeHeaderSize.
    public const int NodeHeaderSize = 16;
    public const int ChildRefSize = 4; // size of a child node reference (int32)
    public const int NoChildRef = 0;   // 0 in a child table means "no reference"

    // Node data types. Numbering inherited from the original Python parser and
    // verified on a real .trc (local_sample.trc): Container 39, Int32 120, BinaryRef 40,
    // Double 362, Utf16String 79 nodes.
    public enum TrcNodeType
    {
        Container   = 0, // container: value = table of child offsets (int32 x N, N = valLen/4)
        Int32       = 1, // int32 (Count, Type, Status, CursorA, EventChanged...)
        BinaryRef   = 2, // binary reference: CLSID (GUID, 16 bytes), RawSamples (raw trace curve —
                         // exported by the RawTrace module, not used for analysis), returns null in the tree
        Double      = 3, // double (Position, Length, Loss, Reflectance...); NaN is treated as "no value"
        Utf16String = 4, // UTF-16LE string (Comment, Date, Identifier...); trailing '\0' is trimmed
        Bool8       = 5, // 1-byte bool — never seen on checked files, kept from the Python parser
    }

    // --------------------------------------------------------------------------
    // Unpack Zlib chunks from an EXFO .trc file
    // --------------------------------------------------------------------------
    public static byte[] DecompressTrc(string filepath)
    {
        byte[] data = File.ReadAllBytes(filepath);
        using var merged = new MemoryStream();

        for (int offset = 0; offset + 1 < data.Length; offset++)
        {
            if (!IsZlibStreamStart(data[offset], data[offset + 1]))
                continue;

            try
            {
                using var inMs = new MemoryStream(data, offset, data.Length - offset, writable: false);
                using var zs = new ZLibStream(inMs, CompressionMode.Decompress);
                using var chunkMs = new MemoryStream();
                zs.CopyTo(chunkMs);
                byte[] chunk = chunkMs.ToArray();
                if (chunk.Length > MinChunkLength)
                    merged.Write(chunk, 0, chunk.Length);
            }
            catch
            {
                // not a valid zlib stream — skip
                // IMPORTANT: do not try to "jump past" the decompressed stream via
                // inMs.Position: ZLibStream uses a read-ahead buffer, so Position is
                // not the end of the zlib stream.
            }
        }

        return merged.ToArray();
    }

    // --------------------------------------------------------------------------
    // Public reader of the 16-byte node header (name/type/value length) without
    // decoding the value — used by the display modules (FileMap, RawTrace).
    // Applies the same validity rules as ReadNodeAt (bounded name length,
    // null-terminated ASCII name, offset inside the buffer).
    // --------------------------------------------------------------------------
    public sealed record NodeHeaderInfo(int Offset, string Name, int Type, int ValLen);

    public static NodeHeaderInfo? ReadNodeHeader(byte[] buf, int offset)
    {
        if (offset <= 0 || offset + NodeHeaderSize > buf.Length)
            return null;

        int nameOffset = BitConverter.ToInt32(buf, offset);
        int type = BitConverter.ToInt32(buf, offset + 4);
        int valLen = BitConverter.ToInt32(buf, offset + 8);

        if (nameOffset < 0 || nameOffset >= buf.Length)
            return null;

        int window = Math.Min(MaxNameLength, buf.Length - nameOffset);
        int nullPos = Array.FindIndex(buf, nameOffset, window, b => b == 0);
        if (nullPos == -1)
            return null;

        return new NodeHeaderInfo(offset, DecodeAscii(buf, nameOffset, nullPos - nameOffset), type, valLen);
    }

    // --------------------------------------------------------------------------
    // Read an EXFO tree node at the given offset
    // --------------------------------------------------------------------------
    public static (string? Name, object? Value) ReadNodeAt(byte[] buf, int offset)
        => ReadNodeAt(buf, offset, new HashSet<int>(), 0);

    private static (string? Name, object? Value) ReadNodeAt(byte[] buf, int offset, HashSet<int> visited, int depth)
    {
        // offset 0 and out-of-buffer = "no node" (0 is used as an empty reference in child tables)
        if (offset <= 0 || offset + NodeHeaderSize > buf.Length)
            return (null, null);

        // protection against cyclic references and excessive depth (otherwise StackOverflow)
        if (depth > MaxTreeDepth || !visited.Add(offset))
            return (null, null);

        int nameOffset = BitConverter.ToInt32(buf, offset);
        int dataType = BitConverter.ToInt32(buf, offset + 4);
        int valLen = BitConverter.ToInt32(buf, offset + 8);

        if (nameOffset < 0 || nameOffset >= buf.Length)
            return (null, null);

        int nameWindow = Math.Min(MaxNameLength, buf.Length - nameOffset);
        int nullPos = Array.FindIndex(buf, nameOffset, nameWindow, b => b == 0);
        if (nullPos == -1)
            return (null, null); // name too long or missing — most likely a bogus offset

        string name = DecodeAscii(buf, nameOffset, nullPos - nameOffset);

        int valStart = offset + NodeHeaderSize;
        if (valStart >= buf.Length)
            return (name, null);
        if (valLen < 0)
            return (name, null); // corrupted value length

        int actualLen = Math.Min(valLen, buf.Length - valStart);

        if (dataType == (int)TrcNodeType.Container)
        {
            int childCount = actualLen / ChildRefSize;
            var obj = new Dictionary<string, object?>();
            for (int i = 0; i < childCount; i++)
            {
                int cOff = BitConverter.ToInt32(buf, valStart + i * ChildRefSize);
                if (cOff != NoChildRef)
                {
                    var (cName, cVal) = ReadNodeAt(buf, cOff, visited, depth + 1);
                    if (!string.IsNullOrEmpty(cName))
                        obj[cName!] = cVal;
                }
            }
            return (name, obj);
        }

        if (dataType == (int)TrcNodeType.Int32 && actualLen == sizeof(int))
        {
            int val = BitConverter.ToInt32(buf, valStart);
            return (name, val);
        }

        if (dataType == (int)TrcNodeType.Double && actualLen == sizeof(double))
        {
            double val = BitConverter.ToDouble(buf, valStart);
            // EXFO encodes "no value" as NaN (e.g. Loss of the input connector)
            return (name, double.IsNaN(val) ? null : val);
        }

        if (dataType == (int)TrcNodeType.Utf16String)
        {
            int len = actualLen & ~1; // UTF-16: even length only, otherwise ArgumentException
            string s = len == 0 ? "" : Encoding.Unicode.GetString(buf, valStart, len).TrimEnd('\0');
            return (name, s);
        }

        if (dataType == (int)TrcNodeType.Bool8 && actualLen == 1)
            return (name, buf[valStart] != 0);

        // TrcNodeType.BinaryRef (CLSID, RawSamples) and unknown types — value not needed
        return (name, null);
    }

    public static string DecodeAscii(byte[] buf, int start, int len)
    {
        var sb = new StringBuilder(len);
        for (int i = 0; i < len; i++)
        {
            byte b = buf[start + i];
            // 128 = boundary of the ASCII range; non-ASCII bytes are replaced with U+FFFD
            sb.Append(b < 128 ? (char)b : '\uFFFD');
        }
        return sb.ToString();
    }

    // --------------------------------------------------------------------------
    // Find the root node: the first occurrence of the 4-byte reference to
    // "OtdrFile\0" may be a coincidental match inside binary data, so each
    // candidate is verified: the node at that offset must parse and be named
    // "OtdrFile".
    // --------------------------------------------------------------------------
    public static Dictionary<string, object?> FindRootTree(byte[] stream)
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

                var (nodeName, treeObj) = ReadNodeAt(stream, pos);
                if (nodeName == "OtdrFile" && treeObj is Dictionary<string, object?> tree)
                    return tree; // verified root

                needleSearch = pos + 1;
            }

            nameSearch = namePos + 1;
        }

        throw new InvalidDataException("verified root node OtdrFile not found");
    }

    // --------------------------------------------------------------------------
    // Tree traversal helpers
    // --------------------------------------------------------------------------
    public static object? Get(Dictionary<string, object?> obj, string key)
        => obj.TryGetValue(key, out var v) ? v : null;

    public static Dictionary<string, object?> AsDict(object? v)
        => v as Dictionary<string, object?> ?? new Dictionary<string, object?>();

    public static double? AsDouble(object? v) => v switch
    {
        double d => d,
        int i => i,
        _ => null,
    };

    public static string ValueToString(object? v) => v switch
    {
        string s => s,
        int i => i.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString("G17", CultureInfo.InvariantCulture),
        _ => "",
    };
}
