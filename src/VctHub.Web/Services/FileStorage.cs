using System.Net.Http.Headers;

namespace VctHub.Web.Services;

public interface IFileStorage
{
    /// <summary>Stores the file and returns its public URL.</summary>
    Task<string> SaveAsync(IFormFile file, string folder, CancellationToken ct = default);
}

public static class FileRules
{
    static readonly HashSet<string> Allowed = [".png", ".jpg", ".jpeg", ".webp", ".gif", ".mp4", ".webm"]; // no .svg: it can carry script
    public const long MaxBytes = 20 * 1024 * 1024;

    public static string CheckedExtension(IFormFile file)
    {
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!Allowed.Contains(ext)) throw new InvalidOperationException($"File type {ext} is not allowed");
        if (file.Length > MaxBytes) throw new InvalidOperationException("File is larger than 20 MB");
        return ext;
    }
}

/// <summary>Dev fallback: wwwroot/uploads.</summary>
public class LocalFileStorage(IWebHostEnvironment env) : IFileStorage
{
    public async Task<string> SaveAsync(IFormFile file, string folder, CancellationToken ct = default)
    {
        var ext = FileRules.CheckedExtension(file);
        var dir = Path.Combine(env.WebRootPath, "uploads", folder);
        Directory.CreateDirectory(dir);
        var name = $"{Guid.NewGuid():N}{ext}";
        await using var fs = File.Create(Path.Combine(dir, name));
        await file.CopyToAsync(fs, ct);
        return $"/uploads/{folder}/{name}";
    }
}

/// <summary>
/// Supabase Storage (S3-like blob storage behind a CDN). Needs a public bucket and the secret (service) key.
/// </summary>
public class SupabaseFileStorage(HttpClient http, IConfiguration config) : IFileStorage
{
    readonly string url = $"https://{config["SUPABASE_PROJECT_REF"]}.supabase.co";
    readonly string key = config["SUPABASE_SECRET_KEY"]!;
    readonly string bucket = config["SUPABASE_BUCKET"] ?? "media";

    public async Task<string> SaveAsync(IFormFile file, string folder, CancellationToken ct = default)
    {
        var ext = FileRules.CheckedExtension(file);
        var path = $"{folder}/{Guid.NewGuid():N}{ext}";
        using var content = new StreamContent(file.OpenReadStream());
        content.Headers.ContentType = new MediaTypeHeaderValue(file.ContentType ?? "application/octet-stream");
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{url}/storage/v1/object/{bucket}/{path}") { Content = content };
        req.Headers.Add("apikey", key);
        if (key.Count(c => c == '.') == 2) // legacy JWT service_role key also needs the bearer header
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        req.Headers.Add("cache-control", "public, max-age=31536000");
        var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"Upload failed: {(int)res.StatusCode} {await res.Content.ReadAsStringAsync(ct)}");
        return $"{url}/storage/v1/object/public/{bucket}/{path}";
    }
}
