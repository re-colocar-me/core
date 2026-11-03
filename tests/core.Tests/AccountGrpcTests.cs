using core.domain.Interfaces.Infrastructure;
using core.domain.Services;
using core.domain.Tests;
using Microsoft.Extensions.Logging.Abstractions;

namespace core.Tests;

/// <summary>RPC de exclusão de conta de talento (core/Services/AccountService.cs): o mapeamento de resultado e erro para o <c>defaultReply</c>.</summary>
public class AccountGrpcTests
{
    private const string Ana = "11111111-1111-1111-1111-111111111111";

    private readonly FakeAccountErasureRepository _database = new();
    private readonly FakePersonalDocumentsEraser _documents = new();
    private readonly core.Services.AccountService _grpc;

    public AccountGrpcTests()
    {
        _grpc = new core.Services.AccountService(new AccountErasureService(_database, _documents, NullLogger<AccountErasureService>.Instance));
    }

    private Task<defaultReply> Delete(string profileId) => _grpc.DeleteTalentAccount(new DeleteTalentAccountRequest { ProfileId = profileId }, TestServerCallContext.Create());

    [Fact]
    public async Task Devolve_as_contagens_no_corpo_da_resposta()
    {
        var reply = await Delete(Ana);

        Assert.Equal(core.domain.Constants.SuccessStatusCode, reply.Statuscode);
        var data = reply.Data.Unpack<DeleteTalentAccountReply>();
        Assert.True(data.ProfileFound);
        Assert.True(data.TalentRemoved);
        Assert.Equal((2, 14, 3, 5), (data.Cycles, data.EventLogs, data.Notifications, data.TutorialRecords));
        Assert.Equal((1, 2, 1, 3), (data.ConnectionsAnonymized, data.PurchasesAnonymized, data.SchedulesAnonymized, data.Documents));
    }

    [Theory]
    [InlineData("")]
    [InlineData("nao-e-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Id_invalido_e_400_sem_apagar_nada(string profileId)
    {
        var reply = await Delete(profileId);

        Assert.Equal(400, reply.Statuscode);
        Assert.Equal(400, reply.Error.Code);
        Assert.Empty(_database.Calls);
    }

    [Fact]
    public async Task Perfil_de_consultor_e_409()
    {
        _database.Throws = new AccountErasureRefusedException("Este perfil não é só de talento: a exclusão foi recusada.");

        var reply = await Delete(Ana);

        Assert.Equal(409, reply.Statuscode);
        Assert.Equal(409, reply.Error.Code);
        Assert.Contains("talento", reply.Error.Message);
        Assert.Empty(_documents.Calls);
    }

    [Fact]
    public async Task Falha_inesperada_vira_statuscode_de_falha_com_a_mensagem()
    {
        _documents.Throws = new HttpRequestException("es fora do ar");

        var reply = await Delete(Ana);

        Assert.Equal(core.domain.Constants.FailStatusCode, reply.Statuscode);
        Assert.Contains("es fora do ar", reply.Error.Message);
    }
}
