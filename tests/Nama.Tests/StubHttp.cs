using System.Net;
using System.Text;

namespace Nama.Tests;

/// <summary>
/// Serves canned responses so provider mapping can be tested without the network.
/// Payloads used with this are captured from the real APIs, not invented.
/// </summary>
internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<string> RequestedUrls { get; } = [];

    public int CallCount => RequestedUrls.Count;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        RequestedUrls.Add(request.RequestUri!.ToString());
        return Task.FromResult(respond(request));
    }

    /// <summary>Matches request URLs against substrings, in order. Unmatched requests 404.</summary>
    public static StubHandler ForUrls(params (string Fragment, string Json)[] routes) =>
        new(request =>
        {
            var url = request.RequestUri!.ToString();

            foreach (var (fragment, json) in routes)
            {
                if (url.Contains(fragment, StringComparison.OrdinalIgnoreCase)) return Json(json);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

    public static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    public static HttpResponseMessage Status(HttpStatusCode code) => new(code);

    public static HttpClient Client(HttpMessageHandler handler) => new(handler);
}
