using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Shared;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace HomeMedia;

// Fixed loopback targets only: never an arbitrary URL supplied by the browser.
public class ServiceUiController : BaseController
{
    static readonly HttpClient http = new(new SocketsHttpHandler {
        UseProxy = false, UseCookies = false, AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(8), PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    }) { Timeout = Timeout.InfiniteTimeSpan };
    static readonly string[] Hop = { "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization", "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Host", "Content-Length", "Content-Encoding", "Set-Cookie" };
    public static int Port(string service) => service switch { "channel-manager" => 3002, "torrserver" => 8090, "tvheadend" => 9981, _ => 0 };
    static string Prefix(string service) => "/home/services/" + service + "/";
    public static string Rewrite(string text, string contentType, string prefix, string requestPath = null)
    {
        // Relative resources use the mounted path; root attributes and CSS URLs
        // are scoped too. Filesystem paths and API response data are untouched.
        if (contentType.Contains("text/css"))
            return Regex.Replace(text, @"url\(\s*(['""']?)(?<u>/[^)'""']*)", m => {
                string u = m.Groups["u"].Value;
                if (u.StartsWith("//", StringComparison.Ordinal) || u.StartsWith(prefix, StringComparison.Ordinal)) return m.Value;
                return "url(" + m.Groups[1].Value + prefix + u.TrimStart('/');
            }, RegexOptions.IgnoreCase);
        if (!contentType.Contains("text/html")) return text;
        // Keep the upstream <base> semantics: only prefix the existing href, and
        // add a fallback base only when the page has no href at all. Inserting a
        // new base before the existing one makes the browser ignore the real one.
        text = EnsureBase(text, prefix, requestPath);
        text = Regex.Replace(text, @"(?<a>\b(?:src|href|action|poster)\s*=\s*['""])(?<u>/[^'""]*)", m => {
            string u = m.Groups["u"].Value;
            if (u.StartsWith("//", StringComparison.Ordinal) || u.StartsWith(prefix, StringComparison.Ordinal)) return m.Value;
            return m.Groups["a"].Value + prefix + u.TrimStart('/');
        }, RegexOptions.IgnoreCase);
        string script = @"<script>(function(){
var base=__BASE__;
function map(value){try{var u=new URL(String(value),document.baseURI);if(u.origin===location.origin&&u.pathname.indexOf(base)!==0)return base+u.pathname.replace(/^\//,'')+u.search+u.hash;}catch(e){}return value;}
var fetch0=window.fetch;if(fetch0)window.fetch=function(input,opts){if(input instanceof Request){var url=map(input.url);if(url!==input.url)input=new Request(new URL(url,document.baseURI).href,input);}else input=map(input);return fetch0.call(this,input,opts);};
var open=XMLHttpRequest.prototype.open;XMLHttpRequest.prototype.open=function(){var a=Array.prototype.slice.call(arguments);a[1]=map(a[1]);return open.apply(this,a);};
if(window.EventSource){var ES=window.EventSource;window.EventSource=function(url,opts){return new ES(map(url),opts);};window.EventSource.prototype=ES.prototype;window.EventSource.CONNECTING=ES.CONNECTING;window.EventSource.OPEN=ES.OPEN;window.EventSource.CLOSED=ES.CLOSED;}
if(window.WebSocket){var WS=window.WebSocket;window.WebSocket=function(url,protocols){var u=new URL(url,location.href);if(u.host===location.host&&u.pathname.indexOf(base)!==0)u.pathname=base+u.pathname.replace(/^\//,'');return protocols===undefined?new WS(u.href):new WS(u.href,protocols);};window.WebSocket.prototype=WS.prototype;['CONNECTING','OPEN','CLOSING','CLOSED'].forEach(function(k){window.WebSocket[k]=WS[k];});}
function scope(node){if(!node||node.nodeType!==1)return;['src','href','action','poster'].forEach(function(k){var v=node.getAttribute(k);if(v&&v.charAt(0)==='/'&&v.charAt(1)!=='/'&&v.indexOf(base)!==0)node.setAttribute(k,base+v.slice(1));});}
document.addEventListener('click',function(e){var a=e.target.closest&&e.target.closest('a');if(a)scope(a);},true);
new MutationObserver(function(events){events.forEach(function(e){if(e.type==='attributes')scope(e.target);else Array.prototype.forEach.call(e.addedNodes,function(n){scope(n);if(n.querySelectorAll)Array.prototype.forEach.call(n.querySelectorAll('[src],[href],[action],[poster]'),scope);});});}).observe(document.documentElement,{subtree:true,childList:true,attributes:true,attributeFilter:['src','href','action','poster']});
})();</script>".Replace("__BASE__", Newtonsoft.Json.JsonConvert.SerializeObject(prefix));
        return InsertBeforeHeadEnd(text, script);
    }

    static readonly Regex BaseTagRx = new(@"<base\b[^>]*>", RegexOptions.IgnoreCase);
    static readonly Regex HrefQuotedRx = new(@"(\bhref\s*=\s*)(['""])(.*?)\2", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    static readonly Regex HrefUnquotedRx = new(@"(\bhref\s*=\s*)([^\s>]+)", RegexOptions.IgnoreCase);

    static string EnsureBase(string text, string prefix, string requestPath)
    {
        var match = BaseTagRx.Match(text);
        if (!match.Success)
            return InsertAfterHead(text, "<base href='" + WebUtility.HtmlEncode(prefix) + "'>");
        string tag = match.Value;
        var quoted = HrefQuotedRx.Match(tag);
        if (quoted.Success)
        {
            string mapped = MapBase(quoted.Groups[3].Value, prefix, requestPath);
            if (mapped != null && mapped != quoted.Groups[3].Value)
                tag = tag.Substring(0, quoted.Index) + quoted.Groups[1].Value + quoted.Groups[2].Value
                    + WebUtility.HtmlEncode(mapped) + quoted.Groups[2].Value + tag.Substring(quoted.Index + quoted.Length);
        }
        else
        {
            var unquoted = HrefUnquotedRx.Match(tag);
            if (unquoted.Success)
            {
                string mapped = MapBase(unquoted.Groups[2].Value, prefix, requestPath);
                if (mapped != null && mapped != unquoted.Groups[2].Value)
                    tag = tag.Substring(0, unquoted.Index) + unquoted.Groups[1].Value + "'"
                        + WebUtility.HtmlEncode(mapped) + "'" + tag.Substring(unquoted.Index + unquoted.Length);
            }
            else
            {
                int close = tag.EndsWith("/>", StringComparison.Ordinal) ? tag.Length - 2 : tag.Length - 1;
                tag = tag.Insert(close, " href='" + WebUtility.HtmlEncode(prefix) + "'");
            }
        }
        return text.Substring(0, match.Index) + tag + text.Substring(match.Index + match.Length);
    }

    static string MapBase(string raw, string prefix, string requestPath)
    {
        string href = WebUtility.HtmlDecode(raw ?? "").Trim();
        if (href.Length == 0) return prefix;
        if (href.StartsWith("//", StringComparison.Ordinal)) return null;
        if (Uri.TryCreate(href, UriKind.Absolute, out var absolute) &&
            (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps)) return null;
        if (href.StartsWith(prefix, StringComparison.Ordinal)) return href;
        string path;
        if (href.StartsWith("/", StringComparison.Ordinal)) path = href;
        else
        {
            string page = string.IsNullOrEmpty(requestPath) ? "/" : requestPath;
            if (!page.StartsWith("/", StringComparison.Ordinal)) page = "/" + page;
            if (!page.EndsWith("/", StringComparison.Ordinal))
            {
                int slash = page.LastIndexOf('/');
                page = slash >= 0 ? page.Substring(0, slash + 1) : "/";
            }
            if (!Uri.TryCreate(new Uri("http://service" + page), href, out var resolved)) return null;
            path = resolved.PathAndQuery + resolved.Fragment;
        }
        if (path.StartsWith("//", StringComparison.Ordinal)) return null;
        return prefix.TrimEnd('/') + (path.StartsWith("/", StringComparison.Ordinal) ? path : "/" + path);
    }

    static string InsertAfterHead(string text, string markup)
    {
        var head = Regex.Match(text, @"<head\b[^>]*>", RegexOptions.IgnoreCase);
        return head.Success ? text.Insert(head.Index + head.Length, markup) : markup + text;
    }

    static string InsertBeforeHeadEnd(string text, string markup)
    {
        var end = Regex.Match(text, @"</head\s*>", RegexOptions.IgnoreCase);
        return end.Success ? text.Insert(end.Index, markup) : text + markup;
    }
    [AllowAnonymous, Route("home/services/{service}/{**path}")]
    [AcceptVerbs("GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS")]
    public async Task<IActionResult> Open(string service, string path = null)
    {
        if (!Guard.Ok(Request) || Profiles.FromRequest(HttpContext)?.Admin != true) return StatusCode(403);
        int port = Port(service); if (port == 0) return NotFound();
        string prefix = Prefix(service);
        if (Request.Path.Value == prefix.TrimEnd('/')) return Redirect(prefix + Request.QueryString);
        string targetPath = "/" + (path ?? "");
        var uri = new UriBuilder("http", "127.0.0.1", port) { Path = targetPath, Query = Request.QueryString.Value?.TrimStart('?') }.Uri;
        if (HttpContext.WebSockets.IsWebSocketRequest) {
            using var upstream = new ClientWebSocket();
            foreach (string name in new[] { "Authorization", "Cookie" }) {
                string value = name == "Cookie" ? Cookies(Request.Headers[name]) : Request.Headers[name].ToString();
                if (value.Length > 0) upstream.Options.SetRequestHeader(name, value);
            }
            foreach (string protocol in HttpContext.WebSockets.WebSocketRequestedProtocols) upstream.Options.AddSubProtocol(protocol);
            try {
                await upstream.ConnectAsync(new UriBuilder(uri) { Scheme = "ws" }.Uri, HttpContext.RequestAborted);
                using var downstream = await HttpContext.WebSockets.AcceptWebSocketAsync(upstream.SubProtocol);
                using var cancel = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
                async Task Pump(WebSocket source, WebSocket destination) {
                    var buffer = new byte[32768];
                    while (!cancel.IsCancellationRequested) {
                        var item = await source.ReceiveAsync(new ArraySegment<byte>(buffer), cancel.Token);
                        if (item.MessageType == WebSocketMessageType.Close) return;
                        await destination.SendAsync(new ArraySegment<byte>(buffer, 0, item.Count), item.MessageType, item.EndOfMessage, cancel.Token);
                    }
                }
                var tasks = new[] { Pump(upstream, downstream), Pump(downstream, upstream) };
                await Task.WhenAny(tasks); cancel.Cancel();
                try { await Task.WhenAll(tasks); } catch (OperationCanceledException) { }
            } catch { if (!Response.HasStarted) Response.StatusCode = 502; }
            return new EmptyResult();
        }
        using var request = new HttpRequestMessage(new HttpMethod(Request.Method), uri);
        if (Request.ContentLength > 0 || Request.Headers.ContainsKey("Transfer-Encoding")) {
            request.Content = new StreamContent(Request.Body);
            if (Request.ContentLength.HasValue) request.Content.Headers.ContentLength = Request.ContentLength;
        }
        foreach (var header in Request.Headers) {
            if (Hop.Contains(header.Key, StringComparer.OrdinalIgnoreCase) || header.Key.StartsWith("X-Home-", StringComparison.OrdinalIgnoreCase) || header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase) || header.Key.Equals("Origin", StringComparison.OrdinalIgnoreCase) || header.Key.Equals("Referer", StringComparison.OrdinalIgnoreCase)) continue;
            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray())) request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }
        string cookies = Cookies(Request.Headers["Cookie"]);
        if (cookies.Length > 0) request.Headers.TryAddWithoutValidation("Cookie", cookies);
        request.Headers.TryAddWithoutValidation("X-Forwarded-Prefix", prefix.TrimEnd('/'));
        request.Headers.TryAddWithoutValidation("Origin", uri.GetLeftPart(UriPartial.Authority));
        try {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, HttpContext.RequestAborted);
            Response.StatusCode = (int)response.StatusCode;
            foreach (var header in response.Headers.Concat(response.Content.Headers)) {
                if (Hop.Contains(header.Key, StringComparer.OrdinalIgnoreCase) || header.Key.Equals("Location", StringComparison.OrdinalIgnoreCase) || header.Key.Equals("Content-Security-Policy", StringComparison.OrdinalIgnoreCase)) continue;
                Response.Headers[header.Key] = header.Value.ToArray();
            }
            if (response.Headers.Location != null) {
                var location = new Uri(uri, response.Headers.Location);
                Response.Headers["Location"] = location.Authority == uri.Authority ? prefix.TrimEnd('/') + location.PathAndQuery + location.Fragment : location.AbsoluteUri;
            }
            if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
                foreach (string cookie in setCookies) {
                    string scoped = Regex.IsMatch(cookie, @";\s*Path=", RegexOptions.IgnoreCase)
                        ? Regex.Replace(cookie, @"(;\s*Path=)[^;]*", "$1" + prefix, RegexOptions.IgnoreCase) : cookie + "; Path=" + prefix;
                    scoped = Regex.Replace(scoped, @";\s*Domain=[^;]*", "", RegexOptions.IgnoreCase);
                    Response.Headers.Append("Set-Cookie", scoped);
                }
            string type = response.Content.Headers.ContentType?.MediaType ?? "";
            bool rewrite = type.Contains("text/html") || type.Contains("text/css");
            Response.Headers["Cache-Control"] = "no-store";
            if (HttpMethods.IsHead(Request.Method)) return new EmptyResult();
            if (rewrite) {
                string text = Rewrite(await response.Content.ReadAsStringAsync(HttpContext.RequestAborted), type, prefix, targetPath);
                Response.Headers.Remove("ETag"); Response.Headers.Remove("Content-MD5");
                await Response.WriteAsync(text, Encoding.UTF8, HttpContext.RequestAborted);
            } else {
                Response.ContentLength = response.Content.Headers.ContentLength;
                await response.Content.CopyToAsync(Response.Body, HttpContext.RequestAborted);
            }
        } catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested) { }
        catch { if (Response.HasStarted) HttpContext.Abort(); else Response.StatusCode = 502; }
        return new EmptyResult();
    }
    static string Cookies(string raw) => string.Join("; ", (raw ?? "").Split(';').Select(s => s.Trim()).Where(s => !s.StartsWith("home_", StringComparison.OrdinalIgnoreCase) && s.Length > 0));
}
