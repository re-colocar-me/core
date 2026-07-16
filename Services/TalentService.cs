using core.domain;
using core.domain.Enums;
using core.domain.Interfaces.Services;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace core.Services
{
    public class TalentService : Talent.TalentBase
    {
        private IConsultantServices _services;
        private readonly IProfileService _profileService;

        public TalentService(IConsultantServices services, IProfileService profileService)
        {
            _services = services;
            _profileService = profileService;
        }

        public override async Task<defaultReply> ListConsultants(FilterRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                var response = await _services.Filter(request.Name, request.Services!);
                var data = new ListConsultantsReply();

                foreach (var item in response)
                {
                    var newConsultant = new consultant();
                    newConsultant.Name = item.Name.FullName;

                    foreach (var grouped in item.Availabilities.GroupBy(x => x.WeekDay))
                    {
                        var newAvailability = new availability();
                        newAvailability.Items.AddRange(grouped.Select(x => new availabilityitem()
                        {
                            Id = x.Id.ToString(),
                            Endtime = x.EndTime.ToString(),
                            Starttime = x.StartTime.ToString()
                        }));
                        newConsultant.Availabilities.Add(newAvailability);

                    }

                    newConsultant.Services.AddRange(item.ProvidedServices.Select(x => x.ServiceItem?.Name ?? string.Empty));
                    data.Consultants.Add(newConsultant);
                }

                reply.Statuscode = Constants.SuccessStatusCode;
                reply.Data = Any.Pack(data);
            }
            catch (Exception ex)
            {
                reply.Statuscode = Constants.FailStatusCode;
                reply.Error = new error()
                {
                    Message = ex.Message
                };
            }
            return reply;
        }

        public override async Task<defaultReply> SearchProfiles(SearchProfilesRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                RoleType? role = System.Enum.TryParse<RoleType>(request.Role, out var parsedRole) ? parsedRole : null;
                var profiles = await _profileService.SearchProfilesAsync(request.Name, role);

                var data = new SearchProfilesReply();
                data.Items.AddRange(profiles.Select(p => new profileSearchResult
                {
                    Id = p.Id.ToString(),
                    Name = p.Name.FullName,
                    Role = p.Role.ToString()
                }));

                reply.Statuscode = Constants.SuccessStatusCode;
                reply.Data = Any.Pack(data);
            }
            catch (Exception ex)
            {
                reply.Statuscode = Constants.FailStatusCode;
                reply.Error = new error()
                {
                    Message = ex.Message
                };
            }
            return reply;
        }

        public override async Task<defaultReply> Connect(ConnectRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                await _services.Connect(Guid.Parse(request.ConsultantId), Guid.Parse(request.CandidateId));
                reply.Statuscode = Constants.SuccessStatusCode;
            }
            catch (Exception ex)
            {
                reply.Statuscode = Constants.FailStatusCode;
                reply.Error = new error()
                {
                    Message = ex.Message
                };
            }
            return reply;
        }

    }
}
