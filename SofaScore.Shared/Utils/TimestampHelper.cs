namespace SofaScore.Shared.Utils;

/// <summary>
/// Helper para corrigir timestamps do SofaScore que vêm com offset incorreto de +3h
/// </summary>
public static class TimestampHelper
{
    /// <summary>
    /// Corrige timestamp do SofaScore removendo as 3 horas extras
    /// BUG CONHECIDO: API do SofaScore retorna timestamps 3 horas adiantados
    /// </summary>
    /// <param name="sofascoreTimestamp">Timestamp original da API</param>
    /// <returns>Timestamp corrigido (UTC real)</returns>
    public static long FixSofaScoreTimestamp(long sofascoreTimestamp)
    {
        // SofaScore adiciona +3 horas ao timestamp
        // Precisamos subtrair 3 horas (10800 segundos)
        const long THREE_HOURS_IN_SECONDS = 10800;
        return sofascoreTimestamp - THREE_HOURS_IN_SECONDS;
    }

    /// <summary>
    /// Verifica se um timestamp precisa de correção comparando com a data esperada
    /// </summary>
    public static bool NeedsCorrection(long timestamp, DateTime expectedUtcDate)
    {
        var actualDate = DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime;
        var difference = Math.Abs((actualDate - expectedUtcDate).TotalHours);
        
        // Se a diferença for próxima de 3 horas, precisa correção
        return difference >= 2.5 && difference <= 3.5;
    }
}