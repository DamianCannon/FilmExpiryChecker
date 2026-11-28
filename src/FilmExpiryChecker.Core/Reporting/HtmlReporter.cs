namespace FilmExpiryChecker.Reporting;

public class HtmlReporter
{
    /// <summary>Writes the report and returns the path of the file written.</summary>
    public string Write(List<FilmResult> results, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HHmm");
        var path = Path.Combine(outputDir, $"film-expiry-{timestamp}.html");

        var grouped = results.GroupBy(r => r.Category).OrderBy(g => g.Key);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(HtmlHeader(results, timestamp));

        foreach (var group in grouped)
        {
            var categoryFilms = group.ToList();
            sb.AppendLine(RenderSection(group.Key, categoryFilms));
        }

        sb.AppendLine(HtmlFooter());
        Storage.SafeFile.WriteAllText(path, sb.ToString());
        return path;
    }

    private static string HtmlHeader(List<FilmResult> results, string timestamp)
    {
        var leavingSoon = results.Count(r =>
            r.Category is FilmCategory.LeavingSoonMyServices or FilmCategory.LeavingSoonOtherServices);

        var onMyServices = results.Count(r =>
            r.Category is FilmCategory.AvailableMyServices or FilmCategory.LeavingSoonMyServices);

        return $$$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
              <meta charset="UTF-8">
              <title>Film Expiry Report — {{{timestamp}}}</title>
              <style>
                body {{ font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Arial, sans-serif;
                        font-size: 14px; color: #222; max-width: 1100px; margin: 0 auto; padding: 24px; }}
                h1 {{ font-size: 22px; color: #1a1a2e; border-bottom: 2px solid #e63946; padding-bottom: 6px; }}
                h2 {{ font-size: 16px; margin-top: 32px; margin-bottom: 8px; padding: 6px 12px;
                      border-radius: 4px; }}
                h2.cat1 {{ background: #e63946; color: #fff; }}
                h2.cat2 {{ background: #f4a261; color: #fff; }}
                h2.cat3 {{ background: #2a9d8f; color: #fff; }}
                h2.cat4 {{ background: #457b9d; color: #fff; }}
                h2.cat5 {{ background: #adb5bd; color: #fff; }}
                h2.cat6 {{ background: #dee2e6; color: #555; }}
                .summary {{ background: #f8f9fa; border: 1px solid #dee2e6; border-radius: 6px;
                            padding: 14px 20px; margin-bottom: 28px; font-size: 13px; }}
                .summary span {{ margin-right: 24px; }}
                .urgent {{ color: #e63946; font-weight: 700; }}
                table {{ border-collapse: collapse; width: 100%; margin-bottom: 12px; font-size: 13px; }}
                th {{ background: #343a40; color: #fff; text-align: left; padding: 7px 10px; white-space: nowrap; }}
                td {{ padding: 6px 10px; border-bottom: 1px solid #dee2e6; vertical-align: top; }}
                tr:nth-child(even) td {{ background: #f8f9fa; }}
                .expiry-urgent {{ color: #e63946; font-weight: 600; }}
                .expiry-soon {{ color: #f4a261; font-weight: 600; }}
                .year {{ color: #888; white-space: nowrap; }}
                .title {{ min-width: 150px; }}
                .count {{ font-size: 12px; font-weight: normal; opacity: 0.85; }}
                .nowrap {{ white-space: nowrap; }}
                td a {{ color: inherit; }}
              </style>
            </head>
            <body>
            <h1>🎬 Film Expiry Report</h1>
            <div class="summary">
              <span>Generated: <strong>{{{DateTime.Now:dd MMM yyyy HH:mm}}}</strong></span>
              <span>Total watchlist films: <strong>{{{results.Count}}}</strong></span>
              <span>On your services: <strong>{{{onMyServices}}}</strong></span>
              <span class="urgent">Leaving soon: <strong>{{{leavingSoon}}}</strong></span>
            </div>
            """;
    }

    private static string RenderSection(FilmCategory category, List<FilmResult> films)
    {
        var (cssClass, icon, showExpiry, showOther) = category switch
        {
            FilmCategory.LeavingSoonMyServices    => ("cat1", "🚨", true,  false),
            FilmCategory.LeavingSoonOtherServices => ("cat2", "⚠️",  true,  false),
            FilmCategory.AvailableMyServices      => ("cat3", "✅", false, false),
            FilmCategory.AvailableOtherServices   => ("cat4", "📺", false, true),
            FilmCategory.RentOrBuyOnly            => ("cat5", "💷", false, false),
            FilmCategory.NotOnAnyService          => ("cat5", "❌", false, false),
            FilmCategory.NotFoundOnJustWatch      => ("cat6", "❓", false, false),
            _                                     => ("cat6", "?",  false, false)
        };

        var categoryName = films.First().CategoryDisplay;
        var sb = new System.Text.StringBuilder();

        sb.AppendLine($"""<h2 class="{cssClass}">{icon} {categoryName} <span class="count">({films.Count} films)</span></h2>""");
        sb.AppendLine("<table>");
        sb.Append("<tr><th>Title</th><th>Year</th>");

        if (showExpiry || category is FilmCategory.LeavingSoonMyServices)
            sb.Append("<th>Leaving</th><th>Days left</th>");

        if (category is FilmCategory.LeavingSoonMyServices or FilmCategory.AvailableMyServices)
            sb.Append("<th>Your services</th>");

        if (showOther || category is FilmCategory.LeavingSoonOtherServices)
            sb.Append("<th>Service</th>");

        if (category is FilmCategory.RentOrBuyOnly)
            sb.Append("<th>Rent or buy from</th>");

        sb.AppendLine("</tr>");

        foreach (var film in films)
        {
            sb.Append($"<tr><td class=\"title\"><a href=\"https://letterboxd.com/film/{film.Film.Slug}/\" target=\"_blank\">{HtmlEncode(film.Film.Title)}</a></td>");
            sb.Append($"<td class=\"year\">{film.Film.Year}</td>");

            if (showExpiry || category is FilmCategory.LeavingSoonMyServices)
            {
                var days = film.DaysUntilExpiry;
                var urgencyClass = days.HasValue && days <= 14 ? "expiry-urgent nowrap"
                                 : days.HasValue && days <= 30 ? "expiry-soon nowrap"
                                 : "nowrap";
                sb.Append($"<td class=\"{urgencyClass}\" style=\"white-space:nowrap\">{film.ExpiryDisplay}</td>");
                sb.Append($"<td class=\"{urgencyClass}\" style=\"white-space:nowrap\">{(days switch { null => "", 0 => "last day", _ => $"{days}d" })}</td>");
            }

            if (category is FilmCategory.LeavingSoonMyServices or FilmCategory.AvailableMyServices)
                sb.Append($"<td>{HtmlEncode(film.MyServicesDisplay)}</td>");

            if (showOther || category is FilmCategory.LeavingSoonOtherServices)
                sb.Append($"<td>{HtmlEncode(film.OtherServicesDisplay)}</td>");

            if (category is FilmCategory.RentOrBuyOnly)
                sb.Append($"<td>{HtmlEncode(film.RentOrBuyDisplay)}</td>");

            sb.AppendLine("</tr>");
        }

        sb.AppendLine("</table>");
        return sb.ToString();
    }

    private static string HtmlFooter() => "</body></html>";

    private static string HtmlEncode(string s) =>
        System.Web.HttpUtility.HtmlEncode(s);
}
