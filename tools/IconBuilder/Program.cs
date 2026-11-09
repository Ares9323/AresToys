using System.Drawing;
using System.Drawing.Imaging;
using Svg;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: IconBuilder <input.svg> <output.ico>");
    return 1;
}

var svgPath = args[0];
var icoPath = args[1];

if (!File.Exists(svgPath))
{
    Console.Error.WriteLine($"input not found: {svgPath}");
    return 2;
}

var svg = SvgDocument.Open(svgPath);

// Standard Windows icon sizes. Vista+ embeds 256x256 as PNG; smaller sizes go in as PNG too for
// transparency without the bitmask awkwardness.
int[] sizes = [16, 24, 32, 48, 64, 128, 256];

var pngs = new List<byte[]>(sizes.Length);
foreach (var size in sizes)
{
    using var bmp = svg.Draw(size, size);
    using var ms = new MemoryStream();
    bmp.Save(ms, ImageFormat.Png);
    pngs.Add(ms.ToArray());
    Console.WriteLine($"rendered {size}x{size} ({ms.Length} bytes)");
}

WriteIco(icoPath, sizes, pngs);
Console.WriteLine($"wrote {icoPath}");
return 0;

static void WriteIco(string path, int[] sizes, List<byte[]> pngs)
{
    // ICONDIR: 6 bytes (reserved=0, type=1, count)
    // ICONDIRENTRY: 16 bytes per image (width, height, colors, reserved, planes, bpp, size, offset)
    using var fs = File.Create(path);
    using var bw = new BinaryWriter(fs);

    bw.Write((ushort)0);                     // reserved
    bw.Write((ushort)1);                     // type: 1 = icon
    bw.Write((ushort)sizes.Length);          // count

    var headerSize = 6 + 16 * sizes.Length;
    var offset = headerSize;
    for (var i = 0; i < sizes.Length; i++)
    {
        var size = sizes[i];
        var data = pngs[i];

        bw.Write((byte)(size >= 256 ? 0 : size));   // width  (0 means 256)
        bw.Write((byte)(size >= 256 ? 0 : size));   // height (0 means 256)
        bw.Write((byte)0);                          // color count (0 = >= 256 colors)
        bw.Write((byte)0);                          // reserved
        bw.Write((ushort)1);                        // color planes
        bw.Write((ushort)32);                       // bits per pixel
        bw.Write((uint)data.Length);                // image size
        bw.Write((uint)offset);                     // offset to image data
        offset += data.Length;
    }

    foreach (var data in pngs) bw.Write(data);
}
