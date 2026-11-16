using core.domain.Entities;
using core.domain.Enums;
using core.domain.Interfaces.Infrastructure;
using core.domain.Interfaces.Services;
using core.domain.Services.Conversations;
using Google.Protobuf;

namespace core.Tests;

/// <summary>RPCs da ponte (core/Services/ConversationsService.cs): o que cada lado pode ver e como o erro vira código (docs/specs/ponte-talento-consultor.md).</summary>
public class ConversationsGrpcTests
{
    private static readonly Guid Talent = Guid.Parse("aaaaaaaa-1111-2222-3333-444444444444");
    private static readonly Guid Consultant = Guid.Parse("cccccccc-1111-2222-3333-444444444444");

    private sealed class FakeConversations : IConversationService
    {
        public List<Conversation> Items { get; } = [];
        public Exception? Throw { get; set; }
        public RaiseHandCommand? Raised { get; private set; }

        private Task<Conversation> Result() => Throw is not null ? throw Throw : Task.FromResult(Items.First());

        public Task<Conversation> RaiseHandAsync(RaiseHandCommand command) { Raised = command; return Result(); }
        public Task<Conversation> ProposeAsync(Guid consultantId, Guid conversationId, Guid offeringId) => Result();
        public Task<Conversation> DeclineAsync(Guid consultantId, Guid conversationId) => Result();
        public Task<Conversation> ProposeFirstConversationAsync(Guid consultantId, Guid conversationId) { FirstProposedBy = consultantId; return Result(); }
        public Guid? FirstProposedBy { get; private set; }
        public (Guid Consultant, bool Enabled, int Minutes, int Quota)? FirstSet { get; private set; }
        public Task<FirstConversationStatus> GetFirstConversationAsync(Guid consultantId) => Task.FromResult(new FirstConversationStatus(true, 15, 4, 1, 3));
        public Task<Dictionary<Guid, int>> GetFirstConversationBadgesAsync(IEnumerable<Guid> consultantIds) => Task.FromResult(new Dictionary<Guid, int>());
        public Task EnsureCanBuyFirstConversationAsync(Guid candidateId, Guid consultantId) => Task.CompletedTask;
        // O app do consultor só conhece o id do perfil: o fake o traduz para um Consultant.Id diferente, para o teste provar que o serviço usa o certo.
        public static Guid ConsultantOf(Guid profileId) => new(profileId.ToByteArray().Reverse().ToArray());
        public Guid? LastResolvedOwner { get; private set; }
        public bool? LastCreateIfMissing { get; private set; }
        public Guid? UnknownProfile { get; set; }
        public Task<Guid?> ResolveConsultantAsync(Guid ownerProfileId, bool createIfMissing = false)
        {
            LastResolvedOwner = ownerProfileId; LastCreateIfMissing = createIfMissing;
            return Task.FromResult(ownerProfileId == UnknownProfile && !createIfMissing ? null : (Guid?)ConsultantOf(ownerProfileId));
        }
        public Task<FirstConversationStatus> SetFirstConversationAsync(Guid consultantId, bool enabled, int durationMinutes, int monthlyQuota)
        {
            if (Throw is not null) throw Throw;
            FirstSet = (consultantId, enabled, durationMinutes, monthlyQuota);
            return Task.FromResult(new FirstConversationStatus(enabled, durationMinutes, monthlyQuota, 0, monthlyQuota));
        }
        public Task<Conversation> ConfirmAsync(Guid candidateId, Guid conversationId, CandidateIdentity identity) => Result();
        public Task<Conversation> WithdrawAsync(Guid candidateId, Guid conversationId) => Result();
        public Task<Dictionary<Guid, ConversationConsultant>> GetConsultantsAsync(IEnumerable<Guid> ids)
            => Task.FromResult(ids.Distinct().ToDictionary(x => x, _ => new ConversationConsultant("Marina Costa", "https://x/m.jpg")));
        public Task<List<Conversation>> ListForCandidateAsync(Guid candidateId) => Task.FromResult(Items.ToList());
        public Task<List<Conversation>> ListForConsultantAsync(Guid consultantId, ConversationOrigin? origin, ConversationStatus? status)
            => Task.FromResult(Items.Where(x => (origin is null || x.Origin == origin) && (status is null || x.Status == status)).ToList());
        public Task<int> ExpireDueAsync() => Task.FromResult(0);
    }

    private sealed class FakeAudience : IAudienceService
    {
        public Guid? SetFor { get; private set; }
        public Dictionary<string, List<string>>? SetCriteria { get; private set; }
        public Exception? Throw { get; set; }
        public AudienceCount Count { get; set; } = new(42, false, 10);

