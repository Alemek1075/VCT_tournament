using Microsoft.EntityFrameworkCore;

namespace VctHub.Web.Models;

public class PagedList<T>
{
    public required List<T> Items { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int Total { get; init; }
    public int Pages => Math.Max(1, (int)Math.Ceiling(Total / (double)PageSize));
    public string? Query { get; init; }

    public static async Task<PagedList<T>> CreateAsync(IQueryable<T> source, int page, int pageSize, string? query = null)
    {
        page = Math.Max(1, page);
        var total = await source.CountAsync();
        var items = await source.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
        return new PagedList<T> { Items = items, Page = page, PageSize = pageSize, Total = total, Query = query };
    }
}
