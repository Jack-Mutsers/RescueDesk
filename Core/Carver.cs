using System.IO;
using System.Buffers.Binary;
using System.Text;

namespace RescueDesk.Core;

/// <summary>
/// Signature-based recovery ("file carving"): walks the raw device sector by sector looking for known
/// file headers and works out each file's length from its internal structure. This needs no file system,
/// so it also works on formatted, RAW or badly corrupted partitions, but it cannot recover names or folders,
/// and fragmented files come back damaged.
/// </summary>
public static class Carver
{
    public sealed record FileType(string Key, string Description, long MaxSize);

    public static readonly IReadOnlyList<FileType> Types =
    [
        new("jpg", "JPEG photos", 128L << 20),
        new("png", "PNG images", 256L << 20),
        new("gif", "GIF images", 64L << 20),
        new("bmp", "BMP images", 256L << 20),
        new("pdf", "PDF documents", 1L << 30),
        new("office", "Office documents and ZIP archives (docx, xlsx, pptx, odt, zip…)", 4L << 30),
        new("7z", "7-Zip archives", 8L << 30),
        new("mp4", "Videos (mp4, mov, m4a, 3gp, heic)", 16L << 30),
        new("riff", "WAV audio, AVI video, WebP images", 4L << 30),
        new("sqlite", "SQLite databases", 4L << 30),
    ];

    /// <param name="types">Keys from <see cref="Types"/> to look for.</param>
    /// <param name="onFound">Called (on the scanning thread) for each file as soon as it is found.</param>
    /// <param name="isAllocated">When set, sectors for which this returns true (in use by existing files) are skipped.</param>
    public static List<FoundFile> Scan(RawDevice dev, IReadOnlySet<string> types, Func<long, bool>? isAllocated,
        IProgress<ScanProgress>? progress, CancellationToken ct, Action<FoundFile>? onFound = null)
    {
        var found = new List<FoundFile>();
        var cursor = new Cursor(dev);
        var buf = new byte[4 << 20];
        long skipUntil = 0;
        var lastReport = DateTime.MinValue;

        for (long pos = 0; pos < dev.Length; pos += buf.Length)
        {
            ct.ThrowIfCancellationRequested();
            int n = dev.Read(pos, buf);
            if (n <= 0) break;
            for (int i = 0; i + 16 <= n; i += 512)
            {
                long off = pos + i;
                if (off < skipUntil) continue;
                var head = buf.AsSpan(i, Math.Min(64, n - i));
                var kind = Identify(head, types);
                if (kind == null) continue;
                if (isAllocated != null && isAllocated(off)) continue;

                var type = Types.First(t => t.Key == kind);
                var (len, ext) = Measure(kind, cursor, off, type.MaxSize);
                if (len <= 0 || len > type.MaxSize) continue;

                var file = new FoundFile
                {
                    Name = $"{ext}_{off / 512:D10}.{ext}",
                    Folder = null,
                    Size = len,
                    Kind = FoundKind.Carved,
                    Condition = Condition.Unknown,
                    Extents = [new Extent(off, len)],
                    Note = "Assumes the file was stored in one piece",
                };
                found.Add(file);
                onFound?.Invoke(file);
                skipUntil = off + len;
            }

            if (DateTime.UtcNow - lastReport > TimeSpan.FromMilliseconds(250))
            {
                lastReport = DateTime.UtcNow;
                progress?.Report(new((double)(pos + n) / dev.Length,
                    $"Deep scan… {Format.Size(pos + n)} of {Format.Size(dev.Length)}", found.Count));
            }
        }
        progress?.Report(new(1, "Done", found.Count));
        return found;
    }

    static readonly byte[] PngMagic = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
    static readonly byte[] SevenZipMagic = [(byte)'7', (byte)'z', 0xBC, 0xAF, 0x27, 0x1C];

    static string? Identify(ReadOnlySpan<byte> h, IReadOnlySet<string> types)
    {
        string? k = h[0] switch
        {
            0xFF when h[1] == 0xD8 && h[2] == 0xFF && h[3] >= 0xC0 => "jpg",
            0x89 when h.StartsWith(PngMagic) => "png",
            (byte)'G' when h.StartsWith("GIF87a"u8) || h.StartsWith("GIF89a"u8) => "gif",
            (byte)'B' when h[1] == 'M' && BinaryPrimitives.ReadUInt32LittleEndian(h[6..]) == 0 => "bmp",
            (byte)'%' when h.StartsWith("%PDF-"u8) => "pdf",
            (byte)'P' when h.StartsWith("PK\x03\x04"u8) => "office",
            (byte)'7' when h.StartsWith(SevenZipMagic) => "7z",
            (byte)'R' when h.StartsWith("RIFF"u8) => "riff",
            (byte)'S' when h.StartsWith("SQLite format 3\0"u8) => "sqlite",
            0 when h[4..8].SequenceEqual("ftyp"u8) => "mp4",
            _ => null,
        };
        return k != null && types.Contains(k) ? k : null;
    }

