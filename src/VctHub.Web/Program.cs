using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using VctHub.Web.Data;
using VctHub.Web.Services;

DotEnv.Load();
var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseNpgsql(DbConnection.Build(config), npg => npg.EnableRetryOnFailure(3)));
builder.Services.AddScoped<Seeder>();

if (!string.IsNullOrEmpty(config["SUPABASE_SECRET_KEY"]))
    builder.Services.AddHttpClient<IFileStorage, SupabaseFileStorage>();
else
    builder.Services.AddSingleton<IFileStorage, LocalFileStorage>();

builder.Services.AddControllersWithViews();

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
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
}
app.UseStatusCodePagesWithReExecute("/Home/Status/{0}");

// seed images (the CDN serves the same files from GitHub in production)
var seedImg = Path.Combine(Seeder.SeedDir(app.Environment, config), "img");
if (Directory.Exists(seedImg))
    app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(seedImg), RequestPath = "/seed-img" });
app.UseStaticFiles(); // wwwroot/uploads is written at runtime, MapStaticAssets only knows build-time files

app.UseRouting();
app.UseAuthorization();

app.MapStaticAssets();
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

app.Run();

public partial class Program;
