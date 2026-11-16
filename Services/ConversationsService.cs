using core.domain;
using core.domain.Entities;
using core.domain.Enums;
using core.domain.Interfaces.Infrastructure;
using core.domain.Interfaces.Services;
using Grpc.Core;
using Any = Google.Protobuf.WellKnownTypes.Any;

namespace core.Services
{
    /// <summary>
    /// A ponte talento-consultor (docs/specs/ponte-talento-consultor.md, fatia P1). As regras ficam em <c>ConversationService</c>; aqui só se traduz e se mapeia o erro em código.
    /// <b>A caixa do consultor nunca leva o identificador do talento</b> (ver <see cref="ConversationMapper.ToConsultantItem"/>).
    /// </summary>
    public class ConversationsService : Conversations.ConversationsBase
    {
        private readonly IConversationService _conversations;

        public ConversationsService(IConversationService conversations) => _conversations = conversations;

        public override Task<defaultReply> RaiseHand(ConvRaiseHandRequest request, ServerCallContext context) => Run(async () =>
        {
            var command = new RaiseHandCommand
            {
                CandidateId = ParseId(request.CandidateId, "candidateId"),
                ConsultantId = ParseId(request.ConsultantId, "consultantId"),
                RequestedOfferingId = request.HasRequestedOfferingId ? ParseId(request.RequestedOfferingId, "requestedOfferingId") : null,
                Note = request.HasNote ? request.Note : null,
                ShareSuco = request.ShareSuco,
                Situation = request.HasSituation ? request.Situation : null,
                TimeSinceLayoffBand = request.HasTimeSinceLayoffBand ? request.TimeSinceLayoffBand : null,
                Seniority = request.HasSeniority ? request.Seniority : null,
                AreasTop3 = request.AreasTop3.ToList(),
                SucoSummary = request.HasSucoSummary ? request.SucoSummary : null
            };
            return Pack(await _conversations.RaiseHandAsync(command));
        });

        public override Task<defaultReply> Propose(ConvProposeRequest request, ServerCallContext context) => Run(async () =>
            Pack(await _conversations.ProposeAsync(ParseId(request.ConsultantId, "consultantId"), ParseId(request.ConversationId, "conversationId"), ParseId(request.OfferingId, "offeringId"))));

        public override Task<defaultReply> ProposeFirstConversation(ConvConsultantActionRequest request, ServerCallContext context) => Run(async () =>
            Pack(await _conversations.ProposeFirstConversationAsync(ParseId(request.ConsultantId, "consultantId"), ParseId(request.ConversationId, "conversationId"))));

        public override Task<defaultReply> GetFirstConversation(ConvFirstConversationRequest request, ServerCallContext context) => Run(async () =>
            Any.Pack(ToReply(await _conversations.GetFirstConversationAsync(ParseId(request.ConsultantId, "consultantId")))));

        public override Task<defaultReply> SetFirstConversation(ConvSetFirstConversationRequest request, ServerCallContext context) => Run(async () =>
            Any.Pack(ToReply(await _conversations.SetFirstConversationAsync(ParseId(request.ConsultantId, "consultantId"), request.Enabled, request.DurationMinutes, request.MonthlyQuota))));

        private static ConvFirstConversationReply ToReply(FirstConversationStatus status) => new()
        {
            Enabled = status.Enabled, DurationMinutes = status.DurationMinutes, MonthlyQuota = status.MonthlyQuota, UsedThisMonth = status.UsedThisMonth, Remaining = status.Remaining
        };

        public override Task<defaultReply> Decline(ConvConsultantActionRequest request, ServerCallContext context) => Run(async () =>
            Pack(await _conversations.DeclineAsync(ParseId(request.ConsultantId, "consultantId"), ParseId(request.ConversationId, "conversationId"))));

        public override Task<defaultReply> Confirm(ConvConfirmRequest request, ServerCallContext context) => Run(async () =>
        {
            var identity = new CandidateIdentity
            {
                FirstName = request.FirstName,
                LastName = request.LastName,
                Email = request.HasEmail ? request.Email : null,
                PictureUrl = request.HasPictureUrl ? request.PictureUrl : null,
                TelephoneCountryCode = request.HasTelephoneCountryCode ? request.TelephoneCountryCode : null,
                TelephoneAreaCode = request.HasTelephoneAreaCode ? request.TelephoneAreaCode : null,
                TelephoneNumber = request.HasTelephoneNumber ? request.TelephoneNumber : null
            };
            return Pack(await _conversations.ConfirmAsync(ParseId(request.CandidateId, "candidateId"), ParseId(request.ConversationId, "conversationId"), identity));
        });

        public override Task<defaultReply> Withdraw(ConvCandidateActionRequest request, ServerCallContext context) => Run(async () =>
            Pack(await _conversations.WithdrawAsync(ParseId(request.CandidateId, "candidateId"), ParseId(request.ConversationId, "conversationId"))));

