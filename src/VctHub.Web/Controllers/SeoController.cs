using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VctHub.Web.Data;

namespace VctHub.Web.Controllers;

/// <summary>robots.txt and sitemap.xml for search engines (C7).</summary>
public class SeoController(AppDbContext db) : Controller
{
    string Base => $"{Request.Scheme}://{Request.Host}";

    [HttpGet("/robots.txt")]
    public ContentResult Robots() => Content($"""
        User-agent: *
        Allow: /
        Disallow: /api/
        Disallow: /*/Create
        Disallow: /*/Edit/
        Disallow: /*/Delete/
        Sitemap: {Base}/sitemap.xml
        """, "text/plain", Encoding.UTF8);

    [HttpGet("/sitemap.xml")]
    [ResponseCache(Duration = 3600)]
    public async Task<ContentResult> Sitemap()
    {
        XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
        XElement Url(string path, string freq, string priority) => new(ns + "url",
            new XElement(ns + "loc", Base + path),
            new XElement(ns + "changefreq", freq),
            new XElement(ns + "priority", priority));

        var urls = new List<XElement>
        {
            Url("/", "hourly", "1.0"), Url("/Matches", "hourly", "0.8"), Url("/Teams", "weekly", "0.8"),
            Url("/Players", "daily", "0.7"), Url("/Tournaments", "weekly", "0.7"),
        };
        urls.AddRange((await db.Teams.Select(t => t.Slug).ToListAsync()).Select(s => Url($"/team/{s}", "daily", "0.9")));
        urls.AddRange((await db.Tournaments.Select(t => t.Id).ToListAsync()).Select(id => Url($"/Tournaments/Details/{id}", "daily", "0.6")));
        urls.AddRange((await db.Players.Select(p => p.Id).ToListAsync()).Select(id => Url($"/Players/Details/{id}", "weekly", "0.5")));

        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement(ns + "urlset", urls));
        return Content(doc.Declaration + "\n" + doc.Root, "application/xml", Encoding.UTF8);
    }
}
