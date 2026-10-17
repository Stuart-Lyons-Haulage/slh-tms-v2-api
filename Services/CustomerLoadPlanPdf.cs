using System.Globalization;
using System.Text;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Small dependency-free PDF writer for the daily customer load plan.
/// It deliberately uses PDF core fonts only so the operational export does not
/// depend on a browser renderer or a commercial PDF package in production.
/// </summary>
public static class CustomerLoadPlanPdf
{
    private const double PageWidth = 1190.55; // A3 landscape
    private const double PageHeight = 841.89;
    private const double Margin = 32;
    private const double RowHeight = 23;
    private const int RowsPerPage = 27;

    private static readonly (string Header, double Width)[] Columns =
    [
        ("Load", 58),
        ("Collection site", 146),
        ("Delivery destination", 156),
        ("Reference", 102),
        ("Pallets", 48),
        ("Collect", 62),
        ("Deadline", 66),
        ("Driver", 104),
        ("Vehicle", 70),
        ("Trailer", 60),
        ("Status / notes", 196)
    ];

    public static byte[] Build(CustomerLoadPlanPreview plan)
    {
        var pages = plan.Rows.Chunk(RowsPerPage).ToList();
        if (pages.Count == 0) pages.Add([]);

        var objects = new List<byte[]>();
        var pageIds = Enumerable.Range(0, pages.Count).Select(index => 5 + index * 2).ToArray();
        objects.Add(Ascii("<< /Type /Catalog /Pages 2 0 R >>"));
        objects.Add(Ascii($"<< /Type /Pages /Kids [{string.Join(' ', pageIds.Select(id => $"{id} 0 R"))}] /Count {pages.Count} >>"));
        objects.Add(Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"));
        objects.Add(Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>"));

        for (var pageIndex = 0; pageIndex < pages.Count; pageIndex++)
        {
            var pageObjectId = 5 + pageIndex * 2;
            var contentObjectId = pageObjectId + 1;
            var content = BuildPage(plan, pages[pageIndex], pageIndex + 1, pages.Count);
            var contentBytes = Encoding.ASCII.GetBytes(content);
            objects.Add(Ascii($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {N(PageWidth)} {N(PageHeight)}] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {contentObjectId} 0 R >>"));
            objects.Add(Join(Ascii($"<< /Length {contentBytes.Length} >>\nstream\n"), contentBytes, Ascii("\nendstream")));
        }

        using var stream = new MemoryStream();
        Write(stream, Ascii("%PDF-1.4\n%SLH\n"));
        var offsets = new List<long> { 0 };
        for (var index = 0; index < objects.Count; index++)
        {
            offsets.Add(stream.Position);
            Write(stream, Ascii($"{index + 1} 0 obj\n"));
            Write(stream, objects[index]);
            Write(stream, Ascii("\nendobj\n"));
        }

        var xref = stream.Position;
        Write(stream, Ascii($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n"));
        foreach (var offset in offsets.Skip(1))
            Write(stream, Ascii($"{offset:0000000000} 00000 n \n"));
        Write(stream, Ascii($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF"));
        return stream.ToArray();
    }

    private static string BuildPage(CustomerLoadPlanPreview plan, IReadOnlyList<CustomerLoadPlanRow> rows, int page, int pageCount)
    {
        var b = new StringBuilder();
        // White page, dark Lyons header and green accent.
        Fill(b, 0, 0, PageWidth, PageHeight, 1, 1, 1);
        Fill(b, Margin, PageHeight - 104, PageWidth - Margin * 2, 68, 0.035, 0.18, 0.31);
        Fill(b, Margin, PageHeight - 109, PageWidth - Margin * 2, 5, 0.18, 0.55, 0.32);
        Text(b, "F2", 20, Margin + 18, PageHeight - 66, "STUART LYONS HAULAGE", 1, 1, 1);
        Text(b, "F1", 9, Margin + 19, PageHeight - 84, "Operational load plan", 0.86, 0.94, 0.98);
        TextRight(b, "F2", 15, PageWidth - Margin - 18, PageHeight - 65, plan.CustomerName, 1, 1, 1);
        TextRight(b, "F1", 9, PageWidth - Margin - 18, PageHeight - 84, $"Planning date {plan.PlanningDate:dd/MM/yyyy}   |   Page {page} of {pageCount}", 0.86, 0.94, 0.98);

        var metaY = PageHeight - 137;
        Text(b, "F2", 8.5, Margin, metaY, "Carrier", 0.22, 0.28, 0.34);
        Text(b, "F1", 8.5, Margin + 48, metaY, "Stuart Lyons (Haulage) Ltd", 0.10, 0.13, 0.16);
        Text(b, "F2", 8.5, Margin + 260, metaY, "Email", 0.22, 0.28, 0.34);
        Text(b, "F1", 8.5, Margin + 302, metaY, "info@lyonshaulage.com", 0.10, 0.13, 0.16);
        Text(b, "F2", 8.5, Margin + 500, metaY, "Telephone", 0.22, 0.28, 0.34);
        Text(b, "F1", 8.5, Margin + 565, metaY, "01243 555536", 0.10, 0.13, 0.16);
        Text(b, "F2", 8.5, Margin + 710, metaY, "Total pallets", 0.22, 0.28, 0.34);
        Text(b, "F1", 8.5, Margin + 790, metaY, plan.TotalPallets.ToString(CultureInfo.InvariantCulture), 0.10, 0.13, 0.16);

        var tableTop = PageHeight - 166;
        var tableWidth = Columns.Sum(column => column.Width);
        Fill(b, Margin, tableTop - 31, tableWidth, 31, 0.09, 0.24, 0.37);
        var x = Margin;
        foreach (var column in Columns)
        {
            StrokeRect(b, x, tableTop - 31, column.Width, 31, 0.76, 0.80, 0.83, 0.6);
            Text(b, "F2", 7.4, x + 4, tableTop - 19, column.Header, 1, 1, 1);
            x += column.Width;
        }

        var y = tableTop - 31;
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            y -= RowHeight;
            if (rowIndex % 2 == 1) Fill(b, Margin, y, tableWidth, RowHeight, 0.965, 0.975, 0.98);
            x = Margin;
            var values = new[]
            {
                row.LoadReference,
                row.CollectionSite,
                row.DeliverySite,
                row.OrderReference,
                row.Pallets?.ToString(CultureInfo.InvariantCulture) ?? "-",
                row.PlannedCollectTime ?? "-",
                row.DeliveryDeadline ?? "-",
                row.DriverName ?? "Unallocated",
                row.VehicleRegistration ?? "-",
                row.TrailerNumber ?? "-",
                string.Join(" | ", new[] { row.Status, row.Notes }.Where(value => !string.IsNullOrWhiteSpace(value)))
            };
            for (var columnIndex = 0; columnIndex < Columns.Length; columnIndex++)
            {
                var width = Columns[columnIndex].Width;
                StrokeRect(b, x, y, width, RowHeight, 0.80, 0.83, 0.85, 0.45);
                Text(b, columnIndex == 0 ? "F2" : "F1", 7.1, x + 4, y + 8, Clip(values[columnIndex], width, 7.1), 0.10, 0.13, 0.16);
                x += width;
            }
        }

        var footerY = 25d;
        Stroke(b, Margin, footerY + 10, PageWidth - Margin, footerY + 10, 0.82, 0.85, 0.87, 0.5);
        Text(b, "F1", 7.2, Margin, footerY, "Generated from the live SLH TMS plan. Please notify info@lyonshaulage.com of any amendments.", 0.36, 0.40, 0.44);
        TextRight(b, "F1", 7.2, PageWidth - Margin, footerY, DateTimeOffset.UtcNow.ToString("dd/MM/yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture), 0.36, 0.40, 0.44);
        return b.ToString();
    }

    private static string Clip(string? value, double width, double fontSize)
    {
        var clean = Clean(value);
        if (clean.Length == 0) return "-";
        var max = Math.Max(3, (int)Math.Floor((width - 8) / (fontSize * 0.52)));
        return clean.Length <= max ? clean : clean[..Math.Max(1, max - 3)] + "...";
    }

    private static void Text(StringBuilder b, string font, double size, double x, double y, string value, double r, double g, double bl) =>
        b.Append(FormattableString.Invariant($"BT {r:0.###} {g:0.###} {bl:0.###} rg /{font} {size:0.##} Tf {x:0.##} {y:0.##} Td ({Escape(value)}) Tj ET\n"));

    private static void TextRight(StringBuilder b, string font, double size, double x, double y, string value, double r, double g, double bl)
    {
        var clean = Clean(value);
        var estimated = clean.Length * size * 0.52;
        Text(b, font, size, Math.Max(Margin, x - estimated), y, clean, r, g, bl);
    }

    private static void Fill(StringBuilder b, double x, double y, double width, double height, double r, double g, double bl) =>
        b.Append(FormattableString.Invariant($"{r:0.###} {g:0.###} {bl:0.###} rg {x:0.##} {y:0.##} {width:0.##} {height:0.##} re f\n"));

    private static void StrokeRect(StringBuilder b, double x, double y, double width, double height, double r, double g, double bl, double lineWidth) =>
        b.Append(FormattableString.Invariant($"{lineWidth:0.##} w {r:0.###} {g:0.###} {bl:0.###} RG {x:0.##} {y:0.##} {width:0.##} {height:0.##} re S\n"));

    private static void Stroke(StringBuilder b, double x1, double y1, double x2, double y2, double r, double g, double bl, double lineWidth) =>
        b.Append(FormattableString.Invariant($"{lineWidth:0.##} w {r:0.###} {g:0.###} {bl:0.###} RG {x1:0.##} {y1:0.##} m {x2:0.##} {y2:0.##} l S\n"));

    private static string Escape(string? value) => Clean(value).Replace("\\", "\\\\", StringComparison.Ordinal).Replace("(", "\\(", StringComparison.Ordinal).Replace(")", "\\)", StringComparison.Ordinal);
    private static string Clean(string? value) => new string((value ?? string.Empty).Select(character => character is >= ' ' and <= '~' ? character : character is '–' or '—' ? '-' : character is '→' ? '>' : ' ').ToArray()).Replace("  ", " ", StringComparison.Ordinal).Trim();
    private static string N(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    private static byte[] Ascii(string value) => Encoding.ASCII.GetBytes(value);
    private static byte[] Join(params byte[][] arrays) { var length = arrays.Sum(array => array.Length); var result = new byte[length]; var offset = 0; foreach (var array in arrays) { Buffer.BlockCopy(array, 0, result, offset, array.Length); offset += array.Length; } return result; }
    private static void Write(Stream stream, byte[] bytes) => stream.Write(bytes, 0, bytes.Length);
}
