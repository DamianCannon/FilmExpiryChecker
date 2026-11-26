using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;

namespace FilmExpiryChecker.Reporting;

public class CsvReporter
{
    /// <summary>Writes the report and returns the path of the file written.</summary>
    public string Write(List<FilmResult> results, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HHmm");
        var path = Path.Combine(outputDir, $"film-expiry-{timestamp}.csv");

        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true
        };

        using var writer = new StreamWriter(path);
        using var csv = new CsvWriter(writer, config);

        csv.WriteHeader<CsvRow>();
        csv.NextRecord();

        foreach (var r in results)
        {
            csv.WriteRecord(new CsvRow
            {
                Title = r.Film.Title,
                Year = r.Film.Year?.ToString() ?? "",
                Category = r.CategoryDisplay,
                MyServices = r.MyServicesDisplay,
                OtherServices = r.OtherServicesDisplay,
                ExpiryDate = r.ExpiryDisplay,
                DaysRemaining = r.DaysUntilExpiry?.ToString() ?? ""
            });
            csv.NextRecord();
        }

        return path;
    }

    private class CsvRow
    {
        public string Title { get; set; } = string.Empty;
        public string Year { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string MyServices { get; set; } = string.Empty;
        public string OtherServices { get; set; } = string.Empty;
        public string ExpiryDate { get; set; } = string.Empty;
        public string DaysRemaining { get; set; } = string.Empty;
    }
}
