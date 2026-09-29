using Npgsql;

namespace VctHub.Web.Services;

/// <summary>Reads KEY=VALUE lines from the nearest .env into environment variables (dev convenience).</summary>
public static class DotEnv
{
    public static void Load()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, ".env"))) dir = dir.Parent;
        if (dir == null) return;
        foreach (var line in File.ReadAllLines(Path.Combine(dir.FullName, ".env")))
        {
            var i = line.IndexOf('=');
            if (line.StartsWith('#') || i <= 0) continue;
            var key = line[..i].Trim();
            var value = line[(i + 1)..].Trim();
            if (value.Length > 0 && Environment.GetEnvironmentVariable(key) is null)
                Environment.SetEnvironmentVariable(key, value);
        }
    }
}

public static class DbConnection
{
    /// <summary>
    /// ConnectionStrings:Default wins. Otherwise, if SUPABASE_DB_PASSWORD is set, connect to Supabase
    /// through the IPv4 session pooler (Render has no IPv6). Local dev stays on the docker database
    /// unless USE_SUPABASE=true, so tests never write into production.
    /// </summary>
    public static string Build(IConfiguration config, IHostEnvironment env)
    {
        var explicitCs = config.GetConnectionString("Default");
        if (!string.IsNullOrWhiteSpace(explicitCs)) return explicitCs;

        var password = config["SUPABASE_DB_PASSWORD"];
        var reference = config["SUPABASE_PROJECT_REF"];
        var useSupabase = !env.IsDevelopment() || config["USE_SUPABASE"] == "true";
        if (useSupabase && !string.IsNullOrWhiteSpace(password) && !string.IsNullOrWhiteSpace(reference))
            return new NpgsqlConnectionStringBuilder
            {
                Host = config["SUPABASE_POOLER_HOST"] ?? "aws-1-eu-central-1.pooler.supabase.com",
                Port = 5432,
                Database = "postgres",
                Username = $"postgres.{reference}",
                Password = password,
                SslMode = SslMode.Require,
                MaxPoolSize = 15,
            }.ToString();

        return "Host=localhost;Port=5433;Database=vct;Username=vct;Password=vct";
    }
}
