using core.domain;
using core.domain.Interfaces.Services;
using Grpc.Core;
using Any = Google.Protobuf.WellKnownTypes.Any;

namespace core.Services
{
    /// <summary>
    /// O pedido de exclusão de conta do consultor (docs/specs/exclusao-de-conta-consultor.md). <b>Sem autenticação própria</b>, como o resto do core: a porta é interna ao cluster e só o
    /// <c>consultant-bff</c> a chama, depois de conferir que o token é do próprio consultor. As regras ficam em <c>ConsultantDeletionService</c>; aqui só se traduz e se mapeia o erro.
    /// </summary>
    public class ConsultantDeletionGrpcService : ConsultantDeletion.ConsultantDeletionBase
    {
        private readonly IConsultantDeletionService _deletion;
        private readonly ILogger<ConsultantDeletionGrpcService> _logger;

        public ConsultantDeletionGrpcService(IConsultantDeletionService deletion, ILogger<ConsultantDeletionGrpcService> logger)
        {
            _deletion = deletion;
            _logger = logger;
        }

        public override Task<defaultReply> GetStatus(CdProfileRequest request, ServerCallContext context)
            => Run(async () => Pack(await _deletion.GetAsync(ParseId(request.ProfileId))));

        public override Task<defaultReply> Request(CdRequest request, ServerCallContext context)
            => Run(async () => Pack(await _deletion.RequestAsync(ParseId(request.ProfileId), request.Confirmation)));

        public override Task<defaultReply> Cancel(CdProfileRequest request, ServerCallContext context)
            => Run(async () => Pack(await _deletion.CancelAsync(ParseId(request.ProfileId))));

        internal static Any Pack(ConsultantDeletionStatus s) => Any.Pack(new CdStatusReply
        {
            Requested = s.Requested, RequestedAt = s.RequestedAt?.ToString("O") ?? string.Empty, ScheduledFor = s.ScheduledFor?.ToString("O") ?? string.Empty, Completed = s.Completed,
            OpenEngagements = s.OpenEngagements, FutureSessions = s.FutureSessions
        });

        private static Guid ParseId(string value)
            => Guid.TryParse(value, out var id) && id != Guid.Empty ? id : throw new ArgumentException("profileId inválido.");

        private async Task<defaultReply> Run(Func<Task<Any>> action)
        {
            var reply = new defaultReply();
            try
            {
                reply.Data = await action();
                reply.Statuscode = Constants.SuccessStatusCode;
            }
            catch (ArgumentException ex)
            {
                reply.Statuscode = 400;
                reply.Error = new error { Code = 400, Message = ex.Message };
            }
            catch (ConsultantDeletionException ex)
            {
                var code = ex.Error switch
                {
                    ConsultantDeletionError.BadConfirmation => 400,
                    ConsultantDeletionError.NotConsultant => 403,
                    ConsultantDeletionError.NotFound => 404,
                    _ => 409
                };
                reply.Statuscode = code;
                reply.Error = new error { Code = code, Message = ex.Message };
            }
            catch (Exception ex)
            {
                // O texto técnico (do banco, por exemplo) não chega ao app: 500 com mensagem genérica; o detalhe vai para o log.
                _logger.LogError(ex, "Falha no pedido de exclusão de conta do consultor.");
                reply.Statuscode = 500;
                reply.Error = new error { Code = 500, Message = "Não foi possível concluir. Tente de novo." };
            }
            return reply;
        }
    }
}
