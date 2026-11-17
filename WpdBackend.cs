using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using Native = UsbTransfer.WpdInterop;

namespace UsbTransfer
{
    internal sealed class UsbDevice
    {
        public string Id { get; set; }
        public string Name { get; set; }
    }

    internal sealed class UsbFile
    {
        public string DeviceId, ObjectId, PersistentId, Name, Folder;
        public DateTime? CreatedUtc, ModifiedUtc;
        public long Size = -1;
    }

    internal interface IUsbFileDiscovery
    {
        List<UsbFile> ListFiles(UsbDevice device, bool discover, CancellationToken ct, Action<UsbFile> onNewMedia);
    }

    internal interface IUsbInitialCatalog
    {
        List<UsbFile> ListFiles(UsbDevice device, bool discover, CancellationToken ct, Action<UsbFile> onNewMedia,
            Action<IReadOnlyList<UsbFile>> onCatalogBatch);
    }

    internal sealed class WpdBackend : IUsbTransport, IUsbFileDiscovery, IUsbInitialCatalog
    {
        private const string DeviceRoot = "DEVICE";
        private const string TransferFolder = "测试传输";
        private const string UploadFolder = "Download";
        private const int MaxFolderDepth = 5;
        private const int MaxUnfamiliarDepth = 3;
        private const int MaxUnfamiliarFoldersPerStorage = 128;
        private static readonly string[] MtpDateFormats = { "yyyyMMdd'T'HHmmss.FFFFFFFK", "yyyyMMdd'T'HHmmssK" };
        private static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;
        private static readonly HashSet<string> StandardRoots = new HashSet<string>(NameComparer)
        {
            "DCIM", "Pictures", "Movies", "Videos", "Video", "Download", "Downloads", "Documents", "Media",
            "MIUI", "Huawei", "Honor", "OPPO", "ColorOS", "vivo", "OnePlus", "Samsung", "Realme",
            "XRecorder", "Mobizen", "AZRecorder", "Logs", "Log", "录屏", "截图", "相机", "下载", "文档"
        };
        private static readonly HashSet<string> IgnoredFolders = new HashSet<string>(NameComparer)
        {
            "lost+found", "cache", "thumbnails", "RecycleBin", "$RECYCLE.BIN"
        };
        private static readonly HashSet<string> CaptureRoots = new HashSet<string>(NameComparer)
        {
            "DCIM", "Pictures", "Movies", "Videos", "Video", "XRecorder", "Mobizen", "AZRecorder"
        };
        private readonly Dictionary<string, FileCache> caches = new Dictionary<string, FileCache>(NameComparer);

        public List<UsbDevice> GetDevices()
        {
            var manager = Native.Create<Native.IDeviceManager>(Native.ManagerClass);
            try
            {
                manager.RefreshDeviceList();
                var devices = ReadDeviceIds(manager).Where(IsUsbId).Select(id => new UsbDevice
                {
                    Id = id,
                    Name = GetFriendlyName(manager, id)
                }).ToList();
                foreach (var removed in caches.Keys.Where(id => !devices.Any(d => NameComparer.Equals(id, d.Id))).ToList())
                    caches.Remove(removed);
                return devices;
            }
            finally { Native.Release(manager); }
        }

        public List<UsbFile> ListFiles(UsbDevice device, bool discover, CancellationToken ct)
        {
            return ListFiles(device, discover, ct, null);
        }

        public List<UsbFile> ListFiles(UsbDevice device, bool discover, CancellationToken ct, Action<UsbFile> onNewMedia)
        {
            return ListFiles(device, discover, ct, onNewMedia, null);
        }

