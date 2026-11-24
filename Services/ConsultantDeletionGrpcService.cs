using core.domain;
using core.domain.Interfaces.Infrastructure;
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
        public const string ExecutionEnabledKey = "ConsultantDeletion:ExecutionEnabled";

        private readonly IConsultantDeletionService _deletion;
        private readonly IConsultantErasureService _erasure;
        private readonly IConfiguration _configuration;
        private readonly ILogger<ConsultantDeletionGrpcService> _logger;

        public ConsultantDeletionGrpcService(IConsultantDeletionService deletion, IConsultantErasureService erasure, IConfiguration configuration, ILogger<ConsultantDeletionGrpcService> logger)
        {
            _deletion = deletion;
            _erasure = erasure;
            _configuration = configuration;
            _logger = logger;
        }

        /// <summary>A trava da EXECUÇÃO: desligada por padrão, e valor que não é <c>true</c> (vazio, "sim") conta como desligado. Lida a cada chamada. Existe ALÉM da trava do BFF: a exclusão é irreversível.</summary>
        private bool ExecutionEnabled => bool.TryParse(_configuration[ExecutionEnabledKey], out var enabled) && enabled;

        public override Task<defaultReply> GetStatus(CdProfileRequest request, ServerCallContext context)
            => Run(async () => Pack(await _deletion.GetAsync(ParseId(request.ProfileId))));

        public override Task<defaultReply> Request(CdRequest request, ServerCallContext context)
            => Run(async () => Pack(await _deletion.RequestAsync(ParseId(request.ProfileId), request.Confirmation)));

        public override Task<defaultReply> Cancel(CdProfileRequest request, ServerCallContext context)
            => Run(async () => Pack(await _deletion.CancelAsync(ParseId(request.ProfileId))));

        public override Task<defaultReply> EraseData(CdProfileRequest request, ServerCallContext context)
            => Run(async () =>
            {
                RequireExecution();
                var result = await _erasure.EraseDataAsync(ParseId(request.ProfileId), context.CancellationToken);
                var db = result.Database;
                _logger.LogInformation("Exclusão de conta do consultor: dados apagados (já concluída: {AlreadyCompleted}).", db.AlreadyCompleted);
                return Any.Pack(new CdErasureReply
                {
                    AlreadyCompleted = db.AlreadyCompleted, ConsultantFound = db.ConsultantFound, Availabilities = db.Availabilities, Offerings = db.Offerings, Reviews = db.Reviews, Tratativas = db.Tratativas,
                    ConnectionsAnonymized = db.ConnectionsAnonymized, ConversationsClosed = db.ConversationsClosed, ConversationsAnonymized = db.ConversationsAnonymized, Notifications = db.Notifications,
                    TutorialRecords = db.TutorialRecords, Leads = db.Leads, SilencingsRemoved = db.SilencingsRemoved, Documents = result.Documents
                });
            });

        public override Task<defaultReply> ListDue(CdDueRequest request, ServerCallContext context)
            => Run(async () =>
            {
                RequireExecution();
                var reply = new CdDueReply();
                reply.ProfileIds.AddRange((await _erasure.ListDueAsync(request.Take, context.CancellationToken)).Select(x => x.ToString()));
                return Any.Pack(reply);
            });

        public override Task<defaultReply> Complete(CdProfileRequest request, ServerCallContext context)
            => Run(async () =>
            {
                RequireExecution();
                await _erasure.CompleteAsync(ParseId(request.ProfileId), context.CancellationToken);
                _logger.LogInformation("Exclusão de conta do consultor: concluída.");
                return Any.Pack(new CdEmptyReply());
            });

        private void RequireExecution()
        {
            if (!ExecutionEnabled)
                throw new ExecutionDisabledException();
        }

        private sealed class ExecutionDisabledException() : Exception("A execução da exclusão de conta está desligada.");

        internal static Any Pack(ConsultantDeletionStatus s) => Any.Pack(new CdStatusReply
        {
            Requested = s.Requested, RequestedAt = s.RequestedAt?.ToString("O") ?? string.Empty, ScheduledFor = s.ScheduledFor?.ToString("O") ?? string.Empty, Completed = s.Completed,
            OpenEngagements = s.OpenEngagements, FutureSessions = s.FutureSessions, PendingModeration = s.PendingModeration, Changed = s.Changed
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
            catch (ExecutionDisabledException ex)
            {
                reply.Statuscode = 501;
                reply.Error = new error { Code = 501, Message = ex.Message };
            }
            catch (ConsultantErasureException ex)
            {
                // Uma pré-condição barrou (pedido, carência, acesso depois do pedido, atendimento, dados ainda não apagados): 409, e a mensagem diz qual. Não nasce de erro do chamador a ser corrigido na tela.
                var code = ex.Error switch { ConsultantErasureError.NotFound => 404, ConsultantErasureError.NotConsultant => 403, ConsultantErasureError.NoticeNotPublished => 503, _ => 409 };
                reply.Statuscode = code;
                reply.Error = new error { Code = code, Message = ex.Message };
            }
            catch (AccountErasureRefusedException ex)
            {
                reply.Statuscode = 409;
                reply.Error = new error { Code = 409, Message = ex.Message };
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
