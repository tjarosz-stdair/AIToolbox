using System.Globalization;
using System.Text;
using AIToolbox.Ductwork.Contracts;

namespace AIToolbox.Ductwork.Tests.Fixtures;

/// <summary>
/// A minimal, dependency-free PDF writer for CAD-style test pages: stroked lines, rectangles and
/// triangles as vector paths, and either real text (Helvetica) or "outlined" text drawn as filled
/// shapes with no text objects, which is what a CAD export with outlined text looks like.
///
/// Drawing coordinates are the unrotated page with the origin at the TOP-left and y increasing
/// downward, which is how people reason about a sheet. <see cref="ToCanonical"/> converts a box to
/// the canonical space used by the rest of the system (plan section 6.10): after applying /Rotate,
/// origin top-left. Keeping that conversion in one tested place is the point of the fixtures.
/// </summary>
public sealed class SyntheticPdf
{
    private readonly StringBuilder _content = new();
    private readonly double _width;
    private readonly double _height;
    private readonly int _rotate;

    public SyntheticPdf(double width, double height, int rotate = 0)
    {
        if (rotate is not (0 or 90 or 180 or 270)) throw new ArgumentOutOfRangeException(nameof(rotate));
        _width = width;
        _height = height;
        _rotate = rotate;
    }

    public double Width => _width;
    public double Height => _height;
    public int Rotate => _rotate;

    /// <summary>Size of the page as displayed, after /Rotate.</summary>
    public (double Width, double Height) DisplayedSize => _rotate is 90 or 270 ? (_height, _width) : (_width, _height);

    public int TextObjectCount { get; private set; }

    public void Line(double x0, double y0, double x1, double y1, double lineWidth = 0.5)
    {
        _content.Append(F(lineWidth)).Append(" w ")
            .Append(F(x0)).Append(' ').Append(F(_height - y0)).Append(" m ")
            .Append(F(x1)).Append(' ').Append(F(_height - y1)).Append(" l S\n");
    }

    public void Rect(double x0, double y0, double x1, double y1, bool fill = false)
    {
        _content.Append(F(x0)).Append(' ').Append(F(_height - y1)).Append(' ')
            .Append(F(x1 - x0)).Append(' ').Append(F(y1 - y0)).Append(" re ").Append(fill ? "f" : "S").Append('\n');
    }

    public void Triangle(double cx, double cy, double radius)
    {
        _content.Append(F(cx)).Append(' ').Append(F(_height - (cy - radius))).Append(" m ")
            .Append(F(cx - radius)).Append(' ').Append(F(_height - (cy + radius * 0.6))).Append(" l ")
            .Append(F(cx + radius)).Append(' ').Append(F(_height - (cy + radius * 0.6))).Append(" l h S\n");
    }

    /// <summary>Real, extractable text. (x, y) is the top-left of the text box.</summary>
    public BoundingBox Text(double x, double y, string text, double size)
    {
        var box = TextBox(x, y, text, size);
        // Baseline sits about 0.2 em above the bottom of the box.
        _content.Append("BT /F1 ").Append(F(size)).Append(" Tf ")
            .Append(F(x)).Append(' ').Append(F(_height - (y + size * 0.8))).Append(" Td (")
            .Append(Escape(text)).Append(") Tj ET\n");
        TextObjectCount++;
        return box;
    }

    /// <summary>The same text drawn as filled shapes: it looks identical but nothing can be extracted.</summary>
    public BoundingBox OutlinedText(double x, double y, string text, double size)
    {
        var box = TextBox(x, y, text, size);
        var advance = size * 0.556;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == ' ') continue;
            Rect(x + i * advance + advance * 0.15, y + size * 0.1, x + (i + 1) * advance - advance * 0.15, y + size * 0.8, fill: true);
        }

        return box;
    }

    /// <summary>Converts a box in unrotated top-left drawing space to canonical (rotated, top-left) space.</summary>
    public BoundingBox ToCanonical(BoundingBox b)
    {
        (double X, double Y) Map(double x, double y) => _rotate switch
        {
            90 => (_height - y, x),
            180 => (_width - x, _height - y),
            270 => (y, _width - x),
            _ => (x, y)
        };

        var (ax, ay) = Map(b.X0, b.Y0);
        var (bx, by) = Map(b.X1, b.Y1);
        return new BoundingBox(Math.Min(ax, bx), Math.Min(ay, by), Math.Max(ax, bx), Math.Max(ay, by));
    }

    private static BoundingBox TextBox(double x, double y, string text, double size) =>
        new(x, y, x + text.Length * size * 0.556, y + size);

    public byte[] Build()
    {
        var content = _content.ToString();
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {F(_width)} {F(_height)}] /Rotate {_rotate} /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
        };

        var output = new MemoryStream();
        void Write(string s) { var b = Encoding.Latin1.GetBytes(s); output.Write(b, 0, b.Length); }

        Write("%PDF-1.4\n");
        var offsets = new List<long>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(output.Position);
            Write($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = output.Position;
        Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            Write($"{offset:D10} 00000 n \n");
        Write($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return output.ToArray();
    }

    private static string F(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
}