        public List<UsbFile> ListFiles(UsbDevice device, bool discover, CancellationToken ct, Action<UsbFile> onNewMedia,
            Action<IReadOnlyList<UsbFile>> onCatalogBatch)
        {
            using (var session = new Session(device.Id, false))
            {
                var catalog = new Catalog(onCatalogBatch);
                FileCache cache;
                bool first = !caches.TryGetValue(device.Id, out cache);
                if (first)
                    cache = new FileCache();
                var nextEntries = new Dictionary<string, ObjectInfo>(StringComparer.Ordinal);
                var now = DateTime.UtcNow;
                var storages = session.Children(DeviceRoot, ct).Select(id => session.ReadObject(id, true))
                    .Where(item => item != null && item.IsStorage).OrderByDescending(item => item.StorageKind == 3).ToList();
                if (storages.Count == 0)
                    throw new IOException("手机还没有开放共享存储。请解锁手机并选择 USB「文件传输」。");

                bool checkAdditionalRoots = discover || first;
                foreach (var storage in storages)
                {
                    var visited = new HashSet<string>(StringComparer.Ordinal);
                    int unfamiliarBudget = MaxUnfamiliarFoldersPerStorage;
                    Walk(session, device.Id, storage.Id, storage.Name, 0, FolderScope.Root, checkAdditionalRoots,
                        cache, nextEntries, visited, catalog, now, ct, first ? null : onNewMedia, ref unfamiliarBudget);
                }

                cache.Entries = nextEntries;
                caches[device.Id] = cache;
                catalog.Publish();
                return catalog.Files;
            }
        }

        public bool IsReadable(UsbFile file, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using (var session = new Session(file.DeviceId, false))
                {
                    uint bufferSize = 0;
                    var stream = session.OpenRead(file, ref bufferSize, ct);
                    var count = Marshal.AllocCoTaskMem(sizeof(int));
                    try
                    {
                        Marshal.WriteInt32(count, 0);
                        stream.Read(new byte[1], 1, count);
                        return Marshal.ReadInt32(count) == 1;
                    }
                    finally
                    {
                        Marshal.FreeCoTaskMem(count);
                        Native.Release(stream);
                    }
                }
            }
            catch (COMException) { return false; }
            catch (IOException) { return false; }
        }

