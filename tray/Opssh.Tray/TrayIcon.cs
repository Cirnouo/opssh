using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Opssh.Tray;

internal sealed class TrayIcon : IDisposable
{
    private const uint Callback = 0x8001;
    private const uint MouseLeftUp = 0x0202;
    private const uint MouseRightUp = 0x0205;
    private const uint ContextMenu = 0x007b;
    private const uint Add = 0;
    private const uint Modify = 1;
    private const uint Delete = 2;

    private readonly WindowProc callback;
    private readonly uint taskbarCreated;
    private readonly string windowClass = "opssh-tray-" + Guid.NewGuid().ToString("N");
    private readonly nint module;
    private nint window;
    private IconData icon;
    private string tipKey = "Tray.Starting";

    internal event Action? OpenRequested;
    internal event Action? RestartRequested;
    internal event Action? QuitRequested;
    internal bool CanRestart { get; set; }

    internal TrayIcon()
    {
        callback = ProcessMessage;
        module = GetModuleHandleW(null);
        taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        var cls = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(),
            Procedure = Marshal.GetFunctionPointerForDelegate(callback),
            Instance = module,
            Name = windowClass
        };
        if (RegisterClassExW(ref cls) == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not register the tray window.");
        window = CreateWindowExW(0, windowClass, "", 0, 0, 0, 0, 0, 0, 0, module, 0);
        if (window == 0)
        {
            var error = Marshal.GetLastWin32Error();
            UnregisterClassW(windowClass, module);
            throw new Win32Exception(error, "Could not create the tray window.");
        }

        icon = new IconData
        {
            Size = (uint)Marshal.SizeOf<IconData>(),
            Window = window,
            Id = 1,
            Flags = 0x07,
            Message = Callback,
            Icon = LoadIconW(0, 32512),
            Tip = Strings.Get(tipKey),
            Info = "",
            InfoTitle = ""
        };
        Shell_NotifyIconW(Add, ref icon);
    }

    internal void SetTip(string key)
    {
        tipKey = key;
        RefreshLanguage();
    }

    internal void RefreshLanguage()
    {
        var value = Strings.Get(tipKey);
        icon.Tip = value.Length <= 127 ? value : value[..127];
        if (!Shell_NotifyIconW(Modify, ref icon))
            Shell_NotifyIconW(Add, ref icon);
    }

    private nint ProcessMessage(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        if (message == taskbarCreated && taskbarCreated != 0)
        {
            Shell_NotifyIconW(Add, ref icon);
            return 0;
        }
        if (message == Callback)
        {
            if ((uint)lParam == MouseLeftUp)
                OpenRequested?.Invoke();
            else if ((uint)lParam is MouseRightUp or ContextMenu)
                OpenMenu();
            return 0;
        }
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private void OpenMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == 0)
            return;
        try
        {
            AppendMenuW(menu, 0, 1, Strings.Get("Tray.Open"));
            AppendMenuW(menu, CanRestart ? 0u : 1u, 2, Strings.Get("Tray.Restart"));
            AppendMenuW(menu, 0, 3, Strings.Get("Tray.Quit"));
            if (!GetCursorPos(out var cursor))
                return;
            SetForegroundWindow(window);
            var selected = TrackPopupMenu(menu, 0x0182, cursor.X, cursor.Y, 0, window, 0);
            PostMessageW(window, 0, 0, 0);
            switch (selected)
            {
                case 1: OpenRequested?.Invoke(); break;
                case 2: RestartRequested?.Invoke(); break;
                case 3: QuitRequested?.Invoke(); break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (window == 0)
            return;
        Shell_NotifyIconW(Delete, ref icon);
        DestroyWindow(window);
        window = 0;
        UnregisterClassW(windowClass, module);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        internal uint Size, Style;
        internal nint Procedure;
        internal int ClassExtra, WindowExtra;
        internal nint Instance, Icon, Cursor, Background;
        internal string? MenuName;
        internal string Name;
        internal nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct IconData
    {
        internal uint Size;
        internal nint Window;
        internal uint Id, Flags, Message;
        internal nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        internal string Tip;
        internal uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        internal string Info;
        internal uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        internal string InfoTitle;
        internal uint InfoFlags;
        internal Guid Guid;
        internal nint BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        internal int X, Y;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint GetModuleHandleW(string? moduleName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WindowClass cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool UnregisterClassW(string cls, nint instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern nint CreateWindowExW(uint exStyle, string cls, string name, uint style, int x, int y,
        int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint DefWindowProcW(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint RegisterWindowMessageW(string message);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint LoadIconW(nint module, nint resource);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool Shell_NotifyIconW(uint message, ref IconData data);
    [DllImport("user32.dll")]
    private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool AppendMenuW(nint menu, uint flags, nuint item, string label);
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point cursor);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint owner, nint rect);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool PostMessageW(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(nint menu);
}
