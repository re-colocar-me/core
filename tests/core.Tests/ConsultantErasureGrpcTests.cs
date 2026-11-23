using core.domain.Interfaces.Infrastructure;
using core.domain.Interfaces.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace core.Tests;

/// <summary>
/// A EXECUÇÃO da exclusão de conta do consultor na porta gRPC (docs/specs/exclusao-de-conta-consultor.md, EC2). É irreversível: a trava do <c>core</c> vem desligada, só <c>true</c> liga, e cada pré-condição
/// que barra vira 409 com a mensagem, sem apagar nada.
/// </summary>
public class ConsultantErasureGrpcTests
{
    private static readonly Guid Profile = Guid.Parse("cccccccc-0000-0000-0000-000000000001");

    private sealed class FakeErasure : IConsultantErasureService
    {
        public List<Guid> Due { get; set; } = [];
        public int? AskedTake { get; private set; }
        public int DueCalls { get; private set; }

        public Task<IReadOnlyList<Guid>> ListDueAsync(int take, CancellationToken ct = default)
        {
            DueCalls++;
            AskedTake = take;
            return Task.FromResult<IReadOnlyList<Guid>>(Due);
        }

        public int EraseCalls { get; private set; }
        public int CompleteCalls { get; private set; }
        public ConsultantErasureException? Throw { get; set; }
        public Exception? Crash { get; set; }
        public ConsultantErasureResult Result { get; set; } = new(new ConsultantErasureReport(false, true, 2, 1, 3, 4, 5, 6, 7, 8, 9, 1, 2), 2);

        public Task<ConsultantErasureResult> EraseDataAsync(Guid profileId, CancellationToken ct = default)
        {
            EraseCalls++;
            if (Crash is not null) throw Crash;
            return Throw is not null ? throw Throw : Task.FromResult(Result);
        }

        public Task CompleteAsync(Guid profileId, CancellationToken ct = default)
        {
            CompleteCalls++;
            if (Crash is not null) throw Crash;
            return Throw is not null ? throw Throw : Task.CompletedTask;
        }
    }

    private readonly FakeErasure _erasure = new();

    private core.Services.ConsultantDeletionGrpcService Grpc(string? enabled)
    {
        var values = enabled is null ? new Dictionary<string, string?>() : new Dictionary<string, string?> { ["ConsultantDeletion:ExecutionEnabled"] = enabled };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return new core.Services.ConsultantDeletionGrpcService(null!, _erasure, configuration, NullLogger<core.Services.ConsultantDeletionGrpcService>.Instance);
    }

