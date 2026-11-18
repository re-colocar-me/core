using core.domain.Enums;
using core.domain.Interfaces.Services;
using core.infrastructure;
using core.infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace core.Tests;

/// <summary>O pedido de exclusão de conta do consultor no <c>core</c> (docs/specs/exclusao-de-conta-consultor.md, EC1): o SQL das contagens e a tradução dos erros na porta gRPC.</summary>
public class ConsultantDeletionGrpcTests
{
    private static readonly Guid Profile = Guid.Parse("cccccccc-0000-0000-0000-000000000001");

    // ---- o SQL -------------------------------------------------------------------------------------------------------------------

    private static ReColocarmeContext Context()
        => new(new DbContextOptionsBuilder<ReColocarmeContext>().UseNpgsql("Host=localhost;Database=nao-existe;Username=x;Password=x").Options);

    [Fact]
    public void Os_atendimentos_em_andamento_ligam_tratativa_conexao_consultor_e_compra_ativa_nos_tres_estagios()
    {
        using var context = Context();

        var sql = new ConsultantDeletionRepository(context).OpenEngagementsQuery(Profile).ToQueryString();

        Assert.Contains("FROM \"Tratativas\"", sql);
        Assert.Contains("JOIN \"Connections\"", sql);
        Assert.Contains("JOIN \"Consultants\"", sql);
        Assert.Contains("JOIN \"ServicePurchases\"", sql);
        Assert.Contains("\"ProfileId\" =", sql);                                   // pelo perfil do consultor, não pelo id do consultor
        // Só compra ativa (0) e só os estágios CompraRealizada (2), ProcessoAtivo (3) e ProcessoSuspenso (4): nem Conectado, nem Finalizado, nem Cancelado, nem Recolocado.
        Assert.Contains(((int)ServicePurchaseStatus.Active).ToString(), sql);
        Assert.Matches(@"IN \(2, 3, 4\)", sql);
    }

    [Fact]
    public void As_sessoes_futuras_usam_o_perfil_como_dono_excluem_as_canceladas_e_comparam_o_fim_com_o_relogio_dos_horarios()
    {
        using var context = Context();

        var sql = new ConsultantDeletionRepository(context).FutureSessionsQuery(Profile, new DateTime(2026, 11, 18, 9, 0, 0)).ToQueryString();

        Assert.Contains("FROM \"Schedules\"", sql);
        Assert.Contains("\"OwnerId\" =", sql);
        Assert.Contains("\"Status\" <>", sql);
        Assert.Contains("\"EndTime\" >", sql);
        Assert.Contains("2026-11-18T09:00:00", sql);                              // o relógio dos horários (Brasília), não UTC
    }

    [Fact]
    public void As_consultas_nao_selecionam_nenhum_dado_pessoal_so_ids_para_contar()
    {
        using var context = Context();
        var repository = new ConsultantDeletionRepository(context);

        foreach (var sql in new[] { repository.OpenEngagementsQuery(Profile).ToQueryString(), repository.FutureSessionsQuery(Profile, DateTime.UtcNow).ToQueryString() })
        {
            var select = sql[sql.IndexOf("SELECT ", StringComparison.Ordinal)..sql.IndexOf("FROM", StringComparison.Ordinal)];
            foreach (var personal in new[] { "Email", "Name", "Telephone", "PictureUrl", "Observacao", "Notes", "Subject", "Candidate" })
                Assert.DoesNotContain(personal, select);
        }
    }

    // ---- a porta gRPC -------------------------------------------------------------------------------------------------------------

    private sealed class FakeDeletion : IConsultantDeletionService
    {
        public ConsultantDeletionException? Throw { get; set; }
        public Exception? Crash { get; set; }
        public string? LastConfirmation { get; private set; }
        public Guid? LastProfile { get; private set; }

        public ConsultantDeletionStatus Result { get; set; } = new(true, new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc), false, 2, 1);

        private Task<ConsultantDeletionStatus> Run(Guid profileId)
        {
            LastProfile = profileId;
            if (Crash is not null) throw Crash;
            return Throw is not null ? throw Throw : Task.FromResult(Result);
        }

