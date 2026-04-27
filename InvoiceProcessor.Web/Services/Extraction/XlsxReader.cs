using System.IO.Compression;
using System.Xml.Linq;

namespace InvoiceProcessor.Web.Services.Extraction;

/// Minimal zero-dependency XLSX reader. Returns rows as arrays of nullable strings,
/// where index 0 = column A, 1 = column B, etc.
public static class XlsxReader
{
    private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    public static List<string?[]> ReadRows(Stream xlsxStream)
    {
        using var archive = new ZipArchive(xlsxStream, ZipArchiveMode.Read);

        var sharedStrings = new List<string>();
        var ssEntry = archive.GetEntry("xl/sharedStrings.xml");
        if (ssEntry != null)
        {
            using var ss = ssEntry.Open();
            var ssDoc = XDocument.Load(ss);
            foreach (var si in ssDoc.Descendants(Ns + "si"))
                sharedStrings.Add(string.Concat(si.Descendants(Ns + "t").Select(t => t.Value)));
        }

        var sheetEntry = archive.GetEntry("xl/worksheets/sheet1.xml")
            ?? archive.Entries.FirstOrDefault(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase));
        var rows = new List<string?[]>();
        if (sheetEntry == null) return rows;

        using var sheet = sheetEntry.Open();
        var sheetDoc = XDocument.Load(sheet);
        foreach (var row in sheetDoc.Descendants(Ns + "row"))
        {
            var cells = new Dictionary<int, string?>();
            foreach (var c in row.Elements(Ns + "c"))
            {
                var refAttr = (string?)c.Attribute("r");
                if (refAttr == null) continue;
                var typeAttr = (string?)c.Attribute("t") ?? "";
                var colIdx = ColumnLettersToIndex(ExtractLetters(refAttr));

                string? value = null;
                if (typeAttr == "inlineStr")
                {
                    value = string.Concat(c.Descendants(Ns + "t").Select(t => t.Value));
                }
                else
                {
                    var vEl = c.Element(Ns + "v");
                    if (vEl != null)
                    {
                        if (typeAttr == "s" && int.TryParse(vEl.Value, out var si) && si >= 0 && si < sharedStrings.Count)
                            value = sharedStrings[si];
                        else
                            value = vEl.Value;
                    }
                }
                cells[colIdx] = value;
            }

            if (cells.Count == 0) { rows.Add(Array.Empty<string?>()); continue; }
            var maxCol = cells.Keys.Max();
            var arr = new string?[maxCol + 1];
            foreach (var kv in cells) arr[kv.Key] = kv.Value;
            rows.Add(arr);
        }
        return rows;
    }

    private static string ExtractLetters(string cellRef)
    {
        var end = 0;
        while (end < cellRef.Length && char.IsLetter(cellRef[end])) end++;
        return cellRef[..end];
    }

    private static int ColumnLettersToIndex(string letters)
    {
        var idx = 0;
        foreach (var c in letters)
            idx = idx * 26 + (char.ToUpperInvariant(c) - 'A' + 1);
        return idx - 1;
    }
}
