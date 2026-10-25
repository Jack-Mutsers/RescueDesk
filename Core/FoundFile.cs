using System.IO;
using System.ComponentModel;

namespace RescueDesk.Core;

/// <summary>A run of file content on the device. <see cref="Offset"/> &lt; 0 means a sparse (all-zero) run.</summary>
public readonly record struct Extent(long Offset, long Length);

public enum FoundKind { Deleted, Existing, Carved }

public enum Condition { Good, Partial, Overwritten, Unknown }

public sealed class FoundFile : INotifyPropertyChanged
{
    public required string Name { get; init; }
    /// <summary>Folder the file was in, relative to the volume root ("" for root, null when unknown).</summary>
    public string? Folder { get; init; }
    public long Size { get; init; }
    public DateTime? Modified { get; init; }
    public FoundKind Kind { get; init; }
    public Condition Condition { get; set; } = Condition.Unknown;
    public string? Note { get; init; }

    /// <summary>Small files stored directly inside the NTFS file record.</summary>
    public byte[]? ResidentData { get; init; }
    public IReadOnlyList<Extent> Extents { get; init; } = [];
    /// <summary>NTFS-compressed data: extents are per compression unit and need LZNT1 decoding.</summary>
    public CompressedLayout? Compressed { get; init; }

    public string Extension => System.IO.Path.GetExtension(Name).TrimStart('.').ToUpperInvariant();
    public string KindText => Kind switch { FoundKind.Deleted => "Deleted", FoundKind.Existing => "Existing", _ => "Deep scan" };
    public string ConditionText => Condition switch
    {
        Condition.Good => "Good",
        Condition.Partial => "Partly overwritten",
        Condition.Overwritten => "Likely overwritten",
        _ => "Unknown",
    };
    public string SizeText => Format.Size(Size);
    public string FolderText => Folder != null ? "\\" + Folder : Kind == FoundKind.Carved ? "—" : "(unknown folder)";

    bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set { if (_isChecked != value) { _isChecked = value; PropertyChanged?.Invoke(this, new(nameof(IsChecked))); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Writes the file's content to <paramref name="output"/>.</summary>
    public void CopyTo(RawDevice device, Stream output, CancellationToken ct = default)
    {
        if (ResidentData != null)
        {
            output.Write(ResidentData, 0, (int)Math.Min(ResidentData.Length, Size));
            return;
        }
        if (Compressed != null)
        {
            Compressed.CopyTo(device, output, Size, ct);
            return;
        }

        long remaining = Size;
        var buf = new byte[1 << 20];
        foreach (var e in Extents)
        {
            long left = Math.Min(e.Length, remaining);
            long pos = e.Offset;
            while (left > 0)
            {
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min(buf.Length, left);
                if (e.Offset < 0) Array.Clear(buf, 0, n);
                else
                {
                    int got = device.Read(pos, buf.AsSpan(0, n));
                    if (got < n) Array.Clear(buf, got, n - got);
                    pos += n;
                }
                output.Write(buf, 0, n);
                left -= n;
                remaining -= n;
            }
            if (remaining <= 0) break;
        }
        // Runs ended early (damaged record): pad so the file has its recorded size.
        if (remaining > 0 && Extents.Count > 0)
        {
            Array.Clear(buf);
            while (remaining > 0)
            {
                int n = (int)Math.Min(buf.Length, remaining);
                output.Write(buf, 0, n);
                remaining -= n;
            }
        }
    }
}
