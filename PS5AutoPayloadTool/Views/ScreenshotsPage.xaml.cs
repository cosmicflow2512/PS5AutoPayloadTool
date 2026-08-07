using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PS5AutoPayloadTool.Modules.Capture;
using PS5AutoPayloadTool.Modules.Core;

namespace PS5AutoPayloadTool.Views;

public partial class ScreenshotsPage : UserControl
{
    public class ShotItem
    {
        public string        FilePath  { get; init; } = "";
        public string        FileName  { get; init; } = "";
        public string        Meta      { get; init; } = "";
        public ImageSource?  Thumbnail { get; init; }
    }

    private readonly ObservableCollection<ShotItem> _items = new();

    public ScreenshotsPage()
    {
        InitializeComponent();
        Gallery.ItemsSource = _items;
    }

    public void Refresh() => LoadGallery();

    // ── Capture ──────────────────────────────────────────────────────────────

    private void BtnCaptureAll_Click(object sender, RoutedEventArgs e)     => _ = CaptureAsync(CaptureMode.AllScreens);
    private void BtnCapturePrimary_Click(object sender, RoutedEventArgs e) => _ = CaptureAsync(CaptureMode.PrimaryScreen);
    private void BtnCaptureActive_Click(object sender, RoutedEventArgs e)  => _ = CaptureAsync(CaptureMode.ActiveWindow);

    private async Task CaptureAsync(CaptureMode mode)
    {
        SetButtonsEnabled(false);
        SetStatus("Capturing…", isError: false);
        try
        {
            var delayMs         = ParseDelay();
            var copyToClipboard = ChkClipboard.IsChecked == true;
            var hideAppWindow   = ChkHideApp.IsChecked == true;

            var result = await ScreenCaptureService.CaptureAsync(mode, delayMs, copyToClipboard, hideAppWindow);
            if (result == null)
            {
                SetStatus("Capture failed — see Logs.", isError: true);
                return;
            }

            LoadGallery();
            var clip = copyToClipboard ? "  (clipboard ✓)" : "";
            SetStatus($"Saved {Path.GetFileName(result.FilePath)} — {result.Width}×{result.Height}{clip}", isError: false);
        }
        finally
        {
            SetButtonsEnabled(true);
        }
    }

    private int ParseDelay()
    {
        if (CmbDelay.SelectedItem is ComboBoxItem it && int.TryParse(it.Tag?.ToString(), out var ms))
            return ms;
        return 0;
    }

    // ── Gallery ──────────────────────────────────────────────────────────────

    private void LoadGallery()
    {
        _items.Clear();
        try
        {
            Directory.CreateDirectory(AppPaths.ScreenshotsDir);
            var files = Directory.EnumerateFiles(AppPaths.ScreenshotsDir, "*.png")
                                 .Select(p => new FileInfo(p))
                                 .OrderByDescending(fi => fi.LastWriteTime);

            foreach (var fi in files)
            {
                var bmp = TryLoadThumbnail(fi.FullName);
                _items.Add(new ShotItem
                {
                    FilePath  = fi.FullName,
                    FileName  = fi.Name,
                    Meta      = $"{fi.LastWriteTime:yyyy-MM-dd HH:mm}  ·  {fi.Length / 1024} KB",
                    Thumbnail = bmp
                });
            }
        }
        catch (Exception ex)
        {
            LogService.Warn("Capture", $"Gallery load failed: {ex.Message}");
        }

        TxtEmpty.Visibility        = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TxtGalleryHeader.Text      = _items.Count == 0
            ? "GALLERY"
            : $"GALLERY  ({_items.Count} file{(_items.Count == 1 ? "" : "s")})";
    }

    private static BitmapImage? TryLoadThumbnail(string path)
    {
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption   = BitmapCacheOption.OnLoad;   // fully load so we don't lock the file
            bi.DecodePixelWidth = 240;                     // downscale to save memory
            bi.UriSource     = new Uri(path, UriKind.Absolute);
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch
        {
            return null;
        }
    }

    // ── Per-item buttons ─────────────────────────────────────────────────────

    private void Image_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string path)
            OpenFile(path);
    }

    private void BtnOpen_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string path)
            OpenFile(path);
    }

    private void BtnCopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string path)
        {
            try
            {
                Clipboard.SetText(path);
                SetStatus($"Path copied: {Path.GetFileName(path)}", isError: false);
            }
            catch (Exception ex)
            {
                SetStatus($"Clipboard error: {ex.Message}", isError: true);
            }
        }
    }

    private void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string path) return;

        var name   = Path.GetFileName(path);
        var answer = MessageBox.Show($"Delete {name}?", "Delete screenshot",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            File.Delete(path);
            LoadGallery();
            SetStatus($"Deleted {name}", isError: false);
        }
        catch (Exception ex)
        {
            SetStatus($"Delete failed: {ex.Message}", isError: true);
        }
    }

    // ── Gallery toolbar buttons ──────────────────────────────────────────────

    private void BtnRefresh_Click(object sender, RoutedEventArgs e) => LoadGallery();

    private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.ScreenshotsDir);
            Process.Start(new ProcessStartInfo
            {
                FileName        = AppPaths.ScreenshotsDir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            SetStatus($"Open folder failed: {ex.Message}", isError: true);
        }
    }

    private void BtnDeleteAll_Click(object sender, RoutedEventArgs e)
    {
        if (_items.Count == 0) return;

        var answer = MessageBox.Show(
            $"Delete all {_items.Count} screenshot(s)? This cannot be undone.",
            "Delete all screenshots",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        var failed = 0;
        foreach (var item in _items.ToList())
        {
            try { File.Delete(item.FilePath); }
            catch { failed++; }
        }
        LoadGallery();
        SetStatus(failed == 0
            ? "All screenshots deleted"
            : $"Deleted with {failed} failure(s) — see Logs", isError: failed != 0);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void OpenFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName        = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            SetStatus($"Open failed: {ex.Message}", isError: true);
        }
    }

    private void SetButtonsEnabled(bool enabled)
    {
        BtnCaptureAll.IsEnabled     = enabled;
        BtnCapturePrimary.IsEnabled = enabled;
        BtnCaptureActive.IsEnabled  = enabled;
    }

    private void SetStatus(string message, bool isError)
    {
        TxtStatus.Text       = message;
        TxtStatus.Foreground = (Brush)FindResource(isError ? "Red" : "Green");
    }
}
