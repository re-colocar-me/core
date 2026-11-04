using System.Reflection;
using core.domain;
using core.domain.Interfaces.Services;
using core.domain.Tests;
using Microsoft.Extensions.Time.Testing;

namespace core.Tests;

/// <summary>Serviço do consultor por proxy: lembra as chamadas a <c>MarcarRecolocado</c> e pode falhar de propósito.</summary>
public class RecordingConsultants : DispatchProxy
{
    public List<Guid> Marked { get; } = [];
    public bool Fail { get; set; }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod!.Name == nameof(IConsultantServices.MarcarRecolocado))
        {
            if (Fail) throw new InvalidOperationException("banco fora do ar");
            Marked.Add((Guid)args![0]!);
            return Task.FromResult(1);
        }
        throw new NotSupportedException(targetMethod.Name);
    }
}

/// <summary>D6.8: ao confirmar a recolocação, os cartões do talento em Processo Finalizado passam para Recolocados no CRM do consultor.</summary>
public class RecolocadoHookTests
{
    private static readonly string Profile = "11111111-1111-1111-1111-111111111111";

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemoryJourneyRepository _repo = new();
    private readonly RecordingConsultants _consultants;
    private readonly core.Services.JourneyService _grpc;

    public RecolocadoHookTests()
    {
        var proxy = DispatchProxy.Create<IConsultantServices, RecordingConsultants>();
        _consultants = (RecordingConsultants)(object)proxy;
        _grpc = new core.Services.JourneyService(new domain.Services.JourneyService(_repo, _clock), _clock, new FakeProfiles(), proxy);
    }

    private async Task StartCycle()
        => await _grpc.SetJourneySituation(new SetJourneySituationRequest { ProfileId = Profile, Situation = "desligado", TimeSinceLayoff = "semana" }, null!);

    private Task<defaultReply> Confirm(string type = "clt")
        => _grpc.ConfirmRecolocation(new ConfirmRecolocationRequest { ProfileId = Profile, OutcomeType = type }, null!);

    [Fact]
    public async Task Confirmar_move_os_cartoes_do_proprio_talento()
    {
        await StartCycle();

        var reply = await Confirm();

        Assert.Equal(Constants.SuccessStatusCode, reply.Statuscode);
        Assert.Equal(Guid.Parse(Profile), Assert.Single(_consultants.Marked));
    }

    [Fact]
    public async Task Tipo_de_desfecho_invalido_nao_move_nada()
    {
        await StartCycle();

        var reply = await Confirm("tipo-inventado");

        Assert.Equal(Constants.FailStatusCode, reply.Statuscode);
        Assert.Empty(_consultants.Marked);
    }

    [Fact]
    public async Task Se_mover_os_cartoes_falhar_a_recolocacao_continua_registrada()
    {
        await StartCycle();
        _consultants.Fail = true;

        var reply = await Confirm();

        Assert.Equal(Constants.SuccessStatusCode, reply.Statuscode);
        var state = reply.Data.Unpack<JourneyStateReply>();
        Assert.Equal("recolocated", state.Cycle.Status);
    }
}
