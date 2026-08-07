using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using PS5AutoPayloadTool.Modules.Core;
using WinFormsScreen = System.Windows.Forms.Screen;

namespace PS5AutoPayloadTool.Modules.Capture;

public enum CaptureMode { AllScreens, PrimaryScreen, ActiveWindow }

public record CaptureResult(string FilePath, int Width, int Height, long FileSize);

/// <summary>
/// GDI-based screen capture. Writes a PNG under AppPaths.ScreenshotsDir and
/// optionally copies the image to the WPF clipboard.
/// </summary>
public static class ScreenCaptureService
{
    private const string Module = "Capture";

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    /// <summary>
    /// Captures according to <paramref name="mode"/>, waiting <paramref name="delayMs"/>
    /// beforehand so the caller can dismiss any menu/dropdown.
    /// The application's own main window is temporarily hidden when capturing so
    /// it doesn't appear in the screenshot (restored afterwards).
    /// </summary>
    public static async Task<CaptureResult?> CaptureAsync(
        CaptureMode mode,
        int delayMs,
        bool copyToClipboard,
        bool hideAppWindow)
    {
        var main = Application.Current?.MainWindow;
        var wasVisible = main is { IsVisible: true };

        try
        {
            if (hideAppWindow && wasVisible && main != null)
            {
                main.Hide();
                // One dispatcher round-trip so the hide actually renders before we snap.
                await Task.Delay(120);
            }

            if (delayMs > 0)
                await Task.Delay(delayMs);

            return await Task.Run(() => CaptureCore(mode, copyToClipboard));
        }
        finally
        {
            if (hideAppWindow && wasVisible && main != null)
                main.Show();
        }
    }

    private static CaptureResult? CaptureCore(CaptureMode mode, bool copyToClipboard)
    {
        try
        {
            var bounds = ResolveBounds(mode);
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                LogService.Warn(Module, $"Skipped: empty bounds for mode {mode}");
                return null;
            }

            using var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);

            Directory.CreateDirectory(AppPaths.ScreenshotsDir);
            var filename = $"screenshot-{DateTime.Now:yyyyMMdd-HHmmss}.png";
            var path     = Path.Combine(AppPaths.ScreenshotsDir, filename);
            bmp.Save(path, ImageFormat.Png);

            if (copyToClipboard)
                TryCopyToClipboard(path);

            var size = new FileInfo(path).Length;
            LogService.Info(Module, $"Saved {filename} ({bounds.Width}x{bounds.Height}, {size / 1024} KB)");
            return new CaptureResult(path, bounds.Width, bounds.Height, size);
        }
        catch (Exception ex)
        {
            LogService.Error(Module, $"Capture failed ({mode}): {ex.Message}");
            return null;
        }
    }

    private static Rectangle ResolveBounds(CaptureMode mode)
    {
        switch (mode)
        {
            case CaptureMode.PrimaryScreen:
                return WinFormsScreen.PrimaryScreen?.Bounds ?? Rectangle.Empty;

            case CaptureMode.ActiveWindow:
            {
                var hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero || IsIconic(hwnd) ||
                    !GetWindowRect(hwnd, out var r) || r.Right <= r.Left || r.Bottom <= r.Top)
                {
                    LogService.Warn(Module, "Active window not usable — falling back to primary screen");
                    return WinFormsScreen.PrimaryScreen?.Bounds ?? Rectangle.Empty;
                }
                return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            }

            case CaptureMode.AllScreens:
            default:
            {
                var union = Rectangle.Empty;
                foreach (var s in WinFormsScreen.AllScreens)
                    union = union.IsEmpty ? s.Bounds : Rectangle.Union(union, s.Bounds);
                return union;
            }
        }
    }

    private static void TryCopyToClipboard(string pngPath)
    {
        try
        {
            // Marshal onto the UI thread — WPF Clipboard requires STA.
            var app = Application.Current;
            if (app?.Dispatcher == null) return;

            app.Dispatcher.Invoke(() =>
            {
                var bi = new System.Windows.Media.Imaging.BitmapImage();
                bi.BeginInit();
                bi.CacheOption  = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bi.UriSource    = new Uri(pngPath, UriKind.Absolute);
                bi.EndInit();
                bi.Freeze();
                Clipboard.SetImage(bi);
            });
        }
        catch (Exception ex)
        {
            LogService.Warn(Module, $"Clipboard copy failed: {ex.Message}");
        }
    }
}
