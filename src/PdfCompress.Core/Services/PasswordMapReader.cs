using ClosedXML.Excel;

namespace PdfCompress.Core.Services;

public static class PasswordMapReader
{
    /// <summary>
    /// Reads an Excel file with "FileName" and "Password" columns.
    /// Returns a case-insensitive map keyed by filename without extension.
    /// </summary>
    public static Dictionary<string, string> Read(string excelPath)
    {
        using var workbook = new XLWorkbook(excelPath);
        var sheet = workbook.Worksheets.First();

        int fileNameCol = -1, passwordCol = -1;

        var headerRow = sheet.Row(1);
        foreach (var cell in headerRow.CellsUsed())
        {
            var header = cell.GetString().Trim();
            if (header.Equals("FileName", StringComparison.OrdinalIgnoreCase))
                fileNameCol = cell.Address.ColumnNumber;
            else if (header.Equals("Password", StringComparison.OrdinalIgnoreCase))
                passwordCol = cell.Address.ColumnNumber;
        }

        if (fileNameCol < 0 || passwordCol < 0)
            throw new InvalidOperationException(
                "Excel file must contain 'FileName' and 'Password' columns.");

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in sheet.RowsUsed().Skip(1))
        {
            var fileName = row.Cell(fileNameCol).GetString().Trim();
            var password = row.Cell(passwordCol).GetString().Trim();

            if (string.IsNullOrEmpty(fileName))
                continue;

            // Normalize: strip extension so "Invoice_001.pdf" and "Invoice_001" both match
            var key = Path.GetFileNameWithoutExtension(fileName);

            if (map.ContainsKey(key))
                throw new InvalidOperationException(
                    $"Duplicate entry for '{key}' in password file.");

            map[key] = password;
        }

        return map;
    }
}