    static (long Length, string Ext) Measure(string kind, Cursor c, long start, long max)
    {
        try
        {
            c.Limit = Math.Min(c.Device.Length, start + max);
            return kind switch
            {
                "jpg" => (Jpeg(c, start), "jpg"),
                "png" => (Png(c, start), "png"),
                "gif" => (Gif(c, start), "gif"),
                "bmp" => (Bmp(c, start), "bmp"),
                "pdf" => (Pdf(c, start), "pdf"),
                "office" => Zip(c, start),
                "7z" => (SevenZip(c, start), "7z"),
                "riff" => Riff(c, start),
                "sqlite" => (Sqlite(c, start), "db"),
                "mp4" => IsoMedia(c, start),
                _ => (0, ""),
            };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Truncated or garbage structure: not a recoverable file.
            return (0, "");
        }
    }

    static long Jpeg(Cursor c, long start)
    {
        long pos = start + 2;
        bool sawScan = false;
        while (true)
        {
            if (c.Byte(pos) != 0xFF) return 0;
            int marker = c.Byte(pos + 1);
            while (marker == 0xFF) marker = c.Byte(++pos + 1);
            if (marker == 0xD9) return sawScan ? pos + 2 - start : 0;
            if (marker is >= 0xD0 and <= 0xD7 or 0x01) { pos += 2; continue; }
            if (marker < 0xC0) return 0;
            int len = c.U16BE(pos + 2);
            if (len < 2) return 0;
            pos += 2 + len;
            if (marker != 0xDA) continue;

            // Entropy-coded scan data: runs until a marker other than stuffing (FF00) or restart (FFD0-D7).
            sawScan = true;
            while (true)
            {
                long ff = c.IndexOf([0xFF], pos);
                if (ff < 0) return 0;
                int next = c.Byte(ff + 1);
                if (next == 0x00 || next is >= 0xD0 and <= 0xD7) { pos = ff + 2; continue; }
                if (next == 0xFF) { pos = ff + 1; continue; }
                pos = ff;
                break;
            }
        }
    }

    static long Png(Cursor c, long start)
    {
        long pos = start + 8;
        while (true)
        {
            uint len = c.U32BE(pos);
            if (len > 0x7FFFFFFF) return 0;
            for (int i = 4; i < 8; i++)
                if (!char.IsAsciiLetter((char)c.Byte(pos + i))) return 0;
            bool end = c.Byte(pos + 4) == 'I' && c.Byte(pos + 5) == 'E' && c.Byte(pos + 6) == 'N' && c.Byte(pos + 7) == 'D';
            pos += 12 + len;
            if (end) return pos - start;
            if (pos >= c.Limit) return 0;
        }
    }

    static long Gif(Cursor c, long start)
    {
        long pos = start + 6;
        int flags = c.Byte(pos + 4);
        pos += 7;
        if ((flags & 0x80) != 0) pos += 3 * (2 << (flags & 7));
        while (true)
        {
            switch (c.Byte(pos))
            {
                case 0x3B: return pos + 1 - start;
                case 0x21: pos = SkipSubBlocks(c, pos + 2); break;
                case 0x2C:
                    int f = c.Byte(pos + 9);
                    pos += 10;
                    if ((f & 0x80) != 0) pos += 3 * (2 << (f & 7));
                    pos = SkipSubBlocks(c, pos + 1);
                    break;
                default: return 0;
            }
        }
    }

    static long SkipSubBlocks(Cursor c, long pos)
    {
        int n;
        while ((n = c.Byte(pos)) != 0) pos += n + 1;
        return pos + 1;
    }

    static long Bmp(Cursor c, long start)
    {
        uint size = c.U32LE(start + 2);
        uint dataOff = c.U32LE(start + 10);
        uint dib = c.U32LE(start + 14);
        if (dib is not (12 or 40 or 52 or 56 or 64 or 108 or 124)) return 0;
        if (size < 26 || dataOff < 14 + dib || dataOff >= size) return 0;
        return size;
    }

