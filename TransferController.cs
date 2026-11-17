using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.VisualBasic.FileIO;
using Forms = System.Windows.Forms;

namespace UsbTransfer
{
    internal interface IUsbTransport
    {
        List<UsbDevice> GetDevices();
        List<UsbFile> ListFiles(UsbDevice device, bool discover, CancellationToken ct);
        void Download(UsbFile file, Stream destination, Action<long> progress, CancellationToken ct);
        void Upload(UsbDevice device, string localPath, Action<long> progress, CancellationToken ct);
        bool IsReadable(UsbFile file, CancellationToken ct);
    }

    internal sealed class TransferController
    {
        private const int WmDeviceChange = 0x0219;
        private const int OpenWithExecute = 0x00000004;
        private static readonly Guid PortableDeviceInterface = new Guid("6AC27878-A6FA-4155-BA85-F98F491D4F33");
        private static readonly TimeSpan SettlingTime = TimeSpan.FromSeconds(1);
        private readonly Application app;
        private readonly MainWindow panel;
        private readonly TransferState state;
        private readonly string previewDirectory;
        private readonly string clipboardDirectory;
        private readonly IUsbTransport transport;
        private readonly SemaphoreSlim usbGate = new SemaphoreSlim(1);
        private readonly SemaphoreSlim workGate = new SemaphoreSlim(1);
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly DispatcherTimer pollTimer;
        private readonly DispatcherTimer partialRowsTimer;
        private readonly Dictionary<string, Observation> observations = new Dictionary<string, Observation>(StringComparer.Ordinal);
        private readonly HashSet<string> importing = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> clipboardCopies = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly List<PreviewWindow> previews = new List<PreviewWindow>();
        private List<UsbDevice> devices = new List<UsbDevice>();
        private string activeDeviceId;
        private bool polling;
        private bool refreshQueued;
        private bool leaving;
        private bool baseline = true;
        private int catalogPass;
        private int connectionGeneration;
        private int jobs;
        private FileToast toast;
        private Observation toastFile;
        private Forms.NotifyIcon tray;
        private string lastStatus;
        private DateTime lastPartialRenderUtc;
        private IntPtr usbNotifications;

        private sealed class Observation
        {
            internal UsbFile File;
            internal string Key;
            internal FileKind Kind;
            internal DateTime FirstSeen;
            internal DateTime LastChange;
            internal DateTime? FileDateUtc;
            internal int ObservedPass;
            internal bool DiscoveredWhileConnected;
            internal bool Ready;
            internal bool ToastReady;
            internal bool NoticeHandled;
        }

        internal TransferController(Application app, string dataDirectory, string defaultDestination, IUsbTransport transport = null)
        {
            this.app = app;
            this.transport = transport ?? new WpdBackend();
            state = new TransferState(dataDirectory, defaultDestination);
            previewDirectory = Path.Combine(dataDirectory, "preview");
            clipboardDirectory = Path.Combine(dataDirectory, "clipboard");
            panel = new MainWindow();
            app.MainWindow = panel;
            panel.SetDestination(state.SaveDirectory);
            panel.Closing += delegate(object sender, CancelEventArgs args)
            {
                if (leaving) return;
                args.Cancel = true;
                panel.Hide();
            };
            panel.SourceInitialized += delegate
            {
                IntPtr handle = new WindowInteropHelper(panel).Handle;
                var source = HwndSource.FromHwnd(handle);
                if (source != null) source.AddHook(HandleWindowMessage);
                var filter = new DeviceInterfaceNotification
                {
                    Size = Marshal.SizeOf(typeof(DeviceInterfaceNotification)),
                    DeviceType = 5,
                    InterfaceClass = PortableDeviceInterface
                };
                usbNotifications = RegisterDeviceNotification(handle, ref filter, 0);
            };
            panel.DeviceChanged += SelectDevice;
            panel.DaysChanged += delegate { RenderRows(); };
            panel.ChooseFolder += ChooseSaveDirectory;
            panel.OpenFolder += OpenSaveDirectory;
            panel.SendFiles += SendFiles;
            panel.ImportSelected += copy => Import(panel.SelectedRows, copy);
            panel.CopySelected += CopySelection;
            panel.PreviewSelected += PreviewSelected;
            panel.RenameSelected += RenameSelected;
            panel.RecycleSelected += RecycleSelected;
            panel.LocateSelected += LocateSelected;
            panel.RefreshRequested += delegate { Poll(true); };
            pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            pollTimer.Tick += delegate { Poll(false); };
            partialRowsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
            partialRowsTimer.Tick += delegate { RenderPendingRows(); };
            app.SessionEnding += delegate { Stop(); };
            app.Exit += delegate { Stop(); };
        }

