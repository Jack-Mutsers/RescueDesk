using System.IO;
using System.Buffers.Binary;
using System.Text;

namespace RescueDesk.Core;

public readonly record struct ScanProgress(double Fraction, string Status, int Found);

/// <summary>Geometry of an NTFS file system found on a device.</summary>
public sealed class NtfsVolume
{
    public long Offset { get; init; }
    public int ClusterSize { get; init; }
    public int RecordSize { get; init; }
    public long TotalClusters { get; init; }
    public long MftLcn { get; init; }
    public long MftMirrLcn { get; init; }

    public static NtfsVolume? FromBootSector(ReadOnlySpan<byte> b, long offset)
    {
        if (b.Length < 512 || !b.Slice(3, 8).SequenceEqual("NTFS    "u8)) return null;
        int bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(b[0x0B..]);
        int spc = b[0x0D];
        if (bytesPerSector is not (512 or 1024 or 2048 or 4096) || spc == 0) return null;
        int cluster = spc <= 0x80 ? spc * bytesPerSector : bytesPerSector << (256 - spc);
        sbyte cpr = (sbyte)b[0x40];
        int record = cpr > 0 ? cpr * cluster : 1 << -cpr;
        long totalSectors = BinaryPrimitives.ReadInt64LittleEndian(b[0x28..]);
        long mft = BinaryPrimitives.ReadInt64LittleEndian(b[0x30..]);
        long mirr = BinaryPrimitives.ReadInt64LittleEndian(b[0x38..]);
        long clusters = totalSectors * bytesPerSector / cluster;
        if (record is < 256 or > 65536 || mft <= 0 || mft >= clusters || cluster > 2 * 1024 * 1024) return null;
        return new NtfsVolume { Offset = offset, ClusterSize = cluster, RecordSize = record, TotalClusters = clusters, MftLcn = mft, MftMirrLcn = mirr };
    }

    /// <summary>
    /// Finds NTFS file systems on a device: the device itself (via the boot sector or its backup copy
    /// at the end), or the NTFS partitions listed in an MBR / GPT partition table.
    /// </summary>
    public static List<NtfsVolume> Find(RawDevice dev)
    {
        var sector = dev.ReadBytes(0, 512);
        if (FromBootSector(sector, 0) is { } v) return [v];

        var list = new List<NtfsVolume>();
        if (sector.Length == 512 && sector[510] == 0x55 && sector[511] == 0xAA)
        {
            bool gpt = false;
            for (int i = 0; i < 4; i++)
            {
                var e = sector.AsSpan(0x1BE + i * 16, 16);
                if (e[4] == 0xEE) gpt = true;
                else if (e[4] is 0x05 or 0x0F) AddExtended(dev, BinaryPrimitives.ReadUInt32LittleEndian(e[8..]) * 512L, list);
                else TryAdd(dev, BinaryPrimitives.ReadUInt32LittleEndian(e[8..]) * 512L, list);
            }
            if (gpt) AddGpt(dev, list);
        }
        if (list.Count > 0) return list;

        // Damaged boot sector: NTFS keeps a backup in the last sector of the volume.
        if (dev.Length > 4096 && FromBootSector(dev.ReadBytes(dev.Length - 512, 512), 0) is { } backup) return [backup];
        return list;
    }

    static void TryAdd(RawDevice dev, long offset, List<NtfsVolume> list)
    {
        if (offset <= 0 || offset >= dev.Length) return;
        if (FromBootSector(dev.ReadBytes(offset, 512), offset) is { } v && list.All(x => x.Offset != offset)) list.Add(v);
    }

