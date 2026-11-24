using System.Runtime.InteropServices;

namespace QuotaGadget;

internal static unsafe partial class Native
{
    internal const double DefaultDpi = 96;

    internal static int ToPixels(double logical, int dpi) => (int)Math.Round(logical * dpi / DefaultDpi);
    internal static double FromPixels(int physical, int dpi) => physical * DefaultDpi / dpi;

    internal static nint RegisterWindowClass(string className, uint style,
        delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint> proc)
    {
        var module = GetModuleHandleW(0);
        fixed (char* name = className)
        {
            var cls = new WindowClass { Size = (uint)sizeof(WindowClass), Style = style, Proc = proc,
                Instance = module, Name = name, Cursor = LoadCursorW(0, 32512) };
            RegisterClassExW(ref cls);
        }
        return module;
    }

    internal static void SetRoundedFrame(nint hwnd)
    {
        DwmSetWindowAttribute(hwnd, 33, 2, sizeof(int)); // DWMWA_WINDOW_CORNER_PREFERENCE, ROUND
        DwmSetWindowAttribute(hwnd, 34, 0x3d3d3d, sizeof(int)); // DWMWA_BORDER_COLOR
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowClass
    {
        internal uint Size, Style;
        internal delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint> Proc;
        internal int ClassExtra, WindowExtra;
        internal nint Instance, Icon, Cursor, Brush, Menu;
        internal char* Name;
        internal nint SmallIcon;
    }

    internal struct Point(int x, int y) { internal int X = x, Y = y; }
    internal struct Rect(int x, int y, int right, int bottom)
    {
        internal int Left = x, Top = y, Right = right, Bottom = bottom;
        internal readonly int Width => Right - Left;
        internal readonly int Height => Bottom - Top;
    }
    internal struct Message { internal nint Window; internal uint Id; internal nuint WParam; internal nint LParam; internal uint Time; internal Point Position; internal uint Private; }
    internal struct Paint { internal nint DC; internal int Erase; internal Rect Area; internal int Restore, Incremental; internal fixed byte Reserved[32]; }
    internal struct MonitorInfo { internal int Size; internal Rect Monitor, Work; internal uint Flags; }
    internal struct MouseTrack { internal int Size, Flags; internal nint Window; internal uint HoverTime; }
    internal struct NotifyIconData
    {
        internal uint Size;
        internal nint Window;
        internal uint Id, Flags, Callback;
        internal nint Icon;
        internal fixed char Tip[128];
        internal uint State, StateMask;
        internal fixed char Info[256];
        internal uint Version;
        internal fixed char InfoTitle[64];
        internal uint InfoFlags;
        internal Guid Guid;
        internal nint BalloonIcon;
    }
    [LibraryImport("shell32")]
    internal static partial int Shell_NotifyIconW(uint operation, ref NotifyIconData data);
    [LibraryImport("user32", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint RegisterWindowMessageW(string name);
    [LibraryImport("user32")]
    internal static partial int SetForegroundWindow(nint hwnd);
    internal struct IconIdentifier { internal uint Size; internal nint Window; internal uint Id; internal Guid Guid; }
    [LibraryImport("shell32")]
    internal static partial int Shell_NotifyIconGetRect(in IconIdentifier id, out Rect rect);

    [LibraryImport("user32")]
    internal static partial nint MonitorFromPoint(Point point, uint flags);
    [LibraryImport("user32")]
    internal static partial nint GetDC(nint hwnd);
    [LibraryImport("user32")]
    internal static partial int ReleaseDC(nint hwnd, nint dc);
    [LibraryImport("user32")]
    internal static partial nint SetFocus(nint hwnd);
    [LibraryImport("user32")]
    internal static partial int AnimateWindow(nint hwnd, uint milliseconds, uint flags);

    internal struct BitmapInfo
    {
        internal uint Size;
        internal int Width, Height;
        internal ushort Planes, Bits;
        internal uint Compression, ImageSize;
        internal int XPixels, YPixels;
        internal uint Used, Important;
    }
    [LibraryImport("gdi32")]
    internal static partial nint CreateDIBSection(nint dc, in BitmapInfo info, uint usage, out uint* bits, nint section, uint offset);
    [LibraryImport("gdi32")]
    internal static partial int GdiFlush();
    [LibraryImport("user32")]
    internal static partial int UpdateLayeredWindow(nint hwnd, nint target, nint dest, in Point size, nint source,
        in Point origin, uint key, in uint blend, uint flags);

    [LibraryImport("user32")]
    private static partial ushort RegisterClassExW(ref WindowClass c);
    [LibraryImport("user32", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint CreateWindowExW(uint ex, string cls, string? title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [LibraryImport("user32")]
    internal static partial nint DefWindowProcW(nint hwnd, uint msg, nuint wp, nint lp);
    [LibraryImport("user32")]
    internal static partial int GetMessageW(out Message msg, nint hwnd, uint min, uint max);
    [LibraryImport("user32")]
    internal static partial int TranslateMessage(in Message msg);
    [LibraryImport("user32")]
    internal static partial nint DispatchMessageW(in Message msg);
    [LibraryImport("user32")]
    internal static partial int PostMessageW(nint hwnd, uint msg, nuint wp, nint lp);
    [LibraryImport("user32")]
    internal static partial void PostQuitMessage(int code);
    [LibraryImport("user32")]
    internal static partial int DestroyWindow(nint hwnd);
    [LibraryImport("user32")]
    internal static partial int ShowWindow(nint hwnd, int show);
    [LibraryImport("user32")]
    internal static partial int UpdateWindow(nint hwnd);
    [LibraryImport("user32")]
    internal static partial nint BeginPaint(nint hwnd, out Paint ps);
    [LibraryImport("user32")]
    internal static partial int EndPaint(nint hwnd, in Paint ps);
    [LibraryImport("user32")]
    internal static partial int GetClientRect(nint hwnd, out Rect rect);
    [LibraryImport("user32")]
    internal static partial int GetWindowRect(nint hwnd, out Rect rect);
    [LibraryImport("user32")]
    internal static partial int InvalidateRect(nint hwnd, nint rect, int erase);
    [LibraryImport("user32")]
    internal static partial int SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);
    [LibraryImport("user32", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int DrawTextW(nint dc, string text, int count, ref Rect rect, uint format);
    [LibraryImport("user32")]
    private static partial nint LoadCursorW(nint module, nint id);
    [LibraryImport("user32")]
    internal static partial int SetProcessDpiAwarenessContext(nint context);
    [LibraryImport("user32")]
    internal static partial uint GetDpiForWindow(nint hwnd);
    [LibraryImport("user32")]
    internal static partial uint GetDpiForSystem();
    [LibraryImport("user32")]
    internal static partial int GetSystemMetrics(int index);
    [LibraryImport("user32")]
    internal static partial nint MonitorFromWindow(nint hwnd, uint flags);
    [LibraryImport("user32")]
    internal static partial int GetMonitorInfoW(nint monitor, ref MonitorInfo info);
    [LibraryImport("user32")]
    internal static partial nuint SetTimer(nint hwnd, nuint id, uint ms, nint callback);
    [LibraryImport("user32")]
    internal static partial int KillTimer(nint hwnd, nuint id);
    [LibraryImport("user32")]
    internal static partial int GetCursorPos(out Point point);
    [LibraryImport("user32")]
    internal static partial int ClientToScreen(nint hwnd, ref Point p);
    [LibraryImport("user32")]
    internal static partial int ScreenToClient(nint hwnd, ref Point p);
    [LibraryImport("user32")]
    internal static partial int TrackMouseEvent(ref MouseTrack track);
    [LibraryImport("user32")]
    internal static partial nint SetCapture(nint hwnd);
    [LibraryImport("user32")]
    internal static partial int ReleaseCapture();
    [LibraryImport("user32", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int MessageBoxW(nint owner, string text, string title, uint flags);
    [LibraryImport("user32")]
    internal static partial nint CreateIconFromResourceEx(byte* bytes, uint length, int icon, uint version, int w, int h, uint flags);
    [LibraryImport("user32")]
    internal static partial int DrawIconEx(nint dc, int x, int y, nint icon, int w, int h, uint step, nint brush, uint flags);
    [LibraryImport("user32")]
    internal static partial int DestroyIcon(nint icon);
    [LibraryImport("user32")]
    internal static partial int FillRect(nint dc, in Rect rect, nint brush);

    [LibraryImport("gdi32")]
    internal static partial int SaveDC(nint dc);
    [LibraryImport("gdi32")]
    internal static partial int RestoreDC(nint dc, int saved);
    [LibraryImport("gdi32")]
    internal static partial int IntersectClipRect(nint dc, int x, int y, int right, int bottom);
    [LibraryImport("gdi32")]
    internal static partial nint CreateCompatibleDC(nint dc);
    [LibraryImport("gdi32")]
    internal static partial int DeleteDC(nint dc);
    [LibraryImport("gdi32")]
    internal static partial int DeleteObject(nint obj);
    [LibraryImport("gdi32")]
    internal static partial nint SelectObject(nint dc, nint obj);
    [LibraryImport("gdi32")]
    internal static partial nint GetStockObject(int id);
    [LibraryImport("gdi32")]
    internal static partial uint SetDCBrushColor(nint dc, uint color);
    [LibraryImport("gdi32")]
    internal static partial uint SetTextColor(nint dc, uint color);
    [LibraryImport("gdi32")]
    internal static partial int SetBkMode(nint dc, int mode);
    [LibraryImport("gdi32")]
    internal static partial int RoundRect(nint dc, int x, int y, int right, int bottom, int radiusX, int radiusY);
    [LibraryImport("msimg32")]
    internal static partial int AlphaBlend(nint dc, int x, int y, int w, int h, nint source, int sx, int sy, int sw, int sh, uint blend);
    [LibraryImport("gdi32", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint CreateFontW(int height, int width, int escape, int angle, int weight, uint italic, uint underline, uint strike, uint charset, uint output, uint clip, uint quality, uint pitch, string family);
    [LibraryImport("gdi32")]
    internal static partial int Polygon(nint dc, Point* points, int count);
    [LibraryImport("gdi32", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int GetTextExtentPoint32W(nint dc, string text, int length, out Point size);

    [LibraryImport("kernel32")]
    private static partial nint GetModuleHandleW(nint name);

    [LibraryImport("dwmapi")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attr, in int value, int size);
}
