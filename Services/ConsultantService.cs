using core.domain;
using core.domain.Entities;
using core.domain.Enums;
using core.domain.Interfaces.Infrastructure.Repositories;
using core.domain.Interfaces.Services;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace core.Services
{

    public class ConsultantService : Consultant.ConsultantBase
    {
        private IScheduleService _scheduleService;
        private IConsultantServices _consultantServices;
        private IResumeSuggestionService _resumeSuggestionService;
        private IConsultantServicesRepository _repository;

        public ConsultantService(IScheduleService scheduleService,
                                 IConsultantServices consultantServices,
                                 IResumeSuggestionService resumeSuggestionService,
                                 IConsultantServicesRepository repository)
        {
            _scheduleService = scheduleService;
            _consultantServices = consultantServices;
            _resumeSuggestionService = resumeSuggestionService;
            _repository = repository;
        }
        public override async Task<defaultReply> SetAvailability(SetAvailabilityRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();

            try
            {
                await _scheduleService.SetAvailability(Guid.Parse(request.OwnerId),
                                                        request.Wday,
                                                        TimeOnly.Parse(request.Start),
                                                        TimeOnly.Parse(request.End),
                                                        Guid.Parse(request.ServiceId),
                                                        request.HasPriceOverrideInLemonCoins ? request.PriceOverrideInLemonCoins : null);
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
                    newAvailability.Items.AddRange(item.Select(x =>
                    {
                        var newItem = new availabilityitem()
                        {
                            Id = x.Id.ToString(),
                            Endtime = x.EndTime.ToString(),
                            Starttime = x.StartTime.ToString(),
                            Serviceid = x.ServiceId.ToString(),
                            Servicename = x.Service?.Name ?? string.Empty
                        };
                        if (x.PriceOverrideInLemonCoins.HasValue)
                            newItem.Priceoverrideinlemoncoins = x.PriceOverrideInLemonCoins.Value;
                        return newItem;
                    }));
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
                await _consultantServices.SetService(Guid.Parse(request.ServiceId), Guid.Parse(request.OwnerId), System.Enum.Parse<ServiceFormat>(request.Format), request.HasSessionCount ? request.SessionCount : null);
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
                    Date = x.StartTime.ToString("yyyy-MM-dd"),
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

        public override async Task<defaultReply> GetConnections(OwnerIdRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                var response = await _consultantServices.GetConnections(Guid.Parse(request.OwnerId));
                var data = new GetConnectionsReply();

                data.Connections.AddRange(response.Select(x => new connection()
                {
                    Id = x.Id.ToString(),
                    Name = x.Candidate.Name.FullName,
                    PictureUrl = x.Candidate.PictureUrl ?? string.Empty,
                    Connectedat = x.ConnectedAt.ToString("dd/MM/yyyy HH:mm:ss")
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

        public override async Task<defaultReply> GetTratativas(OwnerIdRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                var response = await _consultantServices.ListTratativas(Guid.Parse(request.OwnerId));
                var data = new GetTratativasReply();

                var serviceNames = (await _repository.ListAllServicesAsync()).ToDictionary(x => x.Id, x => x.Name);

                data.Tratativas.AddRange(response.Select(x =>
                {
                    var newTratativa = new tratativa()
                    {
                        Id = x.Id.ToString(),
                        ConnectionId = x.ConnectionId.ToString(),
                        CandidateName = x.Connection?.Candidate.Name.FullName ?? string.Empty,
                        CandidatePictureUrl = x.Connection?.Candidate.PictureUrl ?? string.Empty,
                        CandidateEmail = x.Connection?.Candidate.Email ?? string.Empty,
                        CandidateTelephoneCountryCode = x.Connection?.Candidate.Telephone?.CountryCode ?? string.Empty,
                        CandidateTelephoneAreaCode = x.Connection?.Candidate.Telephone?.AreaCode ?? string.Empty,
                        CandidateTelephoneNumber = x.Connection?.Candidate.Telephone?.Number ?? string.Empty,
                        ServiceId = x.ServiceId?.ToString() ?? string.Empty,
                        ServiceName = x.ServiceId.HasValue && serviceNames.TryGetValue(x.ServiceId.Value, out var name) ? name : string.Empty,
                        Stage = x.Stage.ToString(),
                        Observacao = x.Observacao ?? string.Empty,
                        CreatedAt = x.CreatedAt.ToString("dd/MM/yyyy HH:mm:ss"),
                        ConnectedAt = x.Connection?.ConnectedAt.ToString("dd/MM/yyyy HH:mm:ss") ?? string.Empty
                    };

                    newTratativa.StageHistory.AddRange(x.StageHistory.Select(h =>
                    {
                        var change = new tratativaStageChange()
                        {
                            ToStage = h.ToStage.ToString(),
                            ChangedAt = h.ChangedAt.ToString("dd/MM/yyyy HH:mm:ss")
                        };
                        if (h.FromStage.HasValue)
                            change.FromStage = h.FromStage.Value.ToString();
                        return change;
                    }));

                    return newTratativa;
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

        public override async Task<defaultReply> CreateTratativa(CreateTratativaRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                await _consultantServices.CriarTratativaManual(
                    Guid.Parse(request.OwnerId),
                    Guid.Parse(request.ConnectionId),
                    Guid.Parse(request.ServiceId),
                    request.HasObservacao ? request.Observacao : null);
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

        public override async Task<defaultReply> MoveTratativa(MoveTratativaRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                var stage = System.Enum.Parse<TratativaStage>(request.Stage);
                await _consultantServices.MoverTratativa(Guid.Parse(request.OwnerId), Guid.Parse(request.TratativaId), stage);
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

        public override async Task<defaultReply> SetTratativaService(SetTratativaServiceRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                await _consultantServices.DefinirServico(Guid.Parse(request.OwnerId), Guid.Parse(request.TratativaId), Guid.Parse(request.ServiceId));
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

        public override async Task<defaultReply> SetTratativaObservacao(SetTratativaObservacaoRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                await _consultantServices.AtualizarObservacao(Guid.Parse(request.OwnerId), Guid.Parse(request.TratativaId), request.Observacao);
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
                await _consultantServices.RemoveService(Guid.Parse(request.ServiceId), Guid.Parse(request.OwnerId), System.Enum.Parse<ServiceFormat>(request.Format));
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

                data.Services.AddRange(response.OrderBy(x => x.ServiceItem!.Name)
                                               .Select(x => new service()
                                               {
                                                   Id = x.ServiceItem!.Id.ToString(),
                                                   Categoryname = x.ServiceItem.Category?.Name ?? string.Empty,
                                                   Name = x.ServiceItem.Name,
                                                   Description = x.ServiceItem.Description ?? string.Empty,
                                                   Offeringid = x.Id.ToString(),
                                                   Priceinlemoncoins = x.PriceInLemonCoins,
                                                   Format = x.Format.ToString(),
                                                   Sessioncount = x.SessionCount ?? 0
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

                resume.ExperienceEntries = request.ExperienceEntries.Select(x => new ExperienceEntry()
                {
                    Company = x.Company,
                    Title = x.Title,
                    Period = x.Period,
                    Details = x.Details
                }).ToList();

                resume.Skills = [.. request.Skills];

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

                data.ExperienceEntries.AddRange(response.ExperienceEntries.Select(x => new experienceEntry()
                {
                    Company = x.Company,
                    Title = x.Title,
                    Period = x.Period,
                    Details = x.Details
                }));

                data.Skills.AddRange(response.Skills);

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

        public override async Task<defaultReply> SuggestSummary(OwnerIdRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                var suggestedText = await _resumeSuggestionService.SuggestSummaryAsync(Guid.Parse(request.OwnerId));
                reply.Data = Any.Pack(new SuggestSummaryReply { SuggestedText = suggestedText });
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

        public override async Task<defaultReply> SetServicePrice(SetServicePriceRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                await _consultantServices.SetServicePrice(Guid.Parse(request.OwnerId), Guid.Parse(request.ServiceId), System.Enum.Parse<ServiceFormat>(request.Format), request.PriceInLemonCoins, request.HasSessionCount ? request.SessionCount : null);
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

        public override async Task<defaultReply> GetServicePriceHistory(GetServicePriceHistoryRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                var history = await _consultantServices.GetServicePriceHistory(Guid.Parse(request.OwnerId), Guid.Parse(request.ServiceId), System.Enum.Parse<ServiceFormat>(request.Format));
                var data = new GetServicePriceHistoryReply();
                data.Entries.AddRange(history.Select(x => new priceHistoryEntry
                {
                    Price = x.Price,
                    EffectiveFrom = x.EffectiveFrom.ToString("O")
                }));
                reply.Data = Any.Pack(data);
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

        public override async Task<defaultReply> GetOfferingForPayment(OfferingIdRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                var offering = await _consultantServices.GetOfferingForPayment(Guid.Parse(request.OfferingId))
                    ?? throw new ArgumentException("Oferta de serviço não encontrada.");

                if (offering.Consultant is null)
                    throw new ArgumentException("Consultor da oferta não encontrado.");

                reply.Data = Any.Pack(new GetOfferingForPaymentReply
                {
                    ConsultantProfileId = offering.Consultant.ProfileId.ToString(),
                    PriceInLemonCoins = offering.PriceInLemonCoins,
                    ServiceName = offering.ServiceItem?.Name ?? string.Empty
                });
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

        public override async Task<defaultReply> SetSporadicAvailability(SetSporadicAvailabilityRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                await _scheduleService.SetSporadicAvailability(Guid.Parse(request.OwnerId),
                                                                DateOnly.Parse(request.Date),
                                                                TimeOnly.Parse(request.Start),
                                                                TimeOnly.Parse(request.End),
                                                                request.Reason,
                                                                Guid.Parse(request.ServiceId),
                                                                request.HasPriceOverrideInLemonCoins ? request.PriceOverrideInLemonCoins : null);
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

        public override async Task<defaultReply> GetSporadicAvailabilityListByOwner(OwnerIdRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                var list = await _scheduleService.GetSporadicAvailabilityByOwner(Guid.Parse(request.OwnerId));
                var data = new GetSporadicAvailabilityListByOwnerReply();

                data.Items.AddRange(list.Select(x =>
                {
                    var newItem = new sporadicavailabilityitem()
                    {
                        Id = x.Id.ToString(),
                        Date = x.Date.ToString("yyyy-MM-dd"),
                        Starttime = x.StartTime.ToString(),
                        Endtime = x.EndTime.ToString(),
                        Reason = x.Reason ?? string.Empty,
                        Serviceid = x.ServiceId.ToString(),
                        Servicename = x.Service?.Name ?? string.Empty
                    };
                    if (x.PriceOverrideInLemonCoins.HasValue)
                        newItem.Priceoverrideinlemoncoins = x.PriceOverrideInLemonCoins.Value;
                    return newItem;
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

        public override async Task<defaultReply> DeleteSporadicAvailability(DeleteAvailabilityRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                await _scheduleService.RemoveSporadicAvailability(Guid.Parse(request.Availabilityid), Guid.Parse(request.Ownerid));
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

    }


}
