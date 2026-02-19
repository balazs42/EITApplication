#if WINDOWS
using Microsoft.Maui.Controls;
using Microsoft.UI.Windowing;
using WinRT.Interop;

public static class WindowBringToFrontExtensions
{
    public static void BringToFrontWindows(this Window window)
    {
        if (window.Handler?.PlatformView is Microsoft.UI.Xaml.Window nativeWindow)
        {
            var hwnd = WindowNative.GetWindowHandle(nativeWindow);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);

            // Restore if minimized/maximized to normal
            if (appWindow.Presenter is OverlappedPresenter p)
                p.Restore(true);

            // Raise in Z-order
            appWindow.MoveInZOrderAtTop();
        }
    }
}
#endif