        internal void Start()
        {
            tray = new Forms.NotifyIcon
            {
                Icon = System.Drawing.SystemIcons.Application,
                Text = "USB 快捷传输",
                Visible = true,
                ContextMenuStrip = new Forms.ContextMenuStrip()
            };
            tray.ContextMenuStrip.Items.Add("打开最近新增", null, delegate { ShowPanel(); });
            tray.ContextMenuStrip.Items.Add("退出", null, delegate { Stop(); app.Shutdown(); });
            tray.DoubleClick += delegate { ShowPanel(); };
            CleanTemporaryDirectory(previewDirectory);
            CleanTemporaryDirectory(clipboardDirectory);
            ShowPanel();
            RenderRows();
            pollTimer.Start();
            app.Dispatcher.BeginInvoke(new Action(() => Poll(true)), DispatcherPriority.Background);
        }

        private void Stop()
        {
            if (leaving) return;
            leaving = true;
            lifetime.Cancel();
            pollTimer.Stop();
            partialRowsTimer.Stop();
            if (usbNotifications != IntPtr.Zero) UnregisterDeviceNotification(usbNotifications);
            usbNotifications = IntPtr.Zero;
            CloseToast();
            foreach (var preview in previews.ToArray()) preview.Close();
            if (tray != null)
            {
                tray.Visible = false;
                tray.ContextMenuStrip.Dispose();
                tray.Dispose();
                tray = null;
            }
        }

        private void ShowPanel()
        {
            if (leaving) return;
            if (!panel.IsVisible) panel.Show();
            if (panel.WindowState == WindowState.Minimized) panel.WindowState = WindowState.Normal;
            panel.Activate();
        }

