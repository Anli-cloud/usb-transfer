using System;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace UsbTransfer
{
    internal sealed class FileToast : Window
    {
        private const int WmMouseActivate = 0x0021;
        private const int NoActivate = 3;
        private readonly DispatcherTimer timer;
        private readonly TextBlock name;
        private readonly TextBlock details;
        private readonly StackPanel preparation;
        private readonly Button saveAndCopyButton;
        private readonly Button copyOnlyButton;
        private bool ready;

        internal FileToast(UsbFile file, DateTime dateUtc, Action saveAndCopy, Action copyOnly)
        {
            Title = "新增手机文件";
            Width = 380;
            SizeToContent = SizeToContent.Height;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowActivated = false;
            ShowInTaskbar = false;
            Topmost = true;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
            UseLayoutRounding = true;
            var stack = new StackPanel();
            string category = TransferController.KindLabel(TransferCore.Classify(file));
            stack.Children.Add(Theme.TextBlock("新增" + category, 16, Theme.Text, true));
            name = Theme.TextBlock("", 14, Theme.Text);
            name.Margin = new Thickness(0, 7, 0, 0);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            stack.Children.Add(name);
            details = Theme.TextBlock("", 12, Theme.Secondary);
            details.Margin = new Thickness(0, 3, 0, 11);
            stack.Children.Add(details);
            UpdateFile(file, dateUtc);

            preparation = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            var preparingLabel = Theme.TextBlock("准备中", 12, Theme.Secondary);
            preparingLabel.Margin = new Thickness(0, 0, 0, 5);
            preparation.Children.Add(preparingLabel);
            preparation.Children.Add(new ProgressBar
            {
                Height = 5,
                IsIndeterminate = true,
                Foreground = Theme.Accent
            });
            stack.Children.Add(preparation);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancel = Theme.Button("取消");
            cancel.Margin = new Thickness(0, 0, 8, 0);
            cancel.Click += delegate { Close(); };
            copyOnlyButton = Theme.Button("仅复制");
            copyOnlyButton.Margin = new Thickness(0, 0, 8, 0);
            copyOnlyButton.IsEnabled = false;
            copyOnlyButton.Click += delegate { Complete(copyOnly); };
            saveAndCopyButton = Theme.Button("保存并复制", true);
            saveAndCopyButton.IsEnabled = false;
            saveAndCopyButton.Click += delegate { Complete(saveAndCopy); };
            actions.Children.Add(cancel);
            actions.Children.Add(copyOnlyButton);
            actions.Children.Add(saveAndCopyButton);
            stack.Children.Add(actions);
            Content = Theme.Card(stack, new Thickness(16));
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            timer.Tick += delegate { Close(); };
            SourceInitialized += delegate
            {
                var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
                if (source != null) source.AddHook(HandleMessage);
            };
            var area = SystemParameters.WorkArea;
            Left = area.Right - Width - 18;
            Top = area.Bottom - 203 - 18;
            SizeChanged += delegate(object sender, SizeChangedEventArgs change)
            {
                if (change.HeightChanged) Top = area.Bottom - change.NewSize.Height - 18;
            };
            Loaded += delegate { if (ready) timer.Start(); };
            Closed += delegate { timer.Stop(); };
        }

        internal void UpdateFile(UsbFile file, DateTime dateUtc)
        {
            name.Text = file.Name;
            name.ToolTip = file.Name;
            details.Text = dateUtc.ToLocalTime().ToString("HH:mm") + "   ·   " + TransferController.FormatSize(file.Size);
        }

        internal void SetReady()
        {
            if (ready) return;
            ApplyReadyState(true);
            if (IsLoaded) timer.Start();
        }

        internal void SetPreparing()
        {
            if (!ready) return;
            timer.Stop();
            ApplyReadyState(false);
        }

        private void ApplyReadyState(bool isReady)
        {
            ready = isReady;
            preparation.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
            saveAndCopyButton.IsEnabled = ready;
            copyOnlyButton.IsEnabled = ready;
        }

        private void Complete(Action action)
        {
            if (!ready) return;
            Close();
            action();
        }

        private IntPtr HandleMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message != WmMouseActivate) return IntPtr.Zero;
            handled = true;
            return new IntPtr(NoActivate);
        }
    }

    internal sealed class RenameWindow : Window
    {
        private readonly TextBox input;

        internal RenameWindow(string name)
        {
            Title = "重命名电脑副本";
            Width = 410;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
            Background = Brushes.White;
            var stack = new StackPanel { Margin = new Thickness(18) };
            stack.Children.Add(Theme.TextBlock("文件名", 13, Theme.Text, true));
            input = new TextBox { Text = name, Margin = new Thickness(0, 7, 0, 12), MinHeight = 29, FontSize = 13 };
            input.VerticalContentAlignment = VerticalAlignment.Center;
            stack.Children.Add(input);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancel = Theme.Button("取消");
            cancel.IsCancel = true;
            cancel.Margin = new Thickness(0, 0, 8, 0);
            var apply = Theme.Button("重命名", true);
            apply.IsDefault = true;
            apply.IsEnabled = !string.IsNullOrWhiteSpace(input.Text);
            input.TextChanged += delegate { apply.IsEnabled = !string.IsNullOrWhiteSpace(input.Text); };
            apply.Click += delegate { DialogResult = true; };
            actions.Children.Add(cancel);
            actions.Children.Add(apply);
            stack.Children.Add(actions);
            Content = stack;
            Loaded += delegate { input.Focus(); input.SelectAll(); };
        }

        internal string FileName { get { return input.Text.Trim(); } }
    }

    internal enum PreviewMode
    {
        BuiltIn,
        OpenWith
    }

    internal sealed class PreviewChoiceWindow : Window
    {
        internal PreviewMode Choice { get; private set; }

        internal PreviewChoiceWindow(string name)
        {
            Title = "选择打开方式";
            Width = 420;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
            Background = Brushes.White;
            var stack = new StackPanel { Margin = new Thickness(18) };
            var fileName = Theme.TextBlock(name, 15, Theme.Text, true);
            fileName.TextTrimming = TextTrimming.CharacterEllipsis;
            fileName.ToolTip = name;
            stack.Children.Add(fileName);

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0)
            };
            var cancel = Theme.Button("取消");
            cancel.IsCancel = true;
            cancel.Margin = new Thickness(0, 0, 8, 0);
            var system = Theme.Button("Windows 打开方式…");
            system.Margin = new Thickness(0, 0, 8, 0);
            system.Click += delegate { Select(PreviewMode.OpenWith); };
            var builtIn = Theme.Button("内置预览", true);
            builtIn.IsDefault = true;
            builtIn.Click += delegate { Select(PreviewMode.BuiltIn); };
            actions.Children.Add(cancel);
            actions.Children.Add(system);
            actions.Children.Add(builtIn);
            stack.Children.Add(actions);
            Content = stack;
            Loaded += delegate { builtIn.Focus(); };
        }

        private void Select(PreviewMode mode)
        {
            Choice = mode;
            DialogResult = true;
        }
    }

    internal sealed class PreviewWindow : Window
    {
        internal bool OpenedExternally { get; private set; }

        internal PreviewWindow(string title, string path, FileKind kind)
        {
            Title = "预览 · " + title;
            Width = 780;
            Height = 540;
            MinWidth = 380;
            MinHeight = 280;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = Theme.Background;
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
            var root = new DockPanel { Margin = new Thickness(13) };
            Content = root;
            var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(actions, Dock.Right);
            footer.Children.Add(actions);
            var status = Theme.TextBlock("", 12, Theme.Secondary);
            status.TextWrapping = TextWrapping.Wrap;
            status.Margin = new Thickness(0, 0, 10, 0);
            footer.Children.Add(status);
            var previewArea = new Grid { Background = Theme.ColorOf("#131A28") };
            var external = Theme.Button("打开方式…");
            external.Click += delegate
            {
                try
                {
                    if (TransferController.OpenWith(this, path)) OpenedExternally = true;
                }
                catch (Exception error) { status.Text = "无法打开文件。 " + error.GetBaseException().Message; }
            };
            actions.Children.Add(external);

            if (kind == FileKind.Recording || kind == FileKind.Video)
            {
                AddVideo(path, root, previewArea, actions, status);
            }
            else
            {
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.DecodePixelWidth = 1920;
                    using (var stream = File.OpenRead(path))
                    {
                        bitmap.StreamSource = stream;
                        bitmap.EndInit();
                    }
                    bitmap.Freeze();
                    previewArea.Children.Add(new Image { Source = bitmap, Stretch = Stretch.Uniform });
                }
                catch (Exception) { status.Text = "无法在工具内预览此格式。"; }
            }
            root.Children.Add(previewArea);
            PreviewKeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) Close(); };
        }

        private void AddVideo(string path, DockPanel root, Grid previewArea, Panel actions, TextBlock status)
        {
            var zoom = new MatrixTransform();
            previewArea.ClipToBounds = true;
            var video = new MediaElement
            {
                Stretch = Stretch.Uniform,
                LoadedBehavior = MediaState.Manual,
                UnloadedBehavior = MediaState.Close,
                Source = new Uri(path, UriKind.Absolute),
                RenderTransform = zoom
            };
            var play = Theme.Button("暂停", true);
            play.IsEnabled = false;
            play.Margin = new Thickness(0, 0, 8, 0);
            actions.Children.Insert(0, play);
            var volume = Theme.TextBlock("音量 " + VolumePercent(video.Volume) + "%", 12, Theme.Secondary);
            volume.Margin = new Thickness(0, 0, 10, 0);
            volume.ToolTip = "滚轮调音量，↑ / W 提高音量，↓ / S 降低音量";
            actions.Children.Insert(1, volume);
            previewArea.Children.Add(video);

            var timeline = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
            DockPanel.SetDock(timeline, Dock.Bottom);
            root.Children.Add(timeline);
            var elapsed = Theme.TextBlock("00:00 / --:--", 12, Theme.Secondary);
            elapsed.Margin = new Thickness(12, 0, 0, 0);
            DockPanel.SetDock(elapsed, Dock.Right);
            timeline.Children.Add(elapsed);
            var fastRate = Theme.TextBlock("", 12, Theme.Accent, true);
            fastRate.Margin = new Thickness(10, 0, 0, 0);
            fastRate.Visibility = Visibility.Collapsed;
            DockPanel.SetDock(fastRate, Dock.Right);
            timeline.Children.Add(fastRate);
            var zoomLabel = Theme.TextBlock("", 12, Theme.Secondary);
            zoomLabel.Margin = new Thickness(10, 0, 0, 0);
            zoomLabel.Visibility = Visibility.Collapsed;
            DockPanel.SetDock(zoomLabel, Dock.Right);
            timeline.Children.Add(zoomLabel);
            var seek = new Slider
            {
                Minimum = 0,
                Maximum = 1,
                SmallChange = 1,
                LargeChange = 10,
                IsEnabled = false,
                IsMoveToPointEnabled = true,
                VerticalAlignment = VerticalAlignment.Center,
                MinHeight = 24,
                ToolTip = "← / A 和 → / D 点按跳转 5 秒，长按 2 倍速快退或快进"
            };
            AutomationProperties.SetName(seek, "播放进度");
            timeline.Children.Add(seek);

            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
            bool playing = true;
            bool atEnd = false;
            bool dragging = false;
            bool updatingPosition = false;
            Action updateTime = delegate
            {
                elapsed.Text = FormatTime(seek.Value) + " / " + FormatTime(seek.Maximum);
            };
            Action<double> showPosition = seconds =>
            {
                updatingPosition = true;
                seek.Value = seconds;
                updatingPosition = false;
                updateTime();
            };
            Func<double> position = () => atEnd ? seek.Maximum : video.Position.TotalSeconds;
            Action<double> seekTo = seconds =>
            {
                if (!seek.IsEnabled) return;
                double target = Math.Max(0, Math.Min(seconds, seek.Maximum));
                video.Position = TimeSpan.FromSeconds(target);
                atEnd = target >= seek.Maximum;
                showPosition(target);
            };
            Action<double> changeVolume = step =>
            {
                video.Volume = Math.Max(0, Math.Min(1, video.Volume + step * 0.05));
                volume.Text = "音量 " + VolumePercent(video.Volume) + "%";
            };
            var shortcuts = new PreviewShortcuts(video, () => seek.IsEnabled && !dragging, () => playing, position, seekTo,
                changeVolume, rate =>
                {
                    fastRate.Text = rate ?? "";
                    fastRate.Visibility = rate == null ? Visibility.Collapsed : Visibility.Visible;
                });
            PreviewKeyDown += shortcuts.OnKeyDown;
            PreviewKeyUp += shortcuts.OnKeyUp;
            Deactivated += delegate { shortcuts.Cancel(); };
            PreviewMouseWheel += delegate(object sender, MouseWheelEventArgs e)
            {
                double steps = e.Delta / 120.0;
                if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
                {
                    if (!play.IsEnabled) return;
                    var size = new Size(video.ActualWidth, video.ActualHeight);
                    Point pointer = e.GetPosition(video);
                    zoom.Matrix = ZoomVideo(zoom.Matrix, size, pointer, steps);
                    double scale = zoom.Matrix.M11;
                    zoomLabel.Text = "缩放 " + (int)Math.Round(scale * 100) + "%";
                    zoomLabel.Visibility = scale > 1 ? Visibility.Visible : Visibility.Collapsed;
                }
                else
                {
                    changeVolume(steps);
                }
                e.Handled = true;
            };
            video.SizeChanged += delegate(object sender, SizeChangedEventArgs e)
            {
                var matrix = zoom.Matrix;
                if (matrix.IsIdentity || e.PreviousSize.Width <= 0 || e.PreviousSize.Height <= 0) return;
                matrix.OffsetX *= e.NewSize.Width / e.PreviousSize.Width;
                matrix.OffsetY *= e.NewSize.Height / e.PreviousSize.Height;
                zoom.Matrix = matrix;
            };
            timer.Tick += delegate
            {
                if (!dragging) showPosition(position());
            };
            seek.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(delegate
            {
                shortcuts.Cancel();
                dragging = true;
            }), true);
            seek.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(delegate(object sender, DragCompletedEventArgs e)
            {
                dragging = false;
                if (!seek.IsEnabled) return;
                if (e.Canceled) showPosition(position());
                else seekTo(seek.Value);
            }), true);
            seek.ValueChanged += delegate
            {
                if (updatingPosition || !seek.IsEnabled) return;
                updateTime();
                if (!dragging) seekTo(seek.Value);
            };

            play.Click += delegate
            {
                shortcuts.Cancel();
                if (playing)
                {
                    video.Pause();
                }
                else
                {
                    if (atEnd)
                    {
                        video.Position = TimeSpan.Zero;
                        atEnd = false;
                        if (seek.IsEnabled) showPosition(0);
                    }
                    video.Play();
                }
                playing = !playing;
                play.Content = playing ? "暂停" : "播放";
            };
            video.MediaOpened += delegate
            {
                play.IsEnabled = true;
                var duration = video.NaturalDuration;
                if (duration.HasTimeSpan && duration.TimeSpan > TimeSpan.Zero)
                {
                    seek.Maximum = duration.TimeSpan.TotalSeconds;
                    seek.IsEnabled = true;
                    showPosition(video.Position.TotalSeconds);
                    timer.Start();
                }
            };
            video.MediaFailed += delegate
            {
                timer.Stop();
                status.Text = "无法在工具内播放此格式。";
                play.IsEnabled = false;
                seek.IsEnabled = false;
                shortcuts.Cancel();
            };
            video.MediaEnded += delegate
            {
                video.Pause();
                atEnd = true;
                playing = false;
                shortcuts.Cancel();
                play.Content = "播放";
                if (seek.IsEnabled && !dragging) showPosition(seek.Maximum);
            };
            Loaded += delegate { video.Play(); };
            Closing += delegate
            {
                timer.Stop();
                seek.IsEnabled = false;
                shortcuts.Cancel();
                video.Close();
            };
        }

        private static int VolumePercent(double volume)
        {
            return (int)Math.Round(volume * 100);
        }

        internal static Matrix ZoomVideo(Matrix current, Size size, Point pointer, double steps)
        {
            double scale = Math.Max(1, Math.Min(4, current.M11 * Math.Pow(1.2, steps)));
            if (scale == 1) return Matrix.Identity;
            double x = Math.Max(0, Math.Min(size.Width, pointer.X));
            double y = Math.Max(0, Math.Min(size.Height, pointer.Y));
            double offsetX = Math.Max((1 - scale) * size.Width, Math.Min(0, current.OffsetX + (current.M11 - scale) * x));
            double offsetY = Math.Max((1 - scale) * size.Height, Math.Min(0, current.OffsetY + (current.M11 - scale) * y));
            return new Matrix(scale, 0, 0, scale, offsetX, offsetY);
        }

        private static string FormatTime(double seconds)
        {
            var position = TimeSpan.FromSeconds(seconds);
            return position.TotalHours >= 1
                ? ((int)position.TotalHours) + position.ToString(@"\:mm\:ss")
                : position.ToString(@"mm\:ss");
        }
    }
}
