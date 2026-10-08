#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property PublishAot=false

// Builds the README's screenshot slideshow: an animated PNG (APNG) that shows each screenshot with its title and
// position dots, like a slider. GitHub strips scripts from READMEs, but browsers play APNG, and unlike GIF it keeps
// full colour, so text stays sharp. Viewers without APNG support show the first slide.
//   dotnet run tools/MakeSlideshow.cs -- docs/images/slideshow.png
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

var output = args.Length > 0 ? args[0] : "docs/images/slideshow.png";
(string File, string Title)[] slides =
[
    ("docs/images/diagnostics.png", "Diagnostics: battery health, live data, cell groups and the buyer check"),
    ("docs/images/charging-test.png", "Tests › Charging test: finds a cell group with higher resistance"),
    ("docs/images/overnight-test.png", "Tests › Overnight test: finds a cell group losing charge while parked"),
    ("docs/images/charger-check.png", "Tests › Charger: the onboard charger while AC charging"),
    ("docs/images/twelve-volt.png", "Tests › 12 V: the DC-DC converter and the 12 V battery"),
    ("docs/images/car-history.png", "Car history: trends across checks, and cell groups that keep standing out"),
];
const int SlideMilliseconds = 3500;
const int BarHeight = 64;

// WPF drawing needs a single-threaded apartment.
var thread = new Thread(() =>
{
    var frames = slides.Select((slide, i) => Png(Slide(slide.File, slide.Title, i, slides.Length))).ToList();
    File.WriteAllBytes(output, Apng(frames, SlideMilliseconds));
    Console.WriteLine($"Wrote {output}: {frames.Count} slides, {new FileInfo(output).Length / 1024} KB");
});
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();

static BitmapSource Slide(string file, string title, int index, int count)
{
    var shot = new BitmapImage();
    shot.BeginInit();
    shot.CacheOption = BitmapCacheOption.OnLoad;
    shot.UriSource = new Uri(Path.GetFullPath(file));
    shot.EndInit();

    double width = shot.PixelWidth, height = shot.PixelHeight;
    var visual = new DrawingVisual();
    using (var dc = visual.RenderOpen())
    {
        dc.DrawImage(shot, new Rect(0, 0, width, height));
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x0F, 0x17, 0x2A)), null, new Rect(0, height, width, BarHeight));

        var text = new FormattedText(title, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 20, Brushes.White, 1);
        dc.DrawText(text, new Point(28, height + (BarHeight - text.Height) / 2));

        // Position dots, like a slider's.
        const double dot = 12, gap = 10;
        var x = width - 28 - count * dot - (count - 1) * gap;
        for (var i = 0; i < count; i++, x += dot + gap)
        {
            var brush = new SolidColorBrush(i == index ? Color.FromRgb(0x60, 0xA5, 0xFA) : Color.FromRgb(0x47, 0x55, 0x69));
            dc.DrawEllipse(brush, null, new Point(x + dot / 2, height + BarHeight / 2.0), dot / 2, dot / 2);
        }
    }

    var bitmap = new RenderTargetBitmap((int)width, (int)height + BarHeight, 96, 96, PixelFormats.Pbgra32);
    bitmap.Render(visual);
    return new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
}

static byte[] Png(BitmapSource bitmap)
{
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using var stream = new MemoryStream();
    encoder.Save(stream);
    return stream.ToArray();
}

/// <summary>Joins PNGs of the same size into an endlessly looping APNG (acTL, then fcTL + IDAT/fdAT per frame).</summary>
static byte[] Apng(IReadOnlyList<byte[]> pngs, int frameMilliseconds)
{
    var parsed = pngs.Select(Chunks).ToList();
    var header = parsed[0].Single(c => c.Type == "IHDR").Data;
    if (parsed.Any(p => !p.Single(c => c.Type == "IHDR").Data.SequenceEqual(header)))
        throw new InvalidOperationException("All slides must have the same size and colour format.");

    using var output = new MemoryStream();
    output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
    WriteChunk(output, "IHDR", header);
    WriteChunk(output, "acTL", [.. BigEndian(pngs.Count), .. BigEndian(0)]); // 0 plays = loop forever

    uint sequence = 0;
    for (var frame = 0; frame < parsed.Count; frame++)
    {
        WriteChunk(output, "fcTL",
        [
            .. BigEndian((int)sequence++),
            .. header[0..8], // width and height, as in IHDR
            .. BigEndian(0), .. BigEndian(0), // x and y offset
            .. BigEndian16(frameMilliseconds), .. BigEndian16(1000), // delay numerator and denominator
            0, 0, // dispose: none; blend: source
        ]);
        foreach (var data in parsed[frame].Where(c => c.Type == "IDAT").Select(c => c.Data))
        {
            if (frame == 0)
                WriteChunk(output, "IDAT", data);
            else
                WriteChunk(output, "fdAT", [.. BigEndian((int)sequence++), .. data]);
        }
    }

    WriteChunk(output, "IEND", []);
    return output.ToArray();
}

static List<(string Type, byte[] Data)> Chunks(byte[] png)
{
    var chunks = new List<(string, byte[])>();
    for (var offset = 8; offset < png.Length;)
    {
        var length = (png[offset] << 24) | (png[offset + 1] << 16) | (png[offset + 2] << 8) | png[offset + 3];
        var type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
        chunks.Add((type, png[(offset + 8)..(offset + 8 + length)]));
        offset += 12 + length;
    }

    return chunks;
}

static void WriteChunk(Stream output, string type, byte[] data)
{
    var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
    output.Write(BigEndian(data.Length));
    output.Write(typeBytes);
    output.Write(data);
    output.Write(BigEndian((int)Crc([.. typeBytes, .. data])));
}

static byte[] BigEndian(int value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

static byte[] BigEndian16(int value) => [(byte)(value >> 8), (byte)value];

static uint Crc(byte[] bytes)
{
    var crc = 0xFFFFFFFFu;
    foreach (var b in bytes)
    {
        crc ^= b;
        for (var bit = 0; bit < 8; bit++)
            crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
    }

    return crc ^ 0xFFFFFFFFu;
}