        private AudienceView View(IReadOnlyDictionary<string, List<string>> criteria) => new(criteria, AudienceKinds.Supported, 3, Count);

        public Task<AudienceView> GetAsync(Guid consultantId) => Task.FromResult(View(new Dictionary<string, List<string>> { [AudienceKinds.Situation] = ["desligado"] }));
        public Task<AudienceView> SetAsync(Guid consultantId, IReadOnlyDictionary<string, List<string>> criteria)
        {
            if (Throw is not null) throw Throw;
            SetFor = consultantId; SetCriteria = criteria.ToDictionary(x => x.Key, x => x.Value);
            return Task.FromResult(View(criteria));
        }
        public Task<AudienceCount> PreviewAsync(IReadOnlyDictionary<string, List<string>> criteria) => Task.FromResult(Throw is not null ? throw Throw : Count);
    }

    private static Conversation Sample(ConversationStatus status = ConversationStatus.Raised, Guid? connectionId = null)
    {
        var id = Guid.NewGuid();
        return new Conversation
        {
            Id = id, ConsultantId = Consultant, CandidateId = Talent, Origin = ConversationOrigin.HandRaised, Status = status, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            Note = "Quero ajustar meu currículo", ShareSuco = true, ConnectionId = connectionId,
            Card = new ConversationProfileCard { ConversationId = id, Situation = "desligado", TimeSinceLayoffBand = "um_a_tres_meses", AreasTop3 = ["Tecnologia", "Dados"], SucoSummary = "Resumo" },
            Events = [new ConversationEvent { Id = Guid.NewGuid(), ConversationId = id, Type = "Raised", Actor = "Candidate", At = DateTime.UtcNow }]
        };
    }

    private static (core.Services.ConversationsService Grpc, FakeConversations Service) Create()
    {
        var service = new FakeConversations();
        return (new core.Services.ConversationsService(service, new FakeAudience()), service);
    }

    private static (core.Services.ConversationsService Grpc, FakeConversations Service, FakeAudience Audience) CreateWithAudience()
    {
        var service = new FakeConversations();
        var audience = new FakeAudience();
        return (new core.Services.ConversationsService(service, audience), service, audience);
    }

