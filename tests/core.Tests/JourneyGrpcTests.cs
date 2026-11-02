using core.domain;
using core.domain.Services;
using core.domain.Tests;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Time.Testing;

namespace core.Tests;

/// <summary>RPCs do estado da jornada (core/Services/JourneyService.cs), sobre o JourneyService de domínio e o repositório em memória.</summary>
public class JourneyGrpcTests
{
    private static readonly string Profile = "11111111-1111-1111-1111-111111111111";

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemoryJourneyRepository _repo = new();
    private readonly core.Services.JourneyService _grpc;

    public JourneyGrpcTests()
    {
        _grpc = new core.Services.JourneyService(new domain.Services.JourneyService(_repo, _clock), _clock);
    }

    private static T Data<T>(defaultReply reply) where T : Google.Protobuf.IMessage<T>, new()
    {
        Assert.Equal(Constants.SuccessStatusCode, reply.Statuscode);
        return reply.Data.Unpack<T>();
    }

    private static void AssertFail(defaultReply reply, string messagePart)
    {
        Assert.Equal(Constants.FailStatusCode, reply.Statuscode);
        Assert.Contains(messagePart, reply.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private Task<defaultReply> State(string profile = "") => _grpc.GetJourneyState(new JourneyProfileRequest { ProfileId = profile.Length > 0 ? profile : Profile }, null!);

    private Task<defaultReply> Situation(string situation, string timeSince = "", string eventId = "")
        => _grpc.SetJourneySituation(new SetJourneySituationRequest { ProfileId = Profile, Situation = situation, TimeSinceLayoff = timeSince, EventId = eventId }, null!);

    [Fact]
    public async Task Sem_ciclo_o_estado_vem_vazio()
    {
        var state = Data<JourneyStateReply>(await State());

        Assert.False(state.HasCycle);
        Assert.False(state.IsOpen);
        Assert.Equal(0, state.WeekOfSearch);
        Assert.Empty(state.History);
    }

    [Fact]
    public async Task Perfil_invalido_e_erro_e_nao_excecao()
    {
        AssertFail(await State("nao-e-guid"), "profileId");
        AssertFail(await State("00000000-0000-0000-0000-000000000000"), "profileId");
    }

    [Fact]
    public async Task Responder_a_situacao_cria_o_ciclo_e_devolve_o_estado()
    {
        var state = Data<JourneyStateReply>(await Situation("desligado", "semana"));

        Assert.True(state.HasCycle);
        Assert.True(state.IsOpen);
        Assert.Equal(1, state.Cycle.Number);
        Assert.Equal("first_access", state.Cycle.Origin);
        Assert.Equal("active", state.Cycle.Status);
        Assert.Equal("desligado", state.Cycle.Situation);
        Assert.Equal("semana", state.Cycle.TimeSinceLayoff);
        Assert.Equal(1, state.WeekOfSearch);
        Assert.Equal(8, state.Cycle.Steps.Count);
        Assert.Equal("situation", state.Cycle.Steps[0].StepKey);
        Assert.Equal("2026-11-01T12:00:00.0000000Z", state.Cycle.Steps[0].CompletedAt);
        // Situação feita; o primeiro incompleto é o teste, que passou a ser o atual quando a situação foi respondida.
        Assert.Equal("vocational-test", state.CurrentStep);
        Assert.Equal("2026-11-01T12:00:00.0000000Z", state.CurrentStepSince);
        Assert.Single(state.History);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("qualquer", "")]
    [InlineData("desligado", "")]          // desligado exige a faixa de tempo
    [InlineData("desligado", "ontem")]
    public async Task Situacao_invalida_e_recusada_e_nada_e_gravado(string situation, string timeSince)
    {
        AssertFail(await Situation(situation, timeSince), "inválid");

        Assert.Empty(_repo.Cycles);
        Assert.Empty(_repo.Logs);
    }

    [Fact]
    public async Task Faixa_de_tempo_so_vale_para_desligado()
    {
        var state = Data<JourneyStateReply>(await Situation("empregado", "semana"));

        Assert.Equal("empregado", state.Cycle.Situation);
        Assert.Equal("", state.Cycle.TimeSinceLayoff);
    }

    [Fact]
    public async Task Mesmo_eventId_repetido_e_sucesso_e_conta_uma_vez()
    {
        var id = Guid.NewGuid().ToString();
        Data<JourneyStateReply>(await Situation("nao", eventId: id));
        Data<JourneyStateReply>(await Situation("nao", eventId: id));

        Assert.Single(_repo.Logs);
    }

    [Fact]
    public async Task EventId_que_nao_e_guid_e_recusado()
    {
        AssertFail(await Situation("nao", eventId: "abc"), "eventId");
    }

    [Fact]
    public async Task Pausar_sem_ciclo_e_erro_claro_e_com_ciclo_pausa_e_repetir_e_sucesso()
    {
        AssertFail(await _grpc.PauseSearch(new JourneyActionRequest { ProfileId = Profile }, null!), "ciclo aberto");

        Data<JourneyStateReply>(await Situation("nao"));
        _clock.Advance(TimeSpan.FromDays(2));
        var paused = Data<JourneyStateReply>(await _grpc.PauseSearch(new JourneyActionRequest { ProfileId = Profile }, null!));
        Assert.Equal("paused", paused.Cycle.Status);
        Assert.Equal("2026-11-03T12:00:00.0000000Z", paused.Cycle.PausedAt);
        Assert.True(paused.IsOpen);

        // Pausar de novo: a ação já estava feita.
        Assert.Equal("paused", Data<JourneyStateReply>(await _grpc.PauseSearch(new JourneyActionRequest { ProfileId = Profile }, null!)).Cycle.Status);

        _clock.Advance(TimeSpan.FromDays(3));
        var resumed = Data<JourneyStateReply>(await _grpc.ResumeSearch(new JourneyActionRequest { ProfileId = Profile }, null!));
        Assert.Equal("active", resumed.Cycle.Status);
        Assert.Equal("2026-11-06T12:00:00.0000000Z", resumed.Cycle.ResumedAt);
    }

    [Fact]
    public async Task Semana_da_busca_avanca_com_o_tempo()
    {
        Data<JourneyStateReply>(await Situation("nao"));
        _clock.Advance(TimeSpan.FromDays(15));

        Assert.Equal(3, Data<JourneyStateReply>(await State()).WeekOfSearch);
    }

    [Fact]
    public async Task Recolocacao_valida_o_desfecho_e_a_data()
    {
        Data<JourneyStateReply>(await Situation("nao"));

        AssertFail(await Recolocation("emprego-dos-sonhos"), "desfecho");
        AssertFail(await Recolocation("clt", "ontem"), "data");
        AssertFail(await Recolocation("clt", "2027-01-01"), "futuro");
        Assert.Equal("active", Data<JourneyStateReply>(await State()).Cycle.Status);
    }

    private Task<defaultReply> Recolocation(string type, string date = "", bool areaChange = false)
        => _grpc.ConfirmRecolocation(new ConfirmRecolocationRequest { ProfileId = Profile, OutcomeType = type, OutcomeDate = date, AreaChange = areaChange }, null!);

    [Fact]
    public async Task Recolocacao_fecha_o_ciclo_e_o_estado_mostra_o_ultimo_com_o_desfecho()
    {
        Data<JourneyStateReply>(await Situation("nao"));

        var state = Data<JourneyStateReply>(await Recolocation("pj", "2026-10-20", areaChange: true));

        Assert.False(state.IsOpen);
        Assert.Equal("recolocated", state.Cycle.Status);
        Assert.Equal("pj", state.Cycle.OutcomeType);
        Assert.Equal("2026-10-20T00:00:00.0000000Z", state.Cycle.OutcomeDate);
        Assert.True(state.Cycle.HasAreaChange);
        Assert.True(state.Cycle.AreaChange);
        Assert.Equal("", state.CurrentStep);
        Assert.Equal(0, state.WeekOfSearch);
        Assert.Equal("recolocated", state.History[0].Status);
        Assert.Equal("pj", state.History[0].OutcomeType);
    }

    [Fact]
    public async Task Continuar_buscando_abre_o_ciclo_2_e_o_historico_guarda_os_dois()
    {
        Data<JourneyStateReply>(await Situation("nao"));
        Data<JourneyStateReply>(await Recolocation("clt"));
        _clock.Advance(TimeSpan.FromDays(100));

        var state = Data<JourneyStateReply>(await _grpc.StartNewCycle(new StartNewCycleRequest { ProfileId = Profile, Origin = "continue_searching" }, null!));

        Assert.True(state.IsOpen);
        Assert.Equal(2, state.Cycle.Number);
        Assert.Equal("continue_searching", state.Cycle.Origin);
        Assert.Equal(2, state.History.Count);
        Assert.Equal("superseded", state.History[1].Status);
        Assert.Equal("clt", state.History[1].OutcomeType);
        // A situação é respondida de novo no ciclo novo.
        Assert.Equal("situation", state.CurrentStep);
    }

    [Fact]
    public async Task Novo_ciclo_recusa_origem_invalida_e_pessoa_sem_historico()
    {
        AssertFail(await _grpc.StartNewCycle(new StartNewCycleRequest { ProfileId = Profile, Origin = "first_access" }, null!), "origem");
        AssertFail(await _grpc.StartNewCycle(new StartNewCycleRequest { ProfileId = Profile, Origin = "reactivation" }, null!), "ciclo anterior");
    }

    [Fact]
    public void Nomes_de_fio_cobrem_todos_os_passos_e_eventos()
    {
        foreach (var key in System.Enum.GetValues<domain.Enums.JourneyStepKey>())
            Assert.False(string.IsNullOrEmpty(JourneyNames.Of(key)));
        foreach (var type in System.Enum.GetValues<domain.Enums.JourneyEventType>())
        {
            Assert.True(JourneyNames.TryParseEvent(JourneyNames.Of(type), out var parsed));
            Assert.Equal(type, parsed);
        }
        Assert.False(JourneyNames.TryParseEvent("nao-existe", out _));
    }
}
