using core.domain;
using core.domain.Interfaces.Services;
using Grpc.Core;
using Any = Google.Protobuf.WellKnownTypes.Any;

namespace core.Services
{
    /// <summary>
    /// A medição da ponte para a equipe (docs/specs/ponte-talento-consultor.md, seções 16 e 40). <b>Sem autenticação própria</b>, como o resto do core: a porta é interna ao cluster e só o
    /// <c>core-backoffice</c> a chama, depois de conferir o JWT do backoffice e o perfil de administração. Só traduz o resultado agregado do <c>PonteMetricsService</c>: nada aqui identifica ninguém.
    /// </summary>
    public class PonteMetricsGrpcService : PonteMetricsAdmin.PonteMetricsAdminBase
    {
        private readonly IPonteMetricsService _metrics;
        private readonly ILogger<PonteMetricsGrpcService> _logger;

        public PonteMetricsGrpcService(IPonteMetricsService metrics, ILogger<PonteMetricsGrpcService> logger)
        {
            _metrics = metrics;
            _logger = logger;
        }

        public override async Task<defaultReply> GetMetrics(PmMetricsRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                reply.Data = Any.Pack(Map(await _metrics.GetAsync(request.Days)));
                reply.Statuscode = Constants.SuccessStatusCode;
            }
            catch (Exception ex)
            {
                // O texto técnico (do banco, por exemplo) não chega à tela da equipe: 500 com mensagem genérica; o detalhe vai para o log.
                _logger.LogError(ex, "Falha ao calcular a medição da ponte.");
                reply.Statuscode = 500;
                reply.Error = new error { Code = 500, Message = "Não foi possível concluir. Tente de novo." };
            }
            return reply;
        }

        internal static PmMetricsReply Map(PonteMetrics m)
        {
            var time = new PmResponseTime
            {
                Measured = m.ResponseTime.Measured, NoData = m.ResponseTime.NoData,
                HasMedian = m.ResponseTime.MedianMinutes.HasValue, MedianMinutes = m.ResponseTime.MedianMinutes ?? 0,
                HasP90 = m.ResponseTime.P90Minutes.HasValue, P90Minutes = m.ResponseTime.P90Minutes ?? 0
            };
            time.Buckets.AddRange(m.ResponseTime.Buckets.Select(b => new PmBucket { Key = b.Key, Count = b.Count }));

            return new PmMetricsReply
            {
                From = m.From.ToString("O"), To = m.To.ToString("O"), Days = m.Days, Truncated = m.Truncated,
                HandRaise = new PmHandRaiseFunnel
                {
                    Raised = m.HandRaise.Raised, WithProposal = m.HandRaise.WithProposal, Confirmed = m.HandRaise.Confirmed, Declined = m.HandRaise.Declined,
                    ExpiredNoResponse = m.HandRaise.ExpiredNoResponse, ExpiredAfterProposal = m.HandRaise.ExpiredAfterProposal, Withdrawn = m.HandRaise.Withdrawn, Open = m.HandRaise.Open
                },
                FirstConversation = new PmFirstConversation { Proposed = m.FirstConversation.Proposed, Confirmed = m.FirstConversation.Confirmed },
                Invites = new PmInviteFunnel
                {
                    Sent = m.Invites.Sent, Accepted = m.Invites.Accepted, Dismissed = m.Invites.Dismissed, Silenced = m.Invites.Silenced, Expired = m.Invites.Expired, Held = m.Invites.Held, Open = m.Invites.Open
                },
                Reports = new PmReports
                {
                    InviteByTalent = m.Reports.InviteByTalent, HandRaiseByTalent = m.Reports.HandRaiseByTalent, ByConsultant = m.Reports.ByConsultant, Open = m.Reports.Open, Reviewed = m.Reports.Reviewed,
                    SuspensionsAuto = m.Reports.SuspensionsAuto, SuspensionsManual = m.Reports.SuspensionsManual, SuspensionsLifted = m.Reports.SuspensionsLifted
                },
                ResponseTime = time
            };
        }
    }
}
