namespace RescueDesk.Core;

public enum SourceKind { Volume, PhysicalDisk, Image }

/// <summary>Something that can be scanned: a partition, a whole disk or an image file.</summary>
public sealed record ScanSource(SourceKind Kind, string DevicePath, string? MountPoint, string Label, string FileSystem, long Length)
{
    public string Title => Kind switch
    {
        SourceKind.Volume when MountPoint != null => $"{MountPoint.TrimEnd('\\')}  {(Label.Length > 0 ? Label : "Local disk")}",
        SourceKind.Volume => $"Partition without drive letter{(Label.Length > 0 ? $" ({Label})" : "")}",
        SourceKind.PhysicalDisk => $"Physical disk {DevicePath[(DevicePath.IndexOf("Drive", StringComparison.Ordinal) + 5)..]} (entire disk)",
        _ => System.IO.Path.GetFileName(DevicePath),
    };

    public string Details => $"{(FileSystem.Length > 0 ? FileSystem : "RAW / unknown")} · {Format.Size(Length)}";

    public RawDevice Open() => Kind == SourceKind.Image ? RawDevice.OpenImage(DevicePath) : RawDevice.OpenDevice(DevicePath);

    /// <summary>The drive root ("D:\") this source lives on, used to refuse recovering onto the same drive.</summary>
    public string? DriveRoot => Kind switch
    {
        SourceKind.Volume => MountPoint,
        SourceKind.Image => System.IO.Path.GetPathRoot(DevicePath),
        _ => null,
    };
}

public static class SourceCatalog
{
    public static List<ScanSource> Enumerate()
    {
        var result = new List<ScanSource>();
        foreach (var guid in Native.EnumerateVolumeGuids())
        {
            var device = guid.TrimEnd('\\');
            var mount = Native.GetMountPoints(guid).FirstOrDefault();
            var info = Native.GetVolumeInfo(guid);
            long length;
            try
            {
                using var dev = RawDevice.OpenDevice(device);
                length = dev.Length;
            }
            catch { continue; } // CD drives without media, volumes we can't read, …
            if (length == 0) continue;
            result.Add(new ScanSource(SourceKind.Volume, device, mount, info?.Label ?? "", info?.FileSystem ?? "", length));
        }
        result.Sort((a, b) => (a.MountPoint ?? "~").CompareTo(b.MountPoint ?? "~"));

        for (int i = 0; i < 32; i++)
        {
            var path = $@"\\.\PhysicalDrive{i}";
            try
            {
                using var dev = RawDevice.OpenDevice(path);
                if (dev.Length > 0) result.Add(new ScanSource(SourceKind.PhysicalDisk, path, null, "", "", dev.Length));
            }
            catch { }
        }
        return result;
    }

    public static ScanSource FromImage(string path)
    {
        using var dev = RawDevice.OpenImage(path);
        return new ScanSource(SourceKind.Image, path, null, "", "Image file", dev.Length);
    }
}

public static class Format
{
    public static string Size(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes:N0} bytes" : $"{v:0.#} {units[u]}";
    }
}
