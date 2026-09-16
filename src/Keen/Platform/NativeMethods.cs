using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Keen.Platform;

// 拿 NTFS 文件唯一标识(FileId),用于检测「打开的句柄已被原子改名剥离到旧 inode」。
internal static class NativeMethods
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile,
        out BY_HANDLE_FILE_INFORMATION lpFileInformation
    );

    internal readonly struct FileId : IEquatable<FileId>
    {
        public readonly uint VolumeSerial;
        public readonly ulong FileIndex; // (FileIndexHigh << 32) | FileIndexLow

        public FileId(uint volume, ulong index)
        {
            VolumeSerial = volume;
            FileIndex = index;
        }

        public bool Equals(FileId other) =>
            VolumeSerial == other.VolumeSerial && FileIndex == other.FileIndex;

        public override bool Equals(object? obj) => obj is FileId other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(VolumeSerial, FileIndex);
    }

    internal static FileId GetFileIdOrThrow(SafeFileHandle h)
    {
        if (!GetFileInformationByHandle(h, out var info))
            throw new Win32Exception();
        return new FileId(
            info.VolumeSerialNumber,
            ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow
        );
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public long CreationTime; // FILETIME(8 字节,这里不解析)
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
