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

        private readonly IAudienceService _audience;
        private readonly IInviteService _invites;

        public ConversationsService(IConversationService conversations, IAudienceService audience, IInviteService invites)
        {
            _conversations = conversations;
            _audience = audience;
            _invites = invites;
        }

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

        // Nas chamadas do consultor, o campo consultantId traz o id do PERFIL (o app do consultor só conhece esse); as conversas guardam o Consultant.Id, outro Guid. A tradução é aqui.
        private async Task<Guid> ConsultantOf(string ownerProfileId, bool createIfMissing = false)
            => await _conversations.ResolveConsultantAsync(ParseId(ownerProfileId, "consultantId"), createIfMissing) ?? throw new ConversationNotFoundException();

        public override Task<defaultReply> Propose(ConvProposeRequest request, ServerCallContext context) => Run(async () =>
            Pack(await _conversations.ProposeAsync(await ConsultantOf(request.ConsultantId), ParseId(request.ConversationId, "conversationId"), ParseId(request.OfferingId, "offeringId"))));

        public override Task<defaultReply> ProposeFirstConversation(ConvConsultantActionRequest request, ServerCallContext context) => Run(async () =>
            Pack(await _conversations.ProposeFirstConversationAsync(await ConsultantOf(request.ConsultantId), ParseId(request.ConversationId, "conversationId"))));

        // Ler a configuração de quem nunca foi consultor mostra o padrão desligado, sem criar linha nenhuma; só gravar cria.
        public override Task<defaultReply> GetFirstConversation(ConvFirstConversationRequest request, ServerCallContext context) => Run(async () =>
        {
            var consultantId = await _conversations.ResolveConsultantAsync(ParseId(request.ConsultantId, "consultantId")) ?? Guid.NewGuid();
            return Any.Pack(ToReply(await _conversations.GetFirstConversationAsync(consultantId)));
        });

        public override Task<defaultReply> SetFirstConversation(ConvSetFirstConversationRequest request, ServerCallContext context) => Run(async () =>
            Any.Pack(ToReply(await _conversations.SetFirstConversationAsync(await ConsultantOf(request.ConsultantId, createIfMissing: true), request.Enabled, request.DurationMinutes, request.MonthlyQuota))));

        // O convite do consultor (seção 10). O consultor nunca escolhe ninguém: o servidor seleciona. Sem linha de consultor, ler dá 404 e enviar também (não há o que convidar).
        public override Task<defaultReply> GetInviteOverview(ConvInviteOverviewRequest request, ServerCallContext context) => Run(async () =>
            Any.Pack(ToReply(await _invites.GetOverviewAsync(await ConsultantOf(request.ConsultantId)))));

        public override Task<defaultReply> SendInvites(ConvSendInvitesRequest request, ServerCallContext context) => Run(async () =>
        {
            var result = await _invites.SendAsync(await ConsultantOf(request.ConsultantId), ParseId(request.TemplateId, "templateId"), request.HasFreeText ? request.FreeText : null, request.Count);
            return Any.Pack(new ConvSendInvitesReply { Requested = result.Requested, Sent = result.Sent, QuotaRemaining = result.QuotaRemaining });
        });

        private static ConvInviteOverviewReply ToReply(InviteOverview overview)
        {
            var reply = new ConvInviteOverviewReply
            {
                MonthlyQuota = overview.Quota.Monthly, Used = overview.Quota.Used, Remaining = overview.Quota.Remaining, IsNew = overview.Quota.IsNew,
                FirstConversationEnabled = overview.FirstConversationEnabled, FirstConversationMinutes = overview.FirstConversationMinutes, FirstConversationFreeSlots = overview.FirstConversationFreeSlots,
                CanInvite = overview.Readiness.CanInvite,
                ReachableBelowMinimum = overview.Reachable.BelowMinimum, ReachableValue = overview.Reachable.Value ?? 0, ReachableMinimum = overview.Reachable.Minimum,
                Sent = overview.Stats.Sent, Open = overview.Stats.Open, Accepted = overview.Stats.Accepted, Dismissed = overview.Stats.Dismissed, Silenced = overview.Stats.Silenced, Expired = overview.Stats.Expired,
                FreeTextMaxChars = overview.FreeTextMaxChars
            };
            reply.Missing.AddRange(overview.Readiness.Missing);
            reply.Templates.AddRange(overview.Templates.Select(x => new ConvInviteTemplate { Id = x.Id.ToString(), Name = x.Name, Text = x.Text, IsFirstConversation = x.IsFirstConversation }));
            return reply;
        }

        // O talento e os convites (seção 11).
        public override Task<defaultReply> GetInvitePreferences(ConvInvitePreferencesRequest request, ServerCallContext context) => Run(async () =>
            Any.Pack(ToReply(await _invites.GetPreferencesAsync(ParseId(request.CandidateId, "candidateId")))));

        public override Task<defaultReply> SetInvitePreferences(ConvSetInvitePreferencesRequest request, ServerCallContext context) => Run(async () =>
            Any.Pack(ToReply(await _invites.SetPreferencesAsync(ParseId(request.CandidateId, "candidateId"), request.HasReceiveInvites ? request.ReceiveInvites : null, request.HasNoticeSeen ? request.NoticeSeen : null))));

        public override Task<defaultReply> DismissInvite(ConvCandidateActionRequest request, ServerCallContext context) => Run(async () =>
            Pack(await _invites.DismissAsync(ParseId(request.CandidateId, "candidateId"), ParseId(request.ConversationId, "conversationId"))));

        public override Task<defaultReply> SilenceInvite(ConvCandidateActionRequest request, ServerCallContext context) => Run(async () =>
            Pack(await _invites.SilenceAsync(ParseId(request.CandidateId, "candidateId"), ParseId(request.ConversationId, "conversationId"))));

        public override Task<defaultReply> UnsilenceConsultant(ConvUnsilenceRequest request, ServerCallContext context) => Run(async () =>
        {
            await _invites.UnsilenceAsync(ParseId(request.CandidateId, "candidateId"), ParseId(request.ConsultantId, "consultantId"));
            return Any.Pack(ToReply(await _invites.GetPreferencesAsync(ParseId(request.CandidateId, "candidateId"))));
        });

        private static ConvInvitePreferencesReply ToReply(InvitePreferencesView view)
        {
            var reply = new ConvInvitePreferencesReply { ReceiveInvites = view.ReceiveInvites, NoticeSeen = view.NoticeSeen, MaxPerWeek = view.MaxPerWeek };
            reply.Silenced.AddRange(view.Silenced.Select(x => new ConvSilencedConsultant { ConsultantId = x.ConsultantId.ToString(), Name = x.Name, PictureUrl = x.PictureUrl ?? string.Empty, At = x.At.ToString("O") }));
            return reply;
        }

        // O meu público (seção 9). Quem nunca foi consultor vê o público vazio e a contagem geral; só gravar cria a linha de consultor.
        public override Task<defaultReply> GetAudience(ConvAudienceRequest request, ServerCallContext context) => Run(async () =>
        {
            var consultantId = await _conversations.ResolveConsultantAsync(ParseId(request.ConsultantId, "consultantId"));
            return Any.Pack(ToReply(consultantId is null
                ? new AudienceView(new Dictionary<string, List<string>>(), core.domain.Services.Conversations.AudienceKinds.Supported, new core.domain.Services.Conversations.ConversationConfiguration().AudienceMaxCriteria, await _audience.PreviewAsync(new Dictionary<string, List<string>>()))
                : await _audience.GetAsync(consultantId.Value)));
        });

        public override Task<defaultReply> SetAudience(ConvSetAudienceRequest request, ServerCallContext context) => Run(async () =>
            Any.Pack(ToReply(await _audience.SetAsync(await ConsultantOf(request.ConsultantId, createIfMissing: true), ToCriteria(request.Criteria)))));

        public override Task<defaultReply> PreviewAudience(ConvAudiencePreviewRequest request, ServerCallContext context) => Run(async () =>
            Any.Pack(ToReply(await _audience.PreviewAsync(ToCriteria(request.Criteria)))));

        // Um tipo repetido soma os valores (o servidor valida e tira as repetições).
        private static Dictionary<string, List<string>> ToCriteria(IEnumerable<ConvAudienceCriterion> items)
        {
            var result = new Dictionary<string, List<string>>();
            foreach (var item in items)
            {
                if (!result.TryGetValue(item.Kind, out var values))
                    result[item.Kind] = values = [];
                values.AddRange(item.Values);
            }
            return result;
        }

        private static ConvAudienceCountReply ToReply(AudienceCount count) => new() { BelowMinimum = count.BelowMinimum, Value = count.Value ?? 0, Minimum = count.Minimum };

        private static ConvAudienceReply ToReply(AudienceView view)
        {
            var reply = new ConvAudienceReply { MaxCriteria = view.MaxCriteria, Count = ToReply(view.Count) };
            reply.SupportedKinds.AddRange(view.SupportedKinds);
            foreach (var (kind, values) in view.Criteria)
            {
                var criterion = new ConvAudienceCriterion { Kind = kind };
                criterion.Values.AddRange(values);
                reply.Criteria.Add(criterion);
            }
            return reply;
        }

        private static ConvFirstConversationReply ToReply(FirstConversationStatus status) => new()
        {
            Enabled = status.Enabled, DurationMinutes = status.DurationMinutes, MonthlyQuota = status.MonthlyQuota, UsedThisMonth = status.UsedThisMonth, Remaining = status.Remaining
        };

        public override Task<defaultReply> Decline(ConvConsultantActionRequest request, ServerCallContext context) => Run(async () =>
            Pack(await _conversations.DeclineAsync(await ConsultantOf(request.ConsultantId), ParseId(request.ConversationId, "conversationId"))));

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
            // Quem nunca foi consultor não tem conversas: caixa vazia, não erro.
            var consultantId = await _conversations.ResolveConsultantAsync(ParseId(request.ConsultantId, "consultantId"));
            var items = consultantId is null ? [] : await _conversations.ListForConsultantAsync(consultantId.Value, origin, status);
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
                ProposedServiceName = c.ProposedServiceName ?? string.Empty, Note = c.Note ?? string.Empty, ShareSuco = c.ShareSuco, ConnectionId = c.ConnectionId?.ToString() ?? string.Empty,
                InviteText = c.InviteText ?? string.Empty, InviteReason = c.InviteReason ?? string.Empty
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
