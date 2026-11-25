using core.domain.Enums;
using core.domain.Interfaces.Services;
using core.domain.Services.Sessions;
using core.IoC;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Options = Microsoft.Extensions.Options.Options;

namespace core.Tests;

/// <summary>O gRPC e o job da sessão em vídeo (core/Services/SessionsGrpcService.cs, core/IoC/SessionCloseJob.cs): tradução, códigos de erro, a chave desligada e o registro.</summary>
public class SessionsGrpcTests
{
    private static readonly Guid Schedule = Guid.Parse("aaaaaaaa-1111-2222-3333-444444444444");
    private static readonly Guid Profile = Guid.Parse("dddddddd-1111-2222-3333-444444444444");
    private static readonly DateTime Start = new(2026, 12, 1, 17, 0, 0, DateTimeKind.Utc);

    private sealed class FakeSessions : ISessionService
    {
        public Exception? Throw { get; set; }
        public List<string> Calls { get; } = [];
        public int ToClose { get; set; }
        public Guid? SessionId { get; set; }

        private Task<SessionView> Answer(string call)
        {
            if (Throw is not null) throw Throw;
            Calls.Add(call);
            return Task.FromResult(new SessionView(SessionId, Schedule, SessionStatus.InProgress, SessionActor.Talent, Start, Start.AddHours(1), Start.AddMinutes(-10), Start.AddMinutes(75), true));
        }

        public Task<SessionView> GetAsync(Guid scheduleId, Guid profileId) => Answer($"get:{scheduleId}:{profileId}");
        public Task<SessionView> SignalAsync(Guid scheduleId, Guid profileId, PresenceSignal signal) => Answer(signal.ToString());
        public Task<SessionView> EndAsync(Guid scheduleId, Guid profileId) => Answer("end");
        public Task<int> CloseDueAsync()
        {
            if (Throw is not null) throw Throw;
            Calls.Add("close");
            return Task.FromResult(ToClose);
        }
    }

    private readonly FakeSessions _sessions = new();

    private core.Services.SessionsGrpcService Grpc(bool enabled = true)
        => new(_sessions, Options.Create(new SessionHostOptions { Enabled = enabled }), NullLogger<core.Services.SessionsGrpcService>.Instance);

    private static SesRequest Request() => new() { ScheduleId = Schedule.ToString(), ProfileId = Profile.ToString() };
    private static SesSignalRequest Signal(string signal) => new() { ScheduleId = Schedule.ToString(), ProfileId = Profile.ToString(), Signal = signal };

    [Fact]
    public async Task Desligada_responde_501_sem_chamar_o_servico()
    {
        var grpc = Grpc(enabled: false);
        var ctx = TestServerCallContext.Create();

        foreach (var reply in new[] { await grpc.GetSession(Request(), ctx), await grpc.SignalSession(Signal("join"), ctx), await grpc.EndSession(Request(), ctx) })
            Assert.Equal((501, 501), (reply.Statuscode, reply.Error.Code));
        Assert.Empty(_sessions.Calls);
    }

    [Fact]
    public async Task O_estado_vai_com_os_instantes_em_ISO_e_sem_sessao_o_id_vai_vazio()
    {
        var reply = await Grpc().GetSession(Request(), TestServerCallContext.Create());

        var data = reply.Data.Unpack<SesSessionReply>();
        Assert.Equal((200, string.Empty, "InProgress", "talent", true), (reply.Statuscode, data.SessionId, data.Status, data.Role, data.CanJoin));
        Assert.Equal(("2026-12-01T17:00:00.0000000Z", "2026-12-01T18:00:00.0000000Z", "2026-12-01T16:50:00.0000000Z", "2026-12-01T18:15:00.0000000Z"), (data.Start, data.End, data.OpensAt, data.ClosesAt));
        Assert.Equal($"get:{Schedule}:{Profile}", _sessions.Calls.Single());
        _sessions.SessionId = Profile;
        Assert.Equal(Profile.ToString(), (await Grpc().GetSession(Request(), TestServerCallContext.Create())).Data.Unpack<SesSessionReply>().SessionId);
    }

    [Theory]
    [InlineData("join", PresenceSignal.Join)]
    [InlineData("heartbeat", PresenceSignal.Heartbeat)]
    [InlineData("connection-lost", PresenceSignal.ConnectionLost)]
    [InlineData("reconnected", PresenceSignal.Reconnected)]
    [InlineData("leave", PresenceSignal.Leave)]
    [InlineData("join-failed", PresenceSignal.JoinFailed)]
    public async Task Cada_sinal_do_contrato_chega_ao_servico(string wire, PresenceSignal expected)
    {
        Assert.Equal(200, (await Grpc().SignalSession(Signal(wire), TestServerCallContext.Create())).Statuscode);

        Assert.Equal(expected.ToString(), _sessions.Calls.Single());
    }

