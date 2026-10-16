using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using DnsLoadTester.Core.Engine;

namespace DnsLoadTester.Core.Transport;

/// <summary>DNS over HTTPS (RFC 8484) con metodo GET o POST e versione HTTP selezionabile. HttpClient condiviso.</summary>
public sealed class DohTransport(TransportContext ctx) : DnsTransport
{
    private static readonly MediaTypeHeaderValue DnsMessage = new("application/dns-message");
    private readonly Uri _baseUri = new(ctx.Config.DohUrl);
    private readonly Version _version = ctx.Config.DohHttpVersion switch
    {
        DohHttpVersion.Http11 => HttpVersion.Version11,
        DohHttpVersion.Http3 => HttpVersion.Version30,
        _ => HttpVersion.Version20,
    };

    public override bool RequiresZeroId => true;

    public override async ValueTask<int> ExchangeAsync(ReadOnlyMemory<byte> request, ushort id, Memory<byte> response, CancellationToken ct)
    {
        using var req = new HttpRequestMessage { Version = _version, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
        if (ctx.Config.DohMethod == DohMethod.Get)
        {
            var b64 = Convert.ToBase64String(request.Span).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var ub = new UriBuilder(_baseUri);
            ub.Query = string.IsNullOrEmpty(ub.Query) || ub.Query == "?" ? "dns=" + b64 : ub.Query.TrimStart('?') + "&dns=" + b64;
            req.Method = HttpMethod.Get;
            req.RequestUri = ub.Uri;
        }
        else
        {
            req.Method = HttpMethod.Post;
            req.RequestUri = _baseUri;
            req.Content = new ReadOnlyMemoryContent(request);
            req.Content.Headers.ContentType = DnsMessage;
        }
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-message"));

        HttpResponseMessage resp;
        try
        {
            resp = await ctx.Http!.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            var kind = ex.HttpRequestError == HttpRequestError.SecureConnectionError ? TransportErrorKind.Tls : TransportErrorKind.Network;
            throw new DnsTransportException(kind, $"{ex.HttpRequestError}: {ex.Message}", ex);
        }

        using (resp)
        {
            if (resp.StatusCode != HttpStatusCode.OK)
                throw new DnsTransportException(TransportErrorKind.Http, $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
            await using var body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            int total = 0;
            while (true)
            {
                if (total == response.Length) throw new DnsTransportException(TransportErrorKind.Protocol, L.T("Risposta DoH troppo grande"));
                int n = await body.ReadAsync(response[total..], ct).ConfigureAwait(false);
                if (n == 0) break;
                total += n;
            }
            if (total < 12) throw new DnsTransportException(TransportErrorKind.Protocol, L.F("Corpo DoH troppo corto ({0} byte)", total));
            return total;
        }
    }
}
