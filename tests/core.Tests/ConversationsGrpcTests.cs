using core.domain.Entities;
using core.domain.Enums;
using core.domain.Interfaces.Infrastructure;
using core.domain.Interfaces.Services;
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
        public Task<Conversation> ConfirmAsync(Guid candidateId, Guid conversationId, CandidateIdentity identity) => Result();
        public Task<Conversation> WithdrawAsync(Guid candidateId, Guid conversationId) => Result();
        public Task<Dictionary<Guid, ConversationConsultant>> GetConsultantsAsync(IEnumerable<Guid> ids)
            => Task.FromResult(ids.Distinct().ToDictionary(x => x, _ => new ConversationConsultant("Marina Costa", "https://x/m.jpg")));
        public Task<List<Conversation>> ListForCandidateAsync(Guid candidateId) => Task.FromResult(Items.ToList());
        public Task<List<Conversation>> ListForConsultantAsync(Guid consultantId, ConversationOrigin? origin, ConversationStatus? status)
            => Task.FromResult(Items.Where(x => (origin is null || x.Origin == origin) && (status is null || x.Status == status)).ToList());
        public Task<int> ExpireDueAsync() => Task.FromResult(0);
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
        return (new core.Services.ConversationsService(service), service);
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