    [Fact]
    public async Task A_caixa_do_consultor_nunca_carrega_o_identificador_do_talento()
    {
        var (grpc, service) = Create();
        service.Items.Add(Sample());

        var reply = await grpc.ListForConsultant(new ConvListForConsultantRequest { ConsultantId = Consultant.ToString() }, TestServerCallContext.Create());

        Assert.Equal(200, reply.Statuscode);
        var json = JsonFormatter.Default.Format(reply.Data.Unpack<ConvConsultantListReply>());
        Assert.DoesNotContain(Talent.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("candidateId", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("um_a_tres_meses", json);   // a ficha, sim
        Assert.Contains("Resumo", json);
    }

    [Fact]
    public async Task Antes_do_aceite_a_conexao_nao_aparece_para_o_consultor_e_depois_sim()
    {
        var (grpc, service) = Create();
        var connection = Guid.NewGuid();
        service.Items.Add(Sample(ConversationStatus.Proposed, connection));   // dado sujo: conexão em conversa ainda não confirmada
        service.Items.Add(Sample(ConversationStatus.Confirmed, connection));

        var reply = await grpc.ListForConsultant(new ConvListForConsultantRequest { ConsultantId = Consultant.ToString() }, TestServerCallContext.Create());
        var items = reply.Data.Unpack<ConvConsultantListReply>().Items;

        Assert.Equal(string.Empty, items.Single(i => i.Status == "Proposed").ConnectionId);
        Assert.Equal(connection.ToString(), items.Single(i => i.Status == "Confirmed").ConnectionId);
    }

    [Fact]
    public async Task A_caixa_do_talento_mostra_o_consultor_identificado()
    {
        var (grpc, service) = Create();
        service.Items.Add(Sample());

        var reply = await grpc.ListForCandidate(new ConvListForCandidateRequest { CandidateId = Talent.ToString() }, TestServerCallContext.Create());

        var item = Assert.Single(reply.Data.Unpack<ConvCandidateListReply>().Items);
        Assert.Equal("Marina Costa", item.ConsultantName);
        Assert.Equal(Consultant.ToString(), item.ConsultantId);
    }

    [Fact]
    public async Task O_filtro_da_caixa_do_consultor_aceita_origem_e_estado()
    {
        var (grpc, service) = Create();
        service.Items.Add(Sample(ConversationStatus.Raised));
        service.Items.Add(Sample(ConversationStatus.Declined));

        var reply = await grpc.ListForConsultant(new ConvListForConsultantRequest { ConsultantId = Consultant.ToString(), Status = "declined" }, TestServerCallContext.Create());

        Assert.Equal("Declined", Assert.Single(reply.Data.Unpack<ConvConsultantListReply>().Items).Status);
    }

    [Fact]
    public async Task Levantar_a_mao_repassa_a_ficha_e_o_resumo_ao_dominio()
    {
        var (grpc, service) = Create();
        service.Items.Add(Sample());

        var request = new ConvRaiseHandRequest { CandidateId = Talent.ToString(), ConsultantId = Consultant.ToString(), ShareSuco = true, Situation = "desligado", Note = "oi" };
        request.AreasTop3.AddRange(["A", "B"]);
        var reply = await grpc.RaiseHand(request, TestServerCallContext.Create());

        Assert.Equal(200, reply.Statuscode);
        Assert.Equal(Talent, service.Raised!.CandidateId);
        Assert.Equal(["A", "B"], service.Raised.AreasTop3);
        Assert.Equal("desligado", service.Raised.Situation);
        Assert.Null(service.Raised.Seniority);   // campo opcional ausente não vira texto vazio
    }

    [Fact]
    public async Task A_primeira_conversa_gratis_repassa_a_configuracao_e_a_cota()
    {
        var (grpc, service) = Create();

        var set = await grpc.SetFirstConversation(new ConvSetFirstConversationRequest { ConsultantId = Consultant.ToString(), Enabled = true, DurationMinutes = 15, MonthlyQuota = 6 }, TestServerCallContext.Create());
        var got = await grpc.GetFirstConversation(new ConvFirstConversationRequest { ConsultantId = Consultant.ToString() }, TestServerCallContext.Create());

        Assert.Equal((FakeConversations.ConsultantOf(Consultant), true, 15, 6), service.FirstSet);   // o id do perfil virou o Consultant.Id
        Assert.True(service.LastCreateIfMissing is not null);
        Assert.Equal(6, set.Data.Unpack<ConvFirstConversationReply>().Remaining);
        var status = got.Data.Unpack<ConvFirstConversationReply>();
        Assert.Equal((true, 15, 4, 1, 3), (status.Enabled, status.DurationMinutes, status.MonthlyQuota, status.UsedThisMonth, status.Remaining));
    }

    [Fact]
    public async Task Propor_a_primeira_conversa_repassa_o_consultor_e_os_erros_viram_codigo()
    {
        var (grpc, service) = Create();
        service.Items.Add(Sample());
        var request = new ConvConsultantActionRequest { ConsultantId = Consultant.ToString(), ConversationId = Guid.NewGuid().ToString() };

        Assert.Equal(200, (await grpc.ProposeFirstConversation(request, TestServerCallContext.Create())).Statuscode);
        Assert.Equal(FakeConversations.ConsultantOf(Consultant), service.FirstProposedBy);   // o id do perfil virou o Consultant.Id

        service.Throw = new ConversationRuleException("As vagas acabaram");
        Assert.Equal(400, (await grpc.ProposeFirstConversation(request, TestServerCallContext.Create())).Statuscode);
    }

    [Fact]
    public async Task O_app_do_consultor_so_conhece_o_perfil_e_o_servico_recebe_o_Consultant_Id()
    {
        var (grpc, service) = Create();
        service.Items.Add(Sample());

        await grpc.SetFirstConversation(new ConvSetFirstConversationRequest { ConsultantId = Consultant.ToString(), Enabled = true, DurationMinutes = 30, MonthlyQuota = 4 }, TestServerCallContext.Create());
        Assert.Equal(Consultant, service.LastResolvedOwner);
        Assert.True(service.LastCreateIfMissing);   // gravar a configuração cria a linha de consultor se faltar

        await grpc.GetFirstConversation(new ConvFirstConversationRequest { ConsultantId = Consultant.ToString() }, TestServerCallContext.Create());
        Assert.False(service.LastCreateIfMissing);  // ler não cria nada
    }

    [Fact]
    public async Task Quem_nunca_foi_consultor_tem_caixa_vazia_e_nao_responde_conversas_de_ninguem()
    {
        var (grpc, service) = Create();
        service.Items.Add(Sample());
        service.UnknownProfile = Consultant;

        var list = await grpc.ListForConsultant(new ConvListForConsultantRequest { ConsultantId = Consultant.ToString() }, TestServerCallContext.Create());
        Assert.Equal(200, list.Statuscode);
        Assert.Empty(list.Data.Unpack<ConvConsultantListReply>().Items);

        var request = new ConvConsultantActionRequest { ConsultantId = Consultant.ToString(), ConversationId = Guid.NewGuid().ToString() };
        Assert.Equal(404, (await grpc.Decline(request, TestServerCallContext.Create())).Statuscode);
        Assert.Equal(404, (await grpc.ProposeFirstConversation(request, TestServerCallContext.Create())).Statuscode);
    }

    [Fact]
    public async Task Gravar_o_publico_traduz_o_perfil_cria_o_consultor_e_junta_os_valores_do_mesmo_tipo()
    {
        var (grpc, service, audience) = CreateWithAudience();
        var request = new ConvSetAudienceRequest { ConsultantId = Consultant.ToString() };
        request.Criteria.Add(new ConvAudienceCriterion { Kind = "situation", Values = { "desligado" } });
        request.Criteria.Add(new ConvAudienceCriterion { Kind = "situation", Values = { "transicao" } });
        request.Criteria.Add(new ConvAudienceCriterion { Kind = "timeSinceLayoff", Values = { "semana" } });

        var reply = await grpc.SetAudience(request, TestServerCallContext.Create());

        Assert.Equal(200, reply.Statuscode);
        Assert.Equal(FakeConversations.ConsultantOf(Consultant), audience.SetFor);
        Assert.True(service.LastCreateIfMissing);
        Assert.Equal(["desligado", "transicao"], audience.SetCriteria!["situation"]);
        var view = reply.Data.Unpack<ConvAudienceReply>();
        Assert.Equal(3, view.MaxCriteria);
        Assert.Equal(["situation", "timeSinceLayoff"], view.SupportedKinds);
    }

    [Fact]
    public async Task A_contagem_abaixo_do_minimo_sai_sem_numero()
    {
        var (grpc, _, audience) = CreateWithAudience();
        audience.Count = new AudienceCount(null, true, 10);

        var preview = await grpc.PreviewAudience(new ConvAudiencePreviewRequest(), TestServerCallContext.Create());

        var count = preview.Data.Unpack<ConvAudienceCountReply>();
        Assert.Equal((true, 0, 10), (count.BelowMinimum, count.Value, count.Minimum));
    }

    [Fact]
    public async Task Ler_o_publico_de_quem_nunca_foi_consultor_mostra_vazio_com_a_contagem_geral_sem_criar_linha()
    {
        var (grpc, service, _) = CreateWithAudience();
        service.UnknownProfile = Consultant;

        var reply = await grpc.GetAudience(new ConvAudienceRequest { ConsultantId = Consultant.ToString() }, TestServerCallContext.Create());

        Assert.Equal(200, reply.Statuscode);
        var view = reply.Data.Unpack<ConvAudienceReply>();
        Assert.Empty(view.Criteria);
        Assert.Equal(42, view.Count.Value);
        Assert.False(service.LastCreateIfMissing);
    }

    [Fact]
    public async Task Criterio_invalido_vira_400()
    {
        var (grpc, _, audience) = CreateWithAudience();
        audience.Throw = new ConversationRuleException("Há uma opção inválida no meu público.");

        Assert.Equal(400, (await grpc.SetAudience(new ConvSetAudienceRequest { ConsultantId = Consultant.ToString() }, TestServerCallContext.Create())).Statuscode);
        Assert.Equal(400, (await grpc.PreviewAudience(new ConvAudiencePreviewRequest(), TestServerCallContext.Create())).Statuscode);
    }

    [Fact]
    public async Task Os_erros_viram_codigo()
    {
        var (grpc, service) = Create();
        service.Items.Add(Sample());
        var request = new ConvConsultantActionRequest { ConsultantId = Consultant.ToString(), ConversationId = Guid.NewGuid().ToString() };

        service.Throw = new ConversationRuleException("limite");
        Assert.Equal(400, (await grpc.Decline(request, TestServerCallContext.Create())).Statuscode);

        service.Throw = new ConversationNotFoundException();
        Assert.Equal(404, (await grpc.Decline(request, TestServerCallContext.Create())).Statuscode);

        service.Throw = new ConversationAlreadyOpenException();
        Assert.Equal(409, (await grpc.Decline(request, TestServerCallContext.Create())).Statuscode);

        Assert.Equal(400, (await grpc.Decline(new ConvConsultantActionRequest { ConsultantId = "x", ConversationId = "y" }, TestServerCallContext.Create())).Statuscode);
    }
}
