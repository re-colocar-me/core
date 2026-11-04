using core.domain;
using core.domain.Interfaces.Infrastructure;
using core.domain.Interfaces.Services;
using core.domain.Services;
using Grpc.Core;
using Any = Google.Protobuf.WellKnownTypes.Any;

namespace core.Services
{
    /// <summary>Avaliações de consultor (docs/specs/avaliacoes-consultor.md). As regras ficam em <c>ReviewRules</c> e <c>ReviewService</c>; aqui só se traduz e se mapeia o erro em código.</summary>
    public class ReviewsService : Reviews.ReviewsBase
    {
        private readonly IReviewService _reviews;

        public ReviewsService(IReviewService reviews) => _reviews = reviews;

        private static string Wall(DateTime value) => value.ToString("yyyy-MM-ddTHH:mm:ss");

        public override Task<defaultReply> GetReviewEligibility(CandidateRequest request, ServerCallContext context) => Run(async () =>
        {
            var eligibility = await _reviews.GetEligibilityAsync(ParseId(request.CandidateId, "candidateId"));
            var data = new ReviewEligibilityReply();
            data.Encounters.AddRange(eligibility.Encounters.Select(x => new EncounterToReviewItem
            {
                ScheduleId = x.ScheduleId.ToString(), ServicePurchaseId = x.ServicePurchaseId.ToString(), ConsultantId = x.ConsultantId.ToString(),
                ConsultantName = x.ConsultantName, ServiceName = x.ServiceName, StartTime = Wall(x.StartTime), EndTime = Wall(x.EndTime)
            }));
            data.Overalls.AddRange(eligibility.Overalls.Select(x => new OverallToReviewItem
            {
                ServicePurchaseId = x.ServicePurchaseId.ToString(), ConsultantId = x.ConsultantId.ToString(), ConsultantName = x.ConsultantName,
                ServiceName = x.ServiceName, Trigger = x.Trigger.ToString()
            }));
            return Any.Pack(data);
        });

        public override Task<defaultReply> CreateEncounterReview(CreateEncounterReviewRequest request, ServerCallContext context) => Run(async () =>
        {
            var review = await _reviews.CreateEncounterReviewAsync(ParseId(request.CandidateId, "candidateId"), ParseId(request.ScheduleId, "scheduleId"), request.Rating,
                request.Reasons, request.HasText ? request.Text : null);
            return Any.Pack(new CreateReviewReply { Id = review.Id.ToString(), ModerationStatus = review.ModerationStatus.ToString() });
        });

        public override Task<defaultReply> CreateOverallReview(CreateOverallReviewRequest request, ServerCallContext context) => Run(async () =>
        {
            var review = await _reviews.CreateOverallReviewAsync(ParseId(request.CandidateId, "candidateId"), ParseId(request.ServicePurchaseId, "servicePurchaseId"), request.Rating,
                request.HasText ? request.Text : null);
            return Any.Pack(new CreateReviewReply { Id = review.Id.ToString(), ModerationStatus = review.ModerationStatus.ToString() });
        });

        public override Task<defaultReply> GetConsultantReviews(ConsultantRequest request, ServerCallContext context) => Run(async () =>
        {
            var result = await _reviews.GetPublicReviewsAsync(ParseId(request.ConsultantId, "consultantId"));
            var data = new ConsultantReviewsReply { ConsultantId = result.ConsultantId.ToString(), RatingAverage = result.Average, RatingCount = result.Count };
            data.Recommendations.AddRange(result.Recommendations.Select(x => new PublicReviewItem
            {
                AuthorDisplayName = x.AuthorDisplayName, ServiceName = x.ServiceName, Rating = x.Rating, Text = x.Text, CreatedAt = x.CreatedAt.ToString("O")
            }));
            return Any.Pack(data);
        });

        public override Task<defaultReply> ListPendingReviews(ListPendingReviewsRequest request, ServerCallContext context) => Run(async () =>
        {
            var pending = await _reviews.ListPendingTextsAsync(request.Take <= 0 ? 50 : request.Take);
            var data = new ListPendingReviewsReply();
            data.Items.AddRange(pending.Select(x => new PendingReviewItem
            {
                Id = x.Id.ToString(), Kind = x.Kind.ToString(), ConsultantId = x.ConsultantId.ToString(), AuthorDisplayName = x.AuthorDisplayName ?? string.Empty,
                ServiceName = x.ServiceName, Rating = x.Rating, Text = x.Text ?? string.Empty, CreatedAt = x.CreatedAt.ToString("O")
            }));
            return Any.Pack(data);
        });

        public override Task<defaultReply> ModerateReview(ModerateReviewRequest request, ServerCallContext context) => Run(async () =>
        {
            var found = await _reviews.ModerateAsync(ParseId(request.ReviewId, "reviewId"), request.Approve, request.HasReason ? request.Reason : null, request.ModeratedBy);
            if (!found) throw new KeyNotFoundException("Avaliação não encontrada.");
            return Any.Pack(new ReviewActionReply { Found = true });
        });

        public override Task<defaultReply> AnonymizeReview(AnonymizeReviewRequest request, ServerCallContext context) => Run(async () =>
        {
            if (!await _reviews.AnonymizeAsync(ParseId(request.ReviewId, "reviewId"))) throw new KeyNotFoundException("Avaliação não encontrada.");
            return Any.Pack(new ReviewActionReply { Found = true });
        });

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
            catch (Exception ex) when (ex is ArgumentException or ReviewRuleException)
            {
                reply.Statuscode = 400;
                reply.Error = new error { Code = 400, Message = ex.Message };
            }
            catch (KeyNotFoundException ex)
            {
                reply.Statuscode = 404;
                reply.Error = new error { Code = 404, Message = ex.Message };
            }
            catch (ReviewAlreadyExistsException ex)
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
