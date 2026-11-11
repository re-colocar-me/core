using core.domain;
using core.domain.Entities;
using core.domain.Enums;
using core.domain.Interfaces.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace core.Tests;

/// <summary>RPCs dos leads (core/Services/LeadsService.cs): o que o gRPC repassa ao domínio, incluindo a origem da visita (docs/specs/plano-aquisicao-marketing.md, seção 18).</summary>
public class LeadsGrpcTests
{
    private sealed class FakeLeadService : ILeadService
    {
        public List<NewLead> Created { get; } = [];
        public List<Lead> ToList { get; } = [];
        public Exception? Throw { get; set; }

        public Task CreateAsync(NewLead input)
        {
            if (Throw is not null) throw Throw;
            Created.Add(input);
            return Task.CompletedTask;
        }

        public Task<List<Lead>> ListAsync(LeadKind kind) => Task.FromResult(ToList.Where(x => x.Kind == kind).ToList());
    }

    private static (core.Services.LeadsService Grpc, FakeLeadService Service) Create()
    {
        var service = new FakeLeadService();
        return (new core.Services.LeadsService(service, NullLogger<core.Services.LeadsService>.Instance), service);
    }

    private static CreateLeadRequest Request(string kind = "Candidato") => new()
    {
        Kind = kind, Name = "Joao Lima", Email = "joao@exemplo.com", Phone = "", Consent = true,
        ConsentTextVersion = "candidato-aviso-v1", ConsentText = "Concordo.", Source = "landing:lista-espera-candidato",
        UtmSource = "linkedin", UtmMedium = "social", UtmCampaign = "candidato-teste-2026-11", UtmContent = "pagina-1", Referrer = "www.linkedin.com", EntryPage = "/"
    };

    [Fact]
    public async Task Candidato_chega_ao_dominio_com_a_origem_da_visita()
    {
        var (grpc, service) = Create();

        var reply = await grpc.CreateLead(Request(), TestServerCallContext.Create());

        Assert.Equal(Constants.SuccessStatusCode, reply.Statuscode);
        var created = Assert.Single(service.Created);
        Assert.Equal(LeadKind.Candidato, created.Kind);
        Assert.Equal("linkedin", created.Origin!.UtmSource);
        Assert.Equal("social", created.Origin.UtmMedium);
        Assert.Equal("candidato-teste-2026-11", created.Origin.UtmCampaign);
        Assert.Equal("pagina-1", created.Origin.UtmContent);
        Assert.Equal("www.linkedin.com", created.Origin.Referrer);
        Assert.Equal("/", created.Origin.EntryPage);
    }

    [Fact]
    public async Task Tipo_aceita_qualquer_caixa()
    {
        var (grpc, service) = Create();

        await grpc.CreateLead(Request("consultor"), TestServerCallContext.Create());

        Assert.Equal(LeadKind.Consultor, Assert.Single(service.Created).Kind);
    }

    [Fact]
    public async Task Tipo_desconhecido_falha_com_a_mensagem_do_dominio()
    {
        var (grpc, service) = Create();

        var reply = await grpc.CreateLead(Request("Parceiro"), TestServerCallContext.Create());

        Assert.Equal(Constants.FailStatusCode, reply.Statuscode);
        Assert.Equal("Tipo de lead inválido.", reply.Error.Message);
        Assert.Empty(service.Created);
    }

    [Fact]
    public async Task Dado_invalido_devolve_a_mensagem_do_dominio_e_falha_de_infraestrutura_devolve_a_generica()
    {
        var (grpc, service) = Create();

        service.Throw = new ArgumentException("Informe um e-mail válido.");
        var invalid = await grpc.CreateLead(Request(), TestServerCallContext.Create());
        Assert.Equal("Informe um e-mail válido.", invalid.Error.Message);

        service.Throw = new InvalidOperationException("connection string com senha");
        var failure = await grpc.CreateLead(Request(), TestServerCallContext.Create());
        Assert.Equal(core.Services.LeadsService.UnavailableMessage, failure.Error.Message);
        Assert.DoesNotContain("senha", failure.Error.Message);
    }

    [Fact]
    public async Task Listagem_devolve_a_origem_de_cada_lead()
    {
        var (grpc, service) = Create();
        service.ToList.Add(new Lead
        {
            Id = Guid.NewGuid(), Kind = LeadKind.Candidato, Name = "Joao Lima", Email = "joao@exemplo.com", ConsentedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow,
            UtmSource = "linkedin", UtmCampaign = "candidato-teste-2026-11", UtmContent = "pagina-1", Referrer = "www.linkedin.com", EntryPage = "/"
        });
        service.ToList.Add(new Lead { Id = Guid.NewGuid(), Kind = LeadKind.Consultor, Name = "Maria", Email = "maria@exemplo.com", ConsentedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow });

        var reply = await grpc.ListLeads(new ListLeadsRequest { Kind = "Candidato" }, TestServerCallContext.Create());

        Assert.Equal(Constants.SuccessStatusCode, reply.Statuscode);
        var item = Assert.Single(reply.Data.Unpack<ListLeadsReply>().Items);
        Assert.Equal("Candidato", item.Kind);
        Assert.Equal("linkedin", item.UtmSource);
        Assert.Equal("candidato-teste-2026-11", item.UtmCampaign);
        Assert.Equal("pagina-1", item.UtmContent);
        Assert.Equal("www.linkedin.com", item.Referrer);
        Assert.Equal("/", item.EntryPage);
        Assert.Equal(string.Empty, item.UtmMedium);
    }
}
