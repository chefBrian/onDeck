using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// Renders Tabler Icons' `ball-baseball` (MIT) to multi-resolution .ico files.
// Usage: dotnet run --project windows/tools/IconGen -- <output-directory>

// Path data straight from tabler-icons/icons/outline/ball-baseball.svg, 24x24 viewBox,
// stroke-width 2, round caps and joins.
string[] outline =
[
    "M5.636 18.364a9 9 0 1 0 12.728 -12.728a9 9 0 0 0 -12.728 12.728",
    "M12.495 3.02a9 9 0 0 1 -9.475 9.475",
    "M20.98 11.505a9 9 0 0 0 -9.475 9.475",
];

// Tabler's six stitch ticks, kept for reference but NOT shipped. They sit in the band between
// the two seams; at 128 px they already read as a cluttered diagonal smear, and the tray never
// renders above 32 px. Circle + seams alone reads as a baseball at every size we ship.
// Pass `withStitches: true` to a preview render to see them.
string[] stitches =
[
    "M9 9l2 2",
    "M13 13l2 2",
    "M11 7l2 1",
    "M7 11l1 2",
    "M16 11l1 2",
    "M11 16l2 1",
];

(string Name, Color Colour)[] variants =
[
    ("tray-white", Color.FromRgb(0xFF, 0xFF, 0xFF)),
    ("tray-dark", Color.FromRgb(0x1A, 0x1A, 0x1A)),
    ("tray-green", Color.FromRgb(0x34, 0xC7, 0x59)),
];

int[] sizes = [16, 20, 24, 32];

var outputDirectory = args.Length > 0 ? args[0] : ".";
var previewDirectory = args.Length > 1 ? args[1] : null;
Directory.CreateDirectory(outputDirectory);
if (previewDirectory is not null) Directory.CreateDirectory(previewDirectory);

foreach (var (name, colour) in variants)
{
    var frames = sizes.Select(size => RenderPng(size, colour)).ToArray();
    var path = Path.Combine(outputDirectory, name + ".ico");
    File.WriteAllBytes(path, PackIco(sizes, frames));
    Console.WriteLine($"{path}  {new FileInfo(path).Length} bytes  ({string.Join(", ", sizes)})");

    // Blown-up renders for eyeballing the geometry; not shipped.
    if (previewDirectory is null) continue;
    File.WriteAllBytes(Path.Combine(previewDirectory, name + ".preview.png"), RenderPng(128, colour));
    File.WriteAllBytes(
        Path.Combine(previewDirectory, name + ".outline.png"), RenderPng(128, colour, withStitches: false));
    File.WriteAllBytes(Path.Combine(previewDirectory, name + ".16.png"), frames[0]);
}

return 0;

byte[] RenderPng(int size, Color colour, bool? withStitches = null)
{
    var pen = new Pen(new SolidColorBrush(colour), 2)
    {
        StartLineCap = PenLineCap.Round,
        EndLineCap = PenLineCap.Round,
        LineJoin = PenLineJoin.Round,
    };
    pen.Freeze();

    var paths = withStitches == true ? outline.Concat(stitches) : outline;

    var visual = new DrawingVisual();
    using (var context = visual.RenderOpen())
    {
        // The 24-unit design box scales to the target; strokes scale with it.
        context.PushTransform(new ScaleTransform(size / 24.0, size / 24.0));
        foreach (var data in paths) context.DrawGeometry(null, pen, Geometry.Parse(data));
        context.Pop();
    }

    var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
    bitmap.Render(visual);

    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));

    using var stream = new MemoryStream();
    encoder.Save(stream);
    return stream.ToArray();
}

// ICONDIR + one ICONDIRENTRY per frame + the PNG payloads. PNG-compressed frames are
// supported by every Windows since Vista and keep the file small.
static byte[] PackIco(int[] sizes, byte[][] frames)
{
    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream);

    writer.Write((ushort)0);            // reserved
    writer.Write((ushort)1);            // type: icon
    writer.Write((ushort)frames.Length);

    var offset = 6 + (16 * frames.Length);
    for (var i = 0; i < frames.Length; i++)
    {
        writer.Write((byte)sizes[i]);   // width  (0 would mean 256)
        writer.Write((byte)sizes[i]);   // height
        writer.Write((byte)0);          // palette size
        writer.Write((byte)0);          // reserved
        writer.Write((ushort)1);        // colour planes
        writer.Write((ushort)32);       // bits per pixel
        writer.Write(frames[i].Length);
        writer.Write(offset);
        offset += frames[i].Length;
    }

    foreach (var frame in frames) writer.Write(frame);

    writer.Flush();
    return stream.ToArray();
}
