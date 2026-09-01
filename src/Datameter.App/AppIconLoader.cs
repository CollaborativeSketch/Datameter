using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;   // byte[].AsBuffer()
using System.Text;
using Datameter.Core;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Management.Deployment;
using Windows.Storage.Streams;

namespace Datameter.App;

/// <summary>
/// Finds a logo for each app in the breakdown. Windows identifies apps three different ways
/// and none of them is a picture, so there are three routes to one:
///
///   1. the thumbnail the usage API hands back, when it bothers to;
///   2. an NT device path ("\device\harddiskvolume3\...\chrome.exe") — resolve the volume to a
///      drive letter and pull the executable's icon;
///   3. a package family name ("MSTeams_8wekyb3d8bbwe") — ask the package manager for its logo.
///
/// Anything unresolved falls back to a glyph, so the list never waits on a missing icon.
/// </summary>
/// <summary>
/// A logo, plus the background colour its package says it should sit on. Packaged apps ship
/// transparent artwork — often a plain white glyph — that is only legible on that colour.
/// Null means no package said, so the app's own neutral plate is used.
/// </summary>
public sealed record AppIcon(ImageSource Image, Windows.UI.Color? PlateColor);

public sealed class AppIconLoader
{
    private readonly Dictionary<string, AppIcon?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string>? _volumeMap;

    public async Task<AppIcon?> LoadAsync(AppUsage app)
    {
        var id = app.AttributionId ?? string.Empty;
        if (_cache.TryGetValue(id, out var cached)) return cached;

        AppIcon? icon = null;
        try
        {
            var bytes = await BytesFromThumbnailAsync(app.Thumbnail)
                        ?? await BytesFromExecutableAsync(id);

            Windows.UI.Color? plate = null;
            if (bytes is null)
            {
                var packaged = await FromPackageAsync(id);
                bytes = packaged.Bytes;
                plate = packaged.Plate;
            }

            if (bytes is not null)
            {
                var image = await FromBytesAsync(bytes);
                if (image is not null) icon = new AppIcon(image, plate);
            }
        }
        catch (Exception ex)
        {
            App.Log("AppIcon", ex);
        }

        _cache[id] = icon;
        return icon;
    }

    // ---- 1. the API's own thumbnail -----------------------------------------

    private static async Task<byte[]?> BytesFromThumbnailAsync(IRandomAccessStreamReference? reference)
    {
        if (reference is null) return null;

        using var stream = await reference.OpenReadAsync();
        if (stream.Size == 0) return null;

        var buffer = new Windows.Storage.Streams.Buffer((uint)stream.Size);
        await stream.ReadAsync(buffer, (uint)stream.Size, InputStreamOptions.None);
        return buffer.ToArray();
    }

    // ---- 2. an executable on disk -------------------------------------------

    private async Task<byte[]?> BytesFromExecutableAsync(string attributionId)
    {
        var path = ResolveNtPath(attributionId);
        if (path is null) return null;

        // Usage is attributed to the exact binary that moved the bytes. Apps that update into
        // versioned folders leave that path behind, so fall back to the same executable where
        // it lives now — which, for anything currently running, the process list knows.
        if (!File.Exists(path)) path = LocateRunningExecutable(Path.GetFileName(path));
        if (path is null) return null;

        // These are blocking Win32 calls; keep them off the UI thread.
        var resolved = path;
        return await Task.Run(() => ExtractIconBytes(resolved) ?? ShellIconBytes(resolved));
    }

