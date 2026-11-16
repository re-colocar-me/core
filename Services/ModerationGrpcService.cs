using core.domain;
using core.domain.Interfaces.Services;
using Grpc.Core;
using Any = Google.Protobuf.WellKnownTypes.Any;

namespace core.Services
{
    /// <summary>
    /// A moderação da ponte para a equipe (docs/specs/ponte-talento-consultor.md, seção 29). <b>Sem autenticação própria</b>, como o resto do core: a porta é interna ao cluster e só o
    /// <c>core-backoffice</c> a chama, depois de conferir o JWT do backoffice e o perfil de administração. As regras ficam em <c>ModerationService</c>; aqui só se traduz e se mapeia o erro.
    /// </summary>
    public class ModerationGrpcService : Moderation.ModerationBase
    {
        private readonly IModerationService _moderation;

        public ModerationGrpcService(IModerationService moderation) => _moderation = moderation;

        public override Task<defaultReply> ListReports(ModListReportsRequest request, ServerCallContext context) => Run(async () =>
        {
            var reply = new ModListReportsReply();
            reply.Items.AddRange((await _moderation.ListReportsAsync(request.Status, request.Take)).Select(Map));
            return Any.Pack(reply);
        });

        public override Task<defaultReply> ReviewReport(ModReviewReportRequest request, ServerCallContext context) => Run(async () =>
            Any.Pack(Map(await _moderation.ReviewReportAsync(ParseId(request.ReportId, "reportId"), request.Note))));

        public override Task<defaultReply> ListSuspensions(ModEmptyRequest request, ServerCallContext context) => Run(async () =>
        {
            var reply = new ModListSuspensionsReply();
            reply.Items.AddRange((await _moderation.ListSuspensionsAsync()).Select(x => new ModSuspensionItem
            {
                ConsultantId = x.ConsultantId.ToString(), ConsultantName = x.ConsultantName, SuspendedAt = x.SuspendedAt.ToString("O"), Reason = x.Reason, HeldInvites = x.HeldInvites,
                ProfileId = x.ProfileId == Guid.Empty ? string.Empty : x.ProfileId.ToString(), FirstName = x.FirstName ?? string.Empty, Email = x.Email ?? string.Empty
            }));
            return Any.Pack(reply);
        });

        public override Task<defaultReply> SuspendInviting(ModConsultantRequest request, ServerCallContext context) => Run(async () =>
        {
            var suspension = await _moderation.SuspendAsync(ParseId(request.ConsultantId, "consultantId"));
            return Any.Pack(new ModSuspensionReply { ConsultantId = suspension.ConsultantId.ToString(), SuspendedAt = suspension.SuspendedAt.ToString("O"), Reason = suspension.Reason });
        });

        public override Task<defaultReply> LiftInviting(ModLiftRequest request, ServerCallContext context) => Run(async () =>
        {
            await _moderation.LiftSuspensionAsync(ParseId(request.ConsultantId, "consultantId"), request.Note);
            return Any.Pack(new ModEmptyReply());
        });

        public override Task<defaultReply> CloseHeldInvites(ModConsultantRequest request, ServerCallContext context) => Run(async () =>
            Any.Pack(new ModClosedReply { Closed = await _moderation.CloseHeldInvitesAsync(ParseId(request.ConsultantId, "consultantId")) }));

        private static string Iso(DateTime? value) => value?.ToString("O") ?? string.Empty;

        private static ModReportItem Map(ReportQueueItem x) => new()
        {
            ReportId = x.ReportId.ToString(), ConversationId = x.ConversationId.ToString(), CreatedAt = Iso(x.CreatedAt), Status = x.Status, ReporterRole = x.ReporterRole, Reason = x.Reason,
            Details = x.Details ?? string.Empty, ReviewedAt = Iso(x.ReviewedAt), ReviewNote = x.ReviewNote ?? string.Empty, ConsultantId = x.ConsultantId.ToString(), ConsultantName = x.ConsultantName,
            Origin = x.Origin, ConversationStatus = x.ConversationStatus ?? string.Empty, ConversationText = x.ConversationText ?? string.Empty, ConsultantSuspended = x.ConsultantSuspended,
            DistinctInviteReporters30Days = x.DistinctInviteReporters30Days
        };

        private static Guid ParseId(string value, string name)
            => Guid.TryParse(value, out var id) && id != Guid.Empty ? id : throw new ArgumentException($"{name} inválido.");

        private static async Task<defaultReply> Run(Func<Task<Any>> action)
        {
            var reply = new defaultReply();
            try
            {
                reply.Data = await action();
                reply.Statuscode = Constants.SuccessStatusCode;
            }
            catch (Exception ex) when (ex is ArgumentException or ConversationRuleException)
            {
                reply.Statuscode = 400;
                reply.Error = new error { Code = 400, Message = ex.Message };
            }
            catch (Exception ex) when (ex is ReportNotFoundException or ConversationNotFoundException)
            {
                reply.Statuscode = 404;
                reply.Error = new error { Code = 404, Message = ex.Message };
            }
            catch (Exception ex)
            {
                reply.Statuscode = Constants.FailStatusCode;
                reply.Error = new error { Message = ex.Message };
            }
            return reply;
        }
    }
}