        public Task<ConsultantDeletionStatus> GetAsync(Guid profileId) => Run(profileId);
        public Task<ConsultantDeletionStatus> RequestAsync(Guid profileId, string? confirmation) { LastConfirmation = confirmation; return Run(profileId); }
        public Task<ConsultantDeletionStatus> CancelAsync(Guid profileId) => Run(profileId);
    }

    private readonly FakeDeletion _deletion = new();
    // A execução (EraseData e Complete) tem os testes dela em ConsultantErasureGrpcTests; aqui só o pedido, que não toca no apagador nem na configuração.
    private core.Services.ConsultantDeletionGrpcService Grpc() => new(_deletion, null!, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), NullLogger<core.Services.ConsultantDeletionGrpcService>.Instance);

    [Fact]
    public async Task O_estado_chega_completo_com_datas_ISO_e_o_que_impede()
    {
        var reply = await Grpc().GetStatus(new CdProfileRequest { ProfileId = Profile.ToString() }, TestServerCallContext.Create());

        Assert.Equal(200, reply.Statuscode);
        var s = reply.Data.Unpack<CdStatusReply>();
        Assert.Equal((true, false, 2, 1), (s.Requested, s.Completed, s.OpenEngagements, s.FutureSessions));
        Assert.False(s.Changed);
        Assert.Equal("2026-11-18T12:00:00.0000000Z", s.RequestedAt);
        Assert.Equal("2026-11-25T12:00:00.0000000Z", s.ScheduledFor);
    }

    [Fact]
    public async Task Sem_pedido_as_datas_vem_vazias_e_nao_a_data_zero()
    {
        _deletion.Result = new ConsultantDeletionStatus(false, null, null, false, 0, 0);

        var s = (await Grpc().GetStatus(new CdProfileRequest { ProfileId = Profile.ToString() }, TestServerCallContext.Create())).Data.Unpack<CdStatusReply>();

        Assert.False(s.Requested);
        Assert.Equal(string.Empty, s.RequestedAt);
        Assert.Equal(string.Empty, s.ScheduledFor);
    }

    [Fact]
    public async Task Pedir_repassa_o_perfil_e_a_palavra_e_cancelar_devolve_o_estado()
    {
        await Grpc().Request(new CdRequest { ProfileId = Profile.ToString(), Confirmation = "EXCLUIR" }, TestServerCallContext.Create());
        var cancel = await Grpc().Cancel(new CdProfileRequest { ProfileId = Profile.ToString() }, TestServerCallContext.Create());

        Assert.Equal("EXCLUIR", _deletion.LastConfirmation);
        Assert.Equal(Profile, _deletion.LastProfile);
        Assert.Equal(200, cancel.Statuscode);
    }

    [Fact]
    public async Task O_campo_changed_chega_para_o_login_saber_se_cancelou()
    {
        _deletion.Result = _deletion.Result with { Changed = true };

        var s = (await Grpc().Cancel(new CdProfileRequest { ProfileId = Profile.ToString() }, TestServerCallContext.Create())).Data.Unpack<CdStatusReply>();

        Assert.True(s.Changed);
    }

    [Theory]
    [InlineData(ConsultantDeletionError.BadConfirmation, 400)]
    [InlineData(ConsultantDeletionError.NotConsultant, 403)]
    [InlineData(ConsultantDeletionError.NotFound, 404)]
    [InlineData(ConsultantDeletionError.AlreadyDeleted, 409)]
    public async Task Cada_recusa_vira_o_codigo_certo_com_a_mensagem_para_a_pessoa(ConsultantDeletionError error, int code)
    {
        _deletion.Throw = new ConsultantDeletionException(error, "Mensagem para a pessoa.");

        var reply = await Grpc().Request(new CdRequest { ProfileId = Profile.ToString(), Confirmation = "x" }, TestServerCallContext.Create());

        Assert.Equal(code, reply.Statuscode);
        Assert.Equal(code, reply.Error.Code);
        Assert.Equal("Mensagem para a pessoa.", reply.Error.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("lixo")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Perfil_invalido_vira_400_e_o_servico_nem_e_chamado(string profile)
    {
        var reply = await Grpc().GetStatus(new CdProfileRequest { ProfileId = profile }, TestServerCallContext.Create());

        Assert.Equal(400, reply.Statuscode);
        Assert.Null(_deletion.LastProfile);
    }

    [Fact]
    public async Task Falha_inesperada_vira_500_com_mensagem_generica_sem_o_texto_do_banco()
    {
        _deletion.Crash = new InvalidOperationException("password authentication failed for user core_prod");

        var reply = await Grpc().Request(new CdRequest { ProfileId = Profile.ToString(), Confirmation = "EXCLUIR" }, TestServerCallContext.Create());

        Assert.Equal(500, reply.Statuscode);
        Assert.DoesNotContain("password", reply.Error.Message);
    }

    [Fact]
    public void O_contrato_nao_tem_nenhum_campo_com_dado_pessoal()
    {
        var names = CdStatusReply.Descriptor.Fields.InFieldNumberOrder().Select(f => f.Name.ToLowerInvariant()).ToList();

        Assert.DoesNotContain(names, n => n.Contains("email") || n.Contains("name") || n.Contains("phone") || n.Contains("picture") || n.Contains("candidate"));
    }
}