    [Fact]
    public async Task Sinal_ou_identificador_invalido_e_400_e_nada_chega_ao_servico()
    {
        var ctx = TestServerCallContext.Create();

        Assert.Equal(400, (await Grpc().SignalSession(Signal("Join"), ctx)).Statuscode);
        Assert.Equal(400, (await Grpc().SignalSession(Signal("2"), ctx)).Statuscode);
        Assert.Equal(400, (await Grpc().GetSession(new SesRequest { ScheduleId = "x", ProfileId = Profile.ToString() }, ctx)).Statuscode);
        Assert.Equal(400, (await Grpc().EndSession(new SesRequest { ScheduleId = Schedule.ToString(), ProfileId = Guid.Empty.ToString() }, ctx)).Statuscode);
        Assert.Empty(_sessions.Calls);
    }

    [Theory]
    [InlineData(SessionError.NotFound, 404)]
    [InlineData(SessionError.NotAllowed, 403)]
    [InlineData(SessionError.NotConfirmed, 409)]
    [InlineData(SessionError.Closed, 410)]
    [InlineData(SessionError.OutsideWindow, 425)]
    public async Task Cada_recusa_tem_o_seu_codigo_com_a_mensagem(SessionError error, int code)
    {
        _sessions.Throw = new SessionException(error, "motivo");

        var reply = await Grpc().SignalSession(Signal("join"), TestServerCallContext.Create());

        Assert.Equal((code, code, "motivo"), (reply.Statuscode, reply.Error.Code, reply.Error.Message));
    }

    [Fact]
    public async Task Falha_inesperada_nao_leva_a_mensagem_do_banco_a_quem_chamou()
    {
        _sessions.Throw = new InvalidOperationException("relation \"Sessions\" does not exist");

        var reply = await Grpc().GetSession(Request(), TestServerCallContext.Create());

        Assert.NotEqual(200, reply.Statuscode);
        Assert.DoesNotContain("relation", reply.Error.Message);
    }

    private SessionCloseJob Job(bool enabled, int every = 60)
    {
        var provider = new ServiceCollection().AddSingleton<ISessionService>(_sessions).BuildServiceProvider();
        return new SessionCloseJob(provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(new SessionHostOptions { Enabled = enabled, CloseEverySeconds = every }), NullLogger<SessionCloseJob>.Instance);
    }

    [Fact]
    public async Task O_job_desligado_nao_fecha_nada_e_uma_falha_no_ligado_nao_derruba_o_processo()
    {
        var off = Job(enabled: false);
        await off.StartAsync(CancellationToken.None);
        await off.StopAsync(CancellationToken.None);
        Assert.Empty(_sessions.Calls);

        var job = Job(enabled: true);
        _sessions.Throw = new IOException("banco fora");
        await job.TickAsync(CancellationToken.None);                 // não lança
        _sessions.Throw = null;
        await job.TickAsync(CancellationToken.None);
        Assert.Equal(["close"], _sessions.Calls);
        Assert.Equal((TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60)), (Job(true, 1).Interval, job.Interval));
    }

    [Fact]
    public void O_registro_le_a_chave_e_os_parametros_da_secao_Sessions_e_comeca_desligado()
    {
        ServiceProvider Build(Dictionary<string, string?> values)
            => SessionRegistration.AddVideoSessions(new ServiceCollection(), new ConfigurationBuilder().AddInMemoryCollection(values).Build()).BuildServiceProvider();

        using var defaults = Build([]);
        Assert.False(defaults.GetRequiredService<Microsoft.Extensions.Options.IOptions<SessionHostOptions>>().Value.Enabled);
        Assert.Equal((10, 15, 15), (defaults.GetRequiredService<SessionConfiguration>().OpensMinutesBefore, defaults.GetRequiredService<SessionConfiguration>().ClosesMinutesAfter, defaults.GetRequiredService<SessionConfiguration>().NoShowToleranceMinutes));

        using var changed = Build(new() { ["Sessions:Enabled"] = "true", ["Sessions:OpensMinutesBefore"] = "5" });
        Assert.True(changed.GetRequiredService<Microsoft.Extensions.Options.IOptions<SessionHostOptions>>().Value.Enabled);
        Assert.Equal(5, changed.GetRequiredService<SessionConfiguration>().OpensMinutesBefore);
    }
}
