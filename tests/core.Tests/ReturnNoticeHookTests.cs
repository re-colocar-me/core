using System.Reflection;
using core.domain;
using core.domain.Interfaces.Services;
using core.domain.Tests;
using Microsoft.Extensions.Time.Testing;

namespace core.Tests;

/// <summary>Serviço de aviso por proxy: lembra as chamadas e pode falhar de propósito.</summary>
public class RecordingReturnNotice : DispatchProxy
{
    public List<(Guid Candidate, string Display, List<Guid> Consented, int Cycle)> Notices { get; } = [];
    public int Cleared { get; set; }
    public bool Fail { get; set; }
    public List<NotifiableConsultant> Listed { get; } = [];

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        switch (targetMethod!.Name)
        {
            case nameof(IReturnNoticeService.NoticeAsync):
                if (Fail) throw new InvalidOperationException("sino fora do ar");
                Notices.Add(((Guid)args![0]!, (string)args[1]!, ((IReadOnlyCollection<Guid>)args[2]!).ToList(), (int)args[3]!));
                return Task.FromResult(((IReadOnlyCollection<Guid>)args[2]!).Count);
            case nameof(IReturnNoticeService.ClearMarksAsync):
                Cleared++;
                return Task.CompletedTask;
            case nameof(IReturnNoticeService.ListNotifiableAsync):
                return Task.FromResult(Listed.ToList());
            default:
                throw new NotSupportedException(targetMethod.Name);
        }
    }
}

/// <summary>Voltar a buscar (reativacao-jornada-talent.md, RA.5 a RA.8): o aviso só sai com o consentimento, uma vez, e a marca some quando a nova busca termina.</summary>
public class ReturnNoticeHookTests
{
    private static readonly Guid Profile = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Carla = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemoryJourneyRepository _repo = new();
    private readonly RecordingReturnNotice _notice;
    private readonly core.Services.JourneyService _grpc;

    public ReturnNoticeHookTests()
    {
        var profiles = new FakeProfiles();
        profiles.Add(Profile, "Maria", "Silva");
        var proxy = DispatchProxy.Create<IReturnNoticeService, RecordingReturnNotice>();
        _notice = (RecordingReturnNotice)(object)proxy;
        _grpc = new core.Services.JourneyService(new domain.Services.JourneyService(_repo, _clock), _clock, profiles, null, proxy);
    }

    private async Task Recolocated()
    {
        await _grpc.SetJourneySituation(new SetJourneySituationRequest { ProfileId = Profile.ToString(), Situation = "desligado", TimeSinceLayoff = "semana" }, null!);
        await _grpc.ConfirmRecolocation(new ConfirmRecolocationRequest { ProfileId = Profile.ToString(), OutcomeType = "clt" }, null!);
    }

    private Task<defaultReply> Start(string origin = "continue_searching", params string[] consented)
    {
        var request = new StartNewCycleRequest { ProfileId = Profile.ToString(), Origin = origin };
        request.NoticeConsultantIds.AddRange(consented);
        return _grpc.StartNewCycle(request, null!);
    }

    [Fact]
    public async Task Com_consentimento_o_core_avisa_com_o_nome_abreviado_e_o_numero_do_ciclo_novo()
    {
        await Recolocated();

        var reply = await Start("reactivation", Carla.ToString());

        Assert.Equal(Constants.SuccessStatusCode, reply.Statuscode);
        var notice = Assert.Single(_notice.Notices);
        Assert.Equal((Profile, "Maria S.", 2), (notice.Candidate, notice.Display, notice.Cycle));
        Assert.Equal([Carla], notice.Consented);
    }

    [Fact]
    public async Task Sem_consentimento_ninguem_e_avisado()
    {
        await Recolocated();

        var reply = await Start();

        Assert.Equal(Constants.SuccessStatusCode, reply.Statuscode);
        Assert.Empty(_notice.Notices);
    }

    [Fact]
    public async Task Se_o_aviso_falhar_a_nova_busca_ja_esta_aberta()
    {
        await Recolocated();
        _notice.Fail = true;

        var reply = await Start("continue_searching", Carla.ToString());

        Assert.Equal(Constants.SuccessStatusCode, reply.Statuscode);
        Assert.Equal("active", reply.Data.Unpack<JourneyStateReply>().Cycle.Status);
    }

    [Fact]
    public async Task Um_id_que_nao_e_perfil_nao_derruba_a_abertura_e_ninguem_e_avisado()
    {
        await Recolocated();

        var reply = await Start("continue_searching", "isto-nao-e-guid");

        Assert.Equal(Constants.SuccessStatusCode, reply.Statuscode);
        Assert.Empty(_notice.Notices);
    }

    [Fact]
    public async Task A_lista_para_a_tela_traz_os_nomes_abreviados_e_a_versao_do_texto()
    {
        _notice.Listed.Add(new NotifiableConsultant(Carla, "Carla M."));

        var data = (await _grpc.ListReturnNotifiable(new JourneyProfileRequest { ProfileId = Profile.ToString() }, null!)).Data.Unpack<ListReturnNotifiableReply>();

        Assert.Equal(("Carla M.", Carla.ToString()), (data.Consultants.Single().DisplayName, data.Consultants.Single().ProfileId));
        Assert.Equal(core.domain.Services.ReturnNoticeService.ConsentTextVersion, data.ConsentTextVersion);
    }

    [Fact]
    public async Task Confirmar_a_recolocacao_tira_a_marca_dos_cartoes()
    {
        await _grpc.SetJourneySituation(new SetJourneySituationRequest { ProfileId = Profile.ToString(), Situation = "desligado", TimeSinceLayoff = "semana" }, null!);

        await _grpc.ConfirmRecolocation(new ConfirmRecolocationRequest { ProfileId = Profile.ToString(), OutcomeType = "clt" }, null!);

        Assert.Equal(1, _notice.Cleared);
    }
}