        public void Download(UsbFile file, Stream destination, Action<long> progress, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            using (var session = new Session(file.DeviceId, false))
            {
                uint bufferSize = 0;
                var stream = session.OpenRead(file, ref bufferSize, ct);
                var count = Marshal.AllocCoTaskMem(sizeof(int));
                try
                {
                    var buffer = new byte[TransferBufferSize(bufferSize)];
                    long transferred = 0;
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();
                        Marshal.WriteInt32(count, 0);
                        stream.Read(buffer, buffer.Length, count);
                        int read = Marshal.ReadInt32(count);
                        if (read == 0)
                            break;
                        destination.Write(buffer, 0, read);
                        transferred += read;
                        if (progress != null)
                            progress(transferred);
                    }
                    if (file.Size >= 0 && transferred != file.Size)
                        throw new IOException("文件大小在传输期间发生变化。请刷新列表后重试。");
                }
                finally
                {
                    Marshal.FreeCoTaskMem(count);
                    Native.Release(stream);
                }
            }
        }

        public void Upload(UsbDevice device, string localPath, Action<long> progress, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            using (var source = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var session = new Session(device.Id, true))
            {
                var storages = session.Children(DeviceRoot, ct).Select(id => session.ReadObject(id, true))
                    .Where(item => item != null && item.IsStorage)
                    .OrderByDescending(item => item.StorageKind == 3)
                    .ToList();
                if (storages.Count == 0)
                    throw new IOException("找不到可写入的手机存储。请检查手机上的 USB 连接模式。");

                var downloadId = session.GetOrCreateFolder(storages[0].Id, UploadFolder, ct);
                var folderId = session.GetOrCreateFolder(downloadId, TransferFolder, ct);
                var existingNames = new HashSet<string>(session.Children(folderId, ct)
                    .Select(id => session.ReadObject(id)).Where(item => item != null).Select(item => item.Name), NameComparer);
                var name = AvailableName(Path.GetFileName(localPath), existingNames);
                session.UploadFile(folderId, name, source, progress, ct);
            }
        }

        private static void Walk(Session session, string deviceId, string parentId, string path, int depth,
            FolderScope scope, bool discover, FileCache cache, Dictionary<string, ObjectInfo> next,
            HashSet<string> visited, Catalog catalog, DateTime now, CancellationToken ct, Action<UsbFile> onNewMedia,
            ref int unfamiliarBudget)
        {
            if (!visited.Add(parentId))
                return;

            List<string> children;
            try { children = session.Children(parentId, ct); }
            catch (COMException error)
            {
                if (depth != 0 && IsInaccessibleObject(error.ErrorCode)) return;
                throw;
            }
            var ordered = onNewMedia == null ? (IEnumerable<string>)children : PrioritizeChildren(children, cache);
            var folders = new List<ObjectInfo>();
            foreach (var id in ordered)
            {
                ct.ThrowIfCancellationRequested();
                ObjectInfo item;
                bool known = cache.Entries.TryGetValue(id, out item);
                bool report = false;
                if (!known || item.RecheckUtc <= now)
                {
                    var previous = item;
                    item = session.ReadObject(id);
                    if (item == null)
                        continue;
                    var date = item.ModifiedUtc ?? item.CreatedUtc;
                    bool added = !known && cache.Entries.Count != 0;
                    bool changed = known && (item.Size != previous.Size || item.ModifiedUtc != previous.ModifiedUtc ||
                        item.CreatedUtc != previous.CreatedUtc || !NameComparer.Equals(item.Name, previous.Name) ||
                        !StringComparer.Ordinal.Equals(item.PersistentId, previous.PersistentId));
                    bool recent = date.HasValue && date.Value >= now.AddMinutes(-2) && date.Value <= now.AddMinutes(10);
                    bool awaitingContent = item.Size == 0 && (added || recent ||
                        (known && previous.FrequentUntilUtc != DateTime.MinValue));
                    item.FrequentUntilUtc = added || changed || recent ? now.AddSeconds(45) :
                        (known ? previous.FrequentUntilUtc : DateTime.MinValue);
                    item.RecheckUtc = !item.IsFolder && (awaitingContent || item.FrequentUntilUtc > now) ?
                        now.AddSeconds(1) : StaggeredRecheck(now, id);
                    report = added || changed;
                }
                next[id] = item;

                if (item.IsFolder)
                {
                    if (depth < MaxFolderDepth && !ShouldIgnore(item.Name)) folders.Add(item);
                    continue;
                }

                if (!item.IsFunctional && !string.IsNullOrWhiteSpace(item.Name) &&
                    !item.Name.StartsWith(".", StringComparison.Ordinal) && scope != FolderScope.AndroidContainer)
                {
                    var file = new UsbFile
                    {
                        DeviceId = deviceId, ObjectId = id, PersistentId = item.PersistentId, Name = item.Name,
                        Folder = path, Size = item.Size, CreatedUtc = item.CreatedUtc, ModifiedUtc = item.ModifiedUtc
                    };
                    var kind = scope == FolderScope.Unfamiliar || scope == FolderScope.AppMedia || report && onNewMedia != null
                        ? TransferCore.Classify(file) : FileKind.Other;
                    if ((scope == FolderScope.Unfamiliar || scope == FolderScope.AppMedia) &&
                        !IsRelevantInUnfamiliarFolder(kind)) continue;
                    catalog.Add(file);
                    if (report && onNewMedia != null && (kind == FileKind.Recording || kind == FileKind.Screenshot))
                        onNewMedia(file);
                }
            }

            // Read siblings' names before descending so a large Camera album cannot delay sibling recordings.
            foreach (var folder in folders.OrderBy(item => FolderPriority(item.Name))
                .ThenByDescending(item => cache.CandidateFolders.Contains(item.Id)))
            {
                var childScope = ChildScope(scope, folder.Name);
                if (!childScope.HasValue) continue;
                bool limited = childScope == FolderScope.Unfamiliar || childScope == FolderScope.AppMedia;
                if (limited)
                {
                    bool tooDeep = childScope == FolderScope.Unfamiliar && depth >= MaxUnfamiliarDepth;
                    bool shallowOrRecognized = childScope == FolderScope.Unfamiliar ? depth == 0 :
                        depth <= 2 || IsStandardFolder(folder.Name);
                    if (tooDeep || unfamiliarBudget == 0 ||
                        (!shallowOrRecognized && !discover && !cache.CandidateFolders.Contains(folder.Id))) continue;
                    unfamiliarBudget--;
                }
                var childPath = string.IsNullOrEmpty(path) ? folder.Name : path + "/" + folder.Name;
                int countBefore = catalog.Files.Count;
                Walk(session, deviceId, folder.Id, childPath, depth + 1, childScope.Value, discover, cache, next,
                    visited, catalog, now, ct, onNewMedia, ref unfamiliarBudget);
                if (limited && catalog.Files.Count > countBefore)
                    cache.CandidateFolders.Add(folder.Id);
            }
        }

        private enum FolderScope { Root, Shared, Unfamiliar, AndroidContainer, AppMedia }

        private static FolderScope? ChildScope(FolderScope parent, string name)
        {
            if (parent == FolderScope.AndroidContainer)
                return NameComparer.Equals(name, "media") ? FolderScope.AppMedia : (FolderScope?)null;
            if (NameComparer.Equals(name, "Android"))
                return parent == FolderScope.Root ? FolderScope.AndroidContainer : (FolderScope?)null;
            if (parent == FolderScope.AppMedia)
                return FolderScope.AppMedia;
            return parent == FolderScope.Shared || IsStandardFolder(name) ? FolderScope.Shared : FolderScope.Unfamiliar;
        }

        private static bool IsRelevantInUnfamiliarFolder(FileKind kind)
        {
            return kind == FileKind.Recording || kind == FileKind.Screenshot || kind == FileKind.Video || kind == FileKind.Package;
        }

        private static bool IsInaccessibleObject(int error)
        {
            return error == unchecked((int)0x80070002) || error == unchecked((int)0x80070005) ||
                error == unchecked((int)0x80070057) || error == unchecked((int)0x80070490) ||
                error == unchecked((int)0x80042009) || error == unchecked((int)0x8004200F);
        }

        private static int FolderPriority(string name)
        {
            if (TransferCore.IsCaptureFolder(name)) return 0;
            if (CaptureRoots.Contains(name) || NameComparer.Equals(name, "Download") || NameComparer.Equals(name, "Downloads"))
                return NameComparer.Equals(name, "DCIM") ? 2 : 1;
            if (NameComparer.Equals(name, "Android")) return 3;
            return IsStandardFolder(name) ? 4 : 5;
        }

        private static IEnumerable<string> PrioritizeChildren(List<string> children, FileCache cache)
        {
            var captures = new List<string>();
            var recent = new List<string>();
            var rest = new List<string>();
            foreach (var id in children)
            {
                ObjectInfo item;
                if (!cache.Entries.TryGetValue(id, out item))
                    recent.Add(id);
                else if (item.IsFolder && (TransferCore.IsCaptureFolder(item.Name) || CaptureRoots.Contains(item.Name)))
                    captures.Add(id);
                else
                    rest.Add(id);
            }
            return captures.Concat(recent).Concat(rest);
        }

        private static DateTime StaggeredRecheck(DateTime now, string id)
        {
            // Avoid refreshing every old object in the same pass after an initial catalog.
            int offset = (StringComparer.Ordinal.GetHashCode(id) & int.MaxValue) % 60;
            return now.AddSeconds(240 + offset);
        }

        private static bool IsStandardFolder(string name)
        {
            return StandardRoots.Contains(name) || TransferCore.IsCaptureFolder(name);
        }

        private static bool ShouldIgnore(string name)
        {
            return string.IsNullOrWhiteSpace(name) || name.StartsWith(".", StringComparison.Ordinal) || IgnoredFolders.Contains(name);
        }

        private static string AvailableName(string name, HashSet<string> names)
        {
            string candidate = name;
            string baseName = Path.GetFileNameWithoutExtension(name);
            string extension = Path.GetExtension(name);
            for (int i = 2; names.Contains(candidate); i++)
                candidate = baseName + " (" + i.ToString(CultureInfo.InvariantCulture) + ")" + extension;
            return candidate;
        }

        private static int TransferBufferSize(uint suggested)
        {
            return suggested == 0 ? 128 * 1024 : (int)Math.Min(suggested, 2u * 1024 * 1024);
        }

        internal static bool IsUsbId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.IndexOf("vid_05ac", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            return id.StartsWith(@"\\?\usb#vid_", StringComparison.OrdinalIgnoreCase) ||
                id.StartsWith(@"\\?\swd#wpdbusenum#_??_usb#vid_", StringComparison.OrdinalIgnoreCase);
        }

        private static List<string> ReadDeviceIds(Native.IDeviceManager manager)
        {
            uint count = 0;
            CheckResizeResult(manager.GetDevices(IntPtr.Zero, ref count));
            for (int attempt = 0; count != 0 && attempt < 3; attempt++)
            {
                var allocated = checked((int)count);
                var buffer = AllocatePointerBuffer(allocated);
                try
                {
                    int result = manager.GetDevices(buffer, ref count);
                    if (result == Native.InsufficientBuffer || count > allocated)
                        continue;
                    Marshal.ThrowExceptionForHR(result);
                    var ids = new List<string>();
                    for (int i = 0; i < count; i++)
                    {
                        var id = Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer, i * IntPtr.Size));
                        if (!string.IsNullOrWhiteSpace(id))
                            ids.Add(id);
                    }
                    return ids;
                }
                finally { FreePointerBuffer(buffer, allocated); }
            }
            return new List<string>();
        }

        private static string GetFriendlyName(Native.IDeviceManager manager, string id)
        {
            uint length = 0;
            int result = manager.GetDeviceFriendlyName(id, IntPtr.Zero, ref length);
            if (result < 0 && result != Native.InsufficientBuffer || length == 0 || length > 32768)
                return "USB 便携设备";
            var buffer = Marshal.AllocHGlobal((int)length * sizeof(char));
            try
            {
                result = manager.GetDeviceFriendlyName(id, buffer, ref length);
                return result < 0 ? "USB 便携设备" : (Marshal.PtrToStringUni(buffer) ?? "USB 便携设备");
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        private static void CheckResizeResult(int result)
        {
            if (result != Native.InsufficientBuffer)
                Marshal.ThrowExceptionForHR(result);
        }

        private static IntPtr AllocatePointerBuffer(int count)
        {
            var buffer = Marshal.AllocHGlobal(checked(count * IntPtr.Size));
            for (int i = 0; i < count; i++)
                Marshal.WriteIntPtr(buffer, i * IntPtr.Size, IntPtr.Zero);
            return buffer;
        }

        private static void FreePointerBuffer(IntPtr buffer, int count)
        {
            for (int i = 0; i < count; i++)
                Marshal.FreeCoTaskMem(Marshal.ReadIntPtr(buffer, i * IntPtr.Size));
            Marshal.FreeHGlobal(buffer);
        }

        private sealed class FileCache
        {
            public Dictionary<string, ObjectInfo> Entries = new Dictionary<string, ObjectInfo>(StringComparer.Ordinal);
            public readonly HashSet<string> CandidateFolders = new HashSet<string>(StringComparer.Ordinal);
        }

        private sealed class Catalog
        {
            private readonly Action<IReadOnlyList<UsbFile>> publish;
            private readonly List<UsbFile> pending = new List<UsbFile>();
            private readonly Stopwatch elapsed = Stopwatch.StartNew();
            private long nextPublish;
            internal readonly List<UsbFile> Files = new List<UsbFile>();

            internal Catalog(Action<IReadOnlyList<UsbFile>> publish)
            {
                this.publish = publish;
            }

            internal void Add(UsbFile file)
            {
                Files.Add(file);
                if (publish == null) return;
                pending.Add(file);
                if (elapsed.ElapsedMilliseconds >= nextPublish) Publish();
            }

            internal void Publish()
            {
                if (pending.Count == 0) return;
                var batch = pending.ToArray();
                pending.Clear();
                nextPublish = elapsed.ElapsedMilliseconds + 150;
                publish(batch);
            }
        }

        private sealed class ObjectInfo
        {
            public string Id, Name, PersistentId;
            public DateTime? CreatedUtc, ModifiedUtc;
            public DateTime RecheckUtc, FrequentUntilUtc;
            public long Size;
            public bool IsFolder, IsFunctional, IsStorage;
            public uint StorageKind;
        }

        private sealed class Session : IDisposable
        {
            private readonly Native.IDevice device;
            private readonly Native.IContent content;
            private readonly Native.IProperties properties;
            private readonly Native.IKeyCollection keys;
            private readonly Native.IKeyCollection storageKeys;
            private bool opened;

            public Session(string id, bool write)
            {
                if (!IsUsbId(id))
                    throw new IOException("只允许打开 USB 便携设备。");
                device = Native.Create<Native.IDevice>(Native.DeviceClass);
                try
                {
                    var client = Native.Create<Native.IValues>(Native.ValuesClass);
                    try
                    {
                        client.SetStringValue(ref Native.ClientName, "USB 文件传输");
                        client.SetUnsignedIntegerValue(ref Native.ClientMajorVersion, 1);
                        client.SetUnsignedIntegerValue(ref Native.ClientMinorVersion, 0);
                        client.SetUnsignedIntegerValue(ref Native.ClientRevision, 0);
                        client.SetUnsignedIntegerValue(ref Native.ClientDesiredAccess, write ? 0xc0000000u : 0x80000000u);
                        device.Open(id, client);
                        opened = true;
                    }
                    finally { Native.Release(client); }

                    device.Content(out content);
                    content.Properties(out properties);
                    keys = CreateKeys(Native.ContentType, Native.Format, Native.Name, Native.PersistentId,
                        Native.OriginalFileName, Native.Size, Native.Created, Native.Modified);
                    storageKeys = CreateKeys(Native.ContentType, Native.FunctionalCategory, Native.StorageType, Native.Name);
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }

            public void Dispose()
            {
                Native.Release(storageKeys);
                Native.Release(keys);
                Native.Release(properties);
                Native.Release(content);
                if (opened)
                {
                    try { device.Close(); }
                    catch (COMException) { }
                    opened = false;
                }
                Native.Release(device);
            }

            public List<string> Children(string parent, CancellationToken ct)
            {
                const int BatchSize = 128;
                ct.ThrowIfCancellationRequested();
                Native.IObjectEnumerator enumerator;
                content.EnumObjects(0, parent, null, out enumerator);
                try
                {
                    var ids = new List<string>();
                    uint count;
                    do
                    {
                        ct.ThrowIfCancellationRequested();
                        var buffer = AllocatePointerBuffer(BatchSize);
                        try
                        {
                            Marshal.ThrowExceptionForHR(enumerator.Next(BatchSize, buffer, out count));
                            for (int i = 0; i < count; i++)
                            {
                                var id = Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer, i * IntPtr.Size));
                                if (!string.IsNullOrEmpty(id))
                                    ids.Add(id);
                            }
                        }
                        finally { FreePointerBuffer(buffer, BatchSize); }
                    }
                    while (count == BatchSize);
                    return ids;
                }
                finally { Native.Release(enumerator); }
            }

            private static Native.IKeyCollection CreateKeys(params Native.PropertyKey[] requested)
            {
                var collection = Native.Create<Native.IKeyCollection>(Native.KeysClass);
                try
                {
                    for (int i = 0; i < requested.Length; i++) collection.Add(ref requested[i]);
                    return collection;
                }
                catch
                {
                    Native.Release(collection);
                    throw;
                }
            }

            public ObjectInfo ReadObject(string id, bool storageNode = false)
            {
                Native.IValues values;
                try { properties.GetValues(id, storageNode ? storageKeys : keys, out values); }
                catch (COMException ex)
                {
                    if (IsInaccessibleObject(ex.ErrorCode)) return null;
                    throw;
                }

                try
                {
                    Guid contentType;
                    values.GetGuidValue(ref Native.ContentType, out contentType);
                    if (storageNode)
                    {
                        Guid category;
                        uint storageKind;
                        values.GetGuidValue(ref Native.FunctionalCategory, out category);
                        values.GetUnsignedIntegerValue(ref Native.StorageType, out storageKind);
                        return new ObjectInfo
                        {
                            Id = id, Name = ReadString(values, Native.Name) ?? string.Empty,
                            IsStorage = category == Native.StorageCategory, StorageKind = storageKind,
                            IsFunctional = contentType == Native.FunctionalType
                        };
                    }
                    Guid format;
                    values.GetGuidValue(ref Native.Format, out format);
                    bool folder = contentType == Native.FolderType || format == Native.AssociationFormat;
                    var name = string.Empty;
                    if (!folder)
                        name = ReadString(values, Native.OriginalFileName);
                    if (string.IsNullOrEmpty(name))
                        name = ReadString(values, Native.Name);
                    if (folder)
                        return new ObjectInfo { Id = id, Name = name ?? string.Empty, IsFolder = true };
                    ulong size;
                    return new ObjectInfo
                    {
                        Id = id, Name = name ?? string.Empty, PersistentId = ReadString(values, Native.PersistentId),
                        IsFunctional = contentType == Native.FunctionalType,
                        Size = values.GetUnsignedLargeIntegerValue(ref Native.Size, out size) >= 0 && size <= long.MaxValue
                            ? (long)size : -1,
                        CreatedUtc = ReadDate(values, Native.Created), ModifiedUtc = ReadDate(values, Native.Modified)
                    };
                }
                finally { Native.Release(values); }
            }

            public IStream OpenRead(UsbFile source, ref uint bufferSize, CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                var current = ReadObject(source.ObjectId);
                if (!Matches(current, source) && !string.IsNullOrEmpty(source.PersistentId))
                {
                    var resolved = ResolvePersistentId(source.PersistentId);
                    current = string.IsNullOrEmpty(resolved) ? null : ReadObject(resolved);
                }
                if (!Matches(current, source))
                    throw new FileNotFoundException("文件位置或大小已改变。请刷新列表后重试。");

                Native.IResources resources;
                content.Transfer(out resources);
                try
                {
                    IStream stream;
                    resources.GetStream(current.Id, ref Native.DefaultResource, 0, ref bufferSize, out stream);
                    return stream;
                }
                finally { Native.Release(resources); }
            }

            public string GetOrCreateFolder(string parent, string name, CancellationToken ct)
            {
                var existing = Children(parent, ct).Select(id => ReadObject(id))
                    .FirstOrDefault(item => item != null && item.IsFolder && NameComparer.Equals(item.Name, name));
                if (existing != null)
                    return existing.Id;

                var values = Native.Create<Native.IValues>(Native.ValuesClass);
                try
                {
                    values.SetStringValue(ref Native.ParentId, parent);
                    values.SetStringValue(ref Native.Name, name);
                    var folder = Native.FolderType;
                    values.SetGuidValue(ref Native.ContentType, ref folder);
                    IntPtr result;
                    content.CreateObjectWithPropertiesOnly(values, out result);
                    var id = Native.GetString(result);
                    if (string.IsNullOrEmpty(id))
                        throw new IOException("无法在手机上创建目录「" + name + "」。");
                    return id;
                }
                finally { Native.Release(values); }
            }

            public void UploadFile(string parent, string name, FileStream source, Action<long> progress, CancellationToken ct)
            {
                uint bufferSize = 0;
                IStream stream;
                var values = Native.Create<Native.IValues>(Native.ValuesClass);
                try
                {
                    var type = Native.GenericFileType;
                    var format = Native.UnspecifiedFormat;
                    values.SetStringValue(ref Native.ParentId, parent);
                    values.SetStringValue(ref Native.Name, Path.GetFileNameWithoutExtension(name));
                    values.SetStringValue(ref Native.OriginalFileName, name);
                    values.SetUnsignedLargeIntegerValue(ref Native.Size, (ulong)source.Length);
                    values.SetGuidValue(ref Native.ContentType, ref type);
                    values.SetGuidValue(ref Native.Format, ref format);
                    content.CreateObjectWithPropertiesAndData(values, out stream, ref bufferSize, IntPtr.Zero);
                }
                finally { Native.Release(values); }

                var count = Marshal.AllocCoTaskMem(sizeof(int));
                try
                {
                    var buffer = new byte[TransferBufferSize(bufferSize)];
                    long transferred = 0;
                    int read;
                    while ((read = source.Read(buffer, 0, buffer.Length)) != 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        Marshal.WriteInt32(count, 0);
                        stream.Write(buffer, read, count);
                        if (Marshal.ReadInt32(count) != read)
                            throw new IOException("手机未完整接收文件。请检查剩余空间后重试。");
                        transferred += read;
                        if (progress != null)
                            progress(transferred);
                    }
                    ct.ThrowIfCancellationRequested();
                    stream.Commit(0);
                }
                catch
                {
                    try { stream.Revert(); }
                    catch (COMException) { }
                    throw;
                }
                finally
                {
                    Marshal.FreeCoTaskMem(count);
                    Native.Release(stream);
                }
            }

            private string ResolvePersistentId(string id)
            {
                var input = Native.Create<Native.IVariantCollection>(Native.VariantCollectionClass);
                Native.IVariantCollection output = null;
                try
                {
                    var value = new Native.PropVariant { Type = 31, Pointer = Marshal.StringToCoTaskMemUni(id) };
                    try { input.Add(ref value); }
                    finally { Native.PropVariantClear(ref value); }
                    content.GetObjectIDsFromPersistentUniqueIDs(input, out output);
                    uint count;
                    output.GetCount(out count);
                    if (count == 0)
                        return null;
                    output.GetAt(0, out value);
                    try { return value.Type == 31 ? Marshal.PtrToStringUni(value.Pointer) : null; }
                    finally { Native.PropVariantClear(ref value); }
                }
                finally
                {
                    Native.Release(output);
                    Native.Release(input);
                }
            }

            private static bool Matches(ObjectInfo current, UsbFile source)
            {
                return current != null && !current.IsFolder && NameComparer.Equals(current.Name, source.Name) &&
                    (string.IsNullOrEmpty(source.PersistentId) || StringComparer.Ordinal.Equals(source.PersistentId, current.PersistentId)) &&
                    (source.Size < 0 || current.Size < 0 || source.Size == current.Size) &&
                    (!source.ModifiedUtc.HasValue || !current.ModifiedUtc.HasValue || source.ModifiedUtc == current.ModifiedUtc);
            }

            private static string ReadString(Native.IValues values, Native.PropertyKey key)
            {
                IntPtr value;
                return values.GetStringValue(ref key, out value) >= 0 ? Native.GetString(value) : null;
            }

            private static DateTime? ReadDate(Native.IValues values, Native.PropertyKey key)
            {
                Native.PropVariant value;
                if (values.GetValue(ref key, out value) < 0)
                    return null;
                try
                {
                    switch (value.Type)
                    {
                        case 7:
                            var date = DateTime.FromOADate(value.OleDate);
                            return date.Year < 1970 ? (DateTime?)null : date.ToUniversalTime();
                        case 64:
                            return value.FileTime <= 0 ? (DateTime?)null : DateTime.FromFileTimeUtc(value.FileTime);
                        case 8:
                            return value.Pointer == IntPtr.Zero ? (DateTime?)null : ParseDeviceDate(Marshal.PtrToStringBSTR(value.Pointer));
                        case 31:
                            return ParseDeviceDate(Marshal.PtrToStringUni(value.Pointer));
                        default:
                            return null;
                    }
                }
                catch (ArgumentException) { return null; }
                finally { Native.PropVariantClear(ref value); }
            }

            private static DateTime? ParseDeviceDate(string text)
            {
                DateTimeOffset parsed;
                if (string.IsNullOrWhiteSpace(text)) return null;
                if (DateTimeOffset.TryParseExact(text.Trim(), MtpDateFormats, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal, out parsed) || DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal, out parsed)) return parsed.UtcDateTime;
                return null;
            }
        }
    }
}
