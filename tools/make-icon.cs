#:property TargetFramework=net10.0-windows
#:property UseWindowsForms=true
#:property Nullable=enable
#:property PublishAot=false
// Makes Film Expiry Checker's icon: app.ico (16-256 px, PNGs inside, which Windows Vista and later read)
// and icon-256.png (the apple-touch-icon). The same .ico is the launcher's and the web app's favicon.
//
//   dotnet run tools/make-icon.cs -- <out-folder>
//
// The mark: an hourglass whose top and bottom bars are strips of film (sprocket holes along them), with sand
// running through: film, and time running out. Cream on the report's deep red. Below 32 px the holes are left
// out, so it still reads as an hourglass. (From the blazor-desktop-app skill's make-icon.cs.)
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

if (args.Length < 1) { Console.Error.WriteLine("dotnet run tools/make-icon.cs -- <out-folder>"); return 1; }
var output = Directory.CreateDirectory(args[0]).FullName;

int[] sizes = [16, 24, 32, 48, 64, 256];
var pngs = sizes.Select(s => (Size: s, Png: Draw(s))).ToList();
File.WriteAllBytes(Path.Combine(output, "icon-256.png"), pngs[^1].Png);
// Small sizes, drawn at their real size then enlarged, to judge how they read.
foreach (var small in new[] { 16, 32 })
    File.WriteAllBytes(Path.Combine(output, $"preview-{small}px.png"), Enlarged(pngs.Single(p => p.Size == small).Png, 128));

using (var ico = new BinaryWriter(File.Create(Path.Combine(output, "app.ico"))))
{
    ico.Write((short)0); ico.Write((short)1); ico.Write((short)pngs.Count);
    var offset = 6 + (16 * pngs.Count);
    foreach (var (size, png) in pngs)
    {
        ico.Write((byte)(size >= 256 ? 0 : size)); ico.Write((byte)(size >= 256 ? 0 : size)); // 0 means 256
        ico.Write((byte)0); ico.Write((byte)0);      // no palette; reserved
        ico.Write((short)1); ico.Write((short)32);   // planes; bits per pixel
        ico.Write(png.Length); ico.Write(offset);
        offset += png.Length;
    }
    foreach (var (_, png) in pngs) ico.Write(png);
}
Console.WriteLine($"Wrote {Path.Combine(output, "app.ico")}, icon-256.png and previews");
return 0;

static byte[] Draw(int size)
{
    using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(bitmap))
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);
        DrawTile(g, size);
    }
    using var stream = new MemoryStream();
    bitmap.Save(stream, ImageFormat.Png);
    return stream.ToArray();
}

static byte[] Enlarged(byte[] png, int to)
{
    using var small = new Bitmap(new MemoryStream(png));
    using var big = new Bitmap(to, to, PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(big))
    {
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.Clear(Color.White);
        g.DrawImage(small, 0, 0, to, to);
    }
    using var stream = new MemoryStream();
    big.Save(stream, ImageFormat.Png);
    return stream.ToArray();
}


// A rounded tile with the mark on it.
static void DrawTile(Graphics g, float size)
{
    var r = size * 0.2f;
    using var path = new GraphicsPath();
    path.AddArc(0, 0, r * 2, r * 2, 180, 90);
    path.AddArc(size - r * 2 - 1, 0, r * 2, r * 2, 270, 90);
    path.AddArc(size - r * 2 - 1, size - r * 2 - 1, r * 2, r * 2, 0, 90);
    path.AddArc(0, size - r * 2 - 1, r * 2, r * 2, 90, 90);
    path.CloseFigure();
    using (var fill = new SolidBrush(Palette.Tile)) g.FillPath(fill, path);
    DrawMark(g, size);
}

static void DrawMark(Graphics g, float s)
{
    using var film = new SolidBrush(Palette.Film);
    using var sand = new SolidBrush(Palette.Sand);
    using var hole = new SolidBrush(Palette.Tile);

    // The glass: two cream bulbs meeting at a narrow waist.
    PointF P(float x, float y) => new(s * x, s * y);
    using (var glass = new GraphicsPath())
    {
        glass.AddPolygon([P(0.27f, 0.22f), P(0.73f, 0.22f), P(0.535f, 0.5f), P(0.73f, 0.78f), P(0.27f, 0.78f), P(0.465f, 0.5f)]);
        g.FillPath(film, glass);
    }

    // The sand: a little left in the top bulb, a pile in the bottom one, and a thread falling between.
    g.FillPolygon(sand, [P(0.41f, 0.40f), P(0.59f, 0.40f), P(0.5f, 0.49f)]);
    g.FillPolygon(sand, [P(0.34f, 0.76f), P(0.66f, 0.76f), P(0.5f, 0.60f)]);
    if (s >= 32) g.FillRectangle(sand, s * 0.493f, s * 0.49f, s * 0.014f, s * 0.12f);

    // The top and bottom bars: strips of film, with their sprocket holes (too fine to draw below 32 px).
    var barHeight = s >= 32 ? 0.11f : 0.12f;
    foreach (var top in new[] { 0.12f, 0.88f - barHeight })
    {
        g.FillRectangle(film, s * 0.16f, s * top, s * 0.68f, s * barHeight);
        if (s < 32) continue;
        const int holes = 5;
        var holeSize = s * 0.055f;
        for (var i = 0; i < holes; i++)
        {
            var x = s * 0.16f + (s * 0.68f) * (i + 0.5f) / holes - holeSize / 2;
            g.FillRectangle(hole, x, s * (top + barHeight / 2) - holeSize / 2, holeSize, holeSize);
        }
    }
}

static class Palette
{
    public static readonly Color Tile = ColorTranslator.FromHtml("#e63946");   // the report's red
    public static readonly Color Film = ColorTranslator.FromHtml("#fdf6e3");   // cream
    public static readonly Color Sand = ColorTranslator.FromHtml("#f4a261");   // the report's amber
}