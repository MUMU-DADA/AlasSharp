using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace Alas.Server;

/// <summary>Serves only an explicitly selected, trusted UI publication; never the artifacts or source tree.</summary>
internal sealed class StaticUiFiles
{
    private readonly string _root;
    private readonly string _policy;
    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8", [".js"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8", [".json"] = "application/json; charset=utf-8",
        [".wasm"] = "application/wasm", [".dat"] = "application/octet-stream",
        [".png"] = "image/png", [".svg"] = "image/svg+xml", [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg", [".webp"] = "image/webp", [".ico"] = "image/x-icon",
        [".woff"] = "font/woff", [".woff2"] = "font/woff2", [".ttf"] = "font/ttf",
        [".otf"] = "font/otf", [".txt"] = "text/plain; charset=utf-8",
    };

    public StaticUiFiles(string root)
    {
        _root = Path.GetFullPath(root);
        RejectLinks(_root);
        string index = Path.Combine(_root, "index.html");
        RejectLinks(index);
        if (!File.Exists(index)) throw new ArgumentException("ui-root 缺少 index.html，请指定预构建的 wwwroot");
        // Published .NET import maps are inline scripts. Allow their exact bytes,
        // without allowing arbitrary inline executable JavaScript.
        string html = File.ReadAllText(index);
        var hashes = Regex.Matches(html, "<script\\b[^>]*>(.*?)</script\\s*>",
                RegexOptions.Singleline | RegexOptions.IgnoreCase)
            .Select(match => match.Groups[1].Value).Where(body => body.Length > 0)
            .Select(body => "'sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(body))) + "'");
        _policy = "default-src 'self'; script-src 'self' 'wasm-unsafe-eval' " + string.Join(" ", hashes) +
            "; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self' data:; " +
            "connect-src 'self'; worker-src 'self' blob:; object-src 'none'; base-uri 'self'; frame-ancestors 'none'";
    }

    public async Task<bool> TryServe(HttpContext context)
    {
        var request = context.Request;
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method)) return false;
        string path = request.Path.Value ?? "";
        if (path == "/") path = "/index.html";
        string[] parts = path.Split('/').Skip(1).ToArray();
        if (!path.StartsWith('/') || parts.Length == 0 || parts.Any(part => part.Length == 0 ||
                part.StartsWith('.') || part.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))))
            return false;
        if (!Types.TryGetValue(Path.GetExtension(path), out var contentType)) return false;
        string file = Path.Combine(_root, Path.Combine(parts));
        // Check again on access so links added after startup cannot expose files
        // outside the publication. No directory browsing or SPA fallback for 404s.
        try { RejectLinks(file); }
        catch (ArgumentException) { return false; }
        if (!File.Exists(file)) return false;
        context.Response.ContentType = contentType;
        context.Response.ContentLength = new FileInfo(file).Length;
        context.Response.Headers.ContentSecurityPolicy = _policy;
        if (!HttpMethods.IsHead(request.Method))
            await context.Response.SendFileAsync(file, context.RequestAborted);
        return true;
    }

    private static void RejectLinks(string path)
    {
        // Also reject a redirected parent of ui-root, on Windows and Unix alike.
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("UI 静态目录及文件不能使用链接");
    }
}
