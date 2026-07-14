using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SwiftList.App.Services;

namespace SwiftList.App;

/// <summary>
/// Fetches high-resolution shell icons from the system image list (48px ExtraLarge /
/// 256px Jumbo) instead of the fixed 32px SHGFI_LARGEICON, so result icons stay crisp
/// when displayed larger or on high-DPI displays. Returns null on failure so callers
/// can fall back to the legacy 32px path.
/// </summary>
internal static class ShellImageListInterop
{
    private const int SHIL_EXTRALARGE = 2; // 48px
    private const int SHIL_JUMBO = 4;      // 256px
    private const uint SHGFI_SYSICONINDEX = 0x4000;
    private const int ILD_TRANSPARENT = 0x1;
    private static Guid _iidImageList = new("46EB5926-582E-4017-9FDF-E8998DAA0950");

    [DllImport("shell32.dll")]
    private static extern int SHGetImageList(int iImageList, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IImageList ppv);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int PrivateExtractIconsW(string szFileName, int nIconIndex, int cxIcon, int cyIcon, IntPtr[] phicon, int[] piconid, int nIcons, uint flags);

    // Only slots up to GetIcon (index 7) are declared; the rest of the vtable is unused.
    // Order MUST match CommCtrl.h IImageList exactly or calls dispatch to the wrong method.
    [ComImport, Guid("46EB5926-582E-4017-9FDF-E8998DAA0950"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IImageList
    {
        [PreserveSig] int Add();             // 0
        [PreserveSig] int ReplaceIcon();     // 1
        [PreserveSig] int SetOverlayImage(); // 2
        [PreserveSig] int Replace();         // 3
        [PreserveSig] int AddMasked();       // 4
        [PreserveSig] int Draw();            // 5
        [PreserveSig] int Remove();          // 6
        [PreserveSig] int GetIcon(int i, int flags, out IntPtr picon); // 7
    }

    private static double DpiScale
    {
        get { try { return GetDpiForSystem() / 96.0; } catch { return 1.0; } }
    }

    /// <summary>Target icon size in physical pixels for the current display scale. Takes the larger of
    /// the fixed main-window size and the quick window's (user-configurable, scale-applied) size, so
    /// whichever window ends up displaying icons largest still gets a crisp source bitmap.</summary>
    private static int TargetPixels() => (int)Math.Ceiling(Math.Max(UiMetrics.ResultIconSize, UiMetrics.ScaledResultIconSize) * DpiScale);

    private static int CurrentShil() => TargetPixels() <= 48 ? SHIL_EXTRALARGE : SHIL_JUMBO;

    /// <summary>Native pixel size of the currently selected image-list tier.</summary>
    public static int PreferredPixels() => CurrentShil() == SHIL_JUMBO ? 256 : 48;

    public static ImageSource? TryGetIcon(string path, uint attrs, uint extraFlags)
    {
        // Real paths: prefer IShellItemImageFactory (scales correctly; avoids Jumbo centering
        // tiny icons for exes that only ship a small icon). Skip for USEFILEATTRIBUTES lookups,
        // whose "path" is a fake dummy/extension the shell can't parse.
        if ((extraFlags & ShellIconNativeMethods.SHGFI_USEFILEATTRIBUTES) == 0)
        {
            var img = FromFactory(path);
            if (img != null) return img;
        }

        var shfi = new ShellIconNativeMethods.SHFILEINFOW();
        var r = ShellIconNativeMethods.SHGetFileInfoW(path, attrs, ref shfi, (uint)Marshal.SizeOf(shfi), SHGFI_SYSICONINDEX | extraFlags);
        if (r == IntPtr.Zero) return null;

        // USEFILEATTRIBUTES uses a fake path (e.g. ".dll"); the shell image-list at JUMBO
        // centres a small (48px) icon inside a 256px canvas, so after WPF scaling the
        // visible icon shrinks to a speck in the top-left. Force EXTRALARGE (48px).
        var shil = (extraFlags & ShellIconNativeMethods.SHGFI_USEFILEATTRIBUTES) != 0
            ? SHIL_EXTRALARGE
            : CurrentShil();
        return FromImageList(shfi.iIcon, shil);
    }

    public static ImageSource? TryGetIconPidl(IntPtr pidl)
    {
        var img = FromFactoryPidl(pidl);
        if (img != null) return img;

        var shfi = new ShellIconNativeMethods.SHFILEINFOW();
        var r = ShellIconNativeMethods.SHGetFileInfoW(pidl, 0, ref shfi, (uint)Marshal.SizeOf(shfi), SHGFI_SYSICONINDEX | ShellIconNativeMethods.SHGFI_PIDL);
        return r == IntPtr.Zero ? null : FromImageList(shfi.iIcon);
    }

    /// <summary>High-res icon extracted directly from a file/index (for shortcut icon locations).</summary>
    public static ImageSource? ExtractHiRes(string iconPath, int iconIndex)
    {
        var px = PreferredPixels();
        var hicons = new IntPtr[1];
        var ids = new int[1];
        var n = PrivateExtractIconsW(iconPath, iconIndex, px, px, hicons, ids, 1, 0);
        if (n <= 0 || hicons[0] == IntPtr.Zero) return null;
        try { return FromHIcon(hicons[0]); }
        finally { ShellIconNativeMethods.DestroyIcon(hicons[0]); }
    }

    // ---- IShellItemImageFactory: correctly size-scaled icon (no Jumbo small-icon centering) ----
    private const int SIIGBF_ICONONLY = 0x4;
    private const int FactorySize = 96;
    private static Guid _iidImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx; public int cy; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string pszPath, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);
    [DllImport("shell32.dll", PreserveSig = false)]
    private static extern void SHCreateItemFromIDList(IntPtr pidl, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory { [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm); }

    private static ImageSource? FromFactory(string path)
    {
        try
        {
            var iid = _iidImageFactory;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var f);
            try { return ImageFromFactory(f); } finally { Marshal.ReleaseComObject(f); }
        }
        catch { return null; }
    }

    private static ImageSource? FromFactoryPidl(IntPtr pidl)
    {
        try
        {
            var iid = _iidImageFactory;
            SHCreateItemFromIDList(pidl, ref iid, out var f);
            try { return ImageFromFactory(f); } finally { Marshal.ReleaseComObject(f); }
        }
        catch { return null; }
    }

    private static ImageSource? ImageFromFactory(IShellItemImageFactory f)
    {
        if (f.GetImage(new SIZE { cx = FactorySize, cy = FactorySize }, SIIGBF_ICONONLY, out var hbmp) != 0 || hbmp == IntPtr.Zero)
            return null;
        try
        {
            var bmp = Imaging.CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bmp.Freeze();
            return bmp;
        }
        finally { DeleteObject(hbmp); }
    }

    private const int SIIGBF_BIGGERSIZEOK = 0x1; // allow a larger bitmap than requested (avoids upscaling)

    /// <summary>
    /// Fetches a large real thumbnail (video frame, document page, image) at up to <paramref name="size"/>
    /// pixels for the preview pane — no ICONONLY, so the shell returns the actual content thumbnail when it
    /// has one, and its native icon otherwise. Uncached; returns null on failure.
    /// </summary>
    public static ImageSource? TryGetPreviewThumbnail(string path, int size)
    {
        try
        {
            var iid = _iidImageFactory;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var f);
            try
            {
                if (f.GetImage(new SIZE { cx = size, cy = size }, SIIGBF_BIGGERSIZEOK, out var hbmp) != 0 || hbmp == IntPtr.Zero)
                    return null;
                try
                {
                    var bmp = Imaging.CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    bmp.Freeze();
                    return bmp;
                }
                finally { DeleteObject(hbmp); }
            }
            finally { Marshal.ReleaseComObject(f); }
        }
        catch { return null; }
    }

