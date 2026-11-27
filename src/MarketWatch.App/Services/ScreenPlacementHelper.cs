using System.Windows;
using System.Windows.Interop;
using WinForms = System.Windows.Forms;

namespace MarketWatch.App.Services;

public static class ScreenPlacementHelper
{
    /// <summary>
    /// Centers a WPF window on the screen containing the cursor (or active owner window),
    /// properly accounting for Per-Monitor V2 DPI scaling.
    /// </summary>
    public static void CenterOnCurrentWorkArea(Window window, Window? preferredOwner = null)
    {
        var screen = ResolveTargetScreen(preferredOwner);
        var workAreaPx = screen.WorkingArea;

        var dpiScaleX = 1.0;
        var dpiScaleY = 1.0;

        var source = PresentationSource.FromVisual(window)
            ?? (preferredOwner is not null ? PresentationSource.FromVisual(preferredOwner) : null);

        if (source?.CompositionTarget is not null)
        {
            dpiScaleX = source.CompositionTarget.TransformToDevice.M11;
            dpiScaleY = source.CompositionTarget.TransformToDevice.M22;
        }

        var workLeft = workAreaPx.Left / dpiScaleX;
        var workTop = workAreaPx.Top / dpiScaleY;
        var workWidth = workAreaPx.Width / dpiScaleX;
        var workHeight = workAreaPx.Height / dpiScaleY;

        var winW = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
        var winH = window.ActualHeight > 0 ? window.ActualHeight : window.Height;

        if (double.IsNaN(winW) || winW <= 0) winW = 460;
        if (double.IsNaN(winH) || winH <= 0) winH = 340;

        window.Left = Math.Max(workLeft, workLeft + (workWidth - winW) / 2.0);
        window.Top = Math.Max(workTop, workTop + (workHeight - winH) / 2.0);
    }

    public static Rect GetCurrentScreenBoundsDip(Window? referenceWindow = null)
    {
        var screen = ResolveTargetScreen(referenceWindow);
        var bounds = screen.Bounds;

        var dpiScaleX = 1.0;
        var dpiScaleY = 1.0;
        var source = referenceWindow is not null ? PresentationSource.FromVisual(referenceWindow) : null;
        if (source?.CompositionTarget is not null)
        {
            dpiScaleX = source.CompositionTarget.TransformToDevice.M11;
            dpiScaleY = source.CompositionTarget.TransformToDevice.M22;
        }

        return new Rect(
            bounds.Left / dpiScaleX,
            bounds.Top / dpiScaleY,
            bounds.Width / dpiScaleX,
            bounds.Height / dpiScaleY);
    }

    public static void PositionMiniWindowDefault(Window window, double? savedLeft, double? savedTop)
    {
        var screen = WinForms.Screen.PrimaryScreen ?? WinForms.Screen.AllScreens.First();
        var workArea = screen.WorkingArea;
        var source = PresentationSource.FromVisual(window);
        var scaleX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        var scaleY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

        var wLeft = workArea.Left / scaleX;
        var wTop = workArea.Top / scaleY;
        var wWidth = workArea.Width / scaleX;
        var wHeight = workArea.Height / scaleY;

        if (savedLeft.HasValue && savedTop.HasValue)
        {
            // Ensure saved coordinates are within at least one connected screen
            var ptPx = new System.Drawing.Point((int)(savedLeft.Value * scaleX), (int)(savedTop.Value * scaleY));
            if (WinForms.Screen.AllScreens.Any(s => s.WorkingArea.Contains(ptPx)))
            {
                window.Left = savedLeft.Value;
                window.Top = savedTop.Value;
                return;
            }
        }

        var winW = window.ActualWidth > 0 ? window.ActualWidth : (double.IsNaN(window.Width) ? 280 : window.Width);
        window.Left = Math.Max(wLeft, wLeft + wWidth - winW - 20);
        window.Top = wTop + 20;
    }

    private static WinForms.Screen ResolveTargetScreen(Window? preferredOwner)
    {
        try
        {
            if (preferredOwner is not null && preferredOwner.IsVisible)
            {
                var hwnd = new WindowInteropHelper(preferredOwner).Handle;
                if (hwnd != IntPtr.Zero)
                    return WinForms.Screen.FromHandle(hwnd);
            }
            return WinForms.Screen.FromPoint(WinForms.Cursor.Position);
        }
        catch
        {
            return WinForms.Screen.PrimaryScreen ?? WinForms.Screen.AllScreens.First();
        }
    }
}