    static void AddExtended(RawDevice dev, long start, List<NtfsVolume> list)
    {
        long ebr = start;
        for (int guard = 0; guard < 128 && ebr > 0 && ebr < dev.Length; guard++)
        {
            var s = dev.ReadBytes(ebr, 512);
            if (s.Length < 512 || s[510] != 0x55 || s[511] != 0xAA) return;
            TryAdd(dev, ebr + BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(0x1BE + 8)) * 512L, list);
            uint next = BinaryPrimitives.ReadUInt32LittleEndian(s.AsSpan(0x1CE + 8));
            if (next == 0) return;
            ebr = start + next * 512L;
        }
    }

    static void AddGpt(RawDevice dev, List<NtfsVolume> list)
    {
        foreach (int lba in new[] { dev.SectorSize, 512, 4096 }.Distinct())
        {
            var h = dev.ReadBytes(lba, 512);
            if (h.Length < 92 || !h.AsSpan(0, 8).SequenceEqual("EFI PART"u8)) continue;
            long entriesLba = BinaryPrimitives.ReadInt64LittleEndian(h.AsSpan(0x48));
            int count = Math.Min(BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(0x50)), 256);
            int size = BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(0x54));
            if (size < 128 || size > 4096) continue;
            var entries = dev.ReadBytes(entriesLba * lba, count * size);
            for (int i = 0; i + size <= entries.Length; i += size)
            {
                long first = BinaryPrimitives.ReadInt64LittleEndian(entries.AsSpan(i + 0x20));
                if (first > 0) TryAdd(dev, first * lba, list);
            }
            return;
        }
    }

    public long ClusterOffset(long lcn) => Offset + lcn * ClusterSize;
}

/// <summary>
/// Reads the NTFS Master File Table. Deleted files keep their MFT record (with name, size and data runs)
/// until NTFS reuses it, so they can be listed and their clusters copied out.
/// </summary>
public static class NtfsScanner
{
    const int RootRecord = 5;
    const int BitmapRecord = 6;

    sealed class Rec
    {
        public ushort Seq;
        public bool InUse, IsDir, Seen;
        public ulong ParentRef;
        public string? Name;
        public int NameRank = -1;
        public DateTime? Modified;
        public long Size;
        public byte[]? Resident;
        public List<(long Vcn, List<Run> Runs)>? Fragments;
        public bool Compressed, Encrypted;
        public int CompressionUnit;
    }

    public static List<FoundFile> Scan(RawDevice dev, NtfsVolume vol, bool includeExisting, string? prefix,
        IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var mftRuns = ReadMftRuns(dev, vol) ?? throw new InvalidDataException("The NTFS Master File Table could not be read.");
        long mftBytes = mftRuns.Sum(r => r.Clusters) * vol.ClusterSize;
        long count = mftBytes / vol.RecordSize;
        if (count > int.MaxValue) count = int.MaxValue;
        var recs = new Rec?[count];

        const int batchRecords = 1024;
        var buf = new byte[batchRecords * vol.RecordSize];
        long index = 0;
        int batches = 0;
        foreach (var run in mftRuns)
        {
            long runBytes = run.Clusters * vol.ClusterSize;
            for (long off = 0; off < runBytes && index < count; off += buf.Length)
            {
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min(buf.Length, runBytes - off);
                if (run.Lcn < 0) { index += n / vol.RecordSize; continue; }
                int got = dev.Read(vol.ClusterOffset(run.Lcn) + off, buf.AsSpan(0, n));
                for (int i = 0; i + vol.RecordSize <= got && index < count; i += vol.RecordSize, index++)
                    ParseRecord(buf.AsSpan(i, vol.RecordSize), (int)index, recs);
                if (++batches % 32 == 0)
                    progress?.Report(new(0.8 * index / count, $"Reading file records… {index:N0} of {count:N0}", 0));
            }
        }

        progress?.Report(new(0.85, "Checking which clusters are still free…", 0));
        var bitmap = LoadBitmap(dev, vol, recs);

        var paths = new Dictionary<int, string?>();
        var result = new List<FoundFile>();
        for (int i = 16; i < recs.Length; i++)
        {
            var r = recs[i];
            if (r == null || !r.Seen || r.IsDir || r.Name == null) continue;
            if (r.InUse && !includeExisting) continue;
            ct.ThrowIfCancellationRequested();

            string? folder = FolderOf(r, recs, paths, 0);
            if (folder != null && folder.StartsWith("$Extend", StringComparison.OrdinalIgnoreCase)) continue;
            if (prefix != null) folder = folder == null ? null : folder.Length == 0 ? prefix : prefix + "\\" + folder;

            var runs = r.Fragments?.OrderBy(f => f.Vcn).SelectMany(f => f.Runs).ToList() ?? [];
            var file = new FoundFile
            {
                Name = r.Name,
                Folder = folder,
                Size = r.Size,
                Modified = r.Modified,
                Kind = r.InUse ? FoundKind.Existing : FoundKind.Deleted,
                ResidentData = r.Resident,
                Extents = runs.Select(x => new Extent(x.Lcn < 0 ? -1 : vol.ClusterOffset(x.Lcn), x.Clusters * vol.ClusterSize)).ToList(),
                Compressed = r.Compressed && r.Resident == null
                    ? new CompressedLayout(runs, vol.ClusterSize, 1 << r.CompressionUnit, vol.Offset) : null,
                Note = r.Encrypted ? "Encrypted (EFS): content stays encrypted" : r.Compressed ? "NTFS compressed" : null,
            };
            file.Condition = r.InUse || r.Resident != null || r.Size == 0 ? Condition.Good : Assess(runs, bitmap);
            result.Add(file);
        }
        MarkOverlaps(result);
        progress?.Report(new(1, "Done", result.Count));
        return result;
    }

