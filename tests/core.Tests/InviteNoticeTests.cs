using System.Text.Json;
using core.domain.Interfaces.Services;
using core.infrastructure;
using core.infrastructure.Publishers;
using core.infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace core.Tests;

/// <summary>O aviso ao talento dos convites recebidos (docs/specs/ponte-talento-consultor.md, seção 33): a consulta, a mensagem ao motor e o job que a liga e desliga.</summary>
public class InviteNoticeTests
{
    private static readonly Guid Conversation = Guid.Parse("11111111-aaaa-0000-0000-000000000001");
    private static readonly Guid Candidate = Guid.Parse("22222222-2222-2222-2222-222222222222");

    // ---- a consulta traduz para SQL e leva cada regra ---------------------------------------------------------------------------

    [Fact]
    public void A_consulta_dos_avisos_pendentes_traduz_para_SQL_e_leva_todas_as_regras()
    {
        var options = new DbContextOptionsBuilder<ReColocarmeContext>().UseNpgsql("Host=localhost;Database=nao-existe;Username=x;Password=x").Options;
        using var context = new ReColocarmeContext(options);

        var sql = new InviteRepository(context).PendingNoticesQuery(new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc)).ToQueryString();

        Assert.Contains("FROM \"Conversations\"", sql);
        Assert.Contains("'Invite'", sql);                              // só convites
        Assert.Contains("'Sent'", sql);                                // só os abertos (em espera, dispensados e aceitos ficam de fora)
        Assert.Contains("\"NotifiedAt\" IS NULL", sql);                // só os ainda não avisados
        Assert.Contains("\"CreatedAt\" >=", sql);                      // só os recentes
        Assert.Contains("\"ExpiresAt\"", sql);                         // e dentro do prazo
        Assert.Contains("\"Users\"", sql);                             // o cadastro do talento (e-mail e primeiro nome): a tabela dos perfis
        Assert.Contains("\"Consultants\"", sql);                       // o nome do consultor
        Assert.Contains("\"TalentContactPreferences\"", sql);          // a chave "Receber convites" de agora
        Assert.Contains("LEFT JOIN", sql);                             // sem linha de preferência, não recebe (e não derruba a consulta)
        Assert.Contains("ORDER BY", sql);                              // do mais antigo ao mais novo
    }

    // ---- a mensagem ao motor ----------------------------------------------------------------------------------------------------

    private static JsonElement Message(InviteNotice notice)
        => JsonDocument.Parse(RabbitNotificationPublisher.Build(notice)).RootElement;

    private static InviteNotice Notice(string? email = "ana@exemplo.com", string? first = "Ana") =>
        new(Conversation, Candidate, email, first, "Marina Costa", "Combina com você porque você está desligado(a) há 1 a 3 meses.", new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void A_mensagem_segue_o_contrato_v2_do_motor_com_a_chave_do_convite()
    {
        var root = Message(Notice());

        Assert.Equal(2, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal($"convite:{Conversation}", root.GetProperty("idempotencyKey").GetString());     // repetir é seguro: o motor descarta
        Assert.Equal($"convite:{Conversation}", root.GetProperty("correlationId").GetString());
        Assert.Equal("core", root.GetProperty("producer").GetString());
        Assert.Equal("talento.convite-recebido", root.GetProperty("type").GetString());
        Assert.Equal("/conversas", root.GetProperty("link").GetString());                            // rota, sem token
        Assert.Equal("2026-11-17T12:00:00Z", root.GetProperty("requestedAt").GetString());
        var recipient = root.GetProperty("recipient");
        Assert.Equal("talent", recipient.GetProperty("role").GetString());
        Assert.Equal(Candidate.ToString(), recipient.GetProperty("profileId").GetString());
        Assert.Equal("ana@exemplo.com", recipient.GetProperty("email").GetString());
        Assert.Equal("Ana", recipient.GetProperty("name").GetString());
        Assert.Equal("pt-BR", recipient.GetProperty("locale").GetString());
        var data = root.GetProperty("data");
        Assert.Equal("Marina Costa", data.GetProperty("Consultant").GetString());
        Assert.Equal("Combina com você porque você está desligado(a) há 1 a 3 meses.", data.GetProperty("Reason").GetString());
    }

    [Fact]
    public void A_mensagem_so_leva_o_consultor_e_o_motivo_nunca_o_texto_do_convite_nem_dado_sensivel()
    {
        var json = System.Text.Encoding.UTF8.GetString(RabbitNotificationPublisher.Build(Notice())).ToLowerInvariant();

        Assert.DoesNotContain("swot", json);
        Assert.DoesNotContain("resume", json);
        Assert.DoesNotContain("curriculo", json);
        Assert.DoesNotContain("phone", json);
        Assert.DoesNotContain("invitetext", json);
        Assert.DoesNotContain("note", json);
        Assert.Equal(["Consultant", "Reason"], Message(Notice()).GetProperty("data").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Sem_e_mail_ou_sem_nome_o_campo_some_da_mensagem_em_vez_de_ir_vazio()
    {
        var recipient = Message(Notice(email: "  ", first: null)).GetProperty("recipient");

        Assert.False(recipient.TryGetProperty("email", out _));
        Assert.False(recipient.TryGetProperty("name", out _));
        Assert.Equal("talent", recipient.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Sem_RabbitMQ_configurado_a_publicacao_falha_com_uma_mensagem_clara_para_a_rodada_parar()
    {
        using var publisher = new RabbitNotificationPublisher(new ConfigurationBuilder().Build());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => publisher.PublishAsync(Notice(), default));

        Assert.Contains("RabbitMQ:Host", error.Message);
    }

    // ---- o job: desligado por padrão ---------------------------------------------------------------------------------------------

    private sealed class FakeNoticeService : IInviteNoticeService
    {
        public int Calls { get; private set; }
        public Exception? Throw { get; set; }
        public InviteNoticeRun Run { get; set; } = new(2, 1, false);
        public Task<InviteNoticeRun> NotifyPendingAsync(CancellationToken ct)
        {
            Calls++;
            if (Throw is not null) throw Throw;
            return Task.FromResult(Run);
        }
    }

    private static (core.IoC.InviteNoticeJob Job, FakeNoticeService Service) Job(string? enabled)
    {
        var service = new FakeNoticeService();
        var provider = new ServiceCollection().AddSingleton<IInviteNoticeService>(service).BuildServiceProvider();
        var values = enabled is null ? new Dictionary<string, string?>() : new Dictionary<string, string?> { ["InviteNotices:Enabled"] = enabled };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return (new core.IoC.InviteNoticeJob(provider.GetRequiredService<IServiceScopeFactory>(), configuration, NullLogger<core.IoC.InviteNoticeJob>.Instance), service);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("")]
    [InlineData("nao")]
    public async Task Desligado_por_padrao_nao_chama_o_servico_nem_publica_nada(string? enabled)
    {
        var (job, service) = Job(enabled);

        Assert.Null(await job.TickAsync(default));
        Assert.Null(await job.TickAsync(default));

        Assert.Equal(0, service.Calls);
    }

    [Fact]
    public async Task Ligado_roda_o_servico_e_devolve_o_resultado()
    {
        var (job, service) = Job("true");

        var run = await job.TickAsync(default);

        Assert.Equal(new InviteNoticeRun(2, 1, false), run);
        Assert.Equal(1, service.Calls);
    }

    [Fact]
    public async Task Falha_do_servico_vira_log_e_nao_derruba_o_job()
    {
        var (job, service) = Job("true");
        service.Throw = new IOException("RabbitMQ fora do ar");

        Assert.Null(await job.TickAsync(default));          // não lança

        service.Throw = null;
        Assert.NotNull(await job.TickAsync(default));       // a rodada seguinte recupera
    }

    [Fact]
    public async Task A_chave_e_lida_a_cada_rodada_e_o_cancelamento_nao_vira_erro()
    {
        var service = new FakeNoticeService();
        var provider = new ServiceCollection().AddSingleton<IInviteNoticeService>(service).BuildServiceProvider();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["InviteNotices:Enabled"] = "true" }).Build();
        var job = new core.IoC.InviteNoticeJob(provider.GetRequiredService<IServiceScopeFactory>(), configuration, NullLogger<core.IoC.InviteNoticeJob>.Instance);

        configuration["InviteNotices:Enabled"] = "false";
        Assert.Null(await job.TickAsync(default));
        configuration["InviteNotices:Enabled"] = "true";
        Assert.NotNull(await job.TickAsync(default));

        service.Throw = new OperationCanceledException();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Null(await job.TickAsync(cts.Token));
    }
}
