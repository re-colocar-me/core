using core.infrastructure;
using core.infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

namespace core.Tests;

/// <summary>
/// Prova, <b>sem banco</b>, que a consulta de elegibilidade dos convites traduz para SQL do Postgres e que o SQL carrega cada regra da spec (seções 10 e 11). O Npgsql gera o texto da consulta
/// sem abrir conexão. <b>Isto não prova o resultado</b> (só um Postgres de verdade prova): prova que a consulta não quebra por não ser traduzível e que nenhuma regra foi esquecida.
/// </summary>
public class InviteQueryTranslationTests
{
    private static string Sql(IReadOnlyCollection<string> situations, IReadOnlyCollection<string> bands)
    {
        var options = new DbContextOptionsBuilder<ReColocarmeContext>().UseNpgsql("Host=localhost;Database=nao-existe;Username=x;Password=x").Options;
        using var context = new ReColocarmeContext(options);
        var repository = new InviteRepository(context);
        return repository.EligibleSeekersQuery(Guid.NewGuid(), Guid.NewGuid(), situations, bands, new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc), new DateTime(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc), 3).ToQueryString();
    }

    [Fact]
    public void A_consulta_de_elegibilidade_traduz_para_SQL_e_leva_todas_as_regras()
    {
        var sql = Sql(["desligado"], ["semana"]);

        Assert.Contains("FROM \"JourneyCycles\"", sql);
        Assert.Contains("\"Status\" = 0", sql);                                   // em busca: ciclo ativo (não pausado, não recolocado)
        Assert.Contains("FROM \"TalentContactPreferences\"", sql);                 // preferências: só quem viu o aviso e não desligou
        Assert.Contains("\"ReceiveInvites\"", sql);
        Assert.Contains("\"InviteNoticeSeenAt\" IS NOT NULL", sql);
        Assert.Contains("FROM \"TalentSilencedConsultants\"", sql);                // não silenciou o consultor
        Assert.Contains("'Raised'", sql);                                          // conversa aberta com o consultor...
        Assert.Contains("'Proposed'", sql);
        Assert.Contains("'Sent'", sql);
        Assert.Contains("'Invite'", sql);                                          // ...ou convite dele dentro do período de espera, e o teto semanal
        Assert.Contains("count(*)", sql);                                          // o teto de convites da semana é uma contagem
        Assert.Contains("< @__weeklyCap", sql);                                  // o teto vem da configuração, não é número fixo
        Assert.Contains("DISTINCT", sql);                                          // pessoas, não ciclos
        Assert.Contains("\"Situation\" = ANY", sql);                                // o público do consultor
        Assert.Contains("\"TimeSinceLayoff\" = ANY", sql);
    }

    [Fact]
    public void Sem_criterio_de_publico_a_consulta_nao_filtra_situacao_nem_faixa()
    {
        var sql = Sql([], []);

        Assert.DoesNotContain("\"Situation\" = ANY", sql);
        Assert.DoesNotContain("\"TimeSinceLayoff\" = ANY", sql);
        Assert.Contains("\"InviteNoticeSeenAt\" IS NOT NULL", sql);                // as regras de proteção continuam
    }
}
