using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RescueDesk.Core;

/// <summary>
/// Read-only access to a volume, physical disk or disk image at arbitrary byte offsets.
/// Unreadable sectors (bad blocks) are returned as zeros instead of failing the whole read.
/// </summary>
public sealed unsafe class RawDevice : IDisposable
{
    const int BounceSize = 1 << 20;

    readonly SafeFileHandle _handle;
    readonly bool _aligned;
    readonly byte* _bounce;
    readonly Lock _lock = new();

    public string Path { get; }
    public long Length { get; }
    public int SectorSize { get; }
    public long UnreadableSectors { get; private set; }

    RawDevice(string path, SafeFileHandle handle, long length, int sectorSize, bool aligned)
    {
        Path = path;
        _handle = handle;
        Length = length;
        SectorSize = sectorSize;
        _aligned = aligned;
        if (aligned) _bounce = (byte*)NativeMemory.AlignedAlloc(BounceSize, 4096);
    }

    /// <summary>Opens a device path such as <c>\\.\D:</c>, <c>\\?\Volume{…}</c> or <c>\\.\PhysicalDrive1</c>.</summary>
    public static RawDevice OpenDevice(string devicePath)
    {
        var h = Native.CreateFile(devicePath, Native.GENERIC_READ, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
            IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid)
        {
            var err = Marshal.GetLastWin32Error();
            h.Dispose();
            throw err == 5
                ? new UnauthorizedAccessException("Access denied. RescueDesk must run as administrator to read drives.")
                : new Win32Exception(err, $"Cannot open {devicePath}");
        }
        try
        {
            var length = Native.GetLength(h);
            var sector = Native.GetSectorSize(h);
            return new RawDevice(devicePath, h, length, sector, aligned: true);
        }
        catch
        {
            h.Dispose();
            throw;
        }
    }

    /// <summary>Opens a raw disk or partition image (.img, .dd, .raw…).</summary>
    public static RawDevice OpenImage(string path)
    {
        var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return new RawDevice(path, h, RandomAccess.GetLength(h), 512, aligned: false);
    }

    /// <summary>Reads up to <paramref name="dest"/>.Length bytes at <paramref name="offset"/>; returns bytes read (short only at the end).</summary>
    public int Read(long offset, Span<byte> dest)
    {
        if (offset < 0 || offset >= Length) return 0;
        if (dest.Length > Length - offset) dest = dest[..(int)(Length - offset)];
        if (!_aligned) return ReadImage(offset, dest);

        lock (_lock)
        {
            int done = 0;
            while (done < dest.Length)
            {
                long pos = offset + done;
                long alignedStart = pos - pos % SectorSize;
                int skip = (int)(pos - alignedStart);
                int want = Math.Min(BounceSize, RoundUp(skip + dest.Length - done));
                want = (int)Math.Min(want, RoundUp((int)Math.Min(int.MaxValue, Length - alignedStart)));
                var buf = new Span<byte>(_bounce, want);
                ReadAlignedSafe(alignedStart, buf);
                int n = Math.Min(want - skip, dest.Length - done);
                buf.Slice(skip, n).CopyTo(dest[done..]);
                done += n;
            }
            return done;
        }
    }

    public byte[] ReadBytes(long offset, int count)
    {
        var b = new byte[count];
        int n = Read(offset, b);
        return n == count ? b : b[..n];
    }

    int RoundUp(int n) => (n + SectorSize - 1) / SectorSize * SectorSize;

    int ReadImage(long offset, Span<byte> dest)
    {
        int done = 0;
        while (done < dest.Length)
        {
            int n = RandomAccess.Read(_handle, dest[done..], offset + done);
            if (n <= 0) break;
            done += n;
        }
        return done;
    }

    void ReadAlignedSafe(long offset, Span<byte> buf)
    {
        try
        {
            ReadFull(offset, buf);
            return;
        }
        catch (IOException) { }

        // A bad sector somewhere in this block: retry one sector at a time and zero-fill the failures.
        for (int i = 0; i < buf.Length; i += SectorSize)
        {
            var s = buf.Slice(i, SectorSize);
            try { ReadFull(offset + i, s); }
            catch (IOException)
            {
                s.Clear();
                UnreadableSectors++;
            }
        }
    }

    void ReadFull(long offset, Span<byte> buf)
    {
        int done = 0;
        while (done < buf.Length)
        {
            int n = RandomAccess.Read(_handle, buf[done..], offset + done);
            if (n <= 0)
            {
                buf[done..].Clear();
                return;
            }
            done += n;
        }
    }

    public void Dispose()
    {
        _handle.Dispose();
        if (_bounce != null) NativeMemory.AlignedFree(_bounce);
    }
}
