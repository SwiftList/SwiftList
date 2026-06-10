using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Runtime;
using SwiftList.Core.SearchIndex.RecordIndex;

namespace SwiftList.Core.Indexer.Usn
{
    public class UsnIndexer : IDisposable
    {
        public class IndexerStatus
        {
            public string State { get; set; } = "idle";
            public int Progress { get; set; } = 0;
            public int TotalFiles { get; set; } = 0;
            public int TotalDirs { get; set; } = 0;
            public double ElapsedTime { get; set; } = 0.0;
            public List<string> ActiveDrives { get; set; } = new();
            public List<DriveIndexStatus> Drives { get; set; } = new();
        }

        public class DriveIndexStatus
        {
            public string Drive { get; set; } = string.Empty;
            public bool Enabled { get; set; }
            public string Kind { get; set; } = "LocalNtfs";
            public string State { get; set; } = "unknown";
            public int Files { get; set; }
            public int Dirs { get; set; }
            public string CachePath { get; set; } = string.Empty;
        }

        internal readonly object _lockObj = new();
        internal readonly JournalReader _reader = new();
        internal readonly Dictionary<string, DriveRuntimeMetadata> _driveMetadata = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, RuntimeIndex> _recordIndexes = new(StringComparer.OrdinalIgnoreCase);

        public IndexerStatus Status { get; } = new();
        public object LockObj => _lockObj;

        internal sealed class DriveRuntimeMetadata
        {
            public FileRecordSourceKind SourceKind { get; init; }
            public FileRecordIdKind IdKind { get; init; }
            public UInt128 RootId { get; init; }
            public ulong JournalId { get; set; }
            public long NextUsn { get; set; }
        }

        public List<SearchResult> Search(string query, int limit = 500, CancellationToken token = default, string? directoryFilter = null)
        {
            return SearchCoordinator.Search(_recordIndexes, LockObj, query, limit, token, directoryFilter);
        }

        public void SearchStreaming(string query, int limit, Action<SearchResult> onResult, CancellationToken token = default, string? directoryFilter = null)
        {
            SearchCoordinator.SearchStreaming(_recordIndexes, LockObj, query, limit, onResult, token, directoryFilter);
        }

        public void SetDriveStatuses(IEnumerable<DriveIndexStatus> drives)
        {
            lock (LockObj)
            {
                Status.Drives = drives.ToList();
            }
        }

        public void SetDriveState(string drive, string state)
        {
            lock (LockObj)
            {
                var item = Status.Drives.FirstOrDefault(d => d.Drive.Equals(drive, StringComparison.OrdinalIgnoreCase));
                if (item != null)
                    item.State = state;
            }
        }

        public long CatchUpDrive(string drive, ulong journalId, long startUsn)
        {
            var changes = new List<ParsedUsnRecord>();
            long nextUsn = _reader.CatchUpDrive(drive, journalId, startUsn, changes.Add);
            if (nextUsn >= 0 && changes.Count > 0)
                ApplyUsnRecords(drive, changes);

            return nextUsn;
        }

        public void ApplyUsnRecord(string drive, ParsedUsnRecord record)
        {
            ApplyUsnRecords(drive, new[] { record });
        }

        public void ApplyUsnRecords(string drive, IReadOnlyList<ParsedUsnRecord> records)
        {
            lock (LockObj)
            {
                if (!_recordIndexes.TryGetValue(drive, out var runtime))
                    return;
                var namePool = new FileRecordNamePool();

                foreach (var record in records)
                {
                    if ((record.Reason & (Win32Api.USN_REASON_FILE_DELETE | Win32Api.USN_REASON_RENAME_OLD_NAME)) != 0)
                    {
                        runtime.Remove(ToSourceLocalId(record.FileReferenceNumber));
                        continue;
                    }

                    if ((record.Reason & (Win32Api.USN_REASON_FILE_CREATE | Win32Api.USN_REASON_RENAME_NEW_NAME)) == 0)
                        continue;

                    var flags = record.IsDirectory ? FileRecordFlags.Directory : FileRecordFlags.None;
                    var fileRecord = new FileRecord(
                        ToSourceLocalId(record.FileReferenceNumber),
                        ToSourceLocalId(record.ParentFileReferenceNumber),
                        namePool.Get(record.FileName),
                        flags);

                    runtime.Upsert(fileRecord);
                }

                UpdateTotalsFromRuntime();
                UpdateDriveCounts(drive);
            }
        }

        public void CompactMemory()
        {
            try
            {
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                Win32Api.TrimWorkingSet();
            }
            catch { }
        }

        public void ClearCaches()
        {
            SearchCoordinator.ClearCaches();
        }

        internal static DriveRuntimeMetadata CreateMetadata(FileRecordStore store)
        {
            return new DriveRuntimeMetadata
            {
                SourceKind = store.SourceKind,
                IdKind = store.IdKind,
                RootId = store.RootId,
                JournalId = store.JournalId,
                NextUsn = store.NextUsn
            };
        }

        private static UInt128 ToSourceLocalId(UInt128 value)
        {
            return value;
        }

        private void UpdateTotalsFromRuntime()
        {
            Status.TotalFiles = _recordIndexes.Values.Sum(r => r.TotalFiles);
            Status.TotalDirs = _recordIndexes.Values.Sum(r => r.TotalDirs);
        }

        internal void UpdateDriveCounts(string drive)
        {
            var item = Status.Drives.FirstOrDefault(d => d.Drive.Equals(drive, StringComparison.OrdinalIgnoreCase));
            if (item == null)
                return;

            if (_recordIndexes.TryGetValue(drive, out var runtime))
            {
                item.Files = runtime.TotalFiles;
                item.Dirs = runtime.TotalDirs;
            }
            item.State = "ready";
        }

        public void Dispose()
        {
            _driveMetadata.Clear();
            _recordIndexes.Clear();
        }
    }
}
