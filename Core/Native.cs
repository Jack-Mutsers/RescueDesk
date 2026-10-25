using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RescueDesk.Core;

static class Native
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2;
    public const uint OPEN_EXISTING = 3;
    const uint IOCTL_DISK_GET_LENGTH_INFO = 0x0007405C;
    const uint IOCTL_DISK_GET_DRIVE_GEOMETRY = 0x00070000;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, int inSize, out long outBuf, int outSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, int inSize, [Out] byte[] outBuf, int outSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "FindFirstVolumeW")]
    static extern IntPtr FindFirstVolume(StringBuilder name, int len);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "FindNextVolumeW")]
    static extern bool FindNextVolume(IntPtr h, StringBuilder name, int len);

    [DllImport("kernel32.dll")]
    static extern bool FindVolumeClose(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetVolumePathNamesForVolumeNameW")]
    static extern bool GetVolumePathNamesForVolumeName(string volume, [Out] char[] names, int len, out int needed);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetVolumeInformationW")]
    static extern bool GetVolumeInformation(string root, StringBuilder label, int labelLen, out uint serial, out uint maxComponent,
        out uint flags, StringBuilder fs, int fsLen);

    public static long GetLength(SafeFileHandle h)
    {
        if (DeviceIoControl(h, IOCTL_DISK_GET_LENGTH_INFO, IntPtr.Zero, 0, out long len, 8, out _, IntPtr.Zero))
            return len;
        throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot determine the size of the device");
    }

    public static int GetSectorSize(SafeFileHandle h)
    {
        // DISK_GEOMETRY: Cylinders(8) MediaType(4) TracksPerCylinder(4) SectorsPerTrack(4) BytesPerSector(4)
        var buf = new byte[24];
        if (DeviceIoControl(h, IOCTL_DISK_GET_DRIVE_GEOMETRY, IntPtr.Zero, 0, buf, buf.Length, out _, IntPtr.Zero))
        {
            int s = BitConverter.ToInt32(buf, 20);
            if (s >= 512 && (s & (s - 1)) == 0) return s;
        }
        return 512;
    }

    public static List<string> EnumerateVolumeGuids()
    {
        var list = new List<string>();
        var sb = new StringBuilder(260);
        var h = FindFirstVolume(sb, sb.Capacity);
        if (h == new IntPtr(-1)) return list;
        try
        {
            do list.Add(sb.ToString());
            while (FindNextVolume(h, sb, sb.Capacity));
        }
        finally { FindVolumeClose(h); }
        return list;
    }

    public static List<string> GetMountPoints(string volumeGuid)
    {
        var buf = new char[1024];
        if (!GetVolumePathNamesForVolumeName(volumeGuid, buf, buf.Length, out _)) return [];
        return new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    public static (string Label, string FileSystem)? GetVolumeInfo(string root)
    {
        var label = new StringBuilder(261);
        var fs = new StringBuilder(261);
        if (!GetVolumeInformation(root, label, label.Capacity, out _, out _, out _, fs, fs.Capacity)) return null;
        return (label.ToString(), fs.ToString());
    }
}