    static long Pdf(Cursor c, long start)
    {
        // PDFs edited in place get extra "%%EOF" sections appended, so keep going while more follow closely.
        long eof = c.IndexOf("%%EOF"u8, start + 5);
        if (eof < 0) return 0;
        long end = eof + 5;
        while (true)
        {
            long window = Math.Min(c.Limit, end + (8 << 20));
            long next = c.IndexOf("%%EOF"u8, end, window);
            if (next < 0 || c.IndexOf("%PDF-"u8, end, next) >= 0) break;
            end = next + 5;
        }
        if (c.Byte(end) == '\r') end++;
        if (c.Byte(end) == '\n') end++;
        return end - start;
    }

    static (long, string) Zip(Cursor c, long start)
    {
        long pos = start;
        long central = -1;
        while (pos < c.Limit)
        {
            uint sig = c.U32LE(pos);
            switch (sig)
            {
                case 0x04034B50:
                    int flags = c.U16LE(pos + 6);
                    uint csize = c.U32LE(pos + 18);
                    long data = pos + 30 + c.U16LE(pos + 26) + c.U16LE(pos + 28);
                    if ((flags & 8) == 0 && csize != 0xFFFFFFFF) { pos = data + csize; break; }
                    // Size follows the data (data descriptor): find the next zip record.
                    long next = data;
                    while (true)
                    {
                        long pk = c.IndexOf("PK"u8, next);
                        if (pk < 0) return (0, "");
                        int a = c.Byte(pk + 2), b = c.Byte(pk + 3);
                        if (a == 7 && b == 8)
                        {
                            pos = c.U32LE(pk + 16) == 0x04034B50 || c.U32LE(pk + 16) == 0x02014B50 ? pk + 16 : pk + 24;
                            break;
                        }
                        if ((a == 3 && b == 4) || (a == 1 && b == 2)) { pos = pk; break; }
                        next = pk + 1;
                    }
                    break;
                case 0x02014B50:
                    if (central < 0) central = pos;
                    pos += 46 + c.U16LE(pos + 28) + c.U16LE(pos + 30) + c.U16LE(pos + 32);
                    break;
                case 0x06064B50:
                    pos += 12 + (long)c.U64LE(pos + 4);
                    break;
                case 0x07064B50:
                    pos += 20;
                    break;
                case 0x06054B50:
                    long end = pos + 22 + c.U16LE(pos + 20);
                    return (end - start, ZipFlavor(c, start, central < 0 ? pos : central, pos));
                default:
                    return (0, "");
            }
        }
        return (0, "");
    }

    static string ZipFlavor(Cursor c, long start, long central, long eocd)
    {
        var first = new byte[Math.Min(120, c.U16LE(start + 26) + 70)];
        c.Read(start + 30, first);
        var head = Encoding.ASCII.GetString(first);
        if (head.StartsWith("mimetype", StringComparison.Ordinal))
        {
            if (head.Contains("opendocument.text")) return "odt";
            if (head.Contains("opendocument.spreadsheet")) return "ods";
            if (head.Contains("opendocument.presentation")) return "odp";
            if (head.Contains("epub")) return "epub";
        }
        var dir = new byte[(int)Math.Min(eocd - central, 1 << 20)];
        c.Read(central, dir);
        var names = Encoding.ASCII.GetString(dir);
        if (names.Contains("word/document.xml")) return "docx";
        if (names.Contains("xl/workbook.xml")) return "xlsx";
        if (names.Contains("ppt/presentation.xml")) return "pptx";
        if (names.Contains("META-INF/MANIFEST.MF")) return "jar";
        return "zip";
    }

    static long SevenZip(Cursor c, long start)
    {
        long off = (long)c.U64LE(start + 12), size = (long)c.U64LE(start + 20);
        if (off < 0 || size <= 0 || size > 1L << 30) return 0;
        return 32 + off + size;
    }

    static (long, string) Riff(Cursor c, long start)
    {
        long size = c.U32LE(start + 4) + 8L;
        var form = new byte[4];
        c.Read(start + 8, form);
        string ext = Encoding.ASCII.GetString(form) switch { "WAVE" => "wav", "AVI " => "avi", "WEBP" => "webp", _ => "" };
        return ext.Length == 0 || size < 12 ? (0, "") : (size, ext);
    }

    static long Sqlite(Cursor c, long start)
    {
        int page = c.U16BE(start + 16);
        if (page == 1) page = 65536;
        if (page < 512 || (page & (page - 1)) != 0) return 0;
        return (long)page * c.U32BE(start + 28);
    }

