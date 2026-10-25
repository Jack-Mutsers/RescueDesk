using System.IO;

namespace RescueDesk.Core;

public sealed record RecoveryResult(int Recovered, List<string> Failures);

public static class Recovery
{
    /// <summary>
    /// Copies the files to <paramref name="destination"/>, recreating their original folders.
    /// The source device is only ever read.
    /// </summary>
    public static RecoveryResult Run(RawDevice dev, IReadOnlyList<FoundFile> files, string destination,
        IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var failures = new List<string>();
        int done = 0;
        long totalBytes = Math.Max(1, files.Sum(f => f.Size)), copied = 0;
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new((double)copied / totalBytes, $"Recovering {f.Name}… ({done + 1} of {files.Count})", done));
            string folder = f.Kind == FoundKind.Carved ? Path.Combine("Found by signature", f.Extension)
                : f.Folder == null ? "Unknown folder" : f.Folder;
            string dir = Path.Combine(destination, SafeRelative(folder));
            string? path = null;
            try
            {
                Directory.CreateDirectory(dir);
                path = UniquePath(Path.Combine(dir, SafeName(f.Name)));
                using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                    f.CopyTo(dev, output, ct);
                if (f.Modified is { } m) File.SetLastWriteTime(path, m);
                done++;
            }
            catch (OperationCanceledException)
            {
                TryDelete(path);
                throw;
            }
            catch (Exception e)
            {
                TryDelete(path);
                failures.Add($"{f.Name}: {e.Message}");
            }
            copied += f.Size;
        }
        progress?.Report(new(1, "Done", done));
        return new RecoveryResult(done, failures);
    }

    static void TryDelete(string? path)
    {
        try { if (path != null) File.Delete(path); } catch { }
    }

    static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var s = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return s.Length == 0 ? "unnamed" : s;
    }

    static string SafeRelative(string folder) =>
        Path.Combine(folder.Split('\\',StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p is "." or ".." ? "_" : SafeName(p)).ToArray());

    static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!, name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            var p = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(p)) return p;
        }
    }
}
