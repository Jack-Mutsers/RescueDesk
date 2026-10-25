using RescueDesk.Core;

// Usage: RescueCli <image-or-device> [ntfs|carve] [--existing] [--out <dir>]
if (args.Length < 1)
{
    Console.WriteLine("Usage: RescueCli <image-or-device> [ntfs|carve] [--existing] [--out <dir>]");
    return 1;
}
string source = args[0];
string mode = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : "ntfs";
bool existing = args.Contains("--existing");
int outIdx = Array.IndexOf(args, "--out");
string? outDir = outIdx >= 0 ? args[outIdx + 1] : null;

using var dev = source.StartsWith(@"\\") ? RawDevice.OpenDevice(source) : RawDevice.OpenImage(source);
Console.WriteLine($"{source}: {Format.Size(dev.Length)}, sector {dev.SectorSize}");
var volumes = NtfsVolume.Find(dev);
foreach (var v in volumes)
    Console.WriteLine($"NTFS at {v.Offset}: cluster {v.ClusterSize}, record {v.RecordSize}, MFT LCN {v.MftLcn}");

var files = new List<FoundFile>();
if (mode == "ntfs")
{
    int i = 0;
    foreach (var v in volumes)
        files.AddRange(NtfsScanner.Scan(dev, v, existing, volumes.Count > 1 ? $"Partition {++i}" : null, null, default));
}
else
{
    var map = AllocationMap.Load(dev, volumes);
    files = Carver.Scan(dev, Carver.Types.Select(t => t.Key).ToHashSet(), args.Contains("--all") ? null : map.IsAllocated, null, default);
}

foreach (var f in files)
    Console.WriteLine($"{f.KindText,-10} {f.ConditionText,-18} {f.Size,12} {f.Modified:yyyy-MM-dd HH:mm} {f.FolderText}\\{f.Name} {f.Note}");
Console.WriteLine($"{files.Count} files");

if (outDir != null)
{
    var r = Recovery.Run(dev, files, outDir, null, default);
    Console.WriteLine($"Recovered {r.Recovered}, failures {r.Failures.Count}");
    foreach (var e in r.Failures) Console.WriteLine("  " + e);
}
return 0;
