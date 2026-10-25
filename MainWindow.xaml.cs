using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using RescueDesk.Core;
using Condition = RescueDesk.Core.Condition;

namespace RescueDesk;

public partial class MainWindow : Window
{
    static readonly HashSet<string> ImageTypes = ["JPG", "JPEG", "PNG", "GIF", "BMP", "TIF", "TIFF", "ICO", "WEBP", "HEIC"];
    static readonly HashSet<string> TextTypes = ["TXT", "CSV", "LOG", "XML", "JSON", "MD", "INI", "HTML", "HTM", "CSS", "JS", "CS", "PY", "SQL", "BAT", "PS1", "CFG", "YAML", "YML"];
    const string AllTypes = "All types";

    sealed class TypeOption(Carver.FileType type)
    {
        public string Key => type.Key;
        public string Description => type.Description;
        public bool Enabled { get; set; } = true;
    }

    readonly ObservableCollection<FoundFile> _results = [];
    readonly ListCollectionView _view;
    readonly List<TypeOption> _types = Carver.Types.Select(t => new TypeOption(t)).ToList();
    ScanSource? _source;
    RawDevice? _device;
    CancellationTokenSource? _cts;
    CancellationTokenSource? _previewCts;
    bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        _view = (ListCollectionView)CollectionViewSource.GetDefaultView(_results);
        _view.Filter = Matches;
        ResultsGrid.ItemsSource = _view;
        TypeList.ItemsSource = _types;
        RebuildTypeFilter();
        // "RescueDesk.exe image.img" opens that disk image.
        Loaded += (_, _) =>
        {
            var args = Environment.GetCommandLineArgs();
            ScanSource? image = null;
            if (args.Length > 1 && File.Exists(args[1]))
            {
                try { image = SourceCatalog.FromImage(args[1]); }
                catch (Exception ex) { ShowError(ex); }
            }
            LoadSources(image);
        };
        Closing += (_, e) =>
        {
            if (_busy && MessageBox.Show(this, "A scan or recovery is still running. Stop it and close?", "RescueDesk",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            _cts?.Cancel();
        };
        Closed += (_, _) => _device?.Dispose();
        UpdateSummary();
    }

    // ---------- Sources ----------

    async void LoadSources(ScanSource? select = null)
    {
        RefreshButton.IsEnabled = false;
        try
        {
            var list = await Task.Run(SourceCatalog.Enumerate);
            var images = SourceBox.Items.OfType<ScanSource>().Where(s => s.Kind == SourceKind.Image);
            var all = list.Concat(images).ToList();
            if (select != null && !all.Contains(select)) all.Add(select);
            var previous = select ?? _source;
            SourceBox.ItemsSource = all;
            SourceBox.SelectedItem = all.FirstOrDefault(s => s.DevicePath == previous?.DevicePath) ?? all.FirstOrDefault();
            if (all.Count == 0)
                MessageBox.Show(this, "No drives could be opened. RescueDesk needs to run as administrator to read drives directly.",
                    "RescueDesk", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { RefreshButton.IsEnabled = !_busy; }
    }

    void Refresh_Click(object sender, RoutedEventArgs e) => LoadSources();

    void OpenImage_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Open a disk or partition image",
            Filter = "Disk images (*.img;*.dd;*.raw;*.bin;*.iso;*.001)|*.img;*.dd;*.raw;*.bin;*.iso;*.001|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        try { LoadSources(SourceCatalog.FromImage(dlg.FileName)); }
        catch (Exception ex) { ShowError(ex); }
    }

    void SourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var next = SourceBox.SelectedItem as ScanSource;
        if (next?.DevicePath == _source?.DevicePath) { _source = next; return; }
        _source = next;
        _device?.Dispose();
        _device = null;
        _results.Clear();
        RebuildTypeFilter();
        UpdateSummary();
        ShowPreview(null);
    }

    // ---------- Scanning ----------

    async void QuickScan_Click(object sender, RoutedEventArgs e)
    {
        if (!PrepareScan()) return;
        bool includeExisting = IncludeExistingBox.IsChecked == true;
        await RunBusy("Reading file table…", async (progress, ct) =>
        {
            var dev = _device!;
            var files = await Task.Run(() =>
            {
                var volumes = NtfsVolume.Find(dev);
                if (volumes.Count == 0) return null;
                var all = new List<FoundFile>();
                for (int i = 0; i < volumes.Count; i++)
                    all.AddRange(NtfsScanner.Scan(dev, volumes[i], includeExisting, volumes.Count > 1 ? $"Partition {i + 1}" : null, progress, ct));
                return all;
            }, ct);

            if (files == null)
            {
                MessageBox.Show(this, "No NTFS file system was found on this drive. It may be FAT/exFAT, formatted or badly damaged.\n\nUse Deep scan to search it for files instead.",
                    "RescueDesk", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            // Best candidates first: deleted before existing, then by condition.
            foreach (var f in files.OrderBy(f => f.Kind).ThenBy(f => f.Condition).ThenBy(f => f.Folder).ThenBy(f => f.Name))
                _results.Add(f);
        });
    }

    async void DeepScan_Click(object sender, RoutedEventArgs e)
    {
        var types = _types.Where(t => t.Enabled).Select(t => t.Key).ToHashSet();
        if (types.Count == 0)
        {
            MessageBox.Show(this, "Choose at least one file type under \"File types\".", "RescueDesk");
            return;
        }
        if (!PrepareScan()) return;
        bool freeOnly = FreeSpaceOnlyBox.IsChecked == true;
        await RunBusy("Starting deep scan…", async (progress, ct) =>
        {
            var dev = _device!;
            await Task.Run(() =>
            {
                Func<long, bool>? skip = null;
                if (freeOnly)
                {
                    var map = AllocationMap.Load(dev, NtfsVolume.Find(dev));
                    if (map.VolumeCount > 0) skip = map.IsAllocated;
                }
                Carver.Scan(dev, types, skip, progress, ct,
                    f => Dispatcher.BeginInvoke(() => { _results.Add(f); UpdateSummary(); }));
            }, ct);
        });
    }

    bool PrepareScan()
    {
        if (_source == null)
        {
            MessageBox.Show(this, "Choose a drive to scan first.", "RescueDesk");
            return false;
        }
        try
        {
            _device ??= _source.Open();
        }
        catch (Exception ex)
        {
            ShowError(ex);
            return false;
        }
        _results.Clear();
        ShowPreview(null);
        return true;
    }

    async Task RunBusy(string status, Func<IProgress<ScanProgress>, CancellationToken, Task> work)
    {
        _cts = new CancellationTokenSource();
        SetBusy(true, status);
        var progress = new Progress<ScanProgress>(p =>
        {
            if (!_busy) return;
            ProgressBar.Value = p.Fraction;
            ProgressText.Text = p.Status;
        });
        try
        {
            await work(progress, _cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(ex); }
        finally
        {
            SetBusy(false, "");
            _cts.Dispose();
            _cts = null;
            RebuildTypeFilter();
            UpdateSummary();
            if (_device?.UnreadableSectors > 0)
                SelectionText.Text += $"  ·  {_device.UnreadableSectors:N0} unreadable sectors were skipped";
        }
    }

    void SetBusy(bool busy, string status)
    {
        _busy = busy;
        ProgressPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ProgressBar.Value = 0;
        ProgressText.Text = status;
        CancelButton.IsEnabled = true;
        QuickScanButton.IsEnabled = DeepScanButton.IsEnabled = SourceBox.IsEnabled = RefreshButton.IsEnabled = !busy;
        UpdateSummary();
    }

    void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        CancelButton.IsEnabled = false;
        ProgressText.Text = "Stopping…";
    }

    // ---------- Results ----------

    bool Matches(object o)
    {
        var f = (FoundFile)o;
        if (HideOverwrittenBox.IsChecked == true && f.Condition == Condition.Overwritten) return false;
        if (TypeFilter.SelectedItem is string t && t != AllTypes && f.Extension != t) return false;
        var q = SearchBox.Text.Trim();
        return q.Length == 0
            || f.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || (f.Folder?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (_view == null) return;
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _view.Refresh();
        UpdateSummary();
    }

    void RebuildTypeFilter()
    {
        var current = TypeFilter.SelectedItem as string;
        var types = _results.GroupBy(f => f.Extension).Where(g => g.Key.Length > 0)
            .OrderByDescending(g => g.Count()).Select(g => g.Key).Take(60).ToList();
        types.Insert(0, AllTypes);
        TypeFilter.ItemsSource = types;
        TypeFilter.SelectedItem = current != null && types.Contains(current) ? current : AllTypes;
    }

    void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (FoundFile f in _view) f.IsChecked = true;
        UpdateSummary();
    }

    void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var f in _results) f.IsChecked = false;
        UpdateSummary();
    }

    void RowCheck_Click(object sender, RoutedEventArgs e) => UpdateSummary();

    void ResultsGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || ResultsGrid.SelectedItems.Count == 0) return;
        var items = ResultsGrid.SelectedItems.Cast<FoundFile>().ToList();
        bool check = !items.All(f => f.IsChecked);
        foreach (var f in items) f.IsChecked = check;
        UpdateSummary();
        e.Handled = true;
    }

    void UpdateSummary()
    {
        int shown = _view?.Count ?? 0;
        EmptyText.Visibility = _results.Count == 0 && !_busy ? Visibility.Visible : Visibility.Collapsed;
        SummaryText.Text = _results.Count == 0 ? "" : shown == _results.Count
            ? $"{_results.Count:N0} files found"
            : $"{_results.Count:N0} files found, {shown:N0} shown";
        var chosen = _results.Where(f => f.IsChecked).ToList();
        SelectionText.Text = chosen.Count == 0 ? (_results.Count > 0 ? "Tick the files you want back." : "")
            : $"{chosen.Count:N0} selected · {Format.Size(chosen.Sum(f => f.Size))}";
        RecoverButton.IsEnabled = chosen.Count > 0 && !_busy;
    }

    // ---------- Preview ----------

    void ResultsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ShowPreview(ResultsGrid.SelectedItems.Count == 1 ? ResultsGrid.SelectedItem as FoundFile : null);

    async void ShowPreview(FoundFile? f)
    {
        _previewCts?.Cancel();
        PreviewImage.Source = null;
        PreviewText.Text = "";
        PreviewTextScroll.Visibility = Visibility.Collapsed;
        PreviewHint.Visibility = Visibility.Visible;
        DetailsPanel.Visibility = f == null ? Visibility.Collapsed : Visibility.Visible;
        if (f == null)
        {
            PreviewHint.Text = "Select a file to preview it.";
            return;
        }

        DetailName.Text = f.Name;
        var info = new StringBuilder();
        info.AppendLine($"{f.KindText} · {f.SizeText}");
        if (f.Modified is { } m) info.AppendLine($"Modified {m:g}");
        if (f.Folder != null) info.AppendLine(f.FolderText);
        info.Append($"Condition: {f.ConditionText}");
        if (f.Note != null) info.Append($"\n{f.Note}");
        DetailInfo.Text = info.ToString();

        bool image = ImageTypes.Contains(f.Extension) && f.Size <= 64L << 20;
        bool text = TextTypes.Contains(f.Extension);
        if ((!image && !text) || _device == null)
        {
            PreviewHint.Text = "No preview for this file type.";
            return;
        }

        PreviewHint.Text = "Loading preview…";
        var cts = _previewCts = new CancellationTokenSource();
        var dev = _device;
        try
        {
            var result = await Task.Run<object?>(() =>
            {
                var ms = new MemoryStream();
                var limited = image ? f : new FoundFile
                {
                    Name = f.Name, Size = Math.Min(f.Size, 32 * 1024), Extents = f.Extents, ResidentData = f.ResidentData, Compressed = f.Compressed,
                };
                limited.CopyTo(dev, ms, cts.Token);
                ms.Position = 0;
                if (!image) return Encoding.UTF8.GetString(ms.ToArray());
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 600;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }, cts.Token);
            if (cts.IsCancellationRequested) return;
            if (result is BitmapSource b) PreviewImage.Source = b;
            else
            {
                PreviewText.Text = (string)result!;
                PreviewTextScroll.Visibility = Visibility.Visible;
            }
            PreviewHint.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException) { }
        catch
        {
            if (!cts.IsCancellationRequested) PreviewHint.Text = "Preview not available. The file may be damaged or partly overwritten.";
        }
    }

    // ---------- Recovery ----------

    async void Recover_Click(object sender, RoutedEventArgs e)
    {
        var files = _results.Where(f => f.IsChecked).ToList();
        if (files.Count == 0 || _device == null || _source == null) return;

        var dlg = new OpenFolderDialog { Title = "Choose where to save the recovered files (on a different drive)" };
        if (dlg.ShowDialog(this) != true) return;
        var dest = dlg.FolderName;

        if (_source.DriveRoot is { } root && string.Equals(Path.GetPathRoot(dest), root, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, $"You can't save to {root.TrimEnd('\\')}, the drive you are recovering from: writing there could overwrite the files you're trying to rescue.\n\nPick a folder on another drive, such as a USB stick or external disk.",
                "RescueDesk", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_source.Kind == SourceKind.PhysicalDisk && MessageBox.Show(this,
                $"Make sure {dest} is not on the disk you are scanning, or recovered files could overwrite lost ones.\n\nContinue?",
                "RescueDesk", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        long need = files.Sum(f => f.Size);
        try
        {
            var free = new DriveInfo(Path.GetPathRoot(dest)!).AvailableFreeSpace;
            if (free < need && MessageBox.Show(this, $"The selected files need {Format.Size(need)}, but only {Format.Size(free)} is free there. Continue anyway?",
                    "RescueDesk", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
        }
        catch { }

        RecoveryResult? result = null;
        var dev = _device;
        await RunBusy("Recovering…", async (progress, ct) =>
            result = await Task.Run(() => Recovery.Run(dev, files, dest, progress, ct), ct));
        if (result == null) return;

        var msg = $"{result.Recovered:N0} of {files.Count:N0} files were saved to {dest}.";
        if (result.Failures.Count > 0)
            msg += $"\n\n{result.Failures.Count} failed:\n" + string.Join("\n", result.Failures.Take(10)) + (result.Failures.Count > 10 ? "\n…" : "");
        MessageBox.Show(this, msg, "Recovery finished", MessageBoxButton.OK, result.Failures.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dest}\"") { UseShellExecute = true });
    }

    void ShowError(Exception ex) =>
        MessageBox.Show(this, ex.Message, "RescueDesk", MessageBoxButton.OK, MessageBoxImage.Error);
}