    static (long, string) IsoMedia(Cursor c, long start)
    {
        uint first = c.U32BE(start);
        if (first is < 8 or > 512) return (0, "");
        var brandBytes = new byte[4];
        c.Read(start + 8, brandBytes);
        string brand = Encoding.ASCII.GetString(brandBytes);

        long pos = start;
        bool moov = false, mdat = false;
        while (pos + 8 <= c.Limit)
        {
            long size = c.U32BE(pos);
            var t = new byte[4];
            c.Read(pos + 4, t);
            if (!t.All(b => b is >= 0x20 and <= 0x7E or 0xA9)) break;
            if (size == 1) size = (long)c.U64BE(pos + 8);
            if (size < 8) break;
            string type = Encoding.ASCII.GetString(t);
            moov |= type == "moov";
            mdat |= type == "mdat";
            pos += size;
        }
        if (!moov || pos > c.Limit) return (0, "");
        if (!mdat && brand is not ("heic" or "heix" or "mif1" or "avif")) return (0, "");
        string ext = brand switch
        {
            "qt  " => "mov",
            "M4A " or "M4B " => "m4a",
            "heic" or "heix" or "mif1" => "heic",
            "avif" => "avif",
            _ when brand.StartsWith("3g", StringComparison.Ordinal) => "3gp",
            _ => "mp4",
        };
        return (pos - start, ext);
    }

    /// <summary>Buffered random-access reader used while measuring a candidate file.</summary>
    sealed class Cursor(RawDevice dev)
    {
        const int BlockSize = 1 << 18;
        readonly byte[] _block = new byte[BlockSize];
        long _blockStart = -1;
        int _blockLen;

        public RawDevice Device => dev;
        public long Limit { get; set; }

        Span<byte> Load(long pos)
        {
            if (pos < 0 || pos >= Limit) throw new EndOfStreamException();
            if (_blockStart < 0 || pos < _blockStart || pos >= _blockStart + _blockLen)
            {
                _blockStart = pos - pos % 512;
                _blockLen = dev.Read(_blockStart, _block);
                if (_blockLen <= pos - _blockStart) throw new EndOfStreamException();
            }
            return _block.AsSpan((int)(pos - _blockStart), (int)Math.Min(_blockLen - (pos - _blockStart), Limit - pos));
        }

        public int Byte(long pos) => Load(pos)[0];

        public void Read(long pos, Span<byte> dst)
        {
            int done = 0;
            while (done < dst.Length)
            {
                var s = Load(pos + done);
                int n = Math.Min(s.Length, dst.Length - done);
                s[..n].CopyTo(dst[done..]);
                done += n;
            }
        }

        public ushort U16BE(long p) { Span<byte> b = stackalloc byte[2]; Read(p, b); return BinaryPrimitives.ReadUInt16BigEndian(b); }
        public ushort U16LE(long p) { Span<byte> b = stackalloc byte[2]; Read(p, b); return BinaryPrimitives.ReadUInt16LittleEndian(b); }
        public uint U32BE(long p) { Span<byte> b = stackalloc byte[4]; Read(p, b); return BinaryPrimitives.ReadUInt32BigEndian(b); }
        public uint U32LE(long p) { Span<byte> b = stackalloc byte[4]; Read(p, b); return BinaryPrimitives.ReadUInt32LittleEndian(b); }
        public ulong U64BE(long p) { Span<byte> b = stackalloc byte[8]; Read(p, b); return BinaryPrimitives.ReadUInt64BigEndian(b); }
        public ulong U64LE(long p) { Span<byte> b = stackalloc byte[8]; Read(p, b); return BinaryPrimitives.ReadUInt64LittleEndian(b); }

        public long IndexOf(ReadOnlySpan<byte> pattern, long from, long to = -1)
        {
            if (to < 0 || to > Limit) to = Limit;
            long pos = from;
            while (pos + pattern.Length <= to)
            {
                var s = Load(pos);
                if (s.Length > to - pos) s = s[..(int)(to - pos)];
                int i = s.IndexOf(pattern);
                if (i >= 0) return pos + i;
                if (s.Length < pattern.Length)
                {
                    // Pattern may straddle the block boundary: check byte by byte.
                    var tmp = new byte[pattern.Length];
                    Read(pos, tmp);
                    if (tmp.AsSpan().SequenceEqual(pattern)) return pos;
                    pos++;
                    continue;
                }
                pos += s.Length - pattern.Length + 1;
            }
            return -1;
        }
    }
}
