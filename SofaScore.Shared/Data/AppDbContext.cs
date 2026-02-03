using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using SofaScoreScraper;

namespace SofaScore.Shared.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<DbMatch> Matches { get; set; }
    public DbSet<DbMatchStat> MatchStats { get; set; }
    public DbSet<DbIncident> Incidents { get; set; }
    public DbSet<DbRoundState> RoundStates { get; set; }
    public DbSet<DbStanding> Standings { get; set; }
    public DbSet<DbStandingPromotion> StandingPromotions { get; set; } 

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DbMatch>().Property(m => m.Id).ValueGeneratedNever();
        
        modelBuilder.Entity<DbMatchStat>()
            .HasOne(s => s.Match)
            .WithMany(m => m.Stats)
            .HasForeignKey(s => s.MatchId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<DbIncident>()
            .HasOne(i => i.Match)
            .WithMany(m => m.Incidents)
            .HasForeignKey(i => i.MatchId)
            .OnDelete(DeleteBehavior.Cascade); // Se apagar o jogo, limpa os incidentes dele

        modelBuilder.Entity<DbRoundState>()
            .HasIndex(r => new { r.TournamentId, r.SeasonId, r.Round })
            .IsUnique();

        modelBuilder.Entity<DbRoundState>()
            .HasIndex(r => new { r.IsFullyProcessed, r.LastCheck });

        modelBuilder.Entity<DbMatch>()
        .HasIndex(m => new { m.TournamentId, m.Round, m.ProcessingStatus });

        // === Standings ===
        // Chave única: um time aparece uma vez por campeonato/temporada
        modelBuilder.Entity<DbStanding>()
            .HasIndex(s => new { s.TournamentId, s.SeasonId, s.TeamId })
            .IsUnique();

        // Índice para buscar classificação inteira de um campeonato
        modelBuilder.Entity<DbStanding>()
            .HasIndex(s => new { s.TournamentId, s.SeasonId, s.Position });

        // === StandingPromotions ===
        // Relacionamento: cada standing pode ter 0 ou mais promoções
        modelBuilder.Entity<DbStandingPromotion>()
            .HasOne(p => p.Standing)
            .WithMany(s => s.Promotions)
            .HasForeignKey(p => p.StandingId)
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

    // Controle de Processamento
    public MatchProcessingStatus ProcessingStatus { get; set; } = MatchProcessingStatus.Pending;
    
    // Controle de Tentativas (Retry)
    public int EnrichmentAttempts { get; set; } = 0;
    public DateTime? LastEnrichmentAttempt { get; set; }
    public string? LastEnrichmentError { get; set; }

    public List<DbMatchStat> Stats { get; set; } = new();
    public List<DbIncident> Incidents { get; set; } = new();

    // Auxiliar para lógica (não banco)
    [NotMapped]
    public bool NeedsEnrichment => 
        (Status == "Ended" || Status == "Finished") && 
        ProcessingStatus != MatchProcessingStatus.Enriched &&
        ProcessingStatus != MatchProcessingStatus.Cancelled &&
        EnrichmentAttempts < 3;
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

public class DbIncident
{
    public int Id { get; set; } // ID do banco (auto-increment)
    public int MatchId { get; set; } // FK para a partida
    
    public string IncidentType { get; set; } = string.Empty; // "goal", "card", "substitution"
    public string? IncidentClass { get; set; } // "regular", "penalty", "ownGoal", "yellow", "red"
    public int Time { get; set; } // Minuto (ex: 45)
    public int AddedTime { get; set; } // Acréscimos (ex: 3)
    public bool IsHome { get; set; } // true = Mandante, false = Visitante
    
    // Dados do Jogador (simples, sem criar tabela de jogadores por enquanto)
    public string? PlayerName { get; set; }
    public string? AssistName { get; set; } // Se houver assistência

    // Relacionamento
    public DbMatch Match { get; set; } = null!;
}

public class DbRoundState
{
    public int Id { get; set; }
    
    // Chave composta lógica
    public int TournamentId { get; set; }
    public int SeasonId { get; set; }
    public int Round { get; set; }
    
    // Controle de Estado
    public bool IsFullyProcessed { get; set; }
    
    // Métricas de Progresso
    public int TotalMatches { get; set; }
    public int EnrichedMatches { get; set; } 
    public int PostponedMatches { get; set; } // Adiados (contam como pendentes na lógica)
    public int CancelledMatches { get; set; } // Cancelados (contam como resolvidos)
    
    // Controle de Lock
    public DateTime? LockedAt { get; set; }
    public string? LockedBy { get; set; }
    
    // Observabilidade
    public int FailedAttempts { get; set; }
    public string? LastError { get; set; }
    
    // Timestamps
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastCheck { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    // Propriedade auxiliar (não mapeada no banco) para lógica de negócio
    [NotMapped]
    public bool ShouldBeMarkedComplete => (EnrichedMatches + CancelledMatches) == TotalMatches;
}
/// <summary>
/// Representa uma linha da classificação de um campeonato.
/// Uma linha por time por campeonato/temporada.
/// </summary>
public class DbStanding
{
    public int Id { get; set; }

    // Identificação do campeonato
    public int TournamentId { get; set; }
    public int SeasonId { get; set; }

    // Identificação do time
    public int TeamId { get; set; }
    public string TeamName { get; set; } = string.Empty;

    // Classificação
    public int Position { get; set; }

    // Estatísticas
    public int Matches { get; set; }
    public int Wins { get; set; }
    public int Draws { get; set; }
    public int Losses { get; set; }
    public int GoalsFor { get; set; }
    public int GoalsAgainst { get; set; }
    public int GoalDifference { get; set; }
    public int Points { get; set; }

    // Timestamp da última atualização desta classificação
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Relacionamento: promoções/rebaixamentos do time nessa classificação
    public List<DbStandingPromotion> Promotions { get; set; } = new();
}

/// <summary>
/// Representa uma informação de promoção ou rebaixamento associada a um time na classificação.
/// Exemplo: "Classificado para Champions League", "Zona de rebaixamento", etc.
/// Um mesmo time pode ter mais de uma faixa (ex: classificado para CL e também para Europa League
/// se ficar em determinada posição).
/// </summary>
public class DbStandingPromotion
{
    public int Id { get; set; }
    public int StandingId { get; set; } // FK para DbStanding

    /// <summary>
    /// ID da promoção conforme vem da API do SofaScore.
    /// Usado para identificar o tipo (ex: Champions League, Europa League, Rebaixamento).
    /// </summary>
    public int PromotionId { get; set; }

    /// <summary>
    /// Texto descritivo da promoção (ex: "Champions League", "Rebaixamento").
    /// </summary>
    public string Text { get; set; } = string.Empty;

    // Relacionamento
    public DbStanding Standing { get; set; } = null!;
}