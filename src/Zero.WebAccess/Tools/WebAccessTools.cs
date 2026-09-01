using System.ComponentModel;
using System.Net;
using System.ServiceModel.Syndication;
using System.Text;
using System.Xml;
using HtmlAgilityPack;
using ModelContextProtocol.Server;

namespace Zero.WebAccess.Tools;

[McpServerToolType]
public sealed class WebAccessTools
{
    private static readonly HttpClient _http = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        UseCookies             = false,
    })
    {
        Timeout = TimeSpan.FromSeconds(15),
        DefaultRequestHeaders =
        {
            { "User-Agent",      "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36" },
            { "Accept-Language", "en-US,en;q=0.9,id;q=0.8" },
            { "Accept",          "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8" },
        }
    };

    // ─── web_search ───────────────────────────────────────────────────────────

    [McpServerTool, Description(
        "Search the web using DuckDuckGo and return the top results (title + URL + snippet). " +
        "Use this when the user asks to search for news, information, or any topic online.")]
    public static async Task<string> web_search(
        [Description("Search query string (e.g. 'berita terbaru Prabowo', 'latest AI news').")] string query,
        [Description("Maximum number of results to return (default: 5, max: 10).")] int maxResults = 5)
    {
        maxResults = Math.Clamp(maxResults, 1, 10);
        var encoded = Uri.EscapeDataString(query);

        try
        {
            // Scrape DuckDuckGo HTML search results
            var url  = $"https://html.duckduckgo.com/html/?q={encoded}";
            var html = await _http.GetStringAsync(url);

            var htmlDoc = new HtmlDocument();
            htmlDoc.LoadHtml(html);

            // Each result is a <div class="result"> containing:
            //   .result__title  → <a class="result__a">  title + href
            //   .result__snippet → snippet text
            var resultNodes = htmlDoc.DocumentNode
                .SelectNodes("//div[contains(@class,'result') and not(contains(@class,'result--more'))]");

            if (resultNodes is null || resultNodes.Count == 0)
                return $"No results found. Try opening: https://www.google.com/search?q={encoded}";

            var sb    = new StringBuilder();
            var count = 0;

            foreach (var node in resultNodes)
            {
                if (count >= maxResults) break;

                var titleNode   = node.SelectSingleNode(".//a[contains(@class,'result__a')]");
                var snippetNode = node.SelectSingleNode(".//*[contains(@class,'result__snippet')]");

                if (titleNode is null) continue;

                var title   = WebUtility.HtmlDecode(titleNode.InnerText.Trim());
                var href    = titleNode.GetAttributeValue("href", "");
                var snippet = snippetNode is not null
                    ? WebUtility.HtmlDecode(snippetNode.InnerText.Trim())
                    : "";

                // DuckDuckGo href is a redirect — extract actual URL
                if (href.StartsWith("/l/?") || href.StartsWith("//duckduckgo.com/l/"))
                {
                    var uddg = System.Web.HttpUtility.ParseQueryString(
                        href.Contains('?') ? href[(href.IndexOf('?') + 1)..] : href)["uddg"];
                    if (!string.IsNullOrEmpty(uddg))
                        href = Uri.UnescapeDataString(uddg);
                }

                count++;
                sb.AppendLine($"{count}. {title}");
                if (!string.IsNullOrEmpty(href))    sb.AppendLine($"   URL: {href}");
                if (!string.IsNullOrEmpty(snippet)) sb.AppendLine($"   {snippet}");
                sb.AppendLine();
            }

            return count > 0
                ? sb.ToString().TrimEnd()
                : $"No results found. Try opening: https://www.google.com/search?q={encoded}";
        }
        catch (Exception ex)
        {
            return $"Error: web_search failed — {ex.Message}";
        }
    }

    // ─── fetch_page ───────────────────────────────────────────────────────────

    [McpServerTool, Description(
        "Fetch the content of a web page and return it as plain text (HTML stripped). " +
        "Use this to read the content of a specific URL the user wants to know about.")]
    public static async Task<string> fetch_page(
        [Description("Full URL of the page to fetch (e.g. 'https://example.com/article').")] string url,
        [Description("Maximum number of characters to return (default: 4000).")] int maxChars = 4000)
    {
        if (!url.Contains("://"))
            url = "https://" + url;

        try
        {
            var html = await _http.GetStringAsync(url);

            var htmlDoc = new HtmlDocument();
            htmlDoc.LoadHtml(html);

            // Remove script, style, nav, footer, header nodes
            foreach (var node in htmlDoc.DocumentNode
                         .SelectNodes("//script|//style|//nav|//footer|//header|//aside") ?? Enumerable.Empty<HtmlNode>())
                node.Remove();

            // Extract visible text
            var text = htmlDoc.DocumentNode.InnerText;

            // Collapse whitespace
            var lines = text.Split('\n')
                            .Select(l => l.Trim())
                            .Where(l => l.Length > 0);
            var result = string.Join("\n", lines);

            if (result.Length > maxChars)
                result = result[..maxChars] + $"\n\n[...truncated at {maxChars} chars]";

            return result.Length > 0 ? result : "Error: no readable content found on page.";
        }
        catch (Exception ex)
        {
            return $"Error: fetch_page failed for '{url}' — {ex.Message}";
        }
    }

    // ─── read_rss ─────────────────────────────────────────────────────────────

    [McpServerTool, Description(
        "Fetch and parse an RSS or Atom feed, returning recent articles (title + link + summary + date). " +
        "Use this to get the latest news from a specific news source.")]
    public static async Task<string> read_rss(
        [Description("RSS/Atom feed URL (e.g. 'https://feeds.bbci.co.uk/news/rss.xml', 'https://rss.detik.com/index.php/detikcom').")] string feedUrl,
        [Description("Maximum number of items to return (default: 5, max: 20).")] int maxItems = 5)
    {
        maxItems = Math.Clamp(maxItems, 1, 20);

        if (!feedUrl.Contains("://"))
            feedUrl = "https://" + feedUrl;

        try
        {
            var xml = await _http.GetStringAsync(feedUrl);

            using var reader = XmlReader.Create(new StringReader(xml));
            var feed = SyndicationFeed.Load(reader);

            var sb    = new StringBuilder();
            var count = 0;

            sb.AppendLine($"=== {feed.Title?.Text ?? feedUrl} ===");
            sb.AppendLine();

            foreach (var item in feed.Items.Take(maxItems))
            {
                count++;
                var title   = item.Title?.Text ?? "(no title)";
                var link    = item.Links.FirstOrDefault()?.Uri?.ToString() ?? "";
                var summary = item.Summary?.Text ?? "";
                var date    = item.PublishDate != DateTimeOffset.MinValue
                    ? item.PublishDate.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                    : "";

                // Strip HTML from summary
                if (!string.IsNullOrEmpty(summary))
                {
                    var hdoc = new HtmlDocument();
                    hdoc.LoadHtml(summary);
                    summary = hdoc.DocumentNode.InnerText.Trim();
                    if (summary.Length > 200) summary = summary[..200] + "...";
                }

                sb.AppendLine($"{count}. {title}");
                if (!string.IsNullOrEmpty(date))    sb.AppendLine($"   Date: {date}");
                if (!string.IsNullOrEmpty(link))    sb.AppendLine($"   URL:  {link}");
                if (!string.IsNullOrEmpty(summary)) sb.AppendLine($"   {summary}");
                sb.AppendLine();
            }

            return count == 0
                ? "No items found in feed."
                : sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            return $"Error: read_rss failed for '{feedUrl}' — {ex.Message}";
        }
    }
}
