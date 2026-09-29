using Microsoft.EntityFrameworkCore;
using VctHub.Web.Models;

namespace VctHub.Web.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Region> Regions => Set<Region>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<Player> Players => Set<Player>();
    public DbSet<PlayerStat> PlayerStats => Set<PlayerStat>();
    public DbSet<Tournament> Tournaments => Set<Tournament>();
    public DbSet<Match> Matches => Set<Match>();
    public DbSet<RankedAccount> RankedAccounts => Set<RankedAccount>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        b.HasPostgresExtension("pg_trgm");

        b.Entity<Region>().HasIndex(r => r.Code).IsUnique();

        b.Entity<Team>(e =>
        {
            e.HasIndex(t => t.Slug).IsUnique();
            e.HasOne(t => t.Region).WithMany(r => r.Teams).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(t => t.Tournaments).WithMany(t => t.Teams).UsingEntity(j => j.ToTable("TournamentTeams"));
        });

        b.Entity<Player>(e =>
        {
            e.HasOne(p => p.Team).WithMany(t => t.Players).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(p => p.RankedAccount).WithMany().OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(p => p.Nickname);
        });

        b.Entity<PlayerStat>(e =>
        {
            e.HasOne(s => s.Player).WithMany(p => p.EventStats).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(s => s.Tournament).WithMany().OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(s => new { s.PlayerId, s.TournamentId }).IsUnique();
        });

        b.Entity<Tournament>(e =>
        {
            e.HasIndex(t => t.Slug).IsUnique();
            e.HasOne(t => t.Region).WithMany(r => r.Tournaments).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<Match>(e =>
        {
            e.HasOne(m => m.Tournament).WithMany(t => t.Matches).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(m => m.TeamA).WithMany().HasForeignKey(m => m.TeamAId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(m => m.TeamB).WithMany().HasForeignKey(m => m.TeamBId).OnDelete(DeleteBehavior.Restrict);
            e.Property(m => m.Status).HasConversion<string>().HasMaxLength(12);
            e.HasIndex(m => m.ScheduledAt);
        });

        b.Entity<RankedAccount>(e =>
        {
            // trigram index -> ILIKE '%abc%' over a million rows stays in the milliseconds
            e.HasIndex(a => a.RiotId).HasMethod("gin").HasOperators("gin_trgm_ops");
        });
    }
}