        private IntPtr HandleWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == WmDeviceChange)
                app.Dispatcher.BeginInvoke(new Action(() => Poll(false)), DispatcherPriority.Background);
            return IntPtr.Zero;
        }

        private async Task<T> OnUsb<T>(Func<T> operation)
        {
            await usbGate.WaitAsync(lifetime.Token);
            try { return await Task.Run(operation, lifetime.Token); }
            finally { usbGate.Release(); }
        }

        private async Task DoUsb(Action operation)
        {
            await OnUsb(delegate { operation(); return true; });
        }

        private void SelectDevice(string id)
        {
            if (id == activeDeviceId || !devices.Any(device => device.Id == id)) return;
            ActivateDevice(id);
            Poll(true);
        }

        private void ActivateDevice(string id)
        {
            activeDeviceId = id;
            connectionGeneration++;
            observations.Clear();
            baseline = true;
            catalogPass = 0;
            lastPartialRenderUtc = DateTime.MinValue;
            partialRowsTimer.Stop();
            CloseToast();
            panel.SetDevices(devices, activeDeviceId, activeDeviceId != null);
            RenderRows();
        }

        private async void Poll(bool force)
        {
            if (leaving) return;
            if (polling || jobs > 0)
            {
                refreshQueued |= force;
                return;
            }
            force |= refreshQueued;
            refreshQueued = false;
            polling = true;
            try
            {
                var found = await OnUsb(transport.GetDevices);
                if (leaving) return;
                var previous = new HashSet<string>(devices.Select(device => device.Id), StringComparer.Ordinal);
                var arrival = found.FirstOrDefault(device => !previous.Contains(device.Id));
                bool changed = devices.Count != found.Count || found.Any(device => !previous.Contains(device.Id));
                devices = found;
                if (arrival != null)
                {
                    ActivateDevice(arrival.Id);
                    ShowPanel();
                    SetIdleStatus("已连接，正在读取最近文件…");
                }
                else if (activeDeviceId != null && !devices.Any(device => device.Id == activeDeviceId))
                {
                    ActivateDevice(devices.Count == 0 ? null : devices[0].Id);
                    SetIdleStatus(activeDeviceId == null ? "手机已断开，已保存的文件仍可管理。" : "正在切换设备…");
                }
                else if (changed)
                {
                    panel.SetDevices(devices, activeDeviceId, baseline && activeDeviceId != null);
                }

                var active = devices.FirstOrDefault(device => device.Id == activeDeviceId);
                if (active == null) return;
                bool discover = force || catalogPass % 24 == 0;
                int generation = connectionGeneration;
                int pass = catalogPass;
                bool initial = baseline;
                var streaming = transport as IUsbFileDiscovery;
                var partialCatalog = transport as IUsbInitialCatalog;
                var files = await OnUsb(delegate
                {
                    if (initial && partialCatalog != null)
                        return partialCatalog.ListFiles(active, discover, lifetime.Token, null,
                            batch => ReportCatalogBatch(batch, generation, pass));
                    if (!initial && streaming != null)
                        return streaming.ListFiles(active, discover, lifetime.Token, file => ReportDiscovered(file, generation, pass));
                    return transport.ListFiles(active, discover, lifetime.Token);
                });
                if (leaving || generation != connectionGeneration) return;
                catalogPass++;
                await UpdateObservations(active, files, generation, pass);
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (leaving) return;
                panel.SetDevices(devices, activeDeviceId, false);
                SetIdleStatus("读取手机失败。请解锁手机并确认 USB 模式为「文件传输」。", error);
            }
            finally
            {
                polling = false;
                if (refreshQueued && !leaving && jobs == 0)
                    Poll(false);
            }
        }

        private void ReportDiscovered(UsbFile file, int generation, int pass)
        {
            app.Dispatcher.BeginInvoke(new Action(delegate
            {
                if (leaving || generation != connectionGeneration || pass != catalogPass) return;
                var item = Observe(file, DateTime.UtcNow, false, pass);
                OfferToast(item);
                RenderPartialRows();
            }), DispatcherPriority.Normal);
        }

        private void ReportCatalogBatch(IReadOnlyList<UsbFile> files, int generation, int pass)
        {
            app.Dispatcher.BeginInvoke(new Action(delegate
            {
                if (leaving || !baseline || generation != connectionGeneration || pass != catalogPass) return;
                DateTime now = DateTime.UtcNow;
                foreach (var file in files) Observe(file, now, true, pass);
                RenderPartialRows();
            }), DispatcherPriority.Normal);
        }

        private void RenderPartialRows()
        {
            if (DateTime.UtcNow - lastPartialRenderUtc < partialRowsTimer.Interval)
            {
                if (!partialRowsTimer.IsEnabled) partialRowsTimer.Start();
                return;
            }
            RenderPendingRows();
        }

        private void RenderPendingRows()
        {
            partialRowsTimer.Stop();
            lastPartialRenderUtc = DateTime.UtcNow;
            RenderRows();
        }

        private Observation Observe(UsbFile file, DateTime now, bool initial, int pass)
        {
            string identity = TransferCore.FileIdentity(file);
            Observation item;
            if (!observations.TryGetValue(identity, out item))
            {
                string key = TransferCore.FileKey(file);
                var kind = TransferCore.Classify(file);
                var date = TransferCore.FileDateUtc(file, kind);
                bool known = state.HasSeen(file.DeviceId, key);
                item = new Observation
                {
                    File = file, Key = key, Kind = kind,
                    FirstSeen = now, LastChange = now, FileDateUtc = date, ObservedPass = pass,
                    DiscoveredWhileConnected = !initial && !known,
                    NoticeHandled = (initial && file.Size != 0) || known,
                    Ready = initial && file.Size != 0 && date.HasValue && date.Value < now.Subtract(SettlingTime)
                };
                observations.Add(identity, item);
            }
            else
            {
                bool changed = item.File.Size != file.Size || item.File.ModifiedUtc != file.ModifiedUtc ||
                    item.File.CreatedUtc != file.CreatedUtc;
                item.File = file;
                if (changed)
                {
                    item.Key = TransferCore.FileKey(file);
                    item.FileDateUtc = TransferCore.FileDateUtc(file, item.Kind);
                    item.LastChange = now;
                    item.Ready = false;
                    item.ToastReady = false;
                    if (toastFile == item && toast != null)
                    {
                        toast.UpdateFile(file, SortDate(item));
                        toast.SetPreparing();
                    }
                }
                else if (pass != item.ObservedPass && file.Size != 0 && now - item.LastChange >= SettlingTime)
                {
                    item.Ready = true;
                }
                item.ObservedPass = pass;
            }
            return item;
        }

        private async Task UpdateObservations(UsbDevice device, List<UsbFile> files, int generation, int pass)
        {
            DateTime now = DateTime.UtcNow;
            bool initial = baseline;
            var seenKeys = new List<string>();
            var newMedia = new List<Observation>();
            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in files)
            {
                if (!present.Add(TransferCore.FileIdentity(file))) continue;
                var item = Observe(file, now, initial, pass);
                if (!initial && CanNotify(item)) newMedia.Add(item);
                if ((initial && file.Size != 0 || item.Ready) && !state.HasSeen(device.Id, item.Key))
                    seenKeys.Add(item.Key);
            }
            foreach (string missing in observations.Keys.Where(key => !present.Contains(key)).ToArray())
            {
                if (observations[missing] == toastFile) CloseToast();
                observations.Remove(missing);
            }
            baseline = false;
            if (initial)
            {
                panel.SetDevices(devices, activeDeviceId, false);
                SetIdleStatus(files.Count == 0 ? "手机已连接。正在等待新增文件。" : "已读取最近文件");
            }

            var latest = newMedia.OrderByDescending(SortDate).ThenByDescending(item => item.FirstSeen).FirstOrDefault();
            if (latest != null)
            {
                foreach (var item in newMedia.Where(item => item != latest && item != toastFile))
                    item.NoticeHandled = true;
                OfferToast(latest);
            }
            RenderPendingRows();
            if ((initial || seenKeys.Count > 0) && state.MarkSeen(device.Id, seenKeys)) FlushState();

            var preparing = toastFile;
            if (preparing == null || !preparing.Ready || preparing.ToastReady) return;
            var fileToCheck = preparing.File;
            bool readable = await OnUsb(() => transport.IsReadable(fileToCheck, lifetime.Token));
            if (readable && !leaving && generation == connectionGeneration && toastFile == preparing && toast != null &&
                preparing.File == fileToCheck)
            {
                preparing.ToastReady = true;
                toast.SetReady();
            }
        }

        private static DateTime SortDate(Observation item)
        {
            return UseDiscoveryDate(item) ? item.FirstSeen : item.FileDateUtc.Value;
        }

        private static bool UseDiscoveryDate(Observation item)
        {
            return !item.FileDateUtc.HasValue ||
                (item.Kind == FileKind.Recording && item.DiscoveredWhileConnected &&
                    item.FileDateUtc.Value < item.FirstSeen.AddDays(-1));
        }

        private void RenderRows()
        {
            DateTime cutoff = DateTime.UtcNow.AddDays(-panel.SelectedDays);
            var rows = new List<CatalogRow>();
            var local = state.GetSavedFiles()
                .Where(record => record.DateUtc >= cutoff && LocalFileExists(record.Path))
                .ToDictionary(record => Identity(record.DeviceId, record.Key), StringComparer.Ordinal);
            foreach (var item in observations.Values)
            {
                var file = item.File;
                DateTime date = SortDate(item);
                if (date < cutoff) continue;
                string identity = Identity(file.DeviceId, item.Key);
                SavedFile record;
                local.TryGetValue(identity, out record);
                local.Remove(identity);
                var row = CreateRow(file.DeviceId, item.Key, file, record, item.Kind,
                    record == null ? file.Name : Path.GetFileName(record.Path), date, file.Size,
                    item.Ready || record != null);
                if (UseDiscoveryDate(item)) row.DateText += " · 检测";
                rows.Add(row);
            }
            foreach (var record in local.Values)
            {
                rows.Add(CreateRow(record.DeviceId, record.Key, null, record, record.Kind,
                    Path.GetFileName(record.Path), record.DateUtc, record.Size, true));
            }
            panel.SetRows(rows.OrderByDescending(row => row.DateUtc ?? DateTime.MinValue)
                .ThenBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase).ToList());
        }

        private CatalogRow CreateRow(string deviceId, string key, UsbFile remote, SavedFile record,
            FileKind kind, string name, DateTime? date, long size, bool ready)
        {
            string identity = Identity(deviceId, key);
            return new CatalogRow
            {
                Identity = identity, DeviceId = deviceId, FileKey = key, Remote = remote,
                SavedPath = record == null ? null : record.Path, Kind = kind, DateUtc = date,
                Ready = ready && !importing.Contains(identity), Name = name,
                KindText = KindLabel(kind), DateText = date.HasValue ? date.Value.ToLocalTime().ToString("yyyy-MM-dd  HH:mm") : "—",
                SizeText = FormatSize(size),
                Status = importing.Contains(identity) ? "传输中" : record != null ? "已保存" : ready ? "待保存" : "准备中"
            };
        }

        private static string Identity(string deviceId, string fileKey)
        {
            return deviceId + "\n" + fileKey;
        }

        internal static string KindLabel(FileKind kind)
        {
            switch (kind)
            {
                case FileKind.Recording: return "录屏";
                case FileKind.Screenshot: return "截图";
                case FileKind.Package: return "安装包";
                case FileKind.Video: return "视频";
                case FileKind.Image: return "图片";
                default: return "其他";
            }
        }

        internal static string FormatSize(long size)
        {
            if (size < 0) return "—";
            if (size >= 1073741824L) return (size / 1073741824.0).ToString("0.##") + " GB";
            if (size >= 1048576L) return (size / 1048576.0).ToString("0.#") + " MB";
            if (size >= 1024L) return (size / 1024.0).ToString("0.#") + " KB";
            return size + " B";
        }

        private static bool CanNotify(Observation item)
        {
            return !item.NoticeHandled && (item.Kind == FileKind.Recording || item.Kind == FileKind.Screenshot);
        }

        private void OfferToast(Observation item)
        {
            if (!CanNotify(item) || toastFile == item) return;
            if (toastFile != null && SortDate(item) < SortDate(toastFile))
            {
                item.NoticeHandled = true;
                return;
            }
            string identity = Identity(item.File.DeviceId, item.Key);
            if (importing.Contains(identity) || LocalFileExists(state.GetSavedPath(item.File)))
            {
                item.NoticeHandled = true;
                return;
            }
            CloseToast();
            var next = new FileToast(item.File, SortDate(item),
                delegate { Import(new List<CatalogRow> { ToastRow(item) }, true); },
                delegate { CopyOnly(ToastRow(item)); });
            toast = next;
            toastFile = item;
            next.Closed += delegate
            {
                item.NoticeHandled = true;
                if (toast == next)
                {
                    toast = null;
                    toastFile = null;
                }
            };
            next.Show();
        }

        private CatalogRow ToastRow(Observation item)
        {
            var file = item.File;
            return CreateRow(file.DeviceId, item.Key, file, null, item.Kind, file.Name,
                SortDate(item), file.Size, item.Ready);
        }

        private void CloseToast()
        {
            if (toast == null) return;
            var previous = toast;
            toast = null;
            toastFile = null;
            previous.Close();
        }

        private async void Import(List<CatalogRow> selection, bool copyToClipboard)
        {
            var newRows = selection.Where(row => row.Remote != null && row.Ready && row.SavedPath == null &&
                importing.Add(row.Identity)).ToList();
            var paths = selection.Where(row => LocalFileExists(row.SavedPath)).Select(row => row.SavedPath).ToList();
            if (newRows.Count == 0)
            {
                if (copyToClipboard && paths.Count > 0) CopyFiles(paths);
                return;
            }

            string destination = state.SaveDirectory;
            RenderRows();
            await RunJob(async delegate
            {
                var errors = new List<string>();
                int completed = 0;
                foreach (var row in newRows)
                {
                    try
                    {
                        var current = CurrentFile(row);
                        int index = completed + 1;
                        string message = string.Format("正在保存 {0}/{1}  {2}", index, newRows.Count, current.Name);
                        SetStatus(message, null, true);
                        var progress = TransferProgress(message, current.Size);
                        string fullPath = await OnUsb(() => TransferCore.SaveFile(EnsureLocalPath(destination), current.Name,
                            current.Size, stream => transport.Download(current, stream, progress, lifetime.Token),
                            current.ModifiedUtc ?? current.CreatedUtc));
                        state.SetSavedPath(current, fullPath, row.DateUtc);
                        paths.Add(fullPath);
                        FlushState();
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error) { errors.Add(row.Name + "：" + error.GetBaseException().Message); }
                    finally
                    {
                        importing.Remove(row.Identity);
                        completed++;
                        if (!leaving) RenderRows();
                    }
                }
                bool copied = copyToClipboard && paths.Count > 0 && TryCopyFiles(paths);
                if (errors.Count > 0)
                {
                    SetStatus("已保存 " + paths.Count + " 项，" + errors.Count + " 项失败。 " + errors[0]);
                }
                else if (copyToClipboard)
                {
                    SetStatus(copied ? "已保存并复制 " + paths.Count + " 项，可按 Ctrl+V 粘贴文件。" :
                        "已保存 " + paths.Count + " 项。剪贴板暂时不可用，可在列表中重新复制。");
                }
                else SetStatus("已保存 " + paths.Count + " 项到指定目录。");
            }, "保存文件失败");
        }

        private UsbFile CurrentFile(CatalogRow row)
        {
            if (row.DeviceId != activeDeviceId && row.Remote != null && devices.Any(device => device.Id == row.DeviceId))
                return row.Remote;
            Observation item;
            if (row.DeviceId != activeDeviceId || row.Remote == null ||
                !observations.TryGetValue(TransferCore.FileIdentity(row.Remote), out item) ||
                item.Key != row.FileKey || !item.Ready)
                throw new IOException("该文件暂不可读取，请重新连接手机并刷新。");
            return item.File;
        }

        private async void CopyOnly(CatalogRow row)
        {
            if (!row.Ready || !importing.Add(row.Identity)) return;
            RenderRows();
            try
            {
                await RunJob(async delegate
                {
                    var current = CurrentFile(row);
                    string path = state.GetSavedPath(current);
                    if (!LocalFileExists(path) &&
                        (!clipboardCopies.TryGetValue(row.Identity, out path) || !LocalFileExists(path)))
                    {
                        string directory = Path.Combine(clipboardDirectory, Guid.NewGuid().ToString("N"));
                        string message = "正在复制原件  " + current.Name;
                        SetStatus(message, null, true);
                        var progress = TransferProgress(message, current.Size);
                        try
                        {
                            path = await OnUsb(() => TransferCore.SaveFile(directory, current.Name, current.Size,
                                stream => transport.Download(current, stream, progress, lifetime.Token)));
                            clipboardCopies[row.Identity] = path;
                        }
                        catch
                        {
                            RemoveTemporaryDirectory(clipboardDirectory, directory);
                            throw;
                        }
                    }
                    if (!leaving) CopyFiles(new[] { path });
                }, "复制文件失败");
            }
            finally
            {
                importing.Remove(row.Identity);
                if (!leaving) RenderRows();
            }
        }

        private void CopySelection()
        {
            var selection = panel.SelectedRows;
            if (selection.Count == 1 && selection[0].SavedPath == null)
                CopyOnly(selection[0]);
            else
                CopyFiles(selection.Select(row => row.SavedPath));
        }

        private async Task RunJob(Func<Task> operation, string failure)
        {
            jobs++;
            bool acquired = false;
            try
            {
                await workGate.WaitAsync(lifetime.Token);
                acquired = true;
                if (!leaving) await operation();
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { if (!leaving) SetStatus(failure, null, false, error); }
            finally
            {
                if (acquired) workGate.Release();
                jobs--;
            }
        }

        private Action<long> TransferProgress(string message, long length)
        {
            var elapsed = Stopwatch.StartNew();
            long last = -250;
            return bytes =>
            {
                if (elapsed.ElapsedMilliseconds - last < 225) return;
                last = elapsed.ElapsedMilliseconds;
                int? percent = length > 0 ? (int?)Math.Min(100, bytes * 100.0 / length) : null;
                app.Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (!leaving) panel.SetStatus(message + "  ·  " + FormatSize(bytes), percent, !percent.HasValue);
                }), DispatcherPriority.Normal);
            };
        }

        private void CopyFiles(IEnumerable<string> candidates)
        {
            var paths = candidates.Where(LocalFileExists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (paths.Count == 0)
            {
                SetStatus("在电脑上保存文件后即可复制。");
                return;
            }
            SetStatus(TryCopyFiles(paths) ? "已复制 " + paths.Count + " 个文件，可按 Ctrl+V 粘贴。" :
                "剪贴板暂时不可用，请再点一次「复制文件」。");
        }

        internal static DataObject CreateFileClipboardData(IEnumerable<string> paths)
        {
            var data = new DataObject();
            data.SetData(DataFormats.FileDrop, paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
            data.SetData("Preferred DropEffect", new MemoryStream(new byte[] { 1, 0, 0, 0 }));
            return data;
        }

        private static bool TryCopyFiles(IEnumerable<string> paths)
        {
            try
            {
                Clipboard.SetDataObject(CreateFileClipboardData(paths), true);
                return true;
            }
            catch (System.Runtime.InteropServices.ExternalException) { return false; }
        }

        private async void SendFiles()
        {
            var device = devices.FirstOrDefault(item => item.Id == activeDeviceId);
            if (device == null) return;
            var chooser = new Microsoft.Win32.OpenFileDialog { Title = "选择要发送到手机的文件", Multiselect = true };
            if (chooser.ShowDialog(panel) != true) return;
            await RunJob(async delegate
            {
                foreach (var path in chooser.FileNames)
                {
                    string local = EnsureLocalPath(path);
                    string message = "正在发送到手机  " + Path.GetFileName(local);
                    long length = new FileInfo(local).Length;
                    SetStatus(message, null, true);
                    var progress = TransferProgress(message, length);
                    await DoUsb(() => transport.Upload(device, local, progress, lifetime.Token));
                }
                SetStatus("已发送到手机的「Download/测试传输」。");
            }, "发送到手机失败");
            Poll(true);
        }

        private async void PreviewSelected()
        {
            var row = panel.SelectedRows.FirstOrDefault();
            if (row == null) return;
            var choice = new PreviewChoiceWindow(row.Name) { Owner = panel };
            if (choice.ShowDialog() != true || leaving) return;

            string path = row.SavedPath;
            if (!LocalFileExists(path)) clipboardCopies.TryGetValue(row.Identity, out path);
            bool temporary = !LocalFileExists(path);
            if (temporary)
            {
                if (row.Remote == null || !row.Ready) return;
                string previewPath = Path.Combine(previewDirectory, Guid.NewGuid().ToString("N"));
                await RunJob(async delegate
                {
                    var current = CurrentFile(row);
                    string message = "正在读取预览  " + row.Name;
                    SetStatus(message, null, true);
                    var progress = TransferProgress(message, current.Size);
                    path = await OnUsb(() => TransferCore.SaveFile(previewPath, current.Name, current.Size,
                        stream => transport.Download(current, stream, progress, lifetime.Token)));
                    SetStatus("已读取预览。");
                }, "预览读取失败");
                if (!LocalFileExists(path)) { RemoveTemporaryDirectory(previewDirectory, previewPath); return; }
            }
            if (leaving) return;

            if (choice.Choice == PreviewMode.OpenWith)
            {
                bool opened = false;
                try { opened = OpenWith(panel, path); }
                catch (Exception error) { SetStatus("无法打开 Windows 应用选择窗口", null, false, error); }
                finally
                {
                    if (temporary && !opened) RemoveTemporaryDirectory(previewDirectory, Path.GetDirectoryName(path));
                }
                return;
            }

            var window = new PreviewWindow(row.Name, path, row.Kind) { Owner = panel };
            previews.Add(window);
            window.Closed += delegate
            {
                previews.Remove(window);
                if (temporary && !window.OpenedExternally)
                    RemoveTemporaryDirectory(previewDirectory, Path.GetDirectoryName(path));
            };
            window.Show();
        }

        internal static bool OpenWith(Window owner, string path)
        {
            var info = new OpenAsInfo { File = EnsureLocalPath(path), Flags = OpenWithExecute };
            int result = SHOpenWithDialog(new WindowInteropHelper(owner).Handle, ref info);
            if (result == unchecked((int)0x800704C7) || result == unchecked((int)0x80004004)) return false;
            Marshal.ThrowExceptionForHR(result);
            return true;
        }

        private void ChooseSaveDirectory()
        {
            using (var dialog = new Forms.FolderBrowserDialog
            {
                Description = "选择保存录屏、截图和安装包的本地目录",
                SelectedPath = state.SaveDirectory,
                ShowNewFolderButton = true
            })
            {
                if (dialog.ShowDialog(new NativeOwner(panel)) != Forms.DialogResult.OK) return;
                TryUi(delegate
                {
                    string path = EnsureLocalPath(dialog.SelectedPath);
                    Directory.CreateDirectory(path);
                    state.SaveDirectory = path;
                    FlushState();
                    panel.SetDestination(path);
                    SetStatus("已更新保存目录。");
                }, "保存目录设置失败");
            }
        }

        private void OpenSaveDirectory()
        {
            TryUi(delegate
            {
                string folder = EnsureLocalPath(state.SaveDirectory);
                Directory.CreateDirectory(folder);
                Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
            }, "无法打开目录");
        }

        private void RenameSelected()
        {
            var row = panel.SelectedRows.FirstOrDefault();
            if (row == null || !LocalFileExists(row.SavedPath)) return;
            var prompt = new RenameWindow(Path.GetFileNameWithoutExtension(row.SavedPath)) { Owner = panel };
            if (prompt.ShowDialog() != true) return;
            TryUi(delegate
            {
                string name = prompt.FileName + Path.GetExtension(row.SavedPath);
                string newPath = TransferCore.RenameLocalFile(row.SavedPath, name);
                state.SetSavedPath(row.DeviceId, row.FileKey, newPath);
                FlushState();
                RenderRows();
                SetStatus("已重命名电脑副本。");
            }, "重命名失败");
        }

        private void RecycleSelected()
        {
            var rows = panel.SelectedRows.Where(row => LocalFileExists(row.SavedPath)).ToList();
            if (rows.Count == 0) return;
            var result = MessageBox.Show(panel, "将选中的 " + rows.Count + " 个电脑副本移到 Windows 回收站？",
                "移到回收站", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (result != MessageBoxResult.Yes) return;
            TryUi(delegate
            {
                foreach (var row in rows)
                {
                    FileSystem.DeleteFile(row.SavedPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin,
                        UICancelOption.ThrowException);
                    state.SetSavedPath(row.DeviceId, row.FileKey, null);
                    FlushState();
                    RenderRows();
                }
                SetStatus("已移到回收站。");
            }, "移到回收站失败");
        }

        private void LocateSelected()
        {
            var row = panel.SelectedRows.FirstOrDefault();
            if (row == null || !LocalFileExists(row.SavedPath)) return;
            TryUi(delegate
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + row.SavedPath + "\"") { UseShellExecute = true });
            }, "无法打开文件位置");
        }

        private static void CleanTemporaryDirectory(string directory)
        {
            if (!Directory.Exists(directory)) return;
            try
            {
                foreach (string folder in Directory.GetDirectories(directory))
                    if (Directory.GetLastWriteTimeUtc(folder) < DateTime.UtcNow.AddHours(-24))
                        RemoveTemporaryDirectory(directory, folder);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static void RemoveTemporaryDirectory(string parent, string directory)
        {
            string expectedParent = Path.GetFullPath(parent);
            string target = Path.GetFullPath(directory);
            Guid session;
            if (!string.Equals(Path.GetDirectoryName(target), expectedParent, StringComparison.OrdinalIgnoreCase) ||
                !Guid.TryParseExact(Path.GetFileName(target), "N", out session)) return;
            try
            {
                if (!Directory.Exists(target)) return;
                if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0) return;
                foreach (string file in Directory.GetFiles(target)) File.Delete(file);
                Directory.Delete(target);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static bool LocalFileExists(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try { return File.Exists(EnsureLocalPath(path)); }
            catch (ArgumentException) { return false; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (NotSupportedException) { return false; }
        }

        private static string EnsureLocalPath(string path)
        {
            string full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\", StringComparison.Ordinal) ||
                new DriveInfo(Path.GetPathRoot(full)).DriveType == DriveType.Network)
                throw new IOException("请选择电脑本地磁盘上的文件或目录。");
            return full;
        }

        private void FlushState()
        {
            try { state.Flush(); }
            catch (IOException error) { SetStatus("保存本地记录失败", null, false, error); }
            catch (UnauthorizedAccessException error) { SetStatus("保存本地记录失败", null, false, error); }
        }

        private void TryUi(Action action, string failure)
        {
            try { action(); }
            catch (Exception error) { SetStatus(failure, null, false, error); }
        }

        private void SetIdleStatus(string message, Exception error = null)
        {
            if (jobs == 0) SetStatus(message, null, false, error);
        }

        private void SetStatus(string message, int? percent = null, bool indeterminate = false, Exception error = null)
        {
            if (leaving) return;
            if (error != null) message += " " + error.GetBaseException().Message;
            if (!string.Equals(lastStatus, message, StringComparison.Ordinal) || percent.HasValue || indeterminate)
            {
                panel.SetStatus(message, percent, indeterminate);
                lastStatus = message;
            }
            if (tray != null) tray.Text = message.Length > 60 ? message.Substring(0, 58) + "…" : message;
        }

        private sealed class NativeOwner : Forms.IWin32Window
        {
            internal NativeOwner(Window owner) { Handle = new WindowInteropHelper(owner).Handle; }
            public IntPtr Handle { get; private set; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DeviceInterfaceNotification
        {
            internal int Size;
            internal int DeviceType;
            internal int Reserved;
            internal Guid InterfaceClass;
            internal ushort EmptyName;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr RegisterDeviceNotification(IntPtr recipient, ref DeviceInterfaceNotification filter, int flags);

        [DllImport("user32.dll")]
        private static extern bool UnregisterDeviceNotification(IntPtr notification);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OpenAsInfo
        {
            [MarshalAs(UnmanagedType.LPWStr)] internal string File;
            [MarshalAs(UnmanagedType.LPWStr)] internal string FileClass;
            internal int Flags;
        }

        [DllImport("shell32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int SHOpenWithDialog(IntPtr owner, ref OpenAsInfo info);
    }
}
