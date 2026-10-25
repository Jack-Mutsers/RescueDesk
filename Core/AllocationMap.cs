namespace RescueDesk.Core;

/// <summary>Tells which device offsets belong to clusters that NTFS currently has in use.</summary>
public sealed class AllocationMap
{
    readonly List<(NtfsVolume Volume, byte[] Bitmap)> _volumes = [];

    public int VolumeCount => _volumes.Count;

    public static AllocationMap Load(RawDevice dev, IEnumerable<NtfsVolume> volumes)
    {
        var map = new AllocationMap();
        foreach (var v in volumes)
            if (NtfsScanner.ReadClusterBitmap(dev, v) is { } bitmap) map._volumes.Add((v, bitmap));
        return map;
    }

    public bool IsAllocated(long offset)
    {
        foreach (var (v, bitmap) in _volumes)
        {
            long rel = offset - v.Offset;
            if (rel < 0 || rel >= v.TotalClusters * v.ClusterSize) continue;
            long c = rel / v.ClusterSize;
            return (c >> 3) < bitmap.Length && (bitmap[c >> 3] >> (int)(c & 7) & 1) != 0;
        }
        return false;
    }
}
