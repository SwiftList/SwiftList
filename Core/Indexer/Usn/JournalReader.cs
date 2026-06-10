using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SwiftList.Core.Indexer.Usn
{
    public class JournalReader
    {
        public (UInt128 RootFrn,
                Dictionary<UInt128, (string Name, UInt128 ParentFrn, bool IsDir)> SearchItems,
                long NextUsn, ulong JournalId)? IndexDrive(string drive)
        {
            Logger.Log($"[JournalReader] Indexing drive {drive}...");
            string volumePath = $"\\\\.\\{drive}:";
            using var handle = Win32Api.CreateFileW(
                volumePath,
                Win32Api.GENERIC_READ,
                Win32Api.FILE_SHARE_READ | Win32Api.FILE_SHARE_WRITE,
                IntPtr.Zero,
                Win32Api.OPEN_EXISTING,
                0,
                IntPtr.Zero
            );
            if (handle.IsInvalid)
            {
                Logger.Log($"[JournalReader] Failed to open drive {drive} handle.", LogLevel.Error);
                return null;
            }
            string fsType = VolumeHelper.GetFileSystemType(drive);
            var rootFrn = VolumeHelper.GetRootFrn(drive);
            if (!rootFrn.HasValue)
            {
                Logger.Log($"[JournalReader] Failed to resolve root FRN on {drive}.", LogLevel.Error);
                return null;
            }
            byte[] queryBuf = new byte[56];
            uint bytesReturned;
            bool success = Win32Api.DeviceIoControl(
                handle,
                Win32Api.FSCTL_QUERY_USN_JOURNAL,
                IntPtr.Zero, 0,
                queryBuf, (uint)queryBuf.Length,
                out bytesReturned,
                IntPtr.Zero
            );

            if (!success)
            {
                int err = Marshal.GetLastWin32Error();
                fsType = VolumeHelper.GetFileSystemType(drive);
                Logger.Log($"[JournalReader] Failed to query USN journal on {drive}. Error: {err}, FileSystem: {fsType}", LogLevel.Warn);

                if (fsType.Equals("NTFS", StringComparison.OrdinalIgnoreCase) || fsType.Equals("ReFS", StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Log($"[JournalReader] Attempting to create/activate USN journal on {fsType} drive {drive}...");
                    var createData = new Win32Api.CREATE_USN_JOURNAL_DATA
                    {
                        MaximumSize = 0,
                        AllocationDelta = 0
                    };
                    uint bytesReturnedCreate;
                    bool createSuccess = Win32Api.DeviceIoControl(
                        handle,
                        Win32Api.FSCTL_CREATE_USN_JOURNAL,
                        ref createData, (uint)Marshal.SizeOf<Win32Api.CREATE_USN_JOURNAL_DATA>(),
                        IntPtr.Zero, 0,
                        out bytesReturnedCreate,
                        IntPtr.Zero
                    );

                    if (createSuccess)
                    {
                        Logger.Log($"[JournalReader] USN journal successfully created/activated on {drive}. Retrying query...");
                        success = Win32Api.DeviceIoControl(
                            handle,
                            Win32Api.FSCTL_QUERY_USN_JOURNAL,
                            IntPtr.Zero, 0,
                            queryBuf, (uint)queryBuf.Length,
                            out bytesReturned,
                            IntPtr.Zero
                        );
                    }
                    else
                    {
                        int createErr = Marshal.GetLastWin32Error();
                        Logger.Log($"[JournalReader] Failed to create USN journal on {drive}. Error: {createErr}", LogLevel.Error);
                    }
                }
            }

            if (!success)
            {
                Logger.Log($"[JournalReader] Failed to query USN journal on {drive}.", LogLevel.Error);
                return null;
            }

            ulong journalId = BitConverter.ToUInt64(queryBuf, 0);
            long nextUsn = BitConverter.ToInt64(queryBuf, 16);

            if (fsType.Equals("ReFS", StringComparison.OrdinalIgnoreCase))
            {
                return ReFsScanner.ScanDrive(drive, handle, rootFrn.Value, journalId, nextUsn);
            }

            int bufSize = 1024 * 1024;
            byte[] outBuf = new byte[bufSize];
            ulong nextFrn = 0;

            var driveSearchItems = new Dictionary<UInt128, (string Name, UInt128 ParentFrn, bool IsDir)>();

            while (true)
            {
                var input = new Win32Api.MFT_ENUM_DATA_V0
                {
                    StartFileReferenceNumber = nextFrn,
                    LowUsn = 0,
                    HighUsn = nextUsn
                };

                ulong prevNextFrn = nextFrn;
                success = Win32Api.DeviceIoControl(
                    handle,
                    Win32Api.FSCTL_ENUM_USN_DATA,
                    ref input, (uint)Marshal.SizeOf<Win32Api.MFT_ENUM_DATA_V0>(),
                    outBuf, (uint)outBuf.Length,
                    out bytesReturned,
                    IntPtr.Zero
                );

                if (!success)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == Win32Api.ERROR_HANDLE_EOF)
                        break;

                    Logger.Log($"[JournalReader] FSCTL_ENUM_USN_DATA on {drive} failed. Error: {err}", LogLevel.Error);
                    break;
                }

                if (bytesReturned <= 8)
                    break;

                nextFrn = BitConverter.ToUInt64(outBuf, 0);
                if (nextFrn == prevNextFrn)
                    break;

                int offset = 8;
                int returnedSize = (int)bytesReturned;

                while (offset < returnedSize)
                {
                    if (offset + 4 > returnedSize)
                        break;

                    uint recordLen = BitConverter.ToUInt32(outBuf, offset);
                    if (recordLen == 0 || offset + recordLen > returnedSize)
                        break;

                    ReadOnlySpan<byte> recordSpan = new ReadOnlySpan<byte>(outBuf, offset, (int)recordLen);
                    try
                    {
                        var record = UsnRecordParser.ParseRecord(recordSpan);

                        driveSearchItems[record.FileReferenceNumber] = (record.FileName, record.ParentFileReferenceNumber, record.IsDirectory);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[JournalReader] Record parsing error on {drive}: {ex}", LogLevel.Error);
                    }

                    offset += (int)recordLen;
                }
            }

            Logger.Log($"[JournalReader] Drive {drive} enum complete: {driveSearchItems.Count} items.");
            return (rootFrn.Value, driveSearchItems, nextUsn, journalId);
        }

        public long CatchUpDrive(string drive, ulong journalId, long startUsn, Action<ParsedUsnRecord> onRecord)
        {
            Logger.Log($"[JournalReader] Catching up drive {drive} from USN {startUsn}...");
            string volumePath = $"\\\\.\\{drive}:";
            using var handle = Win32Api.CreateFileW(
                volumePath,
                Win32Api.GENERIC_READ,
                Win32Api.FILE_SHARE_READ | Win32Api.FILE_SHARE_WRITE,
                IntPtr.Zero,
                Win32Api.OPEN_EXISTING,
                0,
                IntPtr.Zero
            );

            if (handle.IsInvalid)
            {
                Logger.Log($"[JournalReader] Failed to open drive {drive} handle for catch-up.", LogLevel.Error);
                return -1;
            }

            byte[] queryBuf = new byte[56];
            uint bytesReturned;
            bool success = Win32Api.DeviceIoControl(
                handle,
                Win32Api.FSCTL_QUERY_USN_JOURNAL,
                IntPtr.Zero, 0,
                queryBuf, (uint)queryBuf.Length,
                out bytesReturned,
                IntPtr.Zero
            );

            if (!success)
            {
                Logger.Log($"[JournalReader] Failed to query USN journal for catch-up on {drive}.", LogLevel.Error);
                return -1;
            }

            ulong currentJournalId = BitConverter.ToUInt64(queryBuf, 0);
            long currentNextUsn = BitConverter.ToInt64(queryBuf, 16);

            if (currentJournalId != journalId)
            {
                Logger.Log($"[JournalReader] Journal ID mismatch on {drive} (expected {journalId}, got {currentJournalId}). Need full re-index.", LogLevel.Warn);
                return -1;
            }

            long currentUsn = startUsn;
            int bufSize = 256 * 1024;
            byte[] outBuf = new byte[bufSize];

            int changeCount = 0;

            while (currentUsn < currentNextUsn)
            {
                var input = new Win32Api.READ_USN_JOURNAL_DATA_V0
                {
                    StartUsn = currentUsn,
                    ReasonMask = 0xFFFFFFFF,
                    ReturnOnlyOnClose = 0,
                    Timeout = 0,
                    BytesToWaitFor = 0,
                    UsnJournalID = journalId
                };

                success = Win32Api.DeviceIoControl(
                    handle,
                    Win32Api.FSCTL_READ_USN_JOURNAL,
                    ref input, (uint)Marshal.SizeOf<Win32Api.READ_USN_JOURNAL_DATA_V0>(),
                    outBuf, (uint)outBuf.Length,
                    out bytesReturned,
                    IntPtr.Zero
                );

                if (!success)
                {
                    int err = Marshal.GetLastWin32Error();
                    Logger.Log($"[JournalReader] FSCTL_READ_USN_JOURNAL failed during catch-up on {drive}: {err}", LogLevel.Error);
                    return -1;
                }

                int returnedSize = (int)bytesReturned;
                if (returnedSize <= 8)
                    break;

                currentUsn = BitConverter.ToInt64(outBuf, 0);
                int offset = 8;

                while (offset < returnedSize)
                {
                    if (offset + 4 > returnedSize)
                        break;

                    uint recordLen = BitConverter.ToUInt32(outBuf, offset);
                    if (recordLen == 0 || offset + recordLen > returnedSize)
                        break;

                    ReadOnlySpan<byte> recordSpan = new ReadOnlySpan<byte>(outBuf, offset, (int)recordLen);
                    try
                    {
                        var record = UsnRecordParser.ParseRecord(recordSpan);
                        changeCount++;
                        onRecord(record);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[JournalReader] Catch-up record parse error on {drive}: {ex}", LogLevel.Error);
                    }

                    offset += (int)recordLen;
                }
            }

            Logger.Log($"[JournalReader] Catch-up complete for drive {drive}. Processed {changeCount} changes. Next USN: {currentUsn}");
            return currentUsn;
        }
    }
}