    private static ImageSource? FromImageList(int iIcon, int shil = -1)
    {
        if (shil < 0) shil = CurrentShil();
        IImageList? list = null;
        try
        {
            if (SHGetImageList(shil, ref _iidImageList, out list) < 0 || list == null)
                return null;
            if (list.GetIcon(iIcon, ILD_TRANSPARENT, out var hicon) < 0 || hicon == IntPtr.Zero)
                return null;
            try { return FromHIcon(hicon); }
            finally { ShellIconNativeMethods.DestroyIcon(hicon); }
        }
        catch (Exception ex)
        {
            Core.Logger.Log($"[ShellImageListInterop] Image list icon failed: {ex.Message}", Core.LogLevel.Warn);
            return null;
        }
        finally
        {
            if (list != null) Marshal.ReleaseComObject(list);
        }
    }

    private static ImageSource FromHIcon(IntPtr hicon)
    {
        var bmp = Imaging.CreateBitmapSourceFromHIcon(hicon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        // CreateBitmapSourceFromHIcon inherits system DPI (e.g. 192 on 200% scale).
        // WPF layout computes physical size as pixels / DPI x 96, so on high-DPI the
        // bitmap renders too small. Force 96 DPI so pixel dimensions == WPF units.
        if (bmp.DpiX != 96 || bmp.DpiY != 96)
        {
            var stride = bmp.PixelWidth * ((bmp.Format.BitsPerPixel + 7) / 8);
            var pixels = new byte[stride * bmp.PixelHeight];
            bmp.CopyPixels(pixels, stride, 0);
            var fixedDpi = new WriteableBitmap(bmp.PixelWidth, bmp.PixelHeight, 96, 96, bmp.Format, null);
            fixedDpi.WritePixels(new Int32Rect(0, 0, bmp.PixelWidth, bmp.PixelHeight), pixels, stride, 0);
            fixedDpi.Freeze();
            return fixedDpi;
        }
        bmp.Freeze();
        return bmp;
    }
}
