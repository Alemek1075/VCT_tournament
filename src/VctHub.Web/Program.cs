using System.Text.Json.Serialization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using VctHub.Web.Data;
using VctHub.Web.Services;

DotEnv.Load();
var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// Render passes the port to listen on in $PORT
if (Environment.GetEnvironmentVariable("PORT") is { Length: > 0 } port)
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseNpgsql(DbConnection.Build(config, builder.Environment), npg => npg.EnableRetryOnFailure(3)));
builder.Services.AddScoped<Seeder>();

if (!string.IsNullOrEmpty(config["SUPABASE_SECRET_KEY"]))
    builder.Services.AddHttpClient<IFileStorage, SupabaseFileStorage>();
else
    builder.Services.AddSingleton<IFileStorage, LocalFileStorage>();

builder.Services.AddControllersWithViews()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddApiCache(config);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new() { Title = "VCT Hub API", Version = "v1", Description = "Teams, players, tournaments and matches of VCT 2026" });
    o.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory, "VctHub.Web.xml"));
    // only the JSON API goes into the OpenAPI document, MVC pages stay out
    o.DocInclusionPredicate((_, api) => api.RelativePath?.StartsWith("api/") == true);
});

// Render / Docker sit behind a proxy that terminates TLS
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
    await scope.ServiceProvider.GetRequiredService<Seeder>().RunAsync();
}

app.UseForwardedHeaders();
// API errors come back as application/problem+json with the right status code, pages get the HTML error page
app.UseWhen(c => c.Request.Path.StartsWithSegments("/api"),
    api => api.UseExceptionHandler());
app.UseWhen(c => !c.Request.Path.StartsWithSegments("/api") && !app.Environment.IsDevelopment(),
    web => web.UseExceptionHandler("/Home/Error"));
app.UseWhen(c => !c.Request.Path.StartsWithSegments("/api"),
    web => web.UseStatusCodePagesWithReExecute("/Home/Status/{0}"));

// seed images (the CDN serves the same files from GitHub in production)
var seedImg = Path.Combine(Seeder.SeedDir(app.Environment, config), "img");
if (Directory.Exists(seedImg))
    app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(seedImg), RequestPath = "/seed-img" });
app.UseStaticFiles(); // wwwroot/uploads is written at runtime, MapStaticAssets only knows build-time files

app.UseSwagger();
app.UseSwaggerUI(o => o.DocumentTitle = "VCT Hub API");

app.UseRouting();
app.UseApiCache();
app.UseAuthorization();

app.MapStaticAssets();
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

app.Run();

public partial class Program;
