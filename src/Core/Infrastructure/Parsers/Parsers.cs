using System.Data;
using System.Globalization;
using CsvHelper;

namespace QRD.Core.Infrastructure.Parsers;

// ─── CSV Parser ───────────────────────────────────────────────────────────────

/// <summary>
/// Parses CSV files: extracts headers, sample rows and basic stats.
/// Equivalent to Python's csv_parser.py.
/// </summary>
public class CsvParser
{
    public CsvParseResult Parse(string filePath, int maxRows = 100)
    {
        try
        {
            using var reader = new StreamReader(filePath);
            using var csv    = new CsvReader(reader, CultureInfo.InvariantCulture);

            csv.Read();
            csv.ReadHeader();
            var headers = csv.HeaderRecord ?? [];

            var rows     = new List<Dictionary<string, string>>(maxRows);
            var rowCount = 0;

            while (csv.Read() && rowCount < maxRows)
            {
                var row = new Dictionary<string, string>(headers.Length);
                foreach (var h in headers)
                    row[h] = csv.GetField(h) ?? "";
                rows.Add(row);
                rowCount++;
            }

            return new CsvParseResult { Headers = headers, Rows = rows,
                                        RowCount = rowCount, FilePath = filePath };
        }
        catch (Exception ex)
        {
            return new CsvParseResult { Error = ex.Message, FilePath = filePath };
        }
    }
}

public class CsvParseResult
{
    public string   FilePath { get; init; } = "";
    public string[] Headers  { get; init; } = [];
    public List<Dictionary<string, string>> Rows { get; init; } = [];
    public int      RowCount { get; init; }
    public string?  Error    { get; init; }
}

// ─── Excel Parser ─────────────────────────────────────────────────────────────

/// <summary>
/// Parses .xlsx / .xls files into sheet previews.
/// Equivalent to Python's excel_parser.py.
/// </summary>
public class ExcelParser
{
    public ExcelParseResult Parse(string filePath, int maxRowsPerSheet = 50)
    {
        try
        {
            System.Text.Encoding.RegisterProvider(
                System.Text.CodePagesEncodingProvider.Instance);

            using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read);
            using var reader = ExcelDataReader.ExcelReaderFactory.CreateReader(stream);

            var dataSet = reader.AsDataSet();
            var sheets  = new List<SheetPreview>(dataSet.Tables.Count);

            foreach (DataTable table in dataSet.Tables)
            {
                var headers = table.Columns.Cast<DataColumn>()
                                   .Select(c => c.ColumnName).ToArray();

                var rows = new List<string[]>(Math.Min(table.Rows.Count, maxRowsPerSheet));
                for (var i = 0; i < Math.Min(table.Rows.Count, maxRowsPerSheet); i++)
                    rows.Add(table.Rows[i].ItemArray
                                  .Select(o => o?.ToString() ?? "").ToArray());

                sheets.Add(new SheetPreview(table.TableName, headers, rows, table.Rows.Count));
            }

            return new ExcelParseResult { Sheets = sheets, FilePath = filePath };
        }
        catch (Exception ex)
        {
            return new ExcelParseResult { Error = ex.Message, FilePath = filePath };
        }
    }
}

public class ExcelParseResult
{
    public string           FilePath { get; init; } = "";
    public List<SheetPreview> Sheets { get; init; } = [];
    public string?          Error    { get; init; }
}

public record SheetPreview(
    string         Name,
    string[]       Headers,
    List<string[]> Rows,
    int            TotalRows);

// ─── Image Parser ─────────────────────────────────────────────────────────────

/// <summary>
/// Returns image dimensions and format for embedding in PDFs.
/// Equivalent to Python's image_parser.py.
/// </summary>
public class ImageParser
{
    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp" };

    public bool CanParse(string extension) => Supported.Contains(extension);

    public ImageMeta GetMetadata(string filePath)
    {
        try
        {
            using var img = System.Drawing.Image.FromFile(filePath);
            return new ImageMeta
            {
                FilePath = filePath,
                Width    = img.Width,
                Height   = img.Height,
                Format   = img.RawFormat.ToString()
            };
        }
        catch (Exception ex)
        {
            return new ImageMeta { FilePath = filePath, Error = ex.Message };
        }
    }
}

public class ImageMeta
{
    public string  FilePath { get; init; } = "";
    public int     Width    { get; init; }
    public int     Height   { get; init; }
    public string  Format   { get; init; } = "";
    public string? Error    { get; init; }
}

// ─── Code Parser ─────────────────────────────────────────────────────────────

/// <summary>
/// Reads source-code files and splits them into lines for PDF embedding.
/// Equivalent to Python's code_parser.py.
/// </summary>
public class CodeParser
{
    private const int MaxBytes = 10 * 1024 * 1024; // 10 MB hard cap

    public CodeParseResult Parse(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            if (info.Length > MaxBytes)
                return new CodeParseResult
                {
                    FilePath  = filePath,
                    Content   = "[File too large — truncated]",
                    Truncated = true
                };

            var content = File.ReadAllText(filePath);
            var lines   = content.Split('\n');

            return new CodeParseResult
            {
                FilePath  = filePath,
                Content   = content,
                Lines     = lines,
                LineCount = lines.Length
            };
        }
        catch (Exception ex)
        {
            return new CodeParseResult { FilePath = filePath, Error = ex.Message };
        }
    }
}

public class CodeParseResult
{
    public string   FilePath  { get; init; } = "";
    public string   Content   { get; init; } = "";
    public string[] Lines     { get; init; } = [];
    public int      LineCount { get; init; }
    public bool     Truncated { get; init; }
    public string?  Error     { get; init; }
}
