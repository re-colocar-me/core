using core.infrastructure;
using core.infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

namespace core.Tests;

/// <summary>As consultas da medição da ponte (docs/specs/ponte-talento-consultor.md, seção 40): traduzem para SQL e <b>nunca selecionam</b> nada que identifique alguém.</summary>
public class PonteMetricsSqlTests
{
    private static readonly DateTime From = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);

    private static ReColocarmeContext Context()
        => new(new DbContextOptionsBuilder<ReColocarmeContext>().UseNpgsql("Host=localhost;Database=nao-existe;Username=x;Password=x").Options);

    [Fact]
    public void A_coorte_traduz_para_SQL_com_o_periodo_a_ordem_e_a_primeira_resposta_vinda_dos_eventos()
    {
        using var context = Context();

        var sql = new PonteMetricsRepository(context).CohortQuery(From, To).ToQueryString();

        Assert.Contains("FROM \"Conversations\"", sql);
        Assert.Contains("\"CreatedAt\" >=", sql);
        Assert.Contains("\"CreatedAt\" <", sql);
        Assert.Contains("ORDER BY", sql);
        Assert.Contains("DESC", sql);
        Assert.Contains("\"ConversationEvents\"", sql);                         // a primeira resposta vem dos eventos
        Assert.Contains("min(", sql);
        Assert.Contains("'Proposed'", sql);
        Assert.Contains("'Declined'", sql);
        Assert.Contains("\"AnonymizedAt\" IS NOT NULL", sql);
    }

    [Fact]
    public void A_coorte_nunca_seleciona_o_talento_o_consultor_o_texto_nem_a_ficha()
    {
        using var context = Context();

        var sql = new PonteMetricsRepository(context).CohortQuery(From, To).ToQueryString();

        // A lista de colunas devolvidas (até a subconsulta da primeira resposta): só estado, origem, tipo de proposta e datas.
        var columns = sql[sql.IndexOf("SELECT ", StringComparison.Ordinal)..sql.IndexOf(", (", StringComparison.Ordinal)];      // o ", (" abre a subconsulta (independe do fim de linha do sistema)
        Assert.Equal("SELECT c.\"Origin\", c.\"Status\", c.\"ProposalKind\", c.\"AnonymizedAt\" IS NOT NULL, c.\"CreatedAt\"", columns);

        // E em nenhum ponto da consulta aparecem o talento, o consultor, os textos, a conexão, o preço ou o cadastro. O "Id" da conversa só serve de junção com os eventos.
        foreach (var forbidden in new[] { "CandidateId", "ConsultantId", "\"Note\"", "InviteText", "InviteReason", "ProposedServiceName", "ConnectionId", "ProposedPriceInLemonCoins", "ConversationProfileCards", "\"Users\"" })
            Assert.DoesNotContain(forbidden, sql);
    }

    [Fact]
    public void As_denuncias_agrupam_por_origem_lado_e_estado_so_com_a_contagem()
    {
        using var context = Context();

        var sql = new PonteMetricsRepository(context).ReportCountsQuery(From, To).ToQueryString();

        Assert.Contains("FROM \"ConversationReports\"", sql);
        Assert.Contains("\"CreatedAt\" >=", sql);
        Assert.Contains("GROUP BY", sql);
        Assert.Contains("count(*)", sql);
        // Só origem, lado, estado e a contagem: nenhuma coluna que identifique ou que leve o texto da denúncia.
        foreach (var forbidden in new[] { "CandidateId", "ConsultantId", "Details", "ReviewNote", "ConversationId", "\"Id\"" })
            Assert.DoesNotContain(forbidden, sql);
    }
}
