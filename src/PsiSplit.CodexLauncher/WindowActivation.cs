using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PsiSplit.CodexLauncher;

/// <summary>
/// Brings an already running Codex window to the foreground. A second shortcut
/// launch is normally a request to see the app, not a request for another
/// tunnel, so the single-instance guard should not end in a modal notice while
/// the window is behind other applications.
/// </summary>
internal static class WindowActivation
{
    private const int ShowNormal = 1;
    private const int ShowRestore = 9;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpShowWindow = 0x0040;

    public static bool TryActivateCodex(TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        do
        {
            var window = FindCodexWindow();
            if (window != IntPtr.Zero && Activate(window)) return true;
            Thread.Sleep(200);
        }
        while (stopwatch.Elapsed < timeout);
        return false;
    }

    private static IntPtr FindCodexWindow()
    {
        foreach (var process in Process.GetProcessesByName("ChatGPT"))
        {
            using (process)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (path is null || !path.Contains("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase)) continue;
                    // MainWindowHandle covers the normal visible or minimized
                    // window. Enumerating hidden windows also covers Codex
                    // sitting in the notification area after its window closed.
                    var window = process.MainWindowHandle;
                    if (window != IntPtr.Zero) return window;
                    window = FindHiddenWindow(process.Id);
                    if (window != IntPtr.Zero) return window;
                }
                catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    // The app may be starting or restarting; retry until the timeout.
                }
            }
        }
        return IntPtr.Zero;
    }

    private static IntPtr FindHiddenWindow(int processId)
    {
        var visible = IntPtr.Zero;
        var hidden = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var owner);
            if (owner != (uint)processId || !IsAppWindow(window)) return true;
            if (IsWindowVisible(window)) { visible = window; return false; }
            if (hidden == IntPtr.Zero) hidden = window;
            return true;
        }, IntPtr.Zero);
        return visible != IntPtr.Zero ? visible : hidden;
    }

    private static bool IsAppWindow(IntPtr window)
    {
        // Electron creates several helper windows per process. A title and the
        // Chromium widget class distinguish the real app window from those.
        if (GetWindowTextLength(window) == 0) return false;
        var className = new StringBuilder(64);
        return GetClassName(window, className, className.Capacity) > 0 &&
            className.ToString().StartsWith("Chrome_WidgetWin_", StringComparison.Ordinal);
    }

    private static bool Activate(IntPtr window)
    {
        if (IsIconic(window)) ShowWindow(window, ShowRestore);
        ShowWindow(window, ShowNormal);
        if (SetForegroundWindow(window)) return true;

        // Windows may refuse foreground activation to a helper process that was
        // started moments ago. Temporarily attaching to the foreground thread is
        // the standard fallback for that foreground-lock case.
        var foregroundWindow = GetForegroundWindow();
        var foregroundThread = foregroundWindow == IntPtr.Zero
            ? 0 : GetWindowThreadProcessId(foregroundWindow, out _);
        var targetThread = GetWindowThreadProcessId(window, out _);
        var attached = foregroundThread != 0 && targetThread != 0 && foregroundThread != targetThread &&
            AttachThreadInput(foregroundThread, targetThread, true);
        try
        {
            BringWindowToTop(window);
            SetWindowPos(window, IntPtr.Zero, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);
            SetForegroundWindow(window);
        }
        finally
        {
            if (attached) AttachThreadInput(foregroundThread, targetThread, false);
        }
        if (GetForegroundWindow() == window) return true;

        // Last resort: a top-most round trip normally promotes the window even
        // when the foreground lock is still active. It does not leave it top-most.
        SetWindowPos(window, new IntPtr(-1), 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);
        SetWindowPos(window, new IntPtr(-2), 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);
        return GetForegroundWindow() == window;
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, bool value);
    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr window);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter,
        int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int maxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr window);
}