    // ---- a trava do core ----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("")]
    [InlineData("sim")]
    [InlineData("1")]
    public async Task Desligada_por_padrao_e_com_valor_que_nao_e_true_nada_e_executado_nem_chamado(string? enabled)
    {
        var erase = await Grpc(enabled).EraseData(new CdProfileRequest { ProfileId = Profile.ToString() }, TestServerCallContext.Create());
        var complete = await Grpc(enabled).Complete(new CdProfileRequest { ProfileId = Profile.ToString() }, TestServerCallContext.Create());

        Assert.Equal(501, erase.Statuscode);
        Assert.Equal(501, complete.Statuscode);
        Assert.Equal(0, _erasure.EraseCalls);
        Assert.Equal(0, _erasure.CompleteCalls);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    public async Task So_true_liga_a_execucao(string enabled)
    {
        var reply = await Grpc(enabled).EraseData(new CdProfileRequest { ProfileId = Profile.ToString() }, TestServerCallContext.Create());

        Assert.Equal(200, reply.Statuscode);
        Assert.Equal(1, _erasure.EraseCalls);
    }

    [Fact]
    public async Task A_trava_e_lida_a_cada_chamada_e_o_perfil_invalido_nem_chega_a_ela()
    {
        var bad = await Grpc("true").EraseData(new CdProfileRequest { ProfileId = "lixo" }, TestServerCallContext.Create());

        Assert.Equal(400, bad.Statuscode);
        Assert.Equal(0, _erasure.EraseCalls);
    }

    // ---- o resultado e os erros ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task O_relatorio_chega_com_cada_contagem_e_os_documentos_sem_nada_que_identifique()
    {
        var reply = await Grpc("true").EraseData(new CdProfileRequest { ProfileId = Profile.ToString() }, TestServerCallContext.Create());

        var r = reply.Data.Unpack<CdErasureReply>();
        Assert.Equal((false, true, 2, 1, 3, 4), (r.AlreadyCompleted, r.ConsultantFound, r.Availabilities, r.Offerings, r.Reviews, r.Tratativas));
        Assert.Equal((5, 6, 7, 8, 9, 1, 2, 2), (r.ConnectionsAnonymized, r.ConversationsClosed, r.ConversationsAnonymized, r.Notifications, r.TutorialRecords, r.Leads, r.SilencingsRemoved, r.Documents));

        var names = CdErasureReply.Descriptor.Fields.InFieldNumberOrder().Select(f => f.Name.ToLowerInvariant()).ToList();
        Assert.DoesNotContain(names, n => n.Contains("email") || n.Contains("name") || n.Contains("phone") || n.Contains("profileid"));
    }

    [Theory]
    [InlineData(ConsultantErasureError.NoRequest, 409)]
    [InlineData(ConsultantErasureError.GraceNotOver, 409)]
    [InlineData(ConsultantErasureError.AccessedAfterRequest, 409)]
    [InlineData(ConsultantErasureError.Blocked, 409)]
    [InlineData(ConsultantErasureError.DataNotErased, 409)]
    [InlineData(ConsultantErasureError.NotConsultant, 403)]
    [InlineData(ConsultantErasureError.NotFound, 404)]
    public async Task Cada_pre_condicao_que_barra_vira_o_codigo_certo_com_a_mensagem(ConsultantErasureError error, int code)
    {
        _erasure.Throw = new ConsultantErasureException(error, "Motivo da recusa.");

        var erase = await Grpc("true").EraseData(new CdProfileRequest { ProfileId = Profile.ToString() }, TestServerCallContext.Create());
        var complete = await Grpc("true").Complete(new CdProfileRequest { ProfileId = Profile.ToString() }, TestServerCallContext.Create());

        Assert.Equal(code, erase.Statuscode);
        Assert.Equal(code, complete.Statuscode);
        Assert.Equal("Motivo da recusa.", erase.Error.Message);
    }

    [Fact]
    public async Task Perfil_que_nao_e_de_consultor_recusado_pelo_repositorio_vira_409()
    {
        _erasure.Crash = new AccountErasureRefusedException("Este perfil não é de consultor: a exclusão foi recusada.");

        var reply = await Grpc("true").EraseData(new CdProfileRequest { ProfileId = Profile.ToString() }, TestServerCallContext.Create());

        Assert.Equal(409, reply.Statuscode);
    }

    [Fact]
    public async Task Falha_inesperada_vira_500_com_mensagem_generica_sem_o_texto_do_banco()
    {
        _erasure.Crash = new InvalidOperationException("password authentication failed for user core_prod");

        var reply = await Grpc("true").EraseData(new CdProfileRequest { ProfileId = Profile.ToString() }, TestServerCallContext.Create());

        Assert.Equal(500, reply.Statuscode);
        Assert.DoesNotContain("password", reply.Error.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("sim")]
    public async Task A_lista_dos_vencidos_tambem_fica_atras_da_trava_e_nada_e_lido_com_ela_desligada(string? enabled)
    {
        _erasure.Due = [Profile];

        var reply = await Grpc(enabled).ListDue(new CdDueRequest { Take = 10 }, TestServerCallContext.Create());

        Assert.Equal(501, reply.Statuscode);
        Assert.Equal(0, _erasure.DueCalls);
    }

    [Fact]
    public async Task Ligada_a_lista_devolve_so_os_ids_dos_perfis_vencidos_e_repassa_o_tamanho()
    {
        var other = Guid.Parse("cccccccc-0000-0000-0000-000000000002");
        _erasure.Due = [Profile, other];

        var reply = await Grpc("true").ListDue(new CdDueRequest { Take = 25 }, TestServerCallContext.Create());

        Assert.Equal(200, reply.Statuscode);
        Assert.Equal([Profile.ToString(), other.ToString()], reply.Data.Unpack<CdDueReply>().ProfileIds);
        Assert.Equal(25, _erasure.AskedTake);
        var names = CdDueReply.Descriptor.Fields.InFieldNumberOrder().Select(f => f.Name.ToLowerInvariant()).ToList();
        Assert.Equal(["profileids"], names);                                           // nada além de ids
    }

    [Fact]
    public async Task Aviso_de_conclusao_que_o_broker_nao_guardou_vira_503_para_o_job_repetir_na_proxima_noite()
    {
        _erasure.Throw = new ConsultantErasureException(ConsultantErasureError.NoticeNotPublished, "broker fora do ar");

        var reply = await Grpc("true").Complete(new CdProfileRequest { ProfileId = Profile.ToString() }, TestServerCallContext.Create());

        Assert.Equal(503, reply.Statuscode);
    }

    [Fact]
    public async Task Concluir_chama_a_conclusao_e_devolve_200()
    {
        var reply = await Grpc("true").Complete(new CdProfileRequest { ProfileId = Profile.ToString() }, TestServerCallContext.Create());

        Assert.Equal(200, reply.Statuscode);
        Assert.Equal(1, _erasure.CompleteCalls);
    }

    [Fact]
    public void A_trava_do_core_sai_desligada_no_deployment()
    {
        var yaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "k8s", "20-core.yaml"));

        Assert.Matches(@"ConsultantDeletion__ExecutionEnabled\s*\r?\n\s*value:\s*""false""", yaml);
    }
}
