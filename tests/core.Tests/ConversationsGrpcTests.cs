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

    private sealed class FakeInvites : IInviteService
    {
        public Exception? Throw { get; set; }
        public (Guid Consultant, Guid Template, string? Text, int Count)? Sent { get; private set; }
        public Guid? OverviewFor { get; private set; }
        public (Guid Candidate, bool? Receive, bool? Seen)? PreferencesSet { get; private set; }
        public (Guid Candidate, Guid Conversation, string Kind)? Closed { get; private set; }
        public (Guid Candidate, Guid Consultant)? Unsilenced { get; private set; }
        public List<Conversation> Items { get; } = [];

        private static InvitePreferencesView Prefs(bool receive = true, bool seen = false) => new(receive, seen, [new SilencedConsultant(Guid.NewGuid(), "Marina Costa", "https://x/m.jpg", new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc))], 3);

        public Task<InviteOverview> GetOverviewAsync(Guid consultantId)
        {
            if (Throw is not null) throw Throw;
            OverviewFor = consultantId;
            return Task.FromResult(new InviteOverview(new InviteQuota(3, 1, 2, true), [new InviteTemplateView(Guid.NewGuid(), "Apresentação", "Oi!", false)], true, 15, 2,
                new InviteReadiness(false, ["Defina o seu público em Conversas."]), new AudienceCount(null, true, 10), new InviteStats(4, 1, 1, 1, 1, 0), 200));
        }

        public Task<InviteSendResult> SendAsync(Guid consultantId, Guid templateId, string? freeText, int count)
        {
            if (Throw is not null) throw Throw;
            Sent = (consultantId, templateId, freeText, count);
            return Task.FromResult(new InviteSendResult(count, count - 1, 1));
        }

        public Task<InvitePreferencesView> GetPreferencesAsync(Guid candidateId) => Task.FromResult(Prefs());
        public Task<InvitePreferencesView> SetPreferencesAsync(Guid candidateId, bool? receiveInvites, bool? noticeSeen)
        {
            if (Throw is not null) throw Throw;
            PreferencesSet = (candidateId, receiveInvites, noticeSeen);
            return Task.FromResult(Prefs(receiveInvites ?? true, noticeSeen ?? false));
        }
        public Task<Conversation> DismissAsync(Guid candidateId, Guid conversationId) { Closed = (candidateId, conversationId, "dismiss"); return Task.FromResult(Items.First()); }
        public Task<Conversation> SilenceAsync(Guid candidateId, Guid conversationId) { Closed = (candidateId, conversationId, "silence"); return Task.FromResult(Items.First()); }
        public Task UnsilenceAsync(Guid candidateId, Guid consultantId) { Unsilenced = (candidateId, consultantId); return Task.CompletedTask; }
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
        return (new core.Services.ConversationsService(service, new FakeAudience(), new FakeInvites()), service);
    }

    private static (core.Services.ConversationsService Grpc, FakeConversations Service, FakeInvites Invites) CreateWithInvites()
    {
        var service = new FakeConversations();
        var invites = new FakeInvites();
        return (new core.Services.ConversationsService(service, new FakeAudience(), invites), service, invites);
    }

    private static (core.Services.ConversationsService Grpc, FakeConversations Service, FakeAudience Audience) CreateWithAudience()
    {
        var service = new FakeConversations();
        var audience = new FakeAudience();
        return (new core.Services.ConversationsService(service, audience, new FakeInvites()), service, audience);
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

    // ---------- O convite (seções 10 e 11) ----------

    [Fact]
    public async Task A_visao_do_convite_traduz_o_perfil_e_leva_cota_modelos_o_que_falta_e_a_contagem_sem_numero()
    {
        var (grpc, _, invites) = CreateWithInvites();

        var reply = await grpc.GetInviteOverview(new ConvInviteOverviewRequest { ConsultantId = Consultant.ToString() }, TestServerCallContext.Create());

        Assert.Equal(200, reply.Statuscode);
        Assert.Equal(FakeConversations.ConsultantOf(Consultant), invites.OverviewFor);   // o id do perfil virou o Consultant.Id
        var view = reply.Data.Unpack<ConvInviteOverviewReply>();
        Assert.Equal((3, 1, 2, true), (view.MonthlyQuota, view.Used, view.Remaining, view.IsNew));
        Assert.Equal(["Apresentação"], view.Templates.Select(x => x.Name));
        Assert.Equal(["Defina o seu público em Conversas."], view.Missing);
        Assert.False(view.CanInvite);
        Assert.Equal((true, 0, 10), (view.ReachableBelowMinimum, view.ReachableValue, view.ReachableMinimum));   // abaixo do mínimo, o número não sai
        Assert.Equal((4, 1, 1, 1, 1, 0), (view.Sent, view.Open, view.Accepted, view.Dismissed, view.Silenced, view.Expired));
        Assert.Equal(200, view.FreeTextMaxChars);
    }

    [Fact]
    public async Task Enviar_convites_traduz_o_perfil_repassa_o_pedido_e_devolve_quantos_foram()
    {
        var (grpc, service, invites) = CreateWithInvites();
        var template = Guid.NewGuid();

        var reply = await grpc.SendInvites(new ConvSendInvitesRequest { ConsultantId = Consultant.ToString(), TemplateId = template.ToString(), FreeText = "Vamos conversar", Count = 3 }, TestServerCallContext.Create());

        Assert.Equal(200, reply.Statuscode);
        Assert.Equal((FakeConversations.ConsultantOf(Consultant), template, "Vamos conversar", 3), invites.Sent);
        var result = reply.Data.Unpack<ConvSendInvitesReply>();
        Assert.Equal((3, 2, 1), (result.Requested, result.Sent, result.QuotaRemaining));
        Assert.False(service.LastCreateIfMissing);   // convidar não cria linha de consultor

        // A frase é opcional: ausente chega como nula.
        await grpc.SendInvites(new ConvSendInvitesRequest { ConsultantId = Consultant.ToString(), TemplateId = template.ToString(), Count = 1 }, TestServerCallContext.Create());
        Assert.Null(invites.Sent!.Value.Text);
    }

    [Fact]
    public async Task Quem_nunca_foi_consultor_nao_ve_nem_envia_convites_e_a_regra_recusada_vira_400()
    {
        var (grpc, service, invites) = CreateWithInvites();
        service.UnknownProfile = Consultant;

        Assert.Equal(404, (await grpc.GetInviteOverview(new ConvInviteOverviewRequest { ConsultantId = Consultant.ToString() }, TestServerCallContext.Create())).Statuscode);
        Assert.Equal(404, (await grpc.SendInvites(new ConvSendInvitesRequest { ConsultantId = Consultant.ToString(), TemplateId = Guid.NewGuid().ToString(), Count = 1 }, TestServerCallContext.Create())).Statuscode);

        service.UnknownProfile = null;
        invites.Throw = new ConversationRuleException("A sua cota de 3 convites deste mês acabou.");
        var refused = await grpc.SendInvites(new ConvSendInvitesRequest { ConsultantId = Consultant.ToString(), TemplateId = Guid.NewGuid().ToString(), Count = 1 }, TestServerCallContext.Create());
        Assert.Equal(400, refused.Statuscode);
        Assert.Contains("cota", refused.Error.Message);
    }

    [Fact]
    public async Task As_preferencias_do_talento_repassam_so_o_que_veio_e_listam_os_silenciados()
    {
        var (grpc, _, invites) = CreateWithInvites();

        var seen = await grpc.SetInvitePreferences(new ConvSetInvitePreferencesRequest { CandidateId = Talent.ToString(), NoticeSeen = true }, TestServerCallContext.Create());
        Assert.Equal((Talent, (bool?)null, (bool?)true), invites.PreferencesSet);   // o que não veio não muda

        await grpc.SetInvitePreferences(new ConvSetInvitePreferencesRequest { CandidateId = Talent.ToString(), ReceiveInvites = false }, TestServerCallContext.Create());
        Assert.Equal((Talent, (bool?)false, (bool?)null), invites.PreferencesSet);

        var view = seen.Data.Unpack<ConvInvitePreferencesReply>();
        Assert.True(view.NoticeSeen);
        Assert.Equal(3, view.MaxPerWeek);
        Assert.Equal(["Marina Costa"], view.Silenced.Select(x => x.Name));
    }

    [Fact]
    public async Task Agora_nao_silenciar_e_desfazer_chegam_ao_servico_com_o_talento_certo()
    {
        var (grpc, _, invites) = CreateWithInvites();
        invites.Items.Add(Sample());
        var conversation = Guid.NewGuid();
        var request = new ConvCandidateActionRequest { CandidateId = Talent.ToString(), ConversationId = conversation.ToString() };

        Assert.Equal(200, (await grpc.DismissInvite(request, TestServerCallContext.Create())).Statuscode);
        Assert.Equal((Talent, conversation, "dismiss"), invites.Closed);
        Assert.Equal(200, (await grpc.SilenceInvite(request, TestServerCallContext.Create())).Statuscode);
        Assert.Equal((Talent, conversation, "silence"), invites.Closed);

        var undo = await grpc.UnsilenceConsultant(new ConvUnsilenceRequest { CandidateId = Talent.ToString(), ConsultantId = Consultant.ToString() }, TestServerCallContext.Create());
        Assert.Equal(200, undo.Statuscode);
        Assert.Equal((Talent, Consultant), invites.Unsilenced);
    }

    [Fact]
    public async Task O_convite_na_caixa_do_talento_leva_o_texto_e_o_motivo_e_na_do_consultor_nao_leva_identidade()
    {
        var (grpc, service, _) = CreateWithInvites();
        var invite = Sample(ConversationStatus.Sent);
        invite.Origin = ConversationOrigin.Invite;
        invite.InviteText = "Oi! Sou consultor aqui.";
        invite.InviteReason = "Combina com você porque você está desligado(a) há 1 a 3 meses.";
        service.Items.Add(invite);

        var candidate = (await grpc.ListForCandidate(new ConvListForCandidateRequest { CandidateId = Talent.ToString() }, TestServerCallContext.Create())).Data.Unpack<ConvCandidateListReply>().Items.Single();
        Assert.Equal("Oi! Sou consultor aqui.", candidate.InviteText);
        Assert.Equal("Combina com você porque você está desligado(a) há 1 a 3 meses.", candidate.InviteReason);
        Assert.Equal("Invite", candidate.Origin);

        // O item do consultor não tem campo de motivo nem de identidade do talento: o motivo é fato da pessoa, só para ela.
        var consultantItem = core.Services.ConversationMapper.ToConsultantItem(invite);
        var names = ConvConsultantItem.Descriptor.Fields.InFieldNumberOrder().Select(f => f.Name.ToLowerInvariant()).ToList();
        Assert.DoesNotContain(names, n => n.Contains("reason") || n.Contains("candidate"));
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
