using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Opssh.Tray;

internal sealed class ApplicationIcon
{
    internal const int Size = 48;
    private const int PixelBytes = Size * Size * 4;
    private const uint QueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x00100000;
    private const uint WaitTimeout = 258;
    private const uint DriveFixed = 3;
    private const uint DrawNormal = 3;
    private const int GenericIcon = 32512; // IDI_APPLICATION
    private int extracting;

    internal async Task<byte[]?> ReadAsync(ProcessIdentity process, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Shell extraction is synchronous and may outlive its waiter. Let at most one call run.
        if (Interlocked.CompareExchange(ref extracting, 1, 0) == 0)
        {
            var read = Task.Run(() =>
            {
                try { return ReadVerified(process); }
                finally { Volatile.Write(ref extracting, 0); }
            });
            try
            {
                var pixels = await read.WaitAsync(TimeSpan.FromMilliseconds(250), token);
                if (pixels is not null)
                    return pixels;
            }
            catch (TimeoutException) { }
        }

        token.ThrowIfCancellationRequested();
        return Draw(LoadIconW(0, GenericIcon));
    }

    internal static SafeProcessHandle? OpenVerifiedProcess(ProcessIdentity process, out string? path)
    {
        path = null;
        var handle = OpenProcess(QueryLimitedInformation | Synchronize, false, process.Pid);
        var keep = false;
        try
        {
            if (handle.IsInvalid || !GetProcessTimes(handle, out var created, out _, out _, out _) ||
                created != process.Created || WaitForSingleObject(handle, 0) != WaitTimeout)
                return null;

            var capacity = (uint)process.Path.Length + 1;
            var image = new StringBuilder((int)capacity);
            if (!QueryFullProcessImageNameW(handle, 0, image, ref capacity))
                return null;
            path = image.ToString();
            keep = string.Equals(path, process.Path, StringComparison.OrdinalIgnoreCase);
            return keep ? handle : null;
        }
        finally
        {
            if (!keep)
                handle.Dispose();
        }
    }

    private static byte[]? ReadVerified(ProcessIdentity process)
    {
        using var handle = OpenVerifiedProcess(process, out var path);
        if (handle is null || path is null || (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !IsLocalPath(path)))
            return null;

        // Extract only the verified executable's embedded icon. No shell handler or program is launched.
        var count = ExtractIconExW(path, 0, out var icon, 0, 1);
        try
        {
            if (count != 1 || WaitForSingleObject(handle, 0) != WaitTimeout)
                return null;
            var pixels = Draw(icon);
            return WaitForSingleObject(handle, 0) == WaitTimeout ? pixels : null;
        }
        finally
        {
            if (icon != 0)
                DestroyIcon(icon);
        }
    }

    internal static bool IsLocalPath(string path)
    {
        if (path.Length < 4 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\' ||
            GetDriveTypeW(path[..3]) != DriveFixed)
            return false;

        // Check from the drive root so a junction cannot redirect the icon lookup.
        var directories = 0;
        for (var i = 3; i < path.Length; i++)
        {
            if (path[i] != '\\' && i != path.Length - 1)
                continue;
            if (++directories > 64)
                return false;
            var attributes = GetFileAttributesW(i == path.Length - 1 ? path : path[..i]);
            if (attributes == uint.MaxValue || (attributes & (uint)FileAttributes.ReparsePoint) != 0)
                return false;
        }
        return true;
    }

    private static byte[]? Draw(nint icon)
    {
        if (icon == 0)
            return null;
        var dc = CreateCompatibleDC(0);
        if (dc == 0)
            return null;
        var header = new BitmapHeader
        {
            HeaderSize = (uint)Marshal.SizeOf<BitmapHeader>(),
            Width = Size,
            Height = -Size,
            Planes = 1,
            BitsPerPixel = 32
        };
        var bitmap = CreateDIBSection(dc, ref header, 0, out var bits, 0, 0);
        try
        {
            if (bitmap == 0 || bits == 0)
                return null;
            var previous = SelectObject(dc, bitmap);
            if (previous is 0 or -1)
                return null;
            try
            {
                var pixels = new byte[PixelBytes];
                Marshal.Copy(pixels, 0, bits, PixelBytes);
                if (!DrawIconEx(dc, 0, 0, icon, Size, Size, 0, 0, DrawNormal))
                    return null;
                GdiFlush();
                Marshal.Copy(bits, pixels, 0, PixelBytes);

                for (var i = 3; i < pixels.Length; i += 4)
                    if (pixels[i] != 0)
                        return pixels;

                // Older mask-only icons don't set alpha. Recover it from a second render over white.
                var white = new byte[PixelBytes];
                Array.Fill(white, (byte)255);
                Marshal.Copy(white, 0, bits, PixelBytes);
                if (!DrawIconEx(dc, 0, 0, icon, Size, Size, 0, 0, DrawNormal))
                    return null;
                GdiFlush();
                Marshal.Copy(bits, white, 0, PixelBytes);
                for (var i = 0; i < pixels.Length; i += 4)
                    pixels[i + 3] = (byte)(255 - Math.Clamp(white[i] - pixels[i], 0, 255));
                return pixels;
            }
            finally { SelectObject(dc, previous); }
        }
        finally
        {
            if (bitmap != 0)
                DeleteObject(bitmap);
            DeleteDC(dc);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapHeader
    {
        public uint HeaderSize;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitsPerPixel;
        public uint Compression;
        public uint ImageSize;
        public int XPixelsPerMeter;
        public int YPixelsPerMeter;
        public uint ColorsUsed;
        public uint ColorsImportant;
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out ulong created, out ulong exited,
        out ulong kernel, out ulong user);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder name, ref uint size);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint GetDriveTypeW(string root);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFileAttributesW(string path);

    [DllImport("shell32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconExW(string path, int index, out nint large, nint small, uint count);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint LoadIconW(nint instance, nint name);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DrawIconEx(nint dc, int x, int y, nint icon, int width, int height, uint step, nint brush, uint flags);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern nint CreateCompatibleDC(nint dc);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern nint CreateDIBSection(nint dc, ref BitmapHeader header, uint usage, out nint bits, nint section, uint offset);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern nint SelectObject(nint dc, nint obj);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint obj);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(nint dc);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GdiFlush();
}
