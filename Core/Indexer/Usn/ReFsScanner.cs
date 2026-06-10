using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SwiftList.Core.Indexer.Usn
{
    public static class ReFsScanner
    {
        public static (UInt128 RootFrn,
                Dictionary<UInt128, (string Name, UInt128 ParentFrn, bool IsDir)> SearchItems,
                long NextUsn, ulong JournalId)? ScanDrive(
            string drive,
            SafeFileHandle volumeHandle,
            UInt128 rootFrn,
            ulong journalId,
            long nextUsn)
        {
            Logger.Log($"[ReFsScanner] Starting ReFS initial scan for drive {drive}...");
            var driveSearchItems = new Dictionary<UInt128, (string Name, UInt128 ParentFrn, bool IsDir)>(32768);
            var dirQueue = new Queue<UInt128>(4096);
            dirQueue.Enqueue(rootFrn);

            // 1MB buffer
            int bufSize = 1024 * 1024;
            IntPtr bufPtr = Marshal.AllocHGlobal(bufSize);

            try
            {
                while (dirQueue.Count > 0)
                {
                    UInt128 currentDirId = dirQueue.Dequeue();

                    var fileIdDesc = new Win32Api.FILE_ID_DESCRIPTOR
                    {
                        dwSize = 24,
                        Type = 2, // ExtendedFileIdType
                        ExtendedFileId = new Win32Api.FILE_ID_128
                        {
                            Low = (ulong)currentDirId,
                            High = (ulong)(currentDirId >> 64)
                        }
                    };

                    using var dirHandle = Win32Api.OpenFileById(
                        volumeHandle,
                        ref fileIdDesc,
                        1, // FILE_LIST_DIRECTORY / GENERIC_READ
                        Win32Api.FILE_SHARE_READ | Win32Api.FILE_SHARE_WRITE | 4, // 4 = FILE_SHARE_DELETE
                        IntPtr.Zero,
                        Win32Api.FILE_FLAG_BACKUP_SEMANTICS
                    );

                    if (dirHandle.IsInvalid)
                        continue;

                    bool hasMore = true;
                    while (hasMore)
                    {
                        bool success = Win32Api.GetFileInformationByHandleEx(
                            dirHandle,
                            Win32Api.FileIdExtdDirectoryInfo, // 19
                            bufPtr,
                            (uint)bufSize
                        );

                        if (!success)
                            break;

                        IntPtr currentEntry = bufPtr;
                        while (true)
                        {
                            uint nextEntryOffset = (uint)Marshal.ReadInt32(currentEntry, 0);
                            uint fileAttributes = (uint)Marshal.ReadInt32(currentEntry, 56);
                            uint fileNameLength = (uint)Marshal.ReadInt32(currentEntry, 60);

                            ulong idLow = (ulong)Marshal.ReadInt64(currentEntry, 72);
                            ulong idHigh = (ulong)Marshal.ReadInt64(currentEntry, 80);
                            UInt128 fileId = new UInt128(idHigh, idLow);

                            IntPtr namePtr = currentEntry + 88;
                            string fileName = Marshal.PtrToStringUni(namePtr, (int)fileNameLength / 2);

                            if (fileName != "." && fileName != "..")
                            {
                                bool isDir = (fileAttributes & 0x10) != 0; // FILE_ATTRIBUTE_DIRECTORY
                                driveSearchItems[fileId] = (fileName, currentDirId, isDir);

                                if (isDir)
                                {
                                    dirQueue.Enqueue(fileId);
                                }
                            }

                            if (nextEntryOffset == 0)
                            {
                                hasMore = false;
                                break;
                            }
                            currentEntry += (int)nextEntryOffset;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[ReFsScanner] ReFS Scan error on {drive}: {ex.Message}", LogLevel.Error);
                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(bufPtr);
            }

            Logger.Log($"[ReFsScanner] ReFS Drive {drive} tree traversal complete: {driveSearchItems.Count} items.");
            return (rootFrn, driveSearchItems, nextUsn, journalId);
        }
    }
}
