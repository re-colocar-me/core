using core.domain;
using core.domain.Interfaces.Services;
using core.domain.Services.Sessions;
using core.IoC;
using Grpc.Core;
using Microsoft.Extensions.Options;
using Any = Google.Protobuf.WellKnownTypes.Any;

namespace core.Services
{
    /// <summary>
    /// A sessão em vídeo para os BFFs (docs/specs/sessao-em-video-e-chat.md). <b>Sem autenticação própria</b>, como o resto do core: a porta é interna ao cluster e o BFF manda o perfil do JWT. As regras
    /// ficam em <c>SessionService</c>. Desligada (<c>Sessions:Enabled=false</c>) responde 501 sem tocar no banco.
    /// </summary>
    public class SessionsGrpcService : Sessions.SessionsBase
    {
        private static readonly Dictionary<string, PresenceSignal> Signals = new(StringComparer.Ordinal)
        {
            ["join"] = PresenceSignal.Join, ["heartbeat"] = PresenceSignal.Heartbeat, ["connection-lost"] = PresenceSignal.ConnectionLost,
            ["reconnected"] = PresenceSignal.Reconnected, ["leave"] = PresenceSignal.Leave, ["join-failed"] = PresenceSignal.JoinFailed
        };

        private readonly ISessionService _sessions;
        private readonly SessionHostOptions _options;
        private readonly ILogger<SessionsGrpcService> _logger;

        public SessionsGrpcService(ISessionService sessions, IOptions<SessionHostOptions> options, ILogger<SessionsGrpcService> logger)
        {
            _sessions = sessions;
            _options = options.Value;
            _logger = logger;
        }

        public override Task<defaultReply> GetSession(SesRequest request, ServerCallContext context)
            => Run(() => _sessions.GetAsync(ParseId(request.ScheduleId, "scheduleId"), ParseId(request.ProfileId, "profileId")));

        public override Task<defaultReply> SignalSession(SesSignalRequest request, ServerCallContext context)
            => Run(() => _sessions.SignalAsync(ParseId(request.ScheduleId, "scheduleId"), ParseId(request.ProfileId, "profileId"),
                Signals.TryGetValue(request.Signal, out var signal) ? signal : throw new ArgumentException("Sinal inválido.")));

        public override Task<defaultReply> EndSession(SesRequest request, ServerCallContext context)
            => Run(() => _sessions.EndAsync(ParseId(request.ScheduleId, "scheduleId"), ParseId(request.ProfileId, "profileId")));

        private static Guid ParseId(string value, string name)
            => Guid.TryParse(value, out var id) && id != Guid.Empty ? id : throw new ArgumentException($"{name} inválido.");

        private static SesSessionReply Map(SessionView v) => new()
        {
            SessionId = v.SessionId?.ToString() ?? string.Empty, ScheduleId = v.ScheduleId.ToString(), Status = v.Status.ToString(), Role = v.Role == SessionActor.Consultant ? "consultant" : "talent",
            Start = v.Start.ToString("O"), End = v.End.ToString("O"), OpensAt = v.OpensAt.ToString("O"), ClosesAt = v.ClosesAt.ToString("O"), CanJoin = v.CanJoin
        };

        private async Task<defaultReply> Run(Func<Task<SessionView>> action)
        {
            if (!_options.Enabled)
                return Failure(501, "A sessão em vídeo ainda não está disponível.");

            try
            {
                return new defaultReply { Data = Any.Pack(Map(await action())), Statuscode = Constants.SuccessStatusCode };
            }
            catch (ArgumentException ex)
            {
                return Failure(400, ex.Message);
            }
            catch (SessionException ex)
            {
                var code = ex.Error switch { SessionError.NotFound => 404, SessionError.NotAllowed => 403, SessionError.NotConfirmed => 409, SessionError.Closed => 410, SessionError.OutsideWindow => 425, _ => 400 };
                return Failure(code, ex.Message);
            }
            catch (Exception ex)
            {
                // O detalhe fica no log; quem chama não recebe mensagem de banco.
                _logger.LogError(ex, "Falha ao tratar a sessão em vídeo.");
                return Failure(Constants.FailStatusCode, "Não foi possível tratar a sessão agora.");
            }
        }

        private static defaultReply Failure(int code, string message) => new() { Statuscode = code, Error = new error { Code = code, Message = message } };
    }
}
