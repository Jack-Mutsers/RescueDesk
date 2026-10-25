# RescueDesk

Recover deleted and lost files from a partition, a whole disk, or a disk image. The source is opened read-only and is never written to.

## Scan modes

- **Find deleted files** (fast, NTFS): reads the Master File Table and lists deleted files with their original names, folders, sizes and dates. It handles fragmented, sparse and NTFS-compressed files, and it finds NTFS partitions through MBR/GPT or the backup boot sector when the primary boot sector is damaged. Each file gets a condition rating:
  - *Good*: all of the file's clusters are still free.
  - *Partly / likely overwritten*: some or all of its clusters are now used by another file, or a newer deleted file took them over.
  - Tick **Also list files that still exist** to copy files off a drive that Windows can't open any more.
- **Deep scan** (slow, any file system): searches every sector for file signatures and uses each format's structure to work out the file's length. This works on formatted, RAW and FAT/exFAT drives. It recovers JPEG, PNG, GIF, BMP, PDF, ZIP/Office/OpenDocument, 7z, MP4/MOV/HEIC, WAV/AVI/WebP and SQLite files. Names and folders are lost, and files that were fragmented come back damaged. By default it skips clusters that existing NTFS files are using.

Recovered files keep their folder structure. RescueDesk refuses to save them onto the drive being recovered.

## Building

```
.\build-installer.ps1 -Version 1.0.0
```

This builds `dist\RescueDesk-Setup-<version>.exe`. It needs the .NET 10 SDK and Inno Setup 6. The app runs as administrator because it reads drives directly.

`tools\RescueCli` is a console harness for testing the engine against disk images, for example `dotnet run -- disk.img ntfs --out D:\out`. It is not shipped.