    private static string? LocateRunningExecutable(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;

        try
        {
            var name = Path.GetFileNameWithoutExtension(fileName);
            foreach (var process in System.Diagnostics.Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        var candidate = process.MainModule?.FileName;
                        if (candidate is not null && File.Exists(candidate)) return candidate;
                    }
                    catch
                    {
                        // Elevated or exited processes refuse MainModule; try the next one.
                    }
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static byte[]? ExtractIconBytes(string path)
    {
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            return icon is null ? null : ToPng(icon);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Falls back to the shell's icon, which resolves cases ExtractAssociatedIcon refuses —
    /// non-PE targets, and executables whose icon lives in a side-by-side resource.
    /// </summary>
    private static byte[]? ShellIconBytes(string path)
    {
        var info = new SHFILEINFO();
        var handle = IntPtr.Zero;

        try
        {
            if (SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON) == IntPtr.Zero)
                return null;

            handle = info.hIcon;
            if (handle == IntPtr.Zero) return null;

            using var icon = System.Drawing.Icon.FromHandle(handle);
            return ToPng(icon);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (handle != IntPtr.Zero) DestroyIcon(handle);
        }
    }

    private static byte[] ToPng(System.Drawing.Icon icon)
    {
        using var bitmap = icon.ToBitmap();
        using var memory = new MemoryStream();
        bitmap.Save(memory, System.Drawing.Imaging.ImageFormat.Png);
        return memory.ToArray();
    }

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    /// <summary>
    /// Turns "\device\harddiskvolume3\program files\...\chrome.exe" into "C:\program files\...".
    /// The volume-to-letter map is built once and reused.
    /// </summary>
    private string? ResolveNtPath(string attributionId)
    {
        if (string.IsNullOrWhiteSpace(attributionId)) return null;

        if (!attributionId.StartsWith(@"\device\", StringComparison.OrdinalIgnoreCase))
            return attributionId.Contains('\\') ? attributionId : null;

        _volumeMap ??= BuildVolumeMap();

        foreach (var (device, letter) in _volumeMap)
        {
            if (attributionId.StartsWith(device, StringComparison.OrdinalIgnoreCase))
                return letter + attributionId[device.Length..];
        }

        return null;
    }

    private static Dictionary<string, string> BuildVolumeMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var buffer = new StringBuilder(512);

        for (var letter = 'A'; letter <= 'Z'; letter++)
        {
            var dos = letter + ":";
            if (QueryDosDevice(dos, buffer, buffer.Capacity) != 0)
                map[buffer.ToString()] = dos;
        }

        return map;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int QueryDosDevice(string deviceName, StringBuilder target, int max);

    // ---- 3. a packaged app ---------------------------------------------------

    private static async Task<(byte[]? Bytes, Windows.UI.Color? Plate)> FromPackageAsync(string attributionId)
    {
        if (string.IsNullOrWhiteSpace(attributionId) || attributionId.Contains('\\')) return (null, null);

        Windows.ApplicationModel.Package? package = null;
        try
        {
            package = await Task.Run(() =>
                new PackageManager().FindPackagesForUser(string.Empty, attributionId).FirstOrDefault());
        }
        catch
        {
            return (null, null);   // package queries can be denied; a glyph is fine
        }

        if (package is null) return (null, null);

        var plate = ReadManifestBackground(package);

        // Prefer the asset on disk. A package ships its logo at several scales, and the
        // largest is a crisp 150–256px file; GetLogo below only ever returns the small
        // app-list size, which visibly pixelates once it is scaled up.
        var best = BestLogoFromDisk(package);
        if (best is not null) return (best, plate);

        try
        {
            var entry = (await package.GetAppListEntriesAsync()).FirstOrDefault();

            // Ask for a large logo, not the 44px one we happen to draw: GetLogo returns the
            // closest asset it has, and asking small hands back a 16–24px file that then has
            // to be scaled up, which is what made these look blurry and undersized.
            var logo = entry?.DisplayInfo.GetLogo(new Windows.Foundation.Size(256, 256));
            if (logo is not null)
            {
                var bytes = await BytesFromThumbnailAsync(logo);
                if (bytes is { Length: > 0 }) return (bytes, plate);
            }
        }
        catch
        {
            // Fall through to the manifest logo below.
        }

        try
        {
            if (package.Logo is null) return (null, plate);
            var path = package.Logo.IsFile ? package.Logo.LocalPath : null;
            return path is not null && File.Exists(path)
                ? (await File.ReadAllBytesAsync(path), plate)
                : (null, plate);
        }
        catch
        {
            return (null, plate);
        }
    }

    /// <summary>
    /// Finds the highest-resolution logo a package ships.
    ///
    /// The manifest names a logo like "Assets\Square44x44Logo.png", but that exact file
    /// usually does not exist — what is on disk are scale-qualified siblings
    /// ("Square44x44Logo.scale-400.png", ".targetsize-256.png"). We pick the one with the most
    /// pixels, measured rather than guessed from file size.
    ///
    /// "altform-unplated" variants are wanted, not skipped: they are the high-resolution
    /// artwork meant to be drawn over a colour, which is exactly what we do. Only
    /// "altform-lightunplated" is excluded — that is the dark-glyph version for light
    /// backgrounds, and it would disappear against a saturated tile colour.
    /// </summary>
    private static byte[]? BestLogoFromDisk(Windows.ApplicationModel.Package package)
    {
        try
        {
            var root = package.InstalledLocation.Path;
            var manifest = Path.Combine(root, "AppxManifest.xml");
            if (!File.Exists(manifest)) return null;

            var document = System.Xml.Linq.XDocument.Load(manifest);

            foreach (var attribute in new[] { "Square44x44Logo", "Square150x150Logo", "Logo" })
            {
                var declared = document.Descendants()
                    .Select(e => e.Attribute(attribute)?.Value)
                    .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

                if (declared is null) continue;

                var directory = Path.Combine(root, Path.GetDirectoryName(declared) ?? "");
                if (!Directory.Exists(directory)) continue;

                var stem = Path.GetFileNameWithoutExtension(declared);

                var best = Directory.EnumerateFiles(directory, stem + "*.png")
                    .Where(f => !f.Contains("lightunplated", StringComparison.OrdinalIgnoreCase))
                    .Select(f => new { Path = f, Width = PixelWidth(f) })
                    .Where(x => x.Width > 0)
                    .OrderByDescending(x => x.Width)
                    .FirstOrDefault();

                if (best is not null) return File.ReadAllBytes(best.Path);
            }
        }
        catch
        {
            // Package folders are ACL'd; falling through to GetLogo is fine.
        }

        return null;
    }

    private static int PixelWidth(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var image = System.Drawing.Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);
            return image.Width;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Reads the tile background an MSIX package declares for its logo. This is the colour the
    /// Start menu plates the icon with, and without it a white-on-transparent logo has nothing
    /// to sit on. "transparent" means the package wants no plate, so the neutral one is used.
    /// </summary>
    private static Windows.UI.Color? ReadManifestBackground(Windows.ApplicationModel.Package package)
    {
        try
        {
            var manifest = Path.Combine(package.InstalledLocation.Path, "AppxManifest.xml");
            if (!File.Exists(manifest)) return null;

            var document = System.Xml.Linq.XDocument.Load(manifest);

            // The VisualElements element is namespaced (uap, uap10, …) and the version varies,
            // so match on the attribute name rather than the element's namespace.
            var value = document.Descendants()
                .Select(e => e.Attribute("BackgroundColor")?.Value)
                .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

            return ParseColor(value);
        }
        catch
        {
            return null;
        }
    }

    private static Windows.UI.Color? ParseColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();

        if (value.Equals("transparent", StringComparison.OrdinalIgnoreCase)) return null;

        if (value.StartsWith('#'))
        {
            var hex = value[1..];
            if (hex.Length == 6 &&
                byte.TryParse(hex[..2], System.Globalization.NumberStyles.HexNumber, null, out var r) &&
                byte.TryParse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var g) &&
                byte.TryParse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
            {
                return Windows.UI.Color.FromArgb(0xFF, r, g, b);
            }
            return null;
        }

        // A handful of packages use CSS colour names instead of hex.
        try
        {
            var known = System.Drawing.Color.FromName(value);
            return known.IsKnownColor
                ? Windows.UI.Color.FromArgb(0xFF, known.R, known.G, known.B)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<ImageSource?> FromBytesAsync(byte[] bytes)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(bytes.AsBuffer());
        stream.Seek(0);

        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }
}
