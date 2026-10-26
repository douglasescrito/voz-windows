using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace VozWindows;

public static class NativeInterop
{
    public const string CredentialTarget = "PROJETOS:cli-gcp:pessoal:GEMINI_API_KEY";

    // --- Windows Credential Manager (advapi32.dll) ---
    private const uint CRED_TYPE_GENERIC = 1;
    private const uint CRED_PERSIST_LOCAL_MACHINE = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite([In] ref CREDENTIAL userCredential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredFree", SetLastError = true)]
    private static extern void CredFree([In] IntPtr cred);

    public static string? ReadApiKeyFromVault(string target = CredentialTarget)
    {
        if (!CredRead(target, CRED_TYPE_GENERIC, 0, out IntPtr credPtr) || credPtr == IntPtr.Zero)
            return null;

        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
            if (cred.CredentialBlob == IntPtr.Zero || cred.CredentialBlobSize == 0)
                return null;

            byte[] bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, (int)cred.CredentialBlobSize);
            return Encoding.Unicode.GetString(bytes).Trim('\0', ' ', '\r', '\n', '\t');
        }
        finally
        {
            CredFree(credPtr);
        }
    }

    public static bool WriteApiKeyToVault(string apiKey, string target = CredentialTarget)
    {
        string clean = apiKey.Trim();
        byte[] bytes = Encoding.Unicode.GetBytes(clean);
        IntPtr targetPtr = Marshal.StringToCoTaskMemUni(target);
        IntPtr userPtr = Marshal.StringToCoTaskMemUni("pessoal");
        IntPtr blobPtr = Marshal.AllocCoTaskMem(bytes.Length);

        try
        {
            Marshal.Copy(bytes, 0, blobPtr, bytes.Length);
            var cred = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = targetPtr,
                UserName = userPtr,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blobPtr,
                Persist = CRED_PERSIST_LOCAL_MACHINE
            };
            return CredWrite(ref cred, 0);
        }
        finally
        {
            Marshal.FreeCoTaskMem(targetPtr);
            Marshal.FreeCoTaskMem(userPtr);
            Marshal.FreeCoTaskMem(blobPtr);
        }
    }

    // --- Win32 Window Styles & Hotkeys (user32.dll) ---
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_TOPMOST = 0x00000008;
    public const int WM_HOTKEY = 0x0312;

    public const uint MOD_NONE = 0x0000;
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_NOREPEAT = 0x4000;

    public const uint VK_F8 = 0x77;
    public const uint VK_SPACE = 0x20;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // --- Win32 SendInput Unicode Text Injector ---
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const ushort VK_BACK = 0x08;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public IntPtr type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MOUSEINPUT mi;
        [FieldOffset(0)]
        public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    public static void ForceForegroundWindow(IntPtr hWnd)
    {
        IntPtr fg = GetForegroundWindow();
        uint currentThread = GetCurrentThreadId();
        uint fgThread = fg != IntPtr.Zero ? GetWindowThreadProcessId(fg, out _) : 0;

        if (fgThread != 0 && fgThread != currentThread)
        {
            AttachThreadInput(currentThread, fgThread, true);
            SetForegroundWindow(hWnd);
            AttachThreadInput(currentThread, fgThread, false);
        }
        else
        {
            SetForegroundWindow(hWnd);
        }
    }

    public static uint TypeUnicodeText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        var inputs = new List<INPUT>(text.Length * 2);
        foreach (char c in text)
        {
            if (c == '\r')
                continue;

            inputs.Add(new INPUT
            {
                type = (IntPtr)INPUT_KEYBOARD,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = c,
                        dwFlags = KEYEVENTF_UNICODE
                    }
                }
            });
            inputs.Add(new INPUT
            {
                type = (IntPtr)INPUT_KEYBOARD,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = c,
                        dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP
                    }
                }
            });
        }

        if (inputs.Count > 0)
        {
            INPUT[] arr = inputs.ToArray();
            return SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>());
        }
        return 0;
    }

    public static void SendBackspaces(int count)
    {
        if (count <= 0)
            return;

        var inputs = new INPUT[count * 2];
        for (int i = 0; i < count; i++)
        {
            inputs[i * 2] = new INPUT
            {
                type = (IntPtr)INPUT_KEYBOARD,
                U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_BACK, wScan = 0x0E, dwFlags = 0 } }
            };
            inputs[i * 2 + 1] = new INPUT
            {
                type = (IntPtr)INPUT_KEYBOARD,
                U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_BACK, wScan = 0x0E, dwFlags = KEYEVENTF_KEYUP } }
            };
        }
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    public const int WM_MOUSEACTIVATE = 0x0021;
    public const int MA_NOACTIVATE = 3;

    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_A = 0x41;
    private const ushort VK_V = 0x56;

    public static IntPtr GetForegroundWindowHandle() => GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    public static bool IsPlainEnterPressed()
    {
        const int VK_RETURN = 0x0D;
        const int VK_SHIFT = 0x10;
        bool enterDown = (GetAsyncKeyState(VK_RETURN) & 0x8000) != 0;
        if (!enterDown)
            return false;
        bool shiftDown = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
        return !shiftDown;
    }

    public static void SendSelectAll()
    {
        var downInputs = new INPUT[]
        {
            new INPUT { type = (IntPtr)INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_CONTROL, wScan = 0x1D, dwFlags = 0 } } },
            new INPUT { type = (IntPtr)INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_A, wScan = 0x1E, dwFlags = 0 } } }
        };
        SendInput((uint)downInputs.Length, downInputs, Marshal.SizeOf<INPUT>());

        System.Threading.Thread.Sleep(15);

        var upInputs = new INPUT[]
        {
            new INPUT { type = (IntPtr)INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_A, wScan = 0x1E, dwFlags = KEYEVENTF_KEYUP } } },
            new INPUT { type = (IntPtr)INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_CONTROL, wScan = 0x1D, dwFlags = KEYEVENTF_KEYUP } } }
        };
        SendInput((uint)upInputs.Length, upInputs, Marshal.SizeOf<INPUT>());
    }

    public static void SendPasteClipboard()
    {
        var downInputs = new INPUT[]
        {
            new INPUT { type = (IntPtr)INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_CONTROL, wScan = 0x1D, dwFlags = 0 } } },
            new INPUT { type = (IntPtr)INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_V, wScan = 0x2F, dwFlags = 0 } } }
        };
        SendInput((uint)downInputs.Length, downInputs, Marshal.SizeOf<INPUT>());

        System.Threading.Thread.Sleep(15);

        var upInputs = new INPUT[]
        {
            new INPUT { type = (IntPtr)INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_V, wScan = 0x2F, dwFlags = KEYEVENTF_KEYUP } } },
            new INPUT { type = (IntPtr)INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_CONTROL, wScan = 0x1D, dwFlags = KEYEVENTF_KEYUP } } }
        };
        SendInput((uint)upInputs.Length, upInputs, Marshal.SizeOf<INPUT>());
    }

    // --- Native Windows Voice-Access-Style AppBar (shell32.dll) & Hardware DWM Stack (dwmapi.dll) ---
    public const int WM_NCCALCSIZE = 0x0083;
    public const int APPBAR_CALLBACK_MSG = 0x0400 + 88;
    public const int ABN_POSCHANGED = 0x00000001;

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_ASYNCWINDOWPOS = 0x4000;

    private const uint ABM_NEW = 0x00000000;
    private const uint ABM_REMOVE = 0x00000001;
    private const uint ABM_QUERYPOS = 0x00000002;
    private const uint ABM_SETPOS = 0x00000003;
    private const uint ABE_RIGHT = 2;

    private const int GWL_STYLE = -16;
    private const int WS_MAXIMIZE = 0x01000000;
    private const int WS_CHILD = 0x40000000;
    private const int WS_CAPTION = 0x00C00000;
    private const int WS_THICKFRAME = 0x00040000;
    private const int WS_SYSMENU = 0x00080000;
    private const uint GW_OWNER = 4;
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    private const int DWMWA_TRANSITIONS_FORCEDISABLED = 3;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWCP_DONOTROUND = 1;
    private const int DWMWCP_ROUND = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("shell32.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern IntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr BeginDeferWindowPos(int nNumWindows);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr DeferWindowPos(IntPtr hWinPosInfo, IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EndDeferWindowPos(IntPtr hWinPosInfo);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    private static bool _appBarRegistered;
    private static IntPtr _appBarHwnd = IntPtr.Zero;
    private static IntPtr _appBarMonitor = IntPtr.Zero;
    private static int _currentSlotLeftPx;
    private static int _currentSlotWidthPx;
    private static readonly Dictionary<IntPtr, int> _monitorTaskbarHeights = new();
    private static readonly Dictionary<IntPtr, RECT> _snappedWindowsOnMonitor = new();

    /// <summary>
    /// Configures the window to use 100% hardware-accelerated Windows 11 DWM composition
    /// (no software WS_EX_LAYERED bitmap, no CPU drop-shadow lag, no Win32 titlebar).
    /// </summary>
    public static void ConfigureNativeHardwareWindow(IntPtr hwnd, bool roundedCorners, int borderColorRef)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            int style = GetWindowLong(hwnd, GWL_STYLE);
            int cleanStyle = style & ~(WS_CAPTION | WS_THICKFRAME | WS_SYSMENU);
            if (cleanStyle != style)
            {
                SetWindowLong(hwnd, GWL_STYLE, cleanStyle);
            }

            int disableTransitions = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_TRANSITIONS_FORCEDISABLED, ref disableTransitions, sizeof(int));

            int darkMode = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

            int cornerPref = roundedCorners ? DWMWCP_ROUND : DWMWCP_DONOTROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(int));

            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref borderColorRef, sizeof(int));
        }
        catch { }
    }

    /// <summary>
    /// Returns the TRUE usable area of the monitor containing referenceHwnd (excluding only the taskbar,
    /// never shrinking recursively when our own AppBar is registered).
    /// </summary>
    public static RECT GetTrueMonitorWorkAreaPx(IntPtr referenceHwnd, out IntPtr hMonitor)
    {
        hMonitor = referenceHwnd != IntPtr.Zero
            ? MonitorFromWindow(referenceHwnd, MONITOR_DEFAULTTONEAREST)
            : IntPtr.Zero;

        if (hMonitor != IntPtr.Zero)
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(hMonitor, ref mi))
            {
                if (!_appBarRegistered && !_monitorTaskbarHeights.ContainsKey(hMonitor))
                {
                    int tb = Math.Max(0, mi.rcMonitor.Bottom - mi.rcWork.Bottom);
                    if (tb > 140) tb = 48;
                    _monitorTaskbarHeights[hMonitor] = tb;
                }

                int taskbarH = _monitorTaskbarHeights.TryGetValue(hMonitor, out int cachedTb)
                    ? cachedTb
                    : Math.Clamp(mi.rcMonitor.Bottom - mi.rcWork.Bottom, 0, 100);

                return new RECT
                {
                    Left = mi.rcMonitor.Left,
                    Top = mi.rcMonitor.Top,
                    Right = mi.rcMonitor.Right,
                    Bottom = mi.rcMonitor.Bottom - taskbarH
                };
            }
        }

        var screen = System.Windows.Forms.Screen.PrimaryScreen ?? System.Windows.Forms.Screen.AllScreens[0];
        var bounds = screen.Bounds;
        return new RECT { Left = bounds.Left, Top = bounds.Top, Right = bounds.Right, Bottom = bounds.Bottom - 48 };
    }

    public static bool IsEligibleExternalWindowOnSameMonitor(IntPtr targetHwnd, IntPtr myHwnd)
    {
        if (targetHwnd == IntPtr.Zero || targetHwnd == myHwnd)
            return false;
        if (!IsWindow(targetHwnd) || !IsWindowVisible(targetHwnd) || IsIconic(targetHwnd))
            return false;
        if (GetWindow(targetHwnd, GW_OWNER) != IntPtr.Zero)
            return false;

        if (myHwnd != IntPtr.Zero)
        {
            IntPtr targetMon = MonitorFromWindow(targetHwnd, MONITOR_DEFAULTTONEAREST);
            IntPtr myMon = MonitorFromWindow(myHwnd, MONITOR_DEFAULTTONEAREST);
            if (targetMon == IntPtr.Zero || myMon == IntPtr.Zero || targetMon != myMon)
                return false;
        }

        int style = GetWindowLong(targetHwnd, GWL_STYLE);
        int exStyle = GetWindowLong(targetHwnd, GWL_EXSTYLE);
        if ((style & WS_CHILD) != 0)
            return false;
        if ((exStyle & WS_EX_TOOLWINDOW) != 0 || (exStyle & WS_EX_NOACTIVATE) != 0)
            return false;
        if ((style & (WS_THICKFRAME | WS_CAPTION)) == 0)
            return false;

        var sb = new StringBuilder(128);
        if (GetClassName(targetHwnd, sb, sb.Capacity) > 0)
        {
            string cls = sb.ToString();
            if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
                or "NotifyIconOverflowWindow" or "Windows.UI.Core.CoreWindow"
                or "XamlExplorerHostIslandWindow" or "MultitaskingViewFrame" or "TaskListThumbnailWnd")
            {
                return false;
            }
        }

        return true;
    }

    private static (int LeftMargin, int TopMargin, int RightMargin, int BottomMargin) GetDwmInvisibleBorderMargins(IntPtr hwnd)
    {
        if (GetWindowRect(hwnd, out RECT winRc) &&
            DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT extRc, Marshal.SizeOf<RECT>()) == 0)
        {
            int left = Math.Clamp(extRc.Left - winRc.Left, 0, 16);
            int top = Math.Clamp(extRc.Top - winRc.Top, 0, 16);
            int right = Math.Clamp(winRc.Right - extRc.Right, 0, 16);
            int bottom = Math.Clamp(winRc.Bottom - extRc.Bottom, 0, 16);
            return (left, top, right, bottom);
        }
        return (7, 0, 7, 7);
    }

    private static bool _isUpdatingAppBar;
    private static RECT _lastAppliedAppBarRect;
    private static long _lastAppBarSetTick;

    /// <summary>
    /// Docks the window as a Native Windows Desktop Sidebar AppBar (just like Windows Voice Access),
    /// dividing the current monitor's work area so no window overlaps it, while leaving other monitors untouched.
    /// </summary>
    public static RECT DockNativeSidebarAppBar(IntPtr appBarHwnd, int desiredWidthPx)
    {
        RECT trueWork = GetTrueMonitorWorkAreaPx(appBarHwnd, out IntPtr hMon);
        int slotLeftPx = trueWork.Right - desiredWidthPx;

        if (appBarHwnd != IntPtr.Zero && !_isUpdatingAppBar)
        {
            _isUpdatingAppBar = true;
            try
            {
                var abd = new APPBARDATA
                {
                    cbSize = Marshal.SizeOf<APPBARDATA>(),
                    hWnd = appBarHwnd,
                    uCallbackMessage = APPBAR_CALLBACK_MSG,
                    uEdge = ABE_RIGHT,
                    rc = new RECT
                    {
                        Left = slotLeftPx,
                        Top = trueWork.Top,
                        Right = trueWork.Right,
                        Bottom = trueWork.Bottom
                    }
                };

                if (!_appBarRegistered || _appBarHwnd != appBarHwnd)
                {
                    if (_appBarRegistered && _appBarHwnd != IntPtr.Zero)
                    {
                        var oldAbd = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = _appBarHwnd };
                        SHAppBarMessage(ABM_REMOVE, ref oldAbd);
                    }
                    SHAppBarMessage(ABM_NEW, ref abd);
                    _appBarRegistered = true;
                    _appBarHwnd = appBarHwnd;
                }

                SHAppBarMessage(ABM_QUERYPOS, ref abd);
                abd.rc.Right = trueWork.Right;
                abd.rc.Left = trueWork.Right - desiredWidthPx;
                abd.rc.Top = trueWork.Top;
                abd.rc.Bottom = trueWork.Bottom;
                SHAppBarMessage(ABM_SETPOS, ref abd);

                _appBarMonitor = hMon;
                _currentSlotLeftPx = abd.rc.Left;
                _currentSlotWidthPx = desiredWidthPx;
                _lastAppliedAppBarRect = abd.rc;
                _lastAppBarSetTick = Environment.TickCount64;

                // Apply native DWM square sidebar corners + subtle dark border (no blue frame) and position the AppBar immediately
                ConfigureNativeHardwareWindow(appBarHwnd, roundedCorners: false, borderColorRef: 0x00242428);
                SetWindowPos(
                    appBarHwnd,
                    IntPtr.Zero,
                    abd.rc.Left,
                    abd.rc.Top,
                    abd.rc.Width,
                    abd.rc.Height,
                    SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

                // Also snap any non-maximized overlapping windows ON THIS SAME MONITOR so zero overlap exists
                SnapOverlappingWindowsOnSameMonitor(appBarHwnd, hMon, trueWork, _currentSlotLeftPx);

                return abd.rc;
            }
            finally
            {
                _isUpdatingAppBar = false;
            }
        }

        return new RECT
        {
            Left = slotLeftPx,
            Top = trueWork.Top,
            Right = trueWork.Right,
            Bottom = trueWork.Bottom
        };
    }

    public static void HandleAppBarPosChanged(IntPtr appBarHwnd)
    {
        if (!_appBarRegistered || _isUpdatingAppBar || appBarHwnd == IntPtr.Zero || _currentSlotWidthPx <= 0)
            return;

        if (Environment.TickCount64 - _lastAppBarSetTick < 600)
            return;

        RECT trueWork = GetTrueMonitorWorkAreaPx(appBarHwnd, out _);
        RECT targetRect = new RECT
        {
            Left = trueWork.Right - _currentSlotWidthPx,
            Top = trueWork.Top,
            Right = trueWork.Right,
            Bottom = trueWork.Bottom
        };

        // Ignore redundant ABN_POSCHANGED notifications when monitor bounds haven't changed
        if (targetRect.Left == _lastAppliedAppBarRect.Left &&
            targetRect.Top == _lastAppliedAppBarRect.Top &&
            targetRect.Right == _lastAppliedAppBarRect.Right &&
            targetRect.Bottom == _lastAppliedAppBarRect.Bottom)
        {
            return;
        }

        _isUpdatingAppBar = true;
        try
        {
            var abd = new APPBARDATA
            {
                cbSize = Marshal.SizeOf<APPBARDATA>(),
                hWnd = appBarHwnd,
                uCallbackMessage = APPBAR_CALLBACK_MSG,
                uEdge = ABE_RIGHT,
                rc = targetRect
            };

            SHAppBarMessage(ABM_QUERYPOS, ref abd);
            abd.rc.Right = trueWork.Right;
            abd.rc.Left = trueWork.Right - _currentSlotWidthPx;
            abd.rc.Top = trueWork.Top;
            abd.rc.Bottom = trueWork.Bottom;
            SHAppBarMessage(ABM_SETPOS, ref abd);

            _currentSlotLeftPx = abd.rc.Left;
            _lastAppliedAppBarRect = abd.rc;
            _lastAppBarSetTick = Environment.TickCount64;

            SetWindowPos(
                appBarHwnd,
                IntPtr.Zero,
                abd.rc.Left,
                abd.rc.Top,
                abd.rc.Width,
                abd.rc.Height,
                SWP_NOZORDER | SWP_NOACTIVATE);
        }
        finally
        {
            _isUpdatingAppBar = false;
        }
    }

    private static void SnapOverlappingWindowsOnSameMonitor(
        IntPtr appBarHwnd,
        IntPtr targetMonitor,
        RECT monWork,
        int slotLeftPx)
    {
        var toMove = new List<(IntPtr Hwnd, int X, int Y, int W, int H)>();

        EnumWindows((hWnd, _) =>
        {
            if (!IsEligibleExternalWindowOnSameMonitor(hWnd, appBarHwnd))
                return true;

            // Maximized windows on this monitor are already automatically sized by Windows ABM_SETPOS
            if (IsZoomed(hWnd))
                return true;

            if (!GetWindowRect(hWnd, out RECT winRc))
                return true;

            var (leftMargin, _, rightMargin, _) = GetDwmInvisibleBorderMargins(hWnd);
            int visibleRight = winRc.Right - rightMargin;

            if (visibleRight > slotLeftPx && winRc.Left < slotLeftPx)
            {
                if (!_snappedWindowsOnMonitor.ContainsKey(hWnd))
                {
                    _snappedWindowsOnMonitor[hWnd] = winRc;
                }

                int maxAllowedWidth = (slotLeftPx - monWork.Left) + leftMargin + rightMargin;
                int newWidth = Math.Min(winRc.Width, maxAllowedWidth);
                int newLeft = Math.Max(monWork.Left - leftMargin, (slotLeftPx + rightMargin) - newWidth);
                toMove.Add((hWnd, newLeft, winRc.Top, newWidth, winRc.Height));
            }
            return true;
        }, IntPtr.Zero);

        if (toMove.Count > 0)
        {
            IntPtr hdwp = BeginDeferWindowPos(toMove.Count);
            foreach (var item in toMove)
            {
                if (hdwp != IntPtr.Zero)
                {
                    hdwp = DeferWindowPos(hdwp, item.Hwnd, IntPtr.Zero, item.X, item.Y, item.W, item.H, SWP_NOZORDER | SWP_NOACTIVATE);
                }
                else
                {
                    SetWindowPos(item.Hwnd, IntPtr.Zero, item.X, item.Y, item.W, item.H, SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
                }
            }
            if (hdwp != IntPtr.Zero)
            {
                EndDeferWindowPos(hdwp);
            }
        }
    }

    public static void UndockNativeSidebarAppBar()
    {
        if (_appBarRegistered && _appBarHwnd != IntPtr.Zero)
        {
            try
            {
                var abd = new APPBARDATA
                {
                    cbSize = Marshal.SizeOf<APPBARDATA>(),
                    hWnd = _appBarHwnd
                };
                SHAppBarMessage(ABM_REMOVE, ref abd);
            }
            catch { }
            _appBarRegistered = false;
            _appBarHwnd = IntPtr.Zero;
            _currentSlotLeftPx = 0;
            _currentSlotWidthPx = 0;
        }

        if (_snappedWindowsOnMonitor.Count > 0)
        {
            var toRestore = new List<(IntPtr Hwnd, RECT Orig)>();
            foreach (var kvp in _snappedWindowsOnMonitor)
            {
                IntPtr hWnd = kvp.Key;
                if (IsWindow(hWnd) && IsWindowVisible(hWnd) && !IsIconic(hWnd) && !IsZoomed(hWnd))
                {
                    IntPtr curMon = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
                    if (_appBarMonitor == IntPtr.Zero || curMon == _appBarMonitor)
                    {
                        toRestore.Add((hWnd, kvp.Value));
                    }
                }
            }

            if (toRestore.Count > 0)
            {
                IntPtr hdwp = BeginDeferWindowPos(toRestore.Count);
                foreach (var item in toRestore)
                {
                    if (hdwp != IntPtr.Zero)
                    {
                        hdwp = DeferWindowPos(
                            hdwp,
                            item.Hwnd,
                            IntPtr.Zero,
                            item.Orig.Left,
                            item.Orig.Top,
                            item.Orig.Width,
                            item.Orig.Height,
                            SWP_NOZORDER | SWP_NOACTIVATE);
                    }
                    else
                    {
                        SetWindowPos(
                            item.Hwnd,
                            IntPtr.Zero,
                            item.Orig.Left,
                            item.Orig.Top,
                            item.Orig.Width,
                            item.Orig.Height,
                            SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
                    }
                }
                if (hdwp != IntPtr.Zero)
                {
                    EndDeferWindowPos(hdwp);
                }
            }
            _snappedWindowsOnMonitor.Clear();
        }
        _appBarMonitor = IntPtr.Zero;
    }
}

// --- Native 16kHz Mono PCM Microphone Capture (winmm.dll) ---
public sealed class NativeMicrophoneCapture : IDisposable
{
    private const int WAVE_MAPPER = -1;
    private const ushort WAVE_FORMAT_PCM = 1;
    private const uint CALLBACK_FUNCTION = 0x00030000;
    private const int MM_WIM_DATA = 0x3C0;

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEHDR
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    private delegate void WaveInProc(IntPtr hwi, int uMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2);

    [DllImport("winmm.dll")]
    private static extern int waveInOpen(out IntPtr phwi, int uDeviceID, ref WAVEFORMATEX pwfx, WaveInProc dwCallback, IntPtr dwInstance, uint fdwOpen);

    [DllImport("winmm.dll")]
    private static extern int waveInPrepareHeader(IntPtr hwi, IntPtr pwh, uint cbwh);

    [DllImport("winmm.dll")]
    private static extern int waveInUnprepareHeader(IntPtr hwi, IntPtr pwh, uint cbwh);

    [DllImport("winmm.dll")]
    private static extern int waveInAddBuffer(IntPtr hwi, IntPtr pwh, uint cbwh);

    [DllImport("winmm.dll")]
    private static extern int waveInStart(IntPtr hwi);

    [DllImport("winmm.dll")]
    private static extern int waveInStop(IntPtr hwi);

    [DllImport("winmm.dll")]
    private static extern int waveInReset(IntPtr hwi);

    [DllImport("winmm.dll")]
    private static extern int waveInClose(IntPtr hwi);

    private IntPtr _hWaveIn = IntPtr.Zero;
    private readonly WaveInProc _callbackDelegate;
    private readonly List<IntPtr> _headerPtrs = new();
    private readonly List<IntPtr> _dataPtrs = new();
    private volatile bool _isCapturing;
    private System.Threading.Thread? _loopbackThread;

    /// <summary>
    /// Quando true, captura o áudio reproduzido pelo próprio computador (WASAPI Loopback — vídeos, reuniões, chamadas, áudios)
    /// em vez do microfone físico. Começa sempre desativado (false).
    /// </summary>
    public bool UseSystemLoopback { get; set; } = false;

    public DateTime LastFrameUtc { get; private set; } = DateTime.UtcNow;

    public event Action<byte[], double>? OnPcmFrame;

    public NativeMicrophoneCapture()
    {
        _callbackDelegate = OnWaveInCallback;
    }

    public void Start(int chunkMs = 40, int bufferCount = 8)
    {
        if (_isCapturing)
            return;

        LastFrameUtc = DateTime.UtcNow;

        if (UseSystemLoopback)
        {
            _isCapturing = true;
            _loopbackThread = new System.Threading.Thread(() => RunWasapiLoopbackCapture(chunkMs))
            {
                IsBackground = true,
                Name = "VozWindowsWasapiLoopback"
            };
            _loopbackThread.SetApartmentState(System.Threading.ApartmentState.MTA);
            _loopbackThread.Start();
            return;
        }

        int bufferBytes = 32 * chunkMs; // 16,000 Hz * 2 bytes/sample = 32 bytes/ms
        var format = new WAVEFORMATEX
        {
            wFormatTag = WAVE_FORMAT_PCM,
            nChannels = 1,
            nSamplesPerSec = 16000,
            nAvgBytesPerSec = 32000,
            nBlockAlign = 2,
            wBitsPerSample = 16,
            cbSize = 0
        };

        int rc = waveInOpen(out _hWaveIn, WAVE_MAPPER, ref format, _callbackDelegate, IntPtr.Zero, CALLBACK_FUNCTION);
        if (rc != 0 || _hWaveIn == IntPtr.Zero)
            throw new InvalidOperationException($"Não foi possível abrir o microfone do Windows (código {rc}).");

        _isCapturing = true;
        uint hdrSize = (uint)Marshal.SizeOf<WAVEHDR>();

        for (int i = 0; i < bufferCount; i++)
        {
            IntPtr dataPtr = Marshal.AllocHGlobal(bufferBytes);
            IntPtr hdrPtr = Marshal.AllocHGlobal((int)hdrSize);
            _dataPtrs.Add(dataPtr);
            _headerPtrs.Add(hdrPtr);

            var hdr = new WAVEHDR
            {
                lpData = dataPtr,
                dwBufferLength = (uint)bufferBytes,
                dwBytesRecorded = 0,
                dwUser = IntPtr.Zero,
                dwFlags = 0,
                dwLoops = 0
            };
            Marshal.StructureToPtr(hdr, hdrPtr, false);
            waveInPrepareHeader(_hWaveIn, hdrPtr, hdrSize);
            waveInAddBuffer(_hWaveIn, hdrPtr, hdrSize);
        }

        waveInStart(_hWaveIn);
    }

    private void OnWaveInCallback(IntPtr hwi, int uMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2)
    {
        if (uMsg != MM_WIM_DATA || !_isCapturing || dwParam1 == IntPtr.Zero)
            return;

        try
        {
            LastFrameUtc = DateTime.UtcNow;
            var hdr = Marshal.PtrToStructure<WAVEHDR>(dwParam1);
            if (hdr.dwBytesRecorded > 0 && hdr.lpData != IntPtr.Zero)
            {
                byte[] pcm = new byte[hdr.dwBytesRecorded];
                Marshal.Copy(hdr.lpData, pcm, 0, (int)hdr.dwBytesRecorded);

                double sumSquares = 0;
                int sampleCount = pcm.Length / 2;
                for (int i = 0; i < pcm.Length - 1; i += 2)
                {
                    short sample = (short)(pcm[i] | (pcm[i + 1] << 8));
                    double norm = sample / 32768.0;
                    sumSquares += norm * norm;
                }
                double rms = sampleCount > 0 ? Math.Sqrt(sumSquares / sampleCount) : 0;

                OnPcmFrame?.Invoke(pcm, rms);
            }

            if (_isCapturing && _hWaveIn != IntPtr.Zero)
            {
                waveInAddBuffer(_hWaveIn, dwParam1, (uint)Marshal.SizeOf<WAVEHDR>());
            }
        }
        catch
        {
            // Ignore callback race during shutdown
        }
    }

    public void Stop()
    {
        if (!_isCapturing && _hWaveIn == IntPtr.Zero && _loopbackThread == null)
            return;

        _isCapturing = false;

        var lbThread = _loopbackThread;
        _loopbackThread = null;
        if (lbThread != null && lbThread.IsAlive)
        {
            try { lbThread.Join(350); } catch { }
        }

        IntPtr handle = _hWaveIn;
        _hWaveIn = IntPtr.Zero;

        if (handle != IntPtr.Zero)
        {
            try
            {
                waveInStop(handle);
                waveInReset(handle);
                uint hdrSize = (uint)Marshal.SizeOf<WAVEHDR>();
                foreach (IntPtr hdrPtr in _headerPtrs)
                {
                    waveInUnprepareHeader(handle, hdrPtr, hdrSize);
                    Marshal.FreeHGlobal(hdrPtr);
                }
                foreach (IntPtr dataPtr in _dataPtrs)
                {
                    Marshal.FreeHGlobal(dataPtr);
                }
                _headerPtrs.Clear();
                _dataPtrs.Clear();
                waveInClose(handle);
            }
            catch
            {
            }
        }
    }

    // --- WASAPI System Audio Loopback Capture (Ouvir o áudio do próprio computador em 16kHz PCM) ---
    private const int eRender = 0;
    private const int eConsole = 0;
    private const int CLSCTX_ALL = 23;
    private const int AUDCLNT_SHAREMODE_SHARED = 0;
    private const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    private const int AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = unchecked((int)0x80000000);
    private const int AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;
    private const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IntPtr ppDevices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        int Initialize(int ShareMode, int StreamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr pFormat, IntPtr AudioSessionGuid);
        int GetBufferSize(out uint pNumBufferFrames);
        int GetStreamLatency(out long phnsLatency);
        int GetCurrentPadding(out uint pNumPaddingFrames);
        int IsFormatSupported(int ShareMode, IntPtr pFormat, out IntPtr ppClosestMatch);
        int GetMixFormat(out IntPtr ppDeviceFormat);
        int GetDevicePeriod(out long phnsDefaultDevicePeriod, out long phnsMinimumDevicePeriod);
        int Start();
        int Stop();
        int Reset();
        int SetEventHandle(IntPtr eventHandle);
        int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        int GetBuffer(out IntPtr ppData, out uint pNumFramesToRead, out uint pdwFlags, out ulong pu64DevicePosition, out ulong pu64QPCPosition);
        int ReleaseBuffer(uint NumFramesRead);
        int GetNextPacketSize(out uint pNumFramesInNextPacket);
    }

    private void RunWasapiLoopbackCapture(int chunkMs)
    {
        IAudioClient? audioClient = null;
        IAudioCaptureClient? captureClient = null;
        IntPtr mixFormatPtr = IntPtr.Zero;
        IntPtr targetFormatPtr = IntPtr.Zero;

        try
        {
            var clsidEnumerator = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
            Type? enumType = Type.GetTypeFromCLSID(clsidEnumerator);
            if (enumType == null)
                return;

            var enumerator = (IMMDeviceEnumerator?)Activator.CreateInstance(enumType);
            if (enumerator == null)
                return;

            if (enumerator.GetDefaultAudioEndpoint(eRender, eConsole, out IMMDevice device) != 0 || device == null)
                return;

            var iidAudioClient = typeof(IAudioClient).GUID;
            if (device.Activate(ref iidAudioClient, CLSCTX_ALL, IntPtr.Zero, out object clientObj) != 0)
                return;

            audioClient = (IAudioClient)clientObj;

            var desiredFormat = new WAVEFORMATEX
            {
                wFormatTag = WAVE_FORMAT_PCM,
                nChannels = 1,
                nSamplesPerSec = 16000,
                nAvgBytesPerSec = 32000,
                nBlockAlign = 2,
                wBitsPerSample = 16,
                cbSize = 0
            };

            targetFormatPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEFORMATEX>());
            Marshal.StructureToPtr(desiredFormat, targetFormatPtr, false);

            bool isDirect16kMono = false;
            int autoFlags = AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
            int hrInit = audioClient.Initialize(AUDCLNT_SHAREMODE_SHARED, autoFlags, 1_000_000, 0, targetFormatPtr, IntPtr.Zero);

            int srcSampleRate = 16000;
            int srcChannels = 1;
            int srcBitsPerSample = 16;
            bool srcIsFloat = false;

            if (hrInit == 0)
            {
                isDirect16kMono = true;
            }
            else
            {
                if (audioClient.GetMixFormat(out mixFormatPtr) != 0 || mixFormatPtr == IntPtr.Zero)
                    return;

                var mixFmt = Marshal.PtrToStructure<WAVEFORMATEX>(mixFormatPtr);
                srcChannels = Math.Max(1, (int)mixFmt.nChannels);
                srcSampleRate = Math.Max(8000, (int)mixFmt.nSamplesPerSec);
                srcBitsPerSample = mixFmt.wBitsPerSample;
                ushort formatTag = mixFmt.wFormatTag;
                if (formatTag == 0xFFFE && mixFmt.cbSize >= 22)
                {
                    formatTag = (ushort)Marshal.ReadInt16(mixFormatPtr, 24);
                }
                srcIsFloat = (formatTag == 3) || (srcBitsPerSample == 32);

                if (audioClient.Initialize(AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_LOOPBACK, 1_000_000, 0, mixFormatPtr, IntPtr.Zero) != 0)
                    return;
            }

            var iidCaptureClient = typeof(IAudioCaptureClient).GUID;
            if (audioClient.GetService(ref iidCaptureClient, out object capObj) != 0)
                return;

            captureClient = (IAudioCaptureClient)capObj;
            if (audioClient.Start() != 0)
                return;

            int targetChunkBytes = 32 * chunkMs; // 16kHz * 2 bytes = 32 bytes/ms (1280 bytes por 40ms)
            byte[] outAccumulator = new byte[targetChunkBytes];
            int outOffset = 0;
            double resamplePos = 0.0;
            double stepRatio = (double)srcSampleRate / 16000.0;
            long lastEmitTick = Environment.TickCount64;

            while (_isCapturing)
            {
                LastFrameUtc = DateTime.UtcNow;
                int hrPkt = captureClient.GetNextPacketSize(out uint packetFrames);
                if (hrPkt != 0)
                    break;

                if (packetFrames == 0)
                {
                    long nowTick = Environment.TickCount64;
                    if (nowTick - lastEmitTick >= chunkMs)
                    {
                        lastEmitTick = nowTick;
                        byte[] silentChunk = new byte[targetChunkBytes];
                        OnPcmFrame?.Invoke(silentChunk, 0.0);
                    }
                    System.Threading.Thread.Sleep(8);
                    continue;
                }

                while (packetFrames > 0 && _isCapturing)
                {
                    if (captureClient.GetBuffer(out IntPtr dataPtr, out uint numFrames, out uint flags, out _, out _) != 0)
                        break;

                    try
                    {
                        bool isSilent = (flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0 || dataPtr == IntPtr.Zero;
                        if (isDirect16kMono)
                        {
                            int bytesAvailable = (int)(numFrames * 2);
                            int srcIdx = 0;
                            while (srcIdx < bytesAvailable)
                            {
                                int toCopy = Math.Min(targetChunkBytes - outOffset, bytesAvailable - srcIdx);
                                if (isSilent)
                                {
                                    Array.Clear(outAccumulator, outOffset, toCopy);
                                }
                                else
                                {
                                    Marshal.Copy(IntPtr.Add(dataPtr, srcIdx), outAccumulator, outOffset, toCopy);
                                }
                                outOffset += toCopy;
                                srcIdx += toCopy;

                                if (outOffset >= targetChunkBytes)
                                {
                                    EmitPcm16kChunk(outAccumulator, targetChunkBytes);
                                    outOffset = 0;
                                    lastEmitTick = Environment.TickCount64;
                                }
                            }
                        }
                        else
                        {
                            int bytesPerSample = Math.Max(2, srcBitsPerSample / 8);
                            int frameStride = srcChannels * bytesPerSample;

                            while (resamplePos < numFrames)
                            {
                                int frameIdx = (int)resamplePos;
                                short monoSample = 0;
                                if (!isSilent)
                                {
                                    double sum = 0;
                                    int baseByte = frameIdx * frameStride;
                                    for (int ch = 0; ch < srcChannels; ch++)
                                    {
                                        int sampleByteOffset = baseByte + (ch * bytesPerSample);
                                        if (srcIsFloat && bytesPerSample == 4)
                                        {
                                            int bits = Marshal.ReadInt32(dataPtr, sampleByteOffset);
                                            float f = BitConverter.Int32BitsToSingle(bits);
                                            sum += Math.Clamp(f, -1.0f, 1.0f);
                                        }
                                        else if (bytesPerSample >= 2)
                                        {
                                            short s = Marshal.ReadInt16(dataPtr, sampleByteOffset);
                                            sum += s / 32768.0;
                                        }
                                    }
                                    double avg = sum / srcChannels;
                                    monoSample = (short)Math.Clamp(avg * 32767.0, short.MinValue, short.MaxValue);
                                }

                                outAccumulator[outOffset] = (byte)(monoSample & 0xFF);
                                outAccumulator[outOffset + 1] = (byte)((monoSample >> 8) & 0xFF);
                                outOffset += 2;

                                if (outOffset >= targetChunkBytes)
                                {
                                    EmitPcm16kChunk(outAccumulator, targetChunkBytes);
                                    outOffset = 0;
                                    lastEmitTick = Environment.TickCount64;
                                }

                                resamplePos += stepRatio;
                            }
                            resamplePos -= numFrames;
                            if (resamplePos < 0) resamplePos = 0;
                        }
                    }
                    finally
                    {
                        captureClient.ReleaseBuffer(numFrames);
                    }

                    if (captureClient.GetNextPacketSize(out packetFrames) != 0)
                        break;
                }
            }
        }
        catch
        {
        }
        finally
        {
            try { audioClient?.Stop(); } catch { }
            if (mixFormatPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(mixFormatPtr);
            if (targetFormatPtr != IntPtr.Zero) Marshal.FreeHGlobal(targetFormatPtr);
        }
    }

    private void EmitPcm16kChunk(byte[] buffer, int length)
    {
        byte[] pcm = new byte[length];
        Buffer.BlockCopy(buffer, 0, pcm, 0, length);

        double sumSquares = 0;
        int sampleCount = length / 2;
        for (int i = 0; i < length - 1; i += 2)
        {
            short sample = (short)(pcm[i] | (pcm[i + 1] << 8));
            double norm = sample / 32768.0;
            sumSquares += norm * norm;
        }
        double rms = sampleCount > 0 ? Math.Sqrt(sumSquares / sampleCount) : 0;
        OnPcmFrame?.Invoke(pcm, rms);
    }

    public void Dispose() => Stop();
}
