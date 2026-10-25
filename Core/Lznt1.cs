using System.IO;

namespace RescueDesk.Core;

/// <summary>A cluster run of a non-resident NTFS attribute. <see cref="Lcn"/> is -1 for sparse runs.</summary>
public readonly record struct Run(long Lcn, long Clusters);

/// <summary>Layout of an NTFS-compressed file: data is stored per compression unit (usually 16 clusters).</summary>
public sealed class CompressedLayout(IReadOnlyList<Run> runs, int clusterSize, int unitClusters, long volumeOffset)
{
    public void CopyTo(RawDevice device, Stream output, long size, CancellationToken ct)
    {
        int unitBytes = unitClusters * clusterSize;
        var unit = new byte[unitBytes];
        var packed = new byte[unitBytes];
        var lcns = new long[unitClusters];
        using var clusters = EnumerateClusters().GetEnumerator();
        long written = 0;
        while (written < size)
        {
            ct.ThrowIfCancellationRequested();
            int used = 0;
            for (int i = 0; i < unitClusters; i++)
            {
                lcns[i] = clusters.MoveNext() ? clusters.Current : -1;
                if (lcns[i] >= 0) used++;
            }

            if (used == 0) Array.Clear(unit);
            else
            {
                var target = used == unitClusters ? unit : packed;
                for (int i = 0, j = 0; i < unitClusters; i++)
                {
                    if (lcns[i] < 0) continue;
                    var span = target.AsSpan(j * clusterSize, clusterSize);
                    if (device.Read(volumeOffset + lcns[i] * clusterSize, span) < clusterSize) span.Clear();
                    j++;
                }
                if (used < unitClusters)
                {
                    Array.Clear(unit);
                    Lznt1.Decompress(packed.AsSpan(0, used * clusterSize), unit);
                }
            }

            int n = (int)Math.Min(unitBytes, size - written);
            output.Write(unit, 0, n);
            written += n;
        }
    }

    IEnumerable<long> EnumerateClusters()
    {
        foreach (var r in runs)
            for (long i = 0; i < r.Clusters; i++)
                yield return r.Lcn < 0 ? -1 : r.Lcn + i;
    }
}

/// <summary>Decoder for LZNT1, the compression NTFS uses for compressed files.</summary>
public static class Lznt1
{
    public static int Decompress(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int s = 0, d = 0;
        while (s + 2 <= src.Length && d < dst.Length)
        {
            int header = src[s] | src[s + 1] << 8;
            s += 2;
            if (header == 0) break;
            int chunkEnd = Math.Min(s + (header & 0xFFF) + 1, src.Length);
            int chunkStart = d;

            if ((header & 0x8000) == 0)
            {
                int n = Math.Min(chunkEnd - s, dst.Length - d);
                src.Slice(s, n).CopyTo(dst[d..]);
                d += n;
                s = chunkEnd;
                continue;
            }

            while (s < chunkEnd && d < dst.Length)
            {
                byte flags = src[s++];
                for (int bit = 0; bit < 8 && s < chunkEnd && d < dst.Length; bit++)
                {
                    if ((flags >> bit & 1) == 0)
                    {
                        dst[d++] = src[s++];
                        continue;
                    }
                    if (s + 2 > chunkEnd) { s = chunkEnd; break; }
                    int token = src[s] | src[s + 1] << 8;
                    s += 2;
                    int lengthBits = 12;
                    for (int p = d - chunkStart - 1; p >= 0x10; p >>= 1) lengthBits--;
                    int from = d - ((token >> lengthBits) + 1);
                    int length = (token & ((1 << lengthBits) - 1)) + 3;
                    if (from < chunkStart) return d; // corrupt chunk
                    for (int k = 0; k < length && d < dst.Length; k++) dst[d++] = dst[from++];
                }
            }
            s = chunkEnd;
        }
        return d;
    }
}
