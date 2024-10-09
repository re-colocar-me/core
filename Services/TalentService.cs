using core.domain;
using core.domain.Interfaces.Services;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace core.Services
{
    public class TalentService : Talent.TalentBase
    {
        private IConsultantServices _services;

        public TalentService(IConsultantServices services)
        {
            _services = services;
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

                    newConsultant.Services.AddRange(item.ProvidedServices.Select(x => x.Name));
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
