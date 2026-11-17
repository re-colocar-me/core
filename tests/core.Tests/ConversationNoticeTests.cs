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

/// <summary>Os avisos das conversas da ponte (docs/specs/ponte-talento-consultor.md, seção 38): a consulta, a mensagem ao motor e o job que a liga e desliga.</summary>
public class ConversationNoticeTests
{
    private static readonly Guid Event = Guid.Parse("11111111-bbbb-0000-0000-000000000001");
    private static readonly Guid Candidate = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ConsultantProfile = Guid.Parse("dddddddd-0000-0000-0000-000000000001");

    // ---- a consulta traduz para SQL e leva cada regra ---------------------------------------------------------------------------

    [Fact]
    public void A_consulta_dos_eventos_a_avisar_traduz_para_SQL_e_leva_todas_as_regras()
    {
        var options = new DbContextOptionsBuilder<ReColocarmeContext>().UseNpgsql("Host=localhost;Database=nao-existe;Username=x;Password=x").Options;
        using var context = new ReColocarmeContext(options);

        var sql = new ConversationRepository(context).PendingEventNoticesQuery(new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc)).ToQueryString();

        Assert.Contains("FROM \"ConversationEvents\"", sql);
        Assert.Contains("\"NotifiedAt\" IS NULL", sql);               // só os ainda não avisados
        Assert.Contains("\"At\" >=", sql);                            // só os recentes
        foreach (var type in new[] { "'Raised'", "'Proposed'", "'Confirmed'", "'Declined'", "'Expired'" })
            Assert.Contains(type, sql);                              // só os eventos que viram aviso
        Assert.Contains("\"AnonymizedAt\" IS NULL", sql);             // conversa anonimizada não avisa
        Assert.Contains("\"Consultants\"", sql);                      // o consultor e o nome dele
        Assert.Contains("\"Users\"", sql);                            // o cadastro do talento e o do consultor (e-mail e primeiro nome)
        Assert.Contains("LEFT JOIN", sql);                            // cadastro ausente não derruba a consulta
        Assert.Contains("EXISTS", sql);                               // "já houve proposta", que distingue a expiração
        Assert.Contains("'Proposed'", sql);
        Assert.Contains("ORDER BY", sql);                             // do mais antigo ao mais novo
    }

    // ---- a mensagem ao motor ----------------------------------------------------------------------------------------------------

    private static JsonElement Message(NotificationIntent intent) => JsonDocument.Parse(RabbitNotificationPublisher.Build(intent)).RootElement;

    private static NotificationIntent Intent(string type = "consultor.mao-levantada", string role = "consultant", string? email = "marina@exemplo.com", string? name = "Marina",
        Dictionary<string, string>? data = null, string link = "/feed?area=conversas")
        => new(type, $"conversa:{Event}", role, role == "consultant" ? ConsultantProfile : Candidate, email, name, data ?? new Dictionary<string, string> { ["Deadline"] = "24/11" }, link,
            new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void A_mensagem_segue_o_contrato_v2_do_motor_com_a_chave_do_evento_e_os_dados_do_modelo()
    {
        var root = Message(Intent());

        Assert.Equal(2, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal($"conversa:{Event}", root.GetProperty("idempotencyKey").GetString());       // repetir é seguro
        Assert.Equal($"conversa:{Event}", root.GetProperty("correlationId").GetString());
        Assert.Equal("core", root.GetProperty("producer").GetString());
        Assert.Equal("consultor.mao-levantada", root.GetProperty("type").GetString());
        Assert.Equal("/feed?area=conversas", root.GetProperty("link").GetString());              // rota, sem token
        Assert.Equal("2026-11-17T12:00:00Z", root.GetProperty("requestedAt").GetString());
        var recipient = root.GetProperty("recipient");
        Assert.Equal("consultant", recipient.GetProperty("role").GetString());
        Assert.Equal(ConsultantProfile.ToString(), recipient.GetProperty("profileId").GetString());
        Assert.Equal("marina@exemplo.com", recipient.GetProperty("email").GetString());
        Assert.Equal("Marina", recipient.GetProperty("name").GetString());
        Assert.Equal("pt-BR", recipient.GetProperty("locale").GetString());
        Assert.Equal("24/11", root.GetProperty("data").GetProperty("Deadline").GetString());       // o nome do campo sai exato, como no modelo
    }

    [Fact]
    public void O_talento_recebe_com_o_papel_talent_e_os_dados_da_proposta()
    {
        var root = Message(Intent("talento.proposta-recebida", "talent", "ana@exemplo.com", "Ana",
            new Dictionary<string, string> { ["Consultant"] = "Marina Costa", ["Service"] = "Revisão de currículo", ["Detail"] = "150 Lemon Coins", ["Deadline"] = "24/11" }, "/conversas"));

        Assert.Equal("talent", root.GetProperty("recipient").GetProperty("role").GetString());
        Assert.Equal(Candidate.ToString(), root.GetProperty("recipient").GetProperty("profileId").GetString());
        Assert.Equal(["Consultant", "Deadline", "Detail", "Service"], root.GetProperty("data").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Sem_e_mail_ou_sem_nome_o_campo_some_da_mensagem_em_vez_de_ir_vazio()
    {
        var recipient = Message(Intent(email: "  ", name: null)).GetProperty("recipient");

        Assert.False(recipient.TryGetProperty("email", out _));
        Assert.False(recipient.TryGetProperty("name", out _));
    }

    [Fact]
    public void O_aviso_do_convite_continua_igual_depois_da_generalizacao_do_publicador()
    {
        var notice = new InviteNotice(Event, Candidate, "ana@exemplo.com", "Ana", "Marina Costa", "Combina com você porque você está desligado(a).", new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc));

        var root = JsonDocument.Parse(RabbitNotificationPublisher.Build(notice)).RootElement;

        Assert.Equal("talento.convite-recebido", root.GetProperty("type").GetString());
        Assert.Equal($"convite:{Event}", root.GetProperty("idempotencyKey").GetString());
        Assert.Equal("/conversas", root.GetProperty("link").GetString());
        Assert.Equal(["Consultant", "Reason"], root.GetProperty("data").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    // ---- o job: desligado por padrão ---------------------------------------------------------------------------------------------

    private sealed class FakeNoticeService : IConversationNoticeService
    {
        public int Calls { get; private set; }
        public Exception? Throw { get; set; }
        public ConversationNoticeRun Run { get; set; } = new(2, 1, false);
        public Task<ConversationNoticeRun> NotifyPendingAsync(CancellationToken ct)
        {
            Calls++;
            if (Throw is not null) throw Throw;
            return Task.FromResult(Run);
        }
    }

    private static (core.IoC.ConversationNoticeJob Job, FakeNoticeService Service, IConfigurationRoot Configuration) Job(string? enabled)
    {
        var service = new FakeNoticeService();
        var provider = new ServiceCollection().AddSingleton<IConversationNoticeService>(service).BuildServiceProvider();
        var values = enabled is null ? new Dictionary<string, string?>() : new Dictionary<string, string?> { ["ConversationNotices:Enabled"] = enabled };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return (new core.IoC.ConversationNoticeJob(provider.GetRequiredService<IServiceScopeFactory>(), configuration, NullLogger<core.IoC.ConversationNoticeJob>.Instance), service, configuration);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("")]
    [InlineData("nao")]
    public async Task Desligado_por_padrao_nao_chama_o_servico_nem_publica_nada_nem_com_valor_invalido(string? enabled)
    {
        var (job, service, _) = Job(enabled);

        Assert.Null(await job.TickAsync(default));
        Assert.Null(await job.TickAsync(default));

        Assert.Equal(0, service.Calls);
    }

    [Fact]
    public async Task Ligado_roda_o_servico_e_devolve_o_resultado()
    {
        var (job, service, _) = Job("true");

        var run = await job.TickAsync(default);

        Assert.Equal(new ConversationNoticeRun(2, 1, false), run);
        Assert.Equal(1, service.Calls);
    }

    [Fact]
    public async Task Falha_do_servico_vira_log_e_nao_derruba_o_job_e_a_chave_e_lida_a_cada_rodada()
    {
        var (job, service, configuration) = Job("true");
        service.Throw = new IOException("RabbitMQ fora do ar");

        Assert.Null(await job.TickAsync(default));                // não lança

        service.Throw = null;
        Assert.NotNull(await job.TickAsync(default));
        configuration["ConversationNotices:Enabled"] = "false";
        Assert.Null(await job.TickAsync(default));
    }

    [Fact]
    public async Task O_cancelamento_nao_vira_erro()
    {
        var (job, service, _) = Job("true");
        service.Throw = new OperationCanceledException();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Null(await job.TickAsync(cts.Token));
    }
}
