using System.Text.RegularExpressions;
using Shouldly;

namespace MyStack.Auth.Tests;

// The browser dance every account page expects: GET the page, scrape the antiforgery token out
// of the form, POST it back with the fields.
internal static partial class PageForms
{
    public static async Task<HttpResponseMessage> SubmitAsync(
        HttpClient client,
        string url,
        Dictionary<string, string> fields,
        CancellationToken cancellationToken = default
    )
    {
        var page = await client.GetAsync(url, cancellationToken);
        page.EnsureSuccessStatusCode();
        var html = await page.Content.ReadAsStringAsync(cancellationToken);

        return await PostAsync(client, url, html, fields, cancellationToken);
    }

    // The split version, for tests that assert on the GET before deciding to post.
    public static async Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        string url,
        string formHtml,
        Dictionary<string, string> fields,
        CancellationToken cancellationToken = default
    )
    {
        var antiforgery = AntiforgeryRegex().Match(formHtml);
        antiforgery.Success.ShouldBeTrue("the page should render an antiforgery token");

        var form = new Dictionary<string, string>(fields)
        {
            ["__RequestVerificationToken"] = antiforgery.Groups[1].Value,
        };

        return await client.PostAsync(url, new FormUrlEncodedContent(form), cancellationToken);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryRegex();
}