    /// <summary>
    /// Two deleted files claiming the same clusters means the later one was written over the earlier one
    /// (both are free again, so the bitmap can't tell). The older file gets downgraded.
    /// </summary>
    static void MarkOverlaps(List<FoundFile> files)
    {
        var spans = new List<(long Start, long End, FoundFile File)>();
        foreach (var f in files)
            if (f.Kind == FoundKind.Deleted && f.ResidentData == null)
                foreach (var e in f.Extents)
                    if (e.Offset >= 0) spans.Add((e.Offset, e.Offset + e.Length, f));
        spans.Sort((a, b) => a.Start.CompareTo(b.Start));

        var active = new List<(long End, FoundFile File)>();
        foreach (var s in spans)
        {
            active.RemoveAll(a => a.End <= s.Start);
            foreach (var a in active)
            {
                if (a.File == s.File) continue;
                var older = (a.File.Modified ?? DateTime.MinValue) <= (s.File.Modified ?? DateTime.MinValue) ? a.File : s.File;
                if (older.Condition == Condition.Good) older.Condition = Condition.Partial;
            }
            active.Add((s.End, s.File));
        }
    }

    /// <summary>Reads $Bitmap (one bit per cluster, set when in use) without scanning the whole MFT.</summary>
    public static byte[]? ReadClusterBitmap(RawDevice dev, NtfsVolume vol)
    {
        var mftRuns = ReadMftRuns(dev, vol);
        if (mftRuns == null) return null;
        long off = RecordOffset(mftRuns, BitmapRecord, vol);
        if (off < 0) return null;
        var rec = dev.ReadBytes(off, vol.RecordSize);
        if (rec.Length < vol.RecordSize || !ApplyFixups(rec)) return null;
        var recs = new Rec?[BitmapRecord + 1];
        ParseRecord(rec, BitmapRecord, recs, fixupsApplied: true);
        return LoadBitmap(dev, vol, recs);
    }

    static Condition Assess(List<Run> runs, byte[]? bitmap)
    {
        if (bitmap == null) return Condition.Unknown;
        long used = 0, total = 0;
        foreach (var r in runs)
        {
            if (r.Lcn < 0) continue;
            for (long c = r.Lcn; c < r.Lcn + r.Clusters; c++)
            {
                total++;
                long b = c >> 3;
                if (b >= bitmap.Length || (bitmap[b] >> (int)(c & 7) & 1) != 0) used++;
            }
        }
        if (total == 0) return Condition.Unknown;
        return used == 0 ? Condition.Good : used == total ? Condition.Overwritten : Condition.Partial;
    }

