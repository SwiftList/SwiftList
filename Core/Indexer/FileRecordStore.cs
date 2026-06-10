using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SwiftList.Core
{
    public enum FileRecordSourceKind : byte
    {
        LocalMft = 1,
        NetworkMappedDrive = 2
    }

    public enum FileRecordIdKind : byte
    {
        MftFrn = 1,
        SourceLocalId64 = 2
    }

    [Flags]
    public enum FileRecordFlags : ushort
    {
        None = 0,
        Directory = 1,
        Deleted = 2,
        SourceRoot = 4
    }

    public readonly struct FileRecord
    {
        public FileRecord(
            UInt128 id,
            UInt128 parentId,
            string name,
            FileRecordFlags flags)
        {
            Id = id;
            ParentId = parentId;
            Name = name;
            Flags = flags;
        }

        public UInt128 Id { get; }
        public UInt128 ParentId { get; }
        public string Name { get; }
        public FileRecordFlags Flags { get; }
        public bool IsDirectory => (Flags & FileRecordFlags.Directory) != 0;
        public bool IsDeleted => (Flags & FileRecordFlags.Deleted) != 0;
    }

    public sealed class FileRecordStore
    {
        public string SourceKey { get; set; } = string.Empty;
        public FileRecordSourceKind SourceKind { get; set; }
        public FileRecordIdKind IdKind { get; set; }
        public UInt128 RootId { get; set; }
        public ulong JournalId { get; set; }
        public long NextUsn { get; set; }
        public DateTime LastUpdated { get; set; } = DateTime.Now;
        public List<FileRecord> Records { get; } = new();
    }

    internal sealed class FileRecordNamePool
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, string> _pool = new(StringComparer.Ordinal);

        public string Get(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            lock (_lock)
            {
                if (_pool.TryGetValue(value, out string? pooled))
                    return pooled;

                _pool[value] = value;
                return value;
            }
        }
    }

    public static class FileRecordStoreSerializer
    {
        private const string MetaMagic = "SLRCMETA";
        private const string RecordsMagic = "SLRCREC";
        private const string NamesMagic = "SLRCNAME";
        private const int Version = 6;

        public static string GetBasePath(string cacheDir, string sourceKey)
        {
            return Path.Combine(cacheDir, $"source-{sourceKey.ToLowerInvariant()}");
        }

        public static bool Exists(string cacheDir, string sourceKey)
        {
            string basePath = GetBasePath(cacheDir, sourceKey);
            return File.Exists(basePath + ".meta") &&
                   File.Exists(basePath + ".records") &&
                   File.Exists(basePath + ".names");
        }

        public static void Save(string cacheDir, FileRecordStore store)
        {
            Directory.CreateDirectory(cacheDir);
            string basePath = GetBasePath(cacheDir, store.SourceKey);
            string metaTemp = basePath + ".meta.tmp";
            string recordsTemp = basePath + ".records.tmp";
            string namesTemp = basePath + ".names.tmp";

            using (var names = new FileStream(namesTemp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var records = new FileStream(recordsTemp, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024))
            using (var writer = new BinaryWriter(names, Encoding.UTF8))
            using (var recordWriter = new BinaryWriter(records, Encoding.UTF8))
            {
                writer.Write(NamesMagic);
                writer.Write(Version);
                recordWriter.Write(RecordsMagic);
                recordWriter.Write(Version);
                recordWriter.Write(store.Records.Count);
                for (int i = 0; i < store.Records.Count; i++)
                {
                    var record = store.Records[i];
                    writer.Write(record.Name);
                    recordWriter.Write((ulong)record.Id);
                    recordWriter.Write((ulong)(record.Id >> 64));
                    recordWriter.Write((ulong)record.ParentId);
                    recordWriter.Write((ulong)(record.ParentId >> 64));
                    recordWriter.Write((ushort)record.Flags);
                }
            }

            using (var meta = new FileStream(metaTemp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(meta, Encoding.UTF8))
            {
                writer.Write(MetaMagic);
                writer.Write(Version);
                writer.Write(store.SourceKey);
                writer.Write((byte)store.SourceKind);
                writer.Write((byte)store.IdKind);
                writer.Write((ulong)store.RootId);
                writer.Write((ulong)(store.RootId >> 64));
                writer.Write(store.JournalId);
                writer.Write(store.NextUsn);
                writer.Write(store.Records.Count);
                writer.Write(store.Records.Count(r => !r.IsDeleted));
                writer.Write(store.LastUpdated.ToUniversalTime().Ticks);
            }

            Replace(metaTemp, basePath + ".meta");
            Replace(recordsTemp, basePath + ".records");
            Replace(namesTemp, basePath + ".names");
        }

        public static FileRecordStore? Load(string cacheDir, string sourceKey)
        {
            string basePath = GetBasePath(cacheDir, sourceKey);
            try
            {
                if (!Exists(cacheDir, sourceKey))
                    return null;

                var store = new FileRecordStore();
                using (var meta = File.OpenRead(basePath + ".meta"))
                using (var reader = new BinaryReader(meta, Encoding.UTF8))
                {
                    if (reader.ReadString() != MetaMagic || reader.ReadInt32() != Version)
                        return null;

                    store.SourceKey = reader.ReadString();
                    store.SourceKind = (FileRecordSourceKind)reader.ReadByte();
                    store.IdKind = (FileRecordIdKind)reader.ReadByte();
                    ulong rootLow = reader.ReadUInt64();
                    ulong rootHigh = reader.ReadUInt64();
                    store.RootId = new UInt128(rootHigh, rootLow);
                    store.JournalId = reader.ReadUInt64();
                    store.NextUsn = reader.ReadInt64();
                    _ = reader.ReadInt32();
                    _ = reader.ReadInt32();
                    long ticks = reader.ReadInt64();
                    store.LastUpdated = new DateTime(ticks, DateTimeKind.Utc).ToLocalTime();
                }

                var names = new List<string>();
                var namePool = new FileRecordNamePool();
                using (var nameStream = File.OpenRead(basePath + ".names"))
                using (var reader = new BinaryReader(nameStream, Encoding.UTF8))
                {
                    if (reader.ReadString() != NamesMagic || reader.ReadInt32() != Version)
                        return null;

                    while (nameStream.Position < nameStream.Length)
                    {
                        names.Add(namePool.Get(reader.ReadString()));
                    }
                }

                using (var records = File.OpenRead(basePath + ".records"))
                using (var reader = new BinaryReader(records, Encoding.UTF8))
                {
                    if (reader.ReadString() != RecordsMagic || reader.ReadInt32() != Version)
                        return null;

                    int count = reader.ReadInt32();
                    store.Records.Capacity = count;
                    for (int i = 0; i < count; i++)
                    {
                        ulong idLow = reader.ReadUInt64();
                        ulong idHigh = reader.ReadUInt64();
                        ulong parentIdLow = reader.ReadUInt64();
                        ulong parentIdHigh = reader.ReadUInt64();
                        UInt128 id = new UInt128(idHigh, idLow);
                        UInt128 parentId = new UInt128(parentIdHigh, parentIdLow);
                        var flags = (FileRecordFlags)reader.ReadUInt16();
                        store.Records.Add(new FileRecord(
                            id,
                            parentId,
                            i < names.Count ? names[i] : string.Empty,
                            flags));
                    }
                }

                return store;
            }
            catch (Exception ex)
            {
                Logger.Log($"[FileRecordStore] Failed to load {basePath}: {ex.Message}", LogLevel.Error);
                return null;
            }
        }

        private static void Replace(string tempPath, string finalPath)
        {
            string backupPath = finalPath + ".bak";
            if (File.Exists(finalPath))
            {
                File.Replace(tempPath, finalPath, backupPath, ignoreMetadataErrors: true);
                TryDelete(backupPath);
            }
            else
            {
                File.Move(tempPath, finalPath, overwrite: true);
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }

    }
}
