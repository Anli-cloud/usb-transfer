using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace UsbTransfer
{
    internal sealed class CatalogRow
    {
        public string Identity;
        public string DeviceId;
        public string FileKey;
        public UsbFile Remote;
        public string SavedPath;
        public DateTime? DateUtc;
        public bool Ready;
        public FileKind Kind;
        public string Name { get; set; }
        public string KindText { get; set; }
        public string DateText { get; set; }
        public string SizeText { get; set; }
        public string Status { get; set; }
    }

    internal static class Theme
    {
        internal static readonly Brush Text = ColorOf("#19243B");
        internal static readonly Brush Secondary = ColorOf("#52627A");
        internal static readonly Brush Background = ColorOf("#F3F6FB");
        internal static readonly Brush Line = ColorOf("#DDE5F1");
        internal static readonly Brush Accent = ColorOf("#204ECB");
        internal static readonly Brush AccentLight = ColorOf("#EEF3FF");
        internal static readonly Brush Green = ColorOf("#12624D");

        internal static Brush ColorOf(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

        internal static TextBlock TextBlock(string text, double size, Brush color, bool strong = false)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = size,
                Foreground = color,
                FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        internal static Button Button(string title, bool primary = false)
        {
            var button = new Button
            {
                Content = title,
                Background = primary ? Accent : Brushes.White,
                Foreground = primary ? Brushes.White : Text,
                BorderBrush = primary ? Accent : Line,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(13, 7, 13, 7),
                MinHeight = 35,
                FontSize = 13,
                Cursor = Cursors.Hand,
                FontWeight = FontWeights.SemiBold
            };
            var shape = new FrameworkElementFactory(typeof(Border));
            shape.Name = "Surface";
            shape.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            shape.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            shape.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            shape.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            shape.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            presenter.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
            shape.AppendChild(presenter);
            var template = new ControlTemplate(typeof(Button)) { VisualTree = shape };
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.OpacityProperty, 0.82, "Surface"));
            template.Triggers.Add(hover);
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(Border.OpacityProperty, 0.40, "Surface"));
            template.Triggers.Add(disabled);
            button.Template = template;
            return button;
        }

        internal static Border Card(UIElement child, Thickness padding)
        {
            return new Border
            {
                Background = Brushes.White,
                BorderBrush = Line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = padding,
                Child = child
            };
        }
    }

    internal sealed class MainWindow : Window
    {
        private const int InitialRowCount = 10;
        private static readonly int[] DateRanges = { 1, 3, 7 };

        internal event Action<string> DeviceChanged;
        internal event Action<int> DaysChanged;
        internal event Action ChooseFolder;
        internal event Action OpenFolder;
        internal event Action SendFiles;
        internal event Action<bool> ImportSelected;
        internal event Action CopySelected;
        internal event Action PreviewSelected;
        internal event Action RenameSelected;
        internal event Action RecycleSelected;
        internal event Action LocateSelected;
        internal event Action RefreshRequested;

        private readonly TextBlock connectionText;
        private readonly TextBlock destinationText;
        private readonly TextBlock statusText;
        private readonly TextBlock emptyText;
        private readonly TextBlock countText;
        private readonly ComboBox devices;
        private readonly ComboBox dateRange;
        private readonly ComboBox categories;
        private readonly ComboBox statuses;
        private readonly ListView list;
        private readonly Button save;
        private readonly Button saveCopy;
        private readonly Button preview;
        private readonly Button copy;
        private readonly Button send;
        private readonly Button loadRemaining;
        private readonly ProgressBar progress;
        private List<CatalogRow> rows = new List<CatalogRow>();
        private List<CatalogRow> visibleRows = new List<CatalogRow>();
        private string displayedDeviceId;
        private bool loadingDevices;
        private bool showRemaining;

        internal MainWindow()
        {
            Title = "USB 快捷传输";
            Width = 1040;
            Height = 655;
            MinWidth = 710;
            MinHeight = 500;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Theme.Background;
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
            GuiPadding();

            var root = new Grid { Margin = new Thickness(25, 22, 25, 18) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Content = root;

            var heading = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 17) };
            var headingActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(headingActions, Dock.Right);
            send = AddButton(headingActions, "发送到手机", delegate { Raise(SendFiles); });
            heading.Children.Add(headingActions);
            var titleBox = new StackPanel();
            titleBox.Children.Add(Theme.TextBlock("USB 快捷传输", 27, Theme.Text, true));
            var lead = Theme.TextBlock("手机 → 电脑", 13, Theme.Secondary);
            lead.Margin = new Thickness(0, 5, 0, 0);
            titleBox.Children.Add(lead);
            heading.Children.Add(titleBox);
            root.Children.Add(heading);

            var connectionBar = new Grid();
            connectionBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            connectionBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            connectionBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            connectionText = Theme.TextBlock("等待 USB 文件传输", 13, Theme.Secondary, true);
            connectionText.Margin = new Thickness(0, 0, 14, 0);
            connectionBar.Children.Add(connectionText);
            devices = new ComboBox
            {
                Width = 205, Height = 32, FontSize = 13, DisplayMemberPath = "Name",
                SelectedValuePath = "Id", HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed
            };
            devices.SelectionChanged += delegate
            {
                if (!loadingDevices && devices.SelectedValue is string && DeviceChanged != null)
                    DeviceChanged((string)devices.SelectedValue);
            };
            Grid.SetColumn(devices, 1);
            connectionBar.Children.Add(devices);
            var folderActions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var folder = AddButton(folderActions, "保存目录…", delegate { Raise(ChooseFolder); });
            folder.ToolTip = "选择原件保存到电脑的位置";
            Grid.SetColumn(folderActions, 2);
            connectionBar.Children.Add(folderActions);
            var connectionCard = Theme.Card(connectionBar, new Thickness(13, 8, 9, 8));
            connectionCard.Margin = new Thickness(0, 0, 0, 14);
            Grid.SetRow(connectionCard, 1);
            root.Children.Add(connectionCard);

            var filesPanel = new Grid();
            filesPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            filesPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            filesPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            filesPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var titleBar = new DockPanel { LastChildFill = true, Margin = new Thickness(15, 14, 15, 9) };
            var filters = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(filters, Dock.Right);
            dateRange = Combo(filters, "最近 1 天", "最近 3 天", "最近 7 天");
            AutomationProperties.SetName(dateRange, "文件日期范围");
            categories = Combo(filters, "全部类型", "录屏", "截图", "安装包", "视频", "图片", "其他");
            statuses = Combo(filters, "全部状态", "待保存", "已保存");
            var refresh = AddButton(filters, "刷新", delegate { Raise(RefreshRequested); });
            refresh.ToolTip = "重新检查手机可见的文件";
            titleBar.Children.Add(filters);
            var listHeading = new StackPanel { Orientation = Orientation.Horizontal };
            listHeading.Children.Add(Theme.TextBlock("最近新增", 19, Theme.Text, true));
            countText = Theme.TextBlock("", 12, Theme.Secondary);
            countText.Margin = new Thickness(9, 0, 0, 0);
            listHeading.Children.Add(countText);
            titleBar.Children.Add(listHeading);
            filesPanel.Children.Add(titleBar);

            var actions = new WrapPanel { Margin = new Thickness(15, 0, 15, 11) };
            saveCopy = AddButton(actions, "保存并复制", delegate { if (ImportSelected != null) ImportSelected(true); }, true);
            save = AddButton(actions, "保存原件", delegate { if (ImportSelected != null) ImportSelected(false); });
            preview = AddButton(actions, "预览", delegate { Raise(PreviewSelected); });
            copy = AddButton(actions, "复制文件", delegate { Raise(CopySelected); });
            Grid.SetRow(actions, 1);
            filesPanel.Children.Add(actions);

            list = new ListView
            {
                FontSize = 13, Foreground = Theme.Text, BorderThickness = new Thickness(0, 1, 0, 1),
                BorderBrush = Theme.Line, SelectionMode = SelectionMode.Extended, Background = Brushes.White
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Auto);
            VirtualizingStackPanel.SetIsVirtualizing(list, true);
            VirtualizingStackPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
            var rowStyle = new Style(typeof(ListViewItem));
            rowStyle.Setters.Add(new Setter(Control.MinHeightProperty, 39.0));
            rowStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            list.ItemContainerStyle = rowStyle;
            var view = new GridView { AllowsColumnReorder = false };
            var nameColumn = Column("文件名", "Name", 323);
            view.Columns.Add(nameColumn);
            view.Columns.Add(Column("类型", "KindText", 89));
            view.Columns.Add(Column("日期 ↓", "DateText", 156));
            view.Columns.Add(Column("大小", "SizeText", 84));
            view.Columns.Add(Column("状态", "Status", 97));
            list.View = view;
            list.SizeChanged += delegate
            {
                nameColumn.Width = Math.Max(155, Math.Min(560, list.ActualWidth - 466));
            };
            var context = new ContextMenu();
            MenuAction(context, "预览", delegate { Raise(PreviewSelected); });
            MenuAction(context, "复制文件", delegate { Raise(CopySelected); });
            MenuAction(context, "打开所在目录", delegate { Raise(LocateSelected); });
            MenuAction(context, "重命名电脑副本…", delegate { Raise(RenameSelected); });
            MenuAction(context, "电脑副本移到回收站…", delegate { Raise(RecycleSelected); });
            context.Opened += delegate
            {
                var selected = SelectedRows;
                ((MenuItem)context.Items[0]).IsEnabled = selected.Count == 1 && CanPreview(selected[0]);
                ((MenuItem)context.Items[1]).IsEnabled = CanCopy(selected);
                ((MenuItem)context.Items[2]).IsEnabled = selected.Count == 1 && selected[0].SavedPath != null;
                ((MenuItem)context.Items[3]).IsEnabled = selected.Count == 1 && selected[0].SavedPath != null;
                ((MenuItem)context.Items[4]).IsEnabled = selected.Count > 0 && selected.All(row => row.SavedPath != null);
            };
            list.ContextMenu = context;
            list.SelectionChanged += delegate { UpdateActions(); };
            list.MouseDoubleClick += delegate
            {
                var selected = SelectedRows;
                if (selected.Count == 1 && CanPreview(selected[0])) Raise(PreviewSelected);
            };
            list.PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && copy.IsEnabled)
                {
                    Raise(CopySelected);
                    e.Handled = true;
                }
                else if (e.Key == Key.F2 && SelectedRows.Count == 1 && SelectedRows[0].SavedPath != null)
                {
                    Raise(RenameSelected);
                    e.Handled = true;
                }
            };
            var filesArea = new Grid();
            filesArea.Children.Add(list);
            emptyText = Theme.TextBlock("用数据线连接手机，再在手机上选择「文件传输」。", 14, Theme.Secondary);
            emptyText.HorizontalAlignment = HorizontalAlignment.Center;
            emptyText.TextWrapping = TextWrapping.Wrap;
            emptyText.TextAlignment = TextAlignment.Center;
            emptyText.Margin = new Thickness(22);
            emptyText.IsHitTestVisible = false;
            filesArea.Children.Add(emptyText);
            Grid.SetRow(filesArea, 2);
            filesPanel.Children.Add(filesArea);

            var directoryLine = new DockPanel { Margin = new Thickness(15, 8, 15, 8) };
            var openFolder = Theme.Button("打开保存目录");
            openFolder.Click += delegate { Raise(OpenFolder); };
            DockPanel.SetDock(openFolder, Dock.Right);
            directoryLine.Children.Add(openFolder);
            loadRemaining = Theme.Button("加载剩余");
            loadRemaining.Visibility = Visibility.Collapsed;
            loadRemaining.Margin = new Thickness(0, 0, 8, 0);
            loadRemaining.Click += delegate
            {
                showRemaining = true;
                ApplyFilter();
            };
            DockPanel.SetDock(loadRemaining, Dock.Right);
            directoryLine.Children.Add(loadRemaining);
            destinationText = Theme.TextBlock("", 12, Theme.Secondary);
            destinationText.Margin = new Thickness(0, 0, 12, 0);
            destinationText.TextTrimming = TextTrimming.CharacterEllipsis;
            directoryLine.Children.Add(destinationText);
            Grid.SetRow(directoryLine, 3);
            filesPanel.Children.Add(directoryLine);
            var filesCard = Theme.Card(filesPanel, new Thickness(0));
            Grid.SetRow(filesCard, 2);
            root.Children.Add(filesCard);

            var bottom = new DockPanel { Margin = new Thickness(1, 12, 1, 0), MinHeight = 20 };
            progress = new ProgressBar { Width = 144, Height = 6, Maximum = 100, Foreground = Theme.Accent, Visibility = Visibility.Collapsed };
            DockPanel.SetDock(progress, Dock.Right);
            bottom.Children.Add(progress);
            statusText = Theme.TextBlock("请选择手机上的 USB 文件传输模式", 12, Theme.Secondary);
            statusText.TextTrimming = TextTrimming.CharacterEllipsis;
            bottom.Children.Add(statusText);
            Grid.SetRow(bottom, 3);
            root.Children.Add(bottom);

            dateRange.SelectionChanged += delegate
            {
                showRemaining = false;
                if (DaysChanged != null) DaysChanged(SelectedDays);
                ApplyFilter();
            };
            categories.SelectionChanged += delegate { ResetPage(); };
            statuses.SelectionChanged += delegate { ResetPage(); };
            UpdateActions();
            send.IsEnabled = false;
        }

        internal int SelectedDays
        {
            get { return DateRanges[Math.Max(0, dateRange.SelectedIndex)]; }
        }

        internal List<CatalogRow> SelectedRows
        {
            get { return list.SelectedItems.Cast<CatalogRow>().ToList(); }
        }

        internal void SetDevices(List<UsbDevice> items, string selectedId, bool scanning)
        {
            loadingDevices = true;
            devices.ItemsSource = items;
            devices.SelectedValue = selectedId;
            loadingDevices = false;
            devices.Visibility = items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            var device = items.FirstOrDefault(item => item.Id == selectedId);
            connectionText.Text = device == null ? "等待 USB 文件传输" :
                (items.Count > 1 ? "USB 已连接" : "USB 已连接   " + device.Name);
            connectionText.ToolTip = device == null ? "请解锁手机，选择「文件传输」" : device.Name;
            connectionText.Foreground = device == null ? Theme.Secondary : Theme.Green;
            send.IsEnabled = device != null;
            emptyText.Text = scanning ? "正在读取手机上的最近文件…" :
                (device == null ? "用数据线连接手机，再在手机上选择「文件传输」。" : "没有符合当前筛选条件的文件。");
            if (!string.Equals(displayedDeviceId, selectedId, StringComparison.Ordinal))
            {
                displayedDeviceId = selectedId;
                ResetPage();
            }
        }

        internal void SetDestination(string path)
        {
            destinationText.Text = "保存到  " + path;
            destinationText.ToolTip = path;
        }

        internal void SetRows(List<CatalogRow> updated)
        {
            rows = updated;
            ApplyFilter();
        }

        internal void SetStatus(string message, int? percent, bool indeterminate)
        {
            statusText.Text = message;
            statusText.ToolTip = message;
            progress.Visibility = percent.HasValue || indeterminate ? Visibility.Visible : Visibility.Collapsed;
            progress.IsIndeterminate = indeterminate;
            progress.Value = percent.GetValueOrDefault();
        }

        private void GuiPadding()
        {
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        }

        private static bool SameRow(CatalogRow left, CatalogRow right)
        {
            return left.Identity == right.Identity && left.Name == right.Name && left.Kind == right.Kind &&
                left.Status == right.Status && left.DateUtc == right.DateUtc && left.DateText == right.DateText && left.SizeText == right.SizeText &&
                left.SavedPath == right.SavedPath && left.Ready == right.Ready && (left.Remote == null) == (right.Remote == null);
        }

        private void ApplyFilter()
        {
            int category = categories.SelectedIndex;
            int status = statuses.SelectedIndex;
            int total = 0;
            var shown = new List<CatalogRow>();
            foreach (var row in rows)
            {
                if ((category > 0 && (int)row.Kind != category - 1) ||
                    (status > 0 && (status == 1) != (row.SavedPath == null))) continue;
                total++;
                if (showRemaining || shown.Count < InitialRowCount) shown.Add(row);
            }
            if (visibleRows.Count == shown.Count && visibleRows.Zip(shown, SameRow).All(equal => equal))
            {
                for (int index = 0; index < visibleRows.Count; index++)
                    visibleRows[index].Remote = shown[index].Remote;
            }
            else
            {
                var selected = new HashSet<string>(SelectedRows.Select(row => row.Identity));
                visibleRows = shown;
                list.ItemsSource = visibleRows;
                foreach (var row in visibleRows.Where(row => selected.Contains(row.Identity)))
                    list.SelectedItems.Add(row);
            }
            int remaining = total - shown.Count;
            countText.Text = remaining > 0 ? "显示 " + shown.Count + " / " + total + " 项" : total + " 项";
            loadRemaining.Visibility = remaining > 0 ? Visibility.Visible : Visibility.Collapsed;
            emptyText.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateActions();
        }

        private void ResetPage()
        {
            showRemaining = false;
            ApplyFilter();
        }

        private void UpdateActions()
        {
            var selected = SelectedRows;
            save.IsEnabled = selected.Any(row => row.Ready && row.SavedPath == null);
            saveCopy.IsEnabled = selected.Count > 0 && selected.All(row => row.Ready || row.SavedPath != null);
            copy.IsEnabled = CanCopy(selected);
            preview.IsEnabled = selected.Count == 1 && CanPreview(selected[0]);
        }

        private static bool CanPreview(CatalogRow row)
        {
            return (row.Ready || row.SavedPath != null) &&
                (row.Kind == FileKind.Recording || row.Kind == FileKind.Screenshot || row.Kind == FileKind.Video || row.Kind == FileKind.Image);
        }

        private static bool CanCopy(List<CatalogRow> rows)
        {
            return rows.Count == 1 ? rows[0].Ready || rows[0].SavedPath != null :
                rows.Count > 0 && rows.All(row => row.SavedPath != null);
        }

        private static GridViewColumn Column(string name, string binding, double width)
        {
            var cell = new FrameworkElementFactory(typeof(TextBlock));
            cell.SetBinding(TextBlock.TextProperty, new Binding(binding));
            cell.SetBinding(FrameworkElement.ToolTipProperty, new Binding(binding));
            cell.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            cell.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            return new GridViewColumn { Header = name, Width = width, CellTemplate = new DataTemplate { VisualTree = cell } };
        }

        private static ComboBox Combo(Panel parent, params string[] labels)
        {
            var combo = new ComboBox { Width = 100, Height = 32, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
            combo.ItemsSource = labels;
            combo.SelectedIndex = 0;
            parent.Children.Add(combo);
            return combo;
        }

        private static Button AddButton(Panel parent, string label, Action action, bool primary = false)
        {
            var button = Theme.Button(label, primary);
            button.Margin = new Thickness(0, 0, 7, 0);
            button.Click += delegate { action(); };
            parent.Children.Add(button);
            return button;
        }

        private static void MenuAction(ContextMenu menu, string label, Action action)
        {
            var item = new MenuItem { Header = label };
            item.Click += delegate { action(); };
            menu.Items.Add(item);
        }

        private static void Raise(Action action)
        {
            if (action != null) action();
        }
    }
}