    static byte[]? LoadBitmap(RawDevice dev, NtfsVolume vol, Rec?[] recs)
    {
        if (recs.Length <= BitmapRecord || recs[BitmapRecord] is not { Fragments: not null } r) return null;
        var runs = r.Fragments.OrderBy(f => f.Vcn).SelectMany(f => f.Runs).ToList();
        var file = new FoundFile
        {
            Name = "$Bitmap",
            Size = r.Size,
            Extents = runs.Select(x => new Extent(x.Lcn < 0 ? -1 : vol.ClusterOffset(x.Lcn), x.Clusters * vol.ClusterSize)).ToList(),
        };
        if (file.Size <= 0 || file.Size > 512L * 1024 * 1024) return null;
        using var ms = new MemoryStream((int)file.Size);
        file.CopyTo(dev, ms);
        return ms.ToArray();
    }

    static string? FolderOf(Rec r, Rec?[] recs, Dictionary<int, string?> cache, int depth)
    {
        int parent = (int)(r.ParentRef & 0xFFFFFFFFFFFF);
        ushort parentSeq = (ushort)(r.ParentRef >> 48);
        if (parent == RootRecord) return "";
        if (depth > 64 || parent <= 0 || parent >= recs.Length) return null;
        if (cache.TryGetValue(parent, out var cached)) return cached;

        var p = recs[parent];
        string? path = null;
        // NTFS bumps the sequence number when a record is freed, so a deleted folder is one ahead of its children's reference.
        if (p is { Seen: true, IsDir: true, Name: not null } && (p.Seq == parentSeq || (!p.InUse && p.Seq == (ushort)(parentSeq + 1))))
        {
            var up = FolderOf(p, recs, cache, depth + 1);
            if (up != null) path = up.Length == 0 ? p.Name : up + "\\" + p.Name;
        }
        cache[parent] = path;
        return path;
    }

    static List<Run>? ReadMftRuns(RawDevice dev, NtfsVolume vol)
    {
        foreach (var lcn in new[] { vol.MftLcn, vol.MftMirrLcn })
        {
            var rec = dev.ReadBytes(vol.ClusterOffset(lcn), vol.RecordSize);
            if (rec.Length < vol.RecordSize || !ApplyFixups(rec)) continue;
            var recs = new Rec?[1];
            ParseRecord(rec, 0, recs, fixupsApplied: true);
            if (recs[0]?.Fragments is not { Count: > 0 } frags) continue;

            // A very fragmented $MFT lists further runs in extension records referenced from $ATTRIBUTE_LIST.
            foreach (var ext in AttributeListDataRecords(rec))
            {
                var all = frags.OrderBy(f => f.Vcn).SelectMany(f => f.Runs).ToList();
                long off = RecordOffset(all, ext, vol);
                if (off < 0) continue;
                var e = dev.ReadBytes(off, vol.RecordSize);
                if (e.Length < vol.RecordSize || !ApplyFixups(e)) continue;
                var tmp = new Rec?[1];
                ParseRecord(e, 0, tmp, fixupsApplied: true, ignoreBase: true);
                if (tmp[0]?.Fragments is { } more) frags.AddRange(more.Where(m => m.Vcn > 0 && frags.All(f => f.Vcn != m.Vcn)));
            }
            return frags.OrderBy(f => f.Vcn).SelectMany(f => f.Runs).ToList();
        }
        return null;
    }

    static long RecordOffset(List<Run> runs, long record, NtfsVolume vol)
    {
        long byteOffset = record * vol.RecordSize;
        foreach (var r in runs)
        {
            long len = r.Clusters * vol.ClusterSize;
            if (byteOffset < len) return r.Lcn < 0 ? -1 : vol.ClusterOffset(r.Lcn) + byteOffset;
            byteOffset -= len;
        }
        return -1;
    }

