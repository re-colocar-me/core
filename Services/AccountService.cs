using core.domain;
using core.domain.Interfaces.Infrastructure;
using core.domain.Services;
using Grpc.Core;
using Any = Google.Protobuf.WellKnownTypes.Any;

namespace core.Services
{
    /// <summary>Exclusão de conta de talento (docs/specs/exclusao-de-conta-talent.md). Só interno; o erro vira <c>statuscode</c> com a mensagem, como nos demais serviços.</summary>
    public class AccountService : Account.AccountBase
    {
        private readonly AccountErasureService _erasure;

        public AccountService(AccountErasureService erasure) => _erasure = erasure;

        public override async Task<defaultReply> DeleteTalentAccount(DeleteTalentAccountRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                if (!Guid.TryParse(request.ProfileId, out var profileId) || profileId == Guid.Empty)
                    throw new ArgumentException("profileId inválido.");

                var result = await _erasure.EraseTalentAsync(profileId, context.CancellationToken);
                var db = result.Database;
                reply.Data = Any.Pack(new DeleteTalentAccountReply
                {
                    ProfileFound = db.ProfileFound, TalentRemoved = db.TalentRemoved, Cycles = db.Cycles, EventLogs = db.EventLogs,
                    Notifications = db.Notifications, TutorialRecords = db.TutorialRecords, ConnectionsAnonymized = db.ConnectionsAnonymized,
                    PurchasesAnonymized = db.PurchasesAnonymized, SchedulesAnonymized = db.SchedulesAnonymized, Documents = result.Documents
                });
                reply.Statuscode = Constants.SuccessStatusCode;
            }
            catch (ArgumentException ex)
            {
                reply.Statuscode = 400;
                reply.Error = new error { Code = 400, Message = ex.Message };
            }
            catch (AccountErasureRefusedException ex)
            {
                reply.Statuscode = 409;
                reply.Error = new error { Code = 409, Message = ex.Message };
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
