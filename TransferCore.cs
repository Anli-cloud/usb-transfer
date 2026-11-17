using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace UsbTransfer
{
    internal enum FileKind
    {
        Recording,
        Screenshot,
        Package,
        Video,
        Image,
        Other
    }

    internal static class TransferCore
    {
        private static readonly HashSet<string> VideoExtensions = new HashSet<string>(
            new[] { ".mp4", ".mov", ".mkv", ".webm", ".avi", ".m4v", ".3gp", ".ts" });
        private static readonly HashSet<string> ImageExtensions = new HashSet<string>(
            new[] { ".png", ".jpg", ".jpeg", ".webp", ".heic", ".heif", ".avif", ".gif", ".bmp" });
        private static readonly HashSet<string> PackageExtensions = new HashSet<string>(
            new[] { ".apk", ".apks", ".xapk", ".apkm", ".aab" });
        private static readonly HashSet<char> InvalidNameCharacters = new HashSet<char>(Path.GetInvalidFileNameChars());
        private static readonly string[] RecordingHints =
        {
            "screenrecord", "screenvideo", "screencast", "screencapture",
            "屏幕录制", "屏幕录屏", "屏幕录像", "屏幕录影", "屏幕实录", "录制屏幕", "录屏",
            "螢幕錄影", "螢幕錄製", "螢幕錄屏", "螢幕錄像", "錄製螢幕", "屏幕錄製", "錄屏",
            "画面録画", "スクリーン録画", "화면녹화"
        };
        private static readonly string[] ScreenshotHints =
        {
            "screenshot", "screencapture", "screenphoto", "截图", "截圖", "截屏", "螢幕擷取", "スクリーンショット", "스크린샷"
        };
        private static readonly string[] RecorderFolderHints = { "xrecorder", "mobizen", "azscreenrecorder" };
        private static readonly Regex CaptureTimestamp = new Regex(
            @"(?<![0-9])([0-9]{4})[-_.]?([0-9]{2})[-_.]?([0-9]{2})[-_. T]?([0-9]{2})[-_.]?([0-9]{2})[-_.]?([0-9]{2})(?![0-9])",
            RegexOptions.CultureInvariant);

        public static FileKind Classify(UsbFile file)
        {
            string extension = ExtensionOf(file.Name).ToLowerInvariant();
            if (PackageExtensions.Contains(extension))
                return FileKind.Package;

            if (VideoExtensions.Contains(extension))
            {
                string name = Compact(file.Name);
                return StartsWithHint(name, RecordingHints) || IsCaptureFolder(file.Folder) || IsScreenTimestampName(file.Name)
                    ? FileKind.Recording : FileKind.Video;
            }

            if (ImageExtensions.Contains(extension))
                return StartsWithHint(Compact(file.Name), ScreenshotHints) || ContainsHint(Compact(file.Folder), ScreenshotHints)
                    ? FileKind.Screenshot : FileKind.Image;

            return FileKind.Other;
        }

        internal static bool IsCaptureFolder(string folder)
        {
            string compact = Compact(folder);
            // Some Android vendors store screen recordings and screenshots in the same album.
            return ContainsHint(compact, RecordingHints) || ContainsHint(compact, ScreenshotHints) ||
                ContainsHint(compact, RecorderFolderHints);
        }

        public static DateTime? FileDateUtc(UsbFile file)
        {
            return FileDateUtc(file, Classify(file));
        }

        internal static DateTime? FileDateUtc(UsbFile file, FileKind kind)
        {
            DateTime? created = PlausibleDate(file.CreatedUtc);
            DateTime? modified = PlausibleDate(file.ModifiedUtc);
            if (kind != FileKind.Recording && kind != FileKind.Screenshot)
                return created ?? modified;

            DateTime? newest = modified.HasValue && (!created.HasValue || modified.Value > created.Value) ? modified : created;
            var namedDate = PlausibleDate(ParseCaptureTimestamp(file.Name));
            // The Android capture name can recover a date when WPD reports an old placeholder.
            return namedDate.HasValue && (!newest.HasValue || namedDate.Value - newest.Value > TimeSpan.FromDays(1))
                ? namedDate : newest;
        }

        public static string FileIdentity(UsbFile file)
        {
            string folder = (file.Folder ?? "").Replace('/', '\\').TrimEnd('\\');
            string path = folder + "\\" + (file.Name ?? "");
            return string.IsNullOrWhiteSpace(file.PersistentId)
                ? "path\n" + path
                : "id\n" + file.PersistentId + "\n" + path;
        }

        public static string FileKey(UsbFile file)
        {
            string identity = FileIdentity(file);
            DateTime? revisionDate = file.ModifiedUtc ?? file.CreatedUtc;
            identity += "\n" + (revisionDate.HasValue ? revisionDate.Value.Ticks.ToString(CultureInfo.InvariantCulture) : "");
            identity += "\n" + file.Size.ToString(CultureInfo.InvariantCulture);
            using (SHA256 hash = SHA256.Create())
            {
                return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(identity)));
            }
        }

        public static string SaveFile(string directory, string name, long expectedSize, Action<Stream> write,
            DateTime? modifiedUtc = null)
        {
            string destination = Path.GetFullPath(directory);
            Directory.CreateDirectory(destination);
            string safeName = SafeFileName(name);
            string tempPath = Path.Combine(destination, ".usb-" + Guid.NewGuid().ToString("N") + ".part");
            bool ownsTempFile = false;
            try
            {
                using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.SequentialScan))
                {
                    ownsTempFile = true;
                    write(stream);
                    if (expectedSize >= 0 && stream.Length != expectedSize)
                        throw new IOException("文件大小与手机上的原件不一致，请重新传输。");
                    stream.Flush(true);
                }

                var date = PlausibleDate(modifiedUtc);
                if (date.HasValue)
                    File.SetLastWriteTimeUtc(tempPath, date.Value);
                return MoveToAvailableName(tempPath, destination, safeName);
            }
            finally
            {
                if (ownsTempFile)
                    DeleteTempFile(tempPath);
            }
        }

        public static string RenameLocalFile(string path, string newName)
        {
            if (string.IsNullOrWhiteSpace(newName) || !string.Equals(newName, SafeFileName(newName), StringComparison.Ordinal))
                throw new IOException("请输入有效的 Windows 文件名。");

            string source = Path.GetFullPath(path);
            string destination = Path.Combine(Path.GetDirectoryName(source), newName);
            if (!string.Equals(source, destination, StringComparison.Ordinal))
                File.Move(source, destination);
            return destination;
        }

        internal static string SafeFileName(string name)
        {
            var safe = new StringBuilder();
            foreach (char character in name ?? "")
                safe.Append(InvalidNameCharacters.Contains(character) ? '_' : character);

            string value = safe.ToString().TrimEnd(' ', '.');
            if (value.Length == 0)
                return "未命名文件";

            int firstPeriod = value.IndexOf('.');
            string firstPart = firstPeriod < 0 ? value : value.Substring(0, firstPeriod);
            if (IsReservedWindowsName(firstPart.TrimEnd(' ')))
                value = "_" + value;

            const int maxLength = 180;
            if (value.Length > maxLength)
            {
                string extension = ExtensionOf(value);
                if (extension.Length > 24)
                    extension = "";
                int prefixLength = maxLength - extension.Length;
                if (char.IsHighSurrogate(value[prefixLength - 1]))
                    prefixLength--;
                value = value.Substring(0, prefixLength).TrimEnd(' ', '.') + extension;
            }
            return value;
        }

        private static bool StartsWithHint(string value, IEnumerable<string> hints)
        {
            foreach (string hint in hints)
                if (value.StartsWith(hint, StringComparison.Ordinal))
                    return true;
            return false;
        }

        private static bool ContainsHint(string value, IEnumerable<string> hints)
        {
            foreach (string hint in hints)
                if (value.IndexOf(hint, StringComparison.Ordinal) >= 0)
                    return true;
            return false;
        }

        private static bool IsScreenTimestampName(string name)
        {
            return name != null && name.Length >= 11
                && name.StartsWith("screen", StringComparison.OrdinalIgnoreCase)
                && (name[6] == '-' || name[6] == '_')
                && char.IsDigit(name[7]) && char.IsDigit(name[8]) && char.IsDigit(name[9]) && char.IsDigit(name[10]);
        }

        private static DateTime? ParseCaptureTimestamp(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var match = CaptureTimestamp.Match(name);
            if (!match.Success) return null;
            var timestamp = new StringBuilder(14);
            for (int index = 1; index < match.Groups.Count; index++)
                timestamp.Append(match.Groups[index].Value);
            DateTime parsed;
            return DateTime.TryParseExact(timestamp.ToString(), "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal, out parsed) ? parsed : (DateTime?)null;
        }

        private static string Compact(string text)
        {
            var result = new StringBuilder();
            foreach (char character in text ?? "")
            {
                if (char.IsLetterOrDigit(character))
                    result.Append(char.ToLowerInvariant(character));
            }
            return result.ToString();
        }

        private static string ExtensionOf(string name)
        {
            int period = string.IsNullOrEmpty(name) ? -1 : name.LastIndexOf('.');
            return period > 0 ? name.Substring(period) : "";
        }

        private static DateTime? PlausibleDate(DateTime? value)
        {
            if (!value.HasValue || value.Value.Year < 2000)
                return null;
            DateTime date = value.Value.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
                : value.Value.ToUniversalTime();
            return date <= DateTime.UtcNow.AddDays(2) ? date : (DateTime?)null;
        }

        private static bool IsReservedWindowsName(string name)
        {
            string upper = name.ToUpperInvariant();
            if (upper == "CON" || upper == "PRN" || upper == "AUX" || upper == "NUL" || upper == "CONIN$" || upper == "CONOUT$")
                return true;

            return upper.Length == 4 && (upper.StartsWith("COM", StringComparison.Ordinal) || upper.StartsWith("LPT", StringComparison.Ordinal))
                && "0123456789¹²³".IndexOf(upper[3]) >= 0;
        }

        private static string MoveToAvailableName(string path, string directory, string name)
        {
            string extension = ExtensionOf(name);
            string stem = name.Substring(0, name.Length - extension.Length);
            for (int index = 1; index <= 10000; index++)
            {
                string candidateName = index == 1 ? name : stem + " (" + index.ToString(CultureInfo.InvariantCulture) + ")" + extension;
                string candidate = Path.Combine(directory, candidateName);
                if (Directory.Exists(candidate))
                    continue;
                try
                {
                    File.Move(path, candidate);
                    return candidate;
                }
                catch (IOException)
                {
                    if (!File.Exists(candidate) && !Directory.Exists(candidate))
                        throw;
                }
            }
            throw new IOException("保存目录中存在过多同名文件。");
        }

        internal static void DeleteTempFile(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal sealed class SavedFile
    {
        public string DeviceId;
        public string Key;
        public string Name;
        public string Folder;
        public string Path;
        public FileKind Kind;
        public DateTime? DateUtc;
        public long Size;

        internal SavedFile Copy()
        {
            return (SavedFile)MemberwiseClone();
        }
    }

    internal sealed class TransferState
    {
        private const int SeenLimitPerDevice = 50000;
        private readonly object gate = new object();
        private readonly Dictionary<string, DeviceHistory> devices = new Dictionary<string, DeviceHistory>(StringComparer.OrdinalIgnoreCase);
        private readonly string stateDirectory;
        private readonly string statePath;
        private string saveDirectory;
        private bool dirty;

        public TransferState(string stateDirectory, string defaultSaveDirectory)
        {
            this.stateDirectory = Path.GetFullPath(stateDirectory);
            statePath = Path.Combine(this.stateDirectory, "state.xml");
            saveDirectory = Path.GetFullPath(defaultSaveDirectory);
            Load();
        }

        public string SaveDirectory
        {
            get { lock (gate) return saveDirectory; }
            set
            {
                string fullPath = Path.GetFullPath(value);
                lock (gate)
                {
                    if (string.Equals(saveDirectory, fullPath, StringComparison.OrdinalIgnoreCase))
                        return;
                    saveDirectory = fullPath;
                    dirty = true;
                }
            }
        }

        public bool HasDevice(string deviceId)
        {
            lock (gate) return devices.ContainsKey(deviceId);
        }

        public bool HasSeen(string deviceId, string key)
        {
            lock (gate)
            {
                DeviceHistory history;
                return devices.TryGetValue(deviceId, out history) && history.Seen.Contains(key);
            }
        }

        public bool MarkSeen(string deviceId, IEnumerable<string> keys)
        {
            lock (gate)
            {
                DeviceHistory history;
                bool changed = !devices.TryGetValue(deviceId, out history);
                if (changed)
                    devices.Add(deviceId, history = new DeviceHistory());

                foreach (string key in keys)
                {
                    if (history.AddSeen(key))
                        changed = true;
                }
                dirty |= changed;
                return changed;
            }
        }

        public string GetSavedPath(UsbFile file)
        {
            string key = TransferCore.FileKey(file);
            lock (gate)
            {
                DeviceHistory history;
                SavedFile saved;
                return devices.TryGetValue(file.DeviceId, out history) && history.Saved.TryGetValue(key, out saved)
                    ? saved.Path
                    : null;
            }
        }

        public void SetSavedPath(UsbFile file, string path, DateTime? displayDateUtc = null)
        {
            string key = TransferCore.FileKey(file);
            if (path == null)
            {
                SetSavedPath(file.DeviceId, key, null);
                return;
            }

            FileKind kind = TransferCore.Classify(file);
            var saved = new SavedFile
            {
                DeviceId = file.DeviceId,
                Key = key,
                Name = file.Name,
                Folder = file.Folder,
                Path = Path.GetFullPath(path),
                Kind = kind,
                DateUtc = displayDateUtc ?? TransferCore.FileDateUtc(file, kind),
                Size = file.Size
            };

            lock (gate)
            {
                DeviceHistory history;
                if (!devices.TryGetValue(file.DeviceId, out history))
                    devices.Add(file.DeviceId, history = new DeviceHistory());
                history.Saved[key] = saved;
                history.AddSeen(key);
                dirty = true;
            }
        }

        public void SetSavedPath(string deviceId, string key, string path)
        {
            lock (gate)
            {
                DeviceHistory history;
                SavedFile saved;
                if (!devices.TryGetValue(deviceId, out history) || !history.Saved.TryGetValue(key, out saved))
                    return;

                if (path == null)
                    history.Saved.Remove(key);
                else
                    saved.Path = Path.GetFullPath(path);

                dirty = true;
            }
        }

        public List<SavedFile> GetSavedFiles()
        {
            lock (gate)
            {
                var result = new List<SavedFile>();
                foreach (DeviceHistory history in devices.Values)
                {
                    foreach (SavedFile saved in history.Saved.Values)
                        result.Add(saved.Copy());
                }
                return result;
            }
        }

        public void Flush()
        {
            lock (gate)
            {
                if (!dirty)
                    return;

                Directory.CreateDirectory(stateDirectory);
                string tempPath = Path.Combine(stateDirectory, Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    using (XmlWriter writer = XmlWriter.Create(tempPath))
                        WriteState(writer);

                    if (File.Exists(statePath))
                        File.Replace(tempPath, statePath, null, true);
                    else
                        File.Move(tempPath, statePath);

                    dirty = false;
                }
                finally
                {
                    TransferCore.DeleteTempFile(tempPath);
                }
            }
        }

        private void Load()
        {
            if (!File.Exists(statePath))
                return;

            var document = new XmlDocument { XmlResolver = null };
            try
            {
                using (XmlReader reader = XmlReader.Create(statePath, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                    document.Load(reader);
            }
            catch (XmlException)
            {
                return;
            }

            XmlElement root = document.DocumentElement;
            if (root == null || root.Name != "transfer-state")
                return;

            string directory = root.GetAttribute("save-directory");
            if (Path.IsPathRooted(directory))
                saveDirectory = directory;

            foreach (XmlElement device in root.SelectNodes("device"))
            {
                string deviceId = device.GetAttribute("id");
                if (deviceId.Length == 0 || devices.ContainsKey(deviceId))
                    continue;

                var history = new DeviceHistory();
                devices.Add(deviceId, history);
                foreach (XmlElement seen in device.SelectNodes("seen"))
                    history.AddSeen(seen.GetAttribute("key"));

                foreach (XmlElement element in device.SelectNodes("saved"))
                {
                    SavedFile saved = ReadSaved(deviceId, element);
                    if (saved != null)
                        history.Saved[saved.Key] = saved;
                }
            }
        }

        private static SavedFile ReadSaved(string deviceId, XmlElement element)
        {
            string key = element.GetAttribute("key");
            string path = element.GetAttribute("path");
            if (key.Length == 0 || !Path.IsPathRooted(path))
                return null;

            long ticks;
            long size;
            FileKind kind;
            bool hasDate = long.TryParse(element.GetAttribute("date"), NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks)
                && ticks > 0 && ticks <= DateTime.MaxValue.Ticks;
            bool hasSize = long.TryParse(element.GetAttribute("size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out size);
            bool hasKind = Enum.TryParse(element.GetAttribute("kind"), out kind) && Enum.IsDefined(typeof(FileKind), kind);
            return new SavedFile
            {
                DeviceId = deviceId,
                Key = key,
                Name = ReadText(element, "name"),
                Folder = ReadText(element, "folder"),
                Path = path,
                Kind = hasKind ? kind : FileKind.Other,
                DateUtc = hasDate ? new DateTime(ticks, DateTimeKind.Utc) : (DateTime?)null,
                Size = hasSize ? size : -1
            };
        }

        private void WriteState(XmlWriter writer)
        {
            writer.WriteStartElement("transfer-state");
            writer.WriteAttributeString("save-directory", saveDirectory);
            foreach (KeyValuePair<string, DeviceHistory> device in devices)
            {
                writer.WriteStartElement("device");
                writer.WriteAttributeString("id", device.Key);
                foreach (string key in device.Value.SeenOrder)
                {
                    writer.WriteStartElement("seen");
                    writer.WriteAttributeString("key", key);
                    writer.WriteEndElement();
                }
                foreach (SavedFile saved in device.Value.Saved.Values)
                {
                    writer.WriteStartElement("saved");
                    writer.WriteAttributeString("key", saved.Key);
                    writer.WriteAttributeString("path", saved.Path);
                    writer.WriteAttributeString("name-base64", EncodeText(saved.Name));
                    writer.WriteAttributeString("folder-base64", EncodeText(saved.Folder));
                    writer.WriteAttributeString("kind", saved.Kind.ToString());
                    if (saved.DateUtc.HasValue)
                        writer.WriteAttributeString("date", saved.DateUtc.Value.Ticks.ToString(CultureInfo.InvariantCulture));
                    writer.WriteAttributeString("size", saved.Size.ToString(CultureInfo.InvariantCulture));
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
        }

        private static string EncodeText(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? ""));
        }

        private static string ReadText(XmlElement element, string name)
        {
            if (!element.HasAttribute(name + "-base64"))
                return element.GetAttribute(name);

            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(element.GetAttribute(name + "-base64")));
            }
            catch (FormatException)
            {
                return "";
            }
        }

        private sealed class DeviceHistory
        {
            public readonly HashSet<string> Seen = new HashSet<string>(StringComparer.Ordinal);
            public readonly Queue<string> SeenOrder = new Queue<string>();
            public readonly Dictionary<string, SavedFile> Saved = new Dictionary<string, SavedFile>(StringComparer.Ordinal);

            public bool AddSeen(string key)
            {
                if (string.IsNullOrEmpty(key) || !Seen.Add(key))
                    return false;

                SeenOrder.Enqueue(key);
                if (SeenOrder.Count > SeenLimitPerDevice)
                    Seen.Remove(SeenOrder.Dequeue());
                return true;
            }
        }
    }
}
