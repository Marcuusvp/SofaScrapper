using Microsoft.EntityFrameworkCore;

namespace SofaScore.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<DbMatch> Matches { get; set; }
    public DbSet<DbMatchStat> MatchStats { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DbMatch>().Property(m => m.Id).ValueGeneratedNever();
        
        modelBuilder.Entity<DbMatchStat>()
            .HasOne(s => s.Match)
            .WithMany(m => m.Stats)
            .HasForeignKey(s => s.MatchId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

// Entidades (Podem ficar em arquivos separados se preferir)
public class DbMatch
{
    public int Id { get; set; }
    public int TournamentId { get; set; }
    public int SeasonId { get; set; }
    public int Round { get; set; }
    public string HomeTeam { get; set; } = string.Empty;
    public string AwayTeam { get; set; } = string.Empty;
    public int HomeScore { get; set; }
    public int AwayScore { get; set; }
    public long StartTimestamp { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? Stadium { get; set; }
    public string? Referee { get; set; }
    public int? Attendance { get; set; }
    public List<DbMatchStat> Stats { get; set; } = new();
}

public class DbMatchStat
{
    public int Id { get; set; }
    public int MatchId { get; set; }
    public string Period { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string HomeValue { get; set; } = string.Empty;
    public string AwayValue { get; set; } = string.Empty;
    public int CompareCode { get; set; }
    public DbMatch Match { get; set; } = null!;
}