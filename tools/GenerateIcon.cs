#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property PublishAot=false

// Draws the app icon (the shapes in src/OpenTeslaBuyer.App/Assets/OpenTeslaBuyer.svg; 16 and 20 px use a simpler
// filled battery) at every size Windows asks for and packs them into OpenTeslaBuyer.ico: 32-bit bitmaps up to 64 px,
// PNG at 256 px. Also writes a 256 px PNG for docs.
//   dotnet run tools/GenerateIcon.cs -- src/OpenTeslaBuyer.App/Assets/OpenTeslaBuyer.ico docs/images/icon.png
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

var icoPath = args.Length > 0 ? args[0] : "OpenTeslaBuyer.ico";
var pngPath = args.Length > 1 ? args[1] : null;
int[] sizes = [16, 20, 24, 32, 40, 48, 64, 256];

// WPF drawing needs a single-threaded apartment.
var thread = new Thread(() =>
{
    var images = sizes.Select(size => (Size: size, Bitmap: Render(size))).ToList();
    File.WriteAllBytes(icoPath, Ico(images));
    Console.WriteLine($"Wrote {icoPath} ({string.Join(", ", sizes)} px)");
    if (pngPath is not null)
    {
        File.WriteAllBytes(pngPath, Png(images.Single(i => i.Size == 256).Bitmap));
        Console.WriteLine($"Wrote {pngPath}");
    }
});
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();

static BitmapSource Render(int size)
{
    var s = size / 256.0;
    if (size <= 20)
        return Draw(size, dc => DrawSmall(dc, size));

    // Small icons get thicker strokes than a straight scale-down, so the battery and the check stay readable.
    var outline = Math.Max(16 * s, 1.6);
    var check = Math.Max(20 * s, 2.2);

    var visual = new DrawingVisual();
    using (var dc = visual.RenderOpen())
    {
        dc.DrawRoundedRectangle(Tile(), null, new Rect(8 * s, 8 * s, 240 * s, 240 * s), 56 * s, 56 * s);

        var white = Brushes.White;
        var glass = new SolidColorBrush(Color.FromArgb(31, 255, 255, 255));
        dc.DrawRoundedRectangle(glass, new Pen(white, outline), new Rect(44 * s, 78 * s, 144 * s, 100 * s), 20 * s, 20 * s);
        dc.DrawRoundedRectangle(white, null, new Rect(192 * s, 108 * s, 20 * s, 40 * s), 6 * s, 6 * s);

        var tick = new StreamGeometry();
        using (var g = tick.Open())
        {
            g.BeginFigure(new Point(76 * s, 128 * s), false, false);
            g.LineTo(new Point(106 * s, 156 * s), true, true);
            g.LineTo(new Point(158 * s, 100 * s), true, true);
        }

        var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80)), check)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        dc.DrawGeometry(null, pen, tick);
    }

    return Finish(size, visual);
}

/// <summary>
/// At 16 and 20 px a check mark blurs into a smudge, so the tiniest icons show a charged battery instead: a one-pixel
/// outline and a green fill, placed on whole pixels so they stay sharp.
/// </summary>
static void DrawSmall(DrawingContext dc, int size)
{
    var s = size / 256.0;
    dc.DrawRoundedRectangle(Tile(), null, new Rect(8 * s, 8 * s, 240 * s, 240 * s), 56 * s, 56 * s);

    double x = Math.Round(40 * s), y = Math.Round(80 * s), w = Math.Round(150 * s), h = Math.Round(96 * s);
    dc.DrawRectangle(null, new Pen(Brushes.White, 1), new Rect(x + 0.5, y + 0.5, w - 1, h - 1));
    dc.DrawRectangle(Brushes.White, null, new Rect(x + w, y + Math.Floor(h / 2) - 1, 1, 2 + h % 2));
    dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80)), null, new Rect(x + 2, y + 2, w - 4, h - 4));
}

static Brush Tile() => new LinearGradientBrush(Color.FromRgb(0x3B, 0x82, 0xF6), Color.FromRgb(0x1E, 0x40, 0xAF), 90);

static BitmapSource Draw(int size, Action<DrawingContext> draw)
{
    var visual = new DrawingVisual();
    using (var dc = visual.RenderOpen())
        draw(dc);
    return Finish(size, visual);
}

static BitmapSource Finish(int size, DrawingVisual visual)
{
    var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
    bitmap.Render(visual);

    // Icons store straight (not premultiplied) alpha.
    return new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
}

static byte[] Ico(IReadOnlyList<(int Size, BitmapSource Bitmap)> images)
{
    var payloads = images.Select(i => i.Size >= 256 ? Png(i.Bitmap) : Dib(i.Bitmap)).ToList();
    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream);
    writer.Write((short)0); // reserved
    writer.Write((short)1); // icon
    writer.Write((short)images.Count);

    var offset = 6 + 16 * images.Count;
    for (var i = 0; i < images.Count; i++)
    {
        var size = images[i].Size;
        writer.Write((byte)(size >= 256 ? 0 : size)); // 0 means 256
        writer.Write((byte)(size >= 256 ? 0 : size));
        writer.Write((byte)0); // palette colours
        writer.Write((byte)0); // reserved
        writer.Write((short)1); // planes
        writer.Write((short)32); // bits per pixel
        writer.Write(payloads[i].Length);
        writer.Write(offset);
        offset += payloads[i].Length;
    }

    foreach (var payload in payloads)
        writer.Write(payload);
    return stream.ToArray();
}

/// <summary>A 32-bit icon bitmap: header, bottom-up BGRA pixels, then the 1-bit transparency mask.</summary>
static byte[] Dib(BitmapSource bitmap)
{
    int width = bitmap.PixelWidth, height = bitmap.PixelHeight, stride = width * 4;
    var pixels = new byte[stride * height];
    bitmap.CopyPixels(pixels, stride, 0);

    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream);
    writer.Write(40); // BITMAPINFOHEADER size
    writer.Write(width);
    writer.Write(height * 2); // colour bitmap and mask together
    writer.Write((short)1);
    writer.Write((short)32);
    writer.Write(0); // BI_RGB
    writer.Write(0);
    writer.Write(0);
    writer.Write(0);
    writer.Write(0);
    writer.Write(0);

    for (var y = height - 1; y >= 0; y--)
        writer.Write(pixels, y * stride, stride);

    var maskStride = (width + 31) / 32 * 4;
    for (var y = height - 1; y >= 0; y--)
    {
        var row = new byte[maskStride];
        for (var x = 0; x < width; x++)
        {
            if (pixels[y * stride + x * 4 + 3] == 0)
                row[x / 8] |= (byte)(0x80 >> (x % 8));
        }

        writer.Write(row);
    }

    return stream.ToArray();
}

static byte[] Png(BitmapSource bitmap)
{
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using var stream = new MemoryStream();
    encoder.Save(stream);
    return stream.ToArray();
}