        public override Task<defaultReply> ListForCandidate(ConvListForCandidateRequest request, ServerCallContext context) => Run(async () =>
        {
            var items = await _conversations.ListForCandidateAsync(ParseId(request.CandidateId, "candidateId"));
            var consultants = await _conversations.GetConsultantsAsync(items.Select(x => x.ConsultantId));
            var data = new ConvCandidateListReply();
            data.Items.AddRange(items.Select(x => ConversationMapper.ToCandidateItem(x, consultants.GetValueOrDefault(x.ConsultantId))));
            return Any.Pack(data);
        });

        public override Task<defaultReply> ListForConsultant(ConvListForConsultantRequest request, ServerCallContext context) => Run(async () =>
        {
            ConversationOrigin? origin = request.HasOrigin && Enum.TryParse<ConversationOrigin>(request.Origin, true, out var o) ? o : null;
            ConversationStatus? status = request.HasStatus && Enum.TryParse<ConversationStatus>(request.Status, true, out var s) ? s : null;
            var items = await _conversations.ListForConsultantAsync(ParseId(request.ConsultantId, "consultantId"), origin, status);
            var data = new ConvConsultantListReply();
            data.Items.AddRange(items.Select(ConversationMapper.ToConsultantItem));
            return Any.Pack(data);
        });

        private static Any Pack(Conversation conversation) => Any.Pack(new ConvActionReply { Id = conversation.Id.ToString(), Status = conversation.Status.ToString() });

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
            catch (ConversationNotFoundException ex)
            {
                reply.Statuscode = 404;
                reply.Error = new error { Code = 404, Message = ex.Message };
            }
            catch (ConversationAlreadyOpenException ex)
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

    /// <summary>Os mapeamentos da conversa para o que cada lado pode ver. Separados do serviço para o teste provar o que NÃO sai.</summary>
    public static class ConversationMapper
    {
        private static string Iso(DateTime? value) => value?.ToString("O") ?? string.Empty;

        private static IEnumerable<ConvEventItem> Events(Conversation c)
            => c.Events.OrderBy(e => e.At).Select(e => new ConvEventItem { Type = e.Type, Actor = e.Actor, At = e.At.ToString("O") });

        /// <summary>O talento vê o consultor identificado desde o início.</summary>
        public static ConvCandidateItem ToCandidateItem(Conversation c, ConversationConsultant? consultant)
        {
            var item = new ConvCandidateItem
            {
                Id = c.Id.ToString(), ConsultantId = c.ConsultantId.ToString(), ConsultantName = consultant?.Name ?? string.Empty, ConsultantPictureUrl = consultant?.PictureUrl ?? string.Empty,
                Origin = c.Origin.ToString(), Status = c.Status.ToString(), CreatedAt = Iso(c.CreatedAt), ExpiresAt = Iso(c.ExpiresAt),
                RequestedOfferingId = c.RequestedOfferingId?.ToString() ?? string.Empty, ProposalKind = c.ProposalKind?.ToString() ?? string.Empty,
                ProposedOfferingId = c.ProposedOfferingId?.ToString() ?? string.Empty, ProposedPriceInLemonCoins = c.ProposedPriceInLemonCoins ?? 0,
                ProposedServiceName = c.ProposedServiceName ?? string.Empty, Note = c.Note ?? string.Empty, ShareSuco = c.ShareSuco, ConnectionId = c.ConnectionId?.ToString() ?? string.Empty
            };
            item.Events.AddRange(Events(c));
            return item;
        }

        /// <summary>
        /// O consultor vê a ficha anônima: <b>nunca o identificador do talento</b>, nem o nome, nem a foto, nem o contato (seção 12). Depois do aceite a identidade chega pela
        /// conexão (<c>connectionId</c>), não por aqui.
        /// </summary>
        public static ConvConsultantItem ToConsultantItem(Conversation c)
        {
            var item = new ConvConsultantItem
            {
                Id = c.Id.ToString(), Origin = c.Origin.ToString(), Status = c.Status.ToString(), CreatedAt = Iso(c.CreatedAt), ExpiresAt = Iso(c.ExpiresAt),
                RequestedOfferingId = c.RequestedOfferingId?.ToString() ?? string.Empty, ProposalKind = c.ProposalKind?.ToString() ?? string.Empty,
                ProposedOfferingId = c.ProposedOfferingId?.ToString() ?? string.Empty, ProposedPriceInLemonCoins = c.ProposedPriceInLemonCoins ?? 0,
                ProposedServiceName = c.ProposedServiceName ?? string.Empty, Note = c.Note ?? string.Empty,
                ConnectionId = c.Status == ConversationStatus.Confirmed ? c.ConnectionId?.ToString() ?? string.Empty : string.Empty,
                Card = new ConvCardItem
                {
                    Situation = c.Card?.Situation ?? string.Empty, TimeSinceLayoffBand = c.Card?.TimeSinceLayoffBand ?? string.Empty,
                    Seniority = c.Card?.Seniority ?? string.Empty, SucoSummary = c.Card?.SucoSummary ?? string.Empty
                }
            };
            if (c.Card is not null)
                item.Card.AreasTop3.AddRange(c.Card.AreasTop3);
            item.Events.AddRange(Events(c));
            return item;
        }
    }
}
