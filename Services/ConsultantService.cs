using core.domain;
using core.domain.Entities;
using core.domain.Enums;
using core.domain.Interfaces.Services;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace core.Services
{

    public class ConsultantService : Consultant.ConsultantBase
    {
        private IScheduleService _scheduleService;
        private IConsultantServices _consultantServices;

        public ConsultantService(IScheduleService scheduleService,
                                 IConsultantServices consultantServices)
        {
            _scheduleService = scheduleService;
            _consultantServices = consultantServices;
        }
        public override async Task<defaultReply> SetAvailability(SetAvailabilityRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();

            try
            {
                await _scheduleService.SetAvailability(Guid.Parse(request.OwnerId),
                                                        request.Wday,
                                                        TimeOnly.Parse(request.Start),
                                                        TimeOnly.Parse(request.End));
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

        public override async Task<defaultReply> GetAvailabiltyListByOwner(GetAvailabiltyListByOwnerRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                var list = await _scheduleService.GetAvailabiltyListByOwner(Guid.Parse(request.OwnerId));

                reply.Statuscode = Constants.SuccessStatusCode;

                var groupedList = list.GroupBy(x => x.WeekDay);
                var data = new GetAvailabiltyListByOwnerReply();
                foreach (var item in groupedList)
                {
                    var newAvailability = new availability()
                    {
                        Weekday = item.Key
                    };
                    newAvailability.Items.AddRange(item.Select(x => new availabilityitem() { Id = x.Id.ToString(), Endtime = x.EndTime.ToString(), Starttime = x.StartTime.ToString() }));
                    data.Availabilies.Add(newAvailability);
                }

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

        public override async Task<defaultReply> DeleteAvailability(DeleteAvailabilityRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                await _scheduleService.RemoveAvailability(Guid.Parse(request.Availabilityid), Guid.Parse(request.Ownerid));
                reply.Statuscode = Constants.SuccessStatusCode;
            }
            catch (Exception e)
            {
                reply.Statuscode = Constants.FailStatusCode;
                reply.Error = new error()
                {
                    Message = e.Message
                };
            }
            return reply;
        }

        public override async Task<defaultReply> SetService(SetServiceRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                await _consultantServices.SetService(Guid.Parse(request.ServiceId), Guid.Parse(request.OwnerId));
                reply.Statuscode = Constants.SuccessStatusCode;
            }
            catch (Exception e)
            {
                reply.Statuscode = Constants.FailStatusCode;
                reply.Error = new error()
                {
                    Message = e.Message
                };
            }
            return reply;
        }

        public override async Task<defaultReply> GetSchedules(OwnerIdRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                var response = await _scheduleService.GetSchedule(Guid.Parse(request.OwnerId));
                var data = new GetScheduleReply();

                data.Schedules.AddRange(response.Select(x => new schedule()
                {
                    Id = x.Id.ToString(),
                    Candidate = new candidate() { Name = x.Recipient.Name?.FullName, PictureUrl = x.Recipient.PictureUrl },
                    DateTime = $"{x.StartTime.ToString("t")} - {x.EndTime.ToString("t")}",
                    Notes = x.Notes,
                    Status = x.Status.ToString(),
                    Subject = x.Subject,
                }));

                reply.Statuscode = Constants.SuccessStatusCode;
                reply.Data = Any.Pack(data);
            }
            catch (Exception e)
            {
                reply.Statuscode = Constants.FailStatusCode;
                reply.Error = new error()
                {
                    Message = e.Message
                };
            }
            return reply;
        }

        public override async Task<defaultReply> CancelSchedule(ChangeScheduleStatusRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                var scheduleIds = request.IdList.Select(x => Guid.Parse(x));
                foreach (var id in scheduleIds)
                    await _scheduleService.UpdateStatus(id, ScheduleStatus.Canceled);

                reply.Statuscode = Constants.SuccessStatusCode;
            }
            catch (Exception e)
            {
                reply.Statuscode = Constants.FailStatusCode;
                reply.Error = new error
                {
                    Message = e.Message
                };
            }
            return reply;
        }

        public override async Task<defaultReply> ConfirmSchedule(ChangeScheduleStatusRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                var scheduleIds = request.IdList.Select(x => Guid.Parse(x));
                foreach (var id in scheduleIds)
                    await _scheduleService.UpdateStatus(id, ScheduleStatus.Confirmed);

                reply.Statuscode = Constants.SuccessStatusCode;
            }
            catch (Exception e)
            {
                reply.Statuscode = Constants.FailStatusCode;
                reply.Error = new error
                {
                    Message = e.Message
                };
            }
            return reply;
        }

        public override async Task<defaultReply> RemoveService(SetServiceRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                await _consultantServices.RemoveService(Guid.Parse(request.ServiceId), Guid.Parse(request.OwnerId));
                reply.Statuscode = Constants.SuccessStatusCode;
            }
            catch (Exception e)
            {
                reply.Statuscode = Constants.FailStatusCode;
                reply.Error = new error()
                {
                    Message = e.Message
                };
            }
            return reply;
        }

        public override async Task<defaultReply> GetProvidedServices(OwnerIdRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                var response = await _consultantServices.GetProvidedServices(Guid.Parse(request.OwnerId));
                var data = new GetProvidedServicesReply();

                data.Services.AddRange(response.OrderBy(x => x.Name)
                                               .Select(x => new service()
                                               {
                                                   Id = x.Id.ToString(),
                                                   Categoryname = x.Category.Name,
                                                   Name = x.Name,
                                                   Description = x.Description
                                               }));

                reply.Statuscode = Constants.SuccessStatusCode;
                reply.Data = Any.Pack(data);
            }
            catch (Exception e)
            {
                reply.Statuscode = Constants.FailStatusCode;
                reply.Error = new error()
                {
                    Message = e.Message
                };
            }
            return reply;
        }

        public override async Task<defaultReply> PublishResumeUploadedEvent(PublishResumeUploadedEventRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                await _consultantServices.PublishResumeUploadedEvent(Guid.Parse(request.OwnerId), request.OwnerName, request.OwnerEmail, request.BlobName);
                reply.Statuscode = Constants.SuccessStatusCode;
            }
            catch (Exception e)
            {
                reply.Statuscode = Constants.FailStatusCode;
                reply.Error = new error()
                {
                    Message = e.Message
                };
            }
            return reply;
        }

        public override async Task<defaultReply> SetResumeData(SetResumeDataRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                var resume = new Resume
                {
                    Summary = request.Summary,
                    Experience = [.. request.Experience],
                    Identification = new Identification()
                    {
                        Name = request.Identification.Name,
                        Region = request.Identification.Region,
                        Title = request.Identification.Title
                    },
                    Contact = new Contacts()
                    {
                        Email = request.Contact.Email,
                        LinkedIn = request.Contact.LinkedIn
                    },
                    Languages = request.Languages.Select(x => new Language()
                    {
                        Name = x.Name,
                        Level = x.Level
                    }).ToList(),
                    Educations = request.Education.Select(x => new Education()
                    {
                        School = x.School,
                        TimeRange = x.TtmeRange,
                        Title = x.Title
                    }).ToList()
                };

                if (request.StructuredExperience != null)
                    resume.StructuredExperience = new Experience()
                    {
                        Companies = [.. request.StructuredExperience.Companies],
                        Positions = [.. request.StructuredExperience.Positions]
                    };

                await _consultantServices.SetResumeData(Guid.Parse(request.OwnerId), resume);
                reply.Statuscode = Constants.SuccessStatusCode;
            }
            catch (Exception e)
            {
                reply.Statuscode = Constants.FailStatusCode;
                reply.Error = new error()
                {
                    Message = e.Message
                };
            }
            return reply;
        }

        public override async Task<defaultReply> GetResumeData(OwnerIdRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                var response = await _consultantServices.GetResumeData(Guid.Parse(request.OwnerId));
                var data = new GetResumeDataReply()
                {
                    Contact = new contacts()
                    {
                        Email = response.Contact.Email,
                        LinkedIn = response.Contact.LinkedIn
                    },
                    Identification = new identification()
                    {
                        Name = response.Identification.Name,
                        Title = response.Identification.Title,
                        Region = response.Identification.Region
                    },
                    Summary = response.Summary
                };

                if(response.StructuredExperience != null)
                {
                    data.StructuredExperience = new experience();
                    data.StructuredExperience.Companies.AddRange(response.StructuredExperience.Companies.Select(x => x));
                    data.StructuredExperience.Positions.AddRange(response.StructuredExperience.Positions.Select(x => x));
                }

                data.Experience.AddRange(response.Experience.Select(x => x));

                data.Languages.AddRange(response.Languages.Select(x => new language()
                {
                    Level = x.Level,
                    Name = x.Name
                }));

                data.Education.AddRange(response.Educations.Select(x => new education()
                {
                    School = x.School,
                    Title = x.Title,
                    TtmeRange = x.TimeRange
                }));

                reply.Statuscode = Constants.SuccessStatusCode;
                reply.Data = Any.Pack(data);
            }
            catch (Exception e)
            {
                reply.Statuscode = Constants.FailStatusCode;
                reply.Error = new error()
                {
                    Message = e.Message
                };
            }
            return reply;
        }

    }


}