    static IEnumerable<long> AttributeListDataRecords(byte[] rec)
    {
        int off = BinaryPrimitives.ReadUInt16LittleEndian(rec.AsSpan(0x14));
        while (off + 16 <= rec.Length)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(rec.AsSpan(off));
            int len = BinaryPrimitives.ReadInt32LittleEndian(rec.AsSpan(off + 4));
            if (type == 0xFFFFFFFF || len <= 0 || off + len > rec.Length) yield break;
            if (type == 0x20 && rec[off + 8] == 0)
            {
                int vlen = BinaryPrimitives.ReadInt32LittleEndian(rec.AsSpan(off + 0x10));
                int voff = off + BinaryPrimitives.ReadUInt16LittleEndian(rec.AsSpan(off + 0x14));
                for (int p = voff; p + 0x1A <= voff + vlen && p + 0x1A <= rec.Length;)
                {
                    uint at = BinaryPrimitives.ReadUInt32LittleEndian(rec.AsSpan(p));
                    int elen = BinaryPrimitives.ReadUInt16LittleEndian(rec.AsSpan(p + 4));
                    if (elen == 0) break;
                    long refRec = (long)(BinaryPrimitives.ReadUInt64LittleEndian(rec.AsSpan(p + 0x10)) & 0xFFFFFFFFFFFF);
                    if (at == 0x80 && refRec != 0) yield return refRec;
                    p += elen;
                }
            }
            off += len;
        }
    }

    static bool ApplyFixups(Span<byte> rec)
    {
        if (!rec[..4].SequenceEqual("FILE"u8)) return false;
        int usaOff = BinaryPrimitives.ReadUInt16LittleEndian(rec[4..]);
        int usaCount = BinaryPrimitives.ReadUInt16LittleEndian(rec[6..]);
        if (usaCount < 2 || usaOff + usaCount * 2 > rec.Length || (usaCount - 1) * 512 > rec.Length) return false;
        for (int i = 1; i < usaCount; i++)
        {
            int end = i * 512 - 2;
            // A mismatch means a torn write; restore the bytes anyway and let the bounded parser make the best of it.
            rec[end] = rec[usaOff + i * 2];
            rec[end + 1] = rec[usaOff + i * 2 + 1];
        }
        return true;
    }

    static void ParseRecord(Span<byte> rec, int index, Rec?[] recs, bool fixupsApplied = false, bool ignoreBase = false)
    {
        if (!fixupsApplied && !ApplyFixups(rec)) return;
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(rec[0x16..]);
        ulong baseRef = BinaryPrimitives.ReadUInt64LittleEndian(rec[0x20..]) & 0xFFFFFFFFFFFF;
        int used = Math.Min(BinaryPrimitives.ReadInt32LittleEndian(rec[0x18..]), rec.Length);
        if (used <= 0) used = rec.Length;

        Rec r;
        bool isExtension = baseRef != 0 && !ignoreBase;
        if (isExtension)
        {
            if (baseRef >= (ulong)recs.Length) return;
            r = recs[baseRef] ??= new Rec();
        }
        else
        {
            r = recs[index] ??= new Rec();
            r.Seen = true;
            r.Seq = BinaryPrimitives.ReadUInt16LittleEndian(rec[0x10..]);
            r.InUse = (flags & 1) != 0;
            r.IsDir = (flags & 2) != 0;
        }

        int off = BinaryPrimitives.ReadUInt16LittleEndian(rec[0x14..]);
        while (off + 16 <= used)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(rec[off..]);
            int len = BinaryPrimitives.ReadInt32LittleEndian(rec[(off + 4)..]);
            if (type == 0xFFFFFFFF || len < 16 || off + len > used) break;
            var a = rec.Slice(off, len);
            bool nonResident = a[8] != 0;
            int nameLen = a[9];
            ushort aflags = BinaryPrimitives.ReadUInt16LittleEndian(a[0x0C..]);

            if (!nonResident && len >= 0x18)
            {
                int vlen = BinaryPrimitives.ReadInt32LittleEndian(a[0x10..]);
                int voff = BinaryPrimitives.ReadUInt16LittleEndian(a[0x14..]);
                if (voff + vlen <= len)
                {
                    var v = a.Slice(voff, vlen);
                    switch (type)
                    {
                        case 0x10 when vlen >= 0x10:
                            r.Modified = FileTime(BinaryPrimitives.ReadInt64LittleEndian(v[0x08..]));
                            break;
                        case 0x30 when vlen >= 0x42:
                            int nlen = v[0x40];
                            int ns = v[0x41];
                            int rank = ns switch { 1 or 3 => 2, 0 => 1, _ => 0 };
                            if (rank > r.NameRank && 0x42 + nlen * 2 <= vlen)
                            {
                                r.NameRank = rank;
                                r.Name = Encoding.Unicode.GetString(v.Slice(0x42, nlen * 2));
                                r.ParentRef = BinaryPrimitives.ReadUInt64LittleEndian(v);
                                r.Modified ??= FileTime(BinaryPrimitives.ReadInt64LittleEndian(v[0x10..]));
                            }
                            break;
                        case 0x80 when nameLen == 0:
                            r.Resident = v.ToArray();
                            r.Size = vlen;
                            break;
                    }
                }
            }
            else if (nonResident && type == 0x80 && nameLen == 0 && len >= 0x40)
            {
                long startVcn = BinaryPrimitives.ReadInt64LittleEndian(a[0x10..]);
                int runsOff = BinaryPrimitives.ReadUInt16LittleEndian(a[0x20..]);
                if (startVcn == 0)
                {
                    r.Size = BinaryPrimitives.ReadInt64LittleEndian(a[0x30..]);
                    r.Compressed = (aflags & 0x0001) != 0;
                    r.Encrypted = (aflags & 0x4000) != 0;
                    r.CompressionUnit = a[0x22];
                    if (r.Compressed && r.CompressionUnit is 0 or > 8) r.CompressionUnit = 4;
                }
                if (runsOff < len)
                {
                    r.Fragments ??= [];
                    if (r.Fragments.All(f => f.Vcn != startVcn)) r.Fragments.Add((startVcn, DecodeRuns(a[runsOff..])));
                }
            }
            off += len;
        }
    }

    static List<Run> DecodeRuns(ReadOnlySpan<byte> p)
    {
        var runs = new List<Run>();
        long lcn = 0;
        int i = 0;
        while (i < p.Length && p[i] != 0)
        {
            int lenBytes = p[i] & 0xF, offBytes = p[i] >> 4;
            i++;
            if (lenBytes == 0 || lenBytes > 8 || offBytes > 8 || i + lenBytes + offBytes > p.Length) break;
            long length = ReadVarInt(p.Slice(i, lenBytes), signed: false);
            i += lenBytes;
            if (offBytes == 0)
                runs.Add(new Run(-1, length));
            else
            {
                lcn += ReadVarInt(p.Slice(i, offBytes), signed: true);
                runs.Add(new Run(lcn, length));
            }
            i += offBytes;
            if (length <= 0) break;
        }
        return runs;
    }

    static long ReadVarInt(ReadOnlySpan<byte> b, bool signed)
    {
        long v = 0;
        for (int i = b.Length - 1; i >= 0; i--) v = v << 8 | b[i];
        if (signed && b.Length < 8 && (b[^1] & 0x80) != 0) v -= 1L << (8 * b.Length);
        return v;
    }

    static DateTime? FileTime(long ft)
    {
        if (ft <= 0 || ft > DateTime.MaxValue.ToFileTimeUtc()) return null;
        try { return DateTime.FromFileTimeUtc(ft).ToLocalTime(); }
        catch { return null; }
    }
}
