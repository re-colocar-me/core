using core.domain;
using core.domain.Interfaces.Services;
using Grpc.Core;
using Any = Google.Protobuf.WellKnownTypes.Any;

namespace core.Services
{
    /// <summary>Match talento-consultor (docs/specs/match-talento-consultor.md). Traduz o resultado do <c>MatchService</c>; a pontuação não sai daqui.</summary>
    public class MatchingService : Match.MatchBase
    {
        private readonly IMatchService _match;
        private readonly IProfileService _profiles;

        public MatchingService(IMatchService match, IProfileService profiles)
        {
            _match = match;
            _profiles = profiles;
        }

        public override async Task<defaultReply> ListConsultantsForTalent(ListForTalentRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                if (!Guid.TryParse(request.CandidateId, out var candidateId) || candidateId == Guid.Empty)
                    throw new ArgumentException("candidateId inválido.");

                var listing = await _match.ListForTalentAsync(candidateId, request.HasServiceName ? request.ServiceName : null, request.UseAffinity);
                var profiles = (await _profiles.GetByIdsAsync(listing.Items.Select(x => x.Consultant.ProfileId))).ToDictionary(x => x.Id);

                var data = new ListForTalentReply { AffinityUsed = listing.AffinityUsed };
                foreach (var item in listing.Items)
                {
                    var consultant = item.Consultant;
                    var message = new MatchedConsultantItem
                    {
                        Id = consultant.Id.ToString(), Name = consultant.Name.FullName, RatingAverage = item.RatingAverage, RatingCount = item.RatingCount,
                        Position = item.Position, BestMatch = item.BestMatch, NewHere = item.NewHere
                    };
                    message.Services.AddRange(item.Services);
                    message.Skills.AddRange(consultant.Skills.Select(x => x.Name));
                    if (profiles.TryGetValue(consultant.ProfileId, out var profile))
                    {
                        message.PictureUrl = profile.PictureUrl ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(profile.BioText)) message.Bio = profile.BioText;
                    }
                    if (item.NextSlot is { } slot) message.NextSlot = slot.ToString("yyyy-MM-ddTHH:mm:ss");
                    if (item.Reason is not null) message.Reason = item.Reason;
                    data.Consultants.Add(message);
                }

                reply.Data = Any.Pack(data);
                reply.Statuscode = Constants.SuccessStatusCode;
            }
            catch (ArgumentException ex)
            {
                reply.Statuscode = 400;
                reply.Error = new error { Code = 400, Message = ex.Message };
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
