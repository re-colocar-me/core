using core.domain;
using core.domain.Entities;
using core.domain.Enums;
using core.domain.Interfaces.Infrastructure;
using core.domain.Interfaces.Services;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace core.Services
{
    public class TalentService : Talent.TalentBase
    {
        private IConsultantServices _services;
        private readonly IProfileService _profileService;
        private readonly ITalentServices _talentServices;
        private readonly IResumeSuggestionService _resumeSuggestionService;
        private readonly IScheduleService _scheduleService;
        private readonly ISwotAnalysisRepository _swotAnalysisRepository;

        public TalentService(IConsultantServices services, IProfileService profileService, ITalentServices talentServices, IResumeSuggestionService resumeSuggestionService, IScheduleService scheduleService, ISwotAnalysisRepository swotAnalysisRepository)
        {
            _services = services;
            _profileService = profileService;
            _talentServices = talentServices;
            _resumeSuggestionService = resumeSuggestionService;
            _scheduleService = scheduleService;
            _swotAnalysisRepository = swotAnalysisRepository;
        }

        public override async Task<defaultReply> GetMyConnections(OwnerIdRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                // Candidatos podem ter se conectado mais de uma vez com o mesmo consultor (histórico
                // real de dados de teste) - a lista de conexões é conceitualmente 1 por consultor,
                // então mantém só a mais recente de cada.
                var connections = (await _services.GetConnectionsForCandidate(Guid.Parse(request.OwnerId)))
                    .GroupBy(x => x.ConsultantId)
                    .Select(g => g.OrderByDescending(x => x.ConnectedAt).First())
                    .OrderByDescending(x => x.ConnectedAt)
                    .ToList();
                var consultantIds = connections.Select(x => x.ConsultantId).Distinct().ToList();
                var consultants = new Dictionary<Guid, core.domain.Entities.Consultant>();
                foreach (var consultantId in consultantIds)
                {
                    var consultant = await _services.GetConsultantById(consultantId);
                    if (consultant is not null)
                        consultants[consultantId] = consultant;
                }

                var profiles = (await _profileService.GetByIdsAsync(consultants.Values.Select(x => x.ProfileId)))
                    .ToDictionary(x => x.Id);

                var data = new GetMyConnectionsReply();
                data.Connections.AddRange(connections
                    .Where(x => consultants.ContainsKey(x.ConsultantId))
                    .Select(x =>
                    {
                        var consultant = consultants[x.ConsultantId];
                        var newItem = new connectionSummary()
                        {
                            Id = x.Id.ToString(),
                            ConsultantId = x.ConsultantId.ToString(),
                            ConsultantName = consultant.Name.FullName,
                            ConnectedAt = x.ConnectedAt.ToString("O")
                        };
                        if (profiles.TryGetValue(consultant.ProfileId, out var profile))
                            newItem.PictureUrl = profile.PictureUrl;
                        return newItem;
                    }));

                reply.Statuscode = Constants.SuccessStatusCode;
                reply.Data = Any.Pack(data);
            }
            catch (Exception e)
            {
                reply.Statuscode = Constants.FailStatusCode;
                reply.Error = new error() { Message = e.Message };
            }
            return reply;
        }

        public override async Task<defaultReply> GetConsultantDetails(ConsultantIdRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                var consultant = await _services.GetConsultantById(Guid.Parse(request.ConsultantId))
                    ?? throw new ArgumentException("Consultor não encontrado.");

                var profiles = await _profileService.GetByIdsAsync([consultant.ProfileId]);
                var profile = profiles.FirstOrDefault();

                var providedServices = await _services.GetProvidedServices(consultant.ProfileId);
                var weeklyAvailability = await _scheduleService.GetAvailabiltyListByOwner(consultant.ProfileId);
                var sporadicAvailability = await _scheduleService.GetBookableSporadicAvailabilityByOwner(consultant.ProfileId);

                var data = new GetConsultantDetailsReply()
                {
                    Id = consultant.Id.ToString(),
                    Name = consultant.Name.FullName,
                    PictureUrl = profile?.PictureUrl ?? string.Empty
                };
                if (!string.IsNullOrWhiteSpace(profile?.BioText))
                    data.Bio = profile!.BioText;

                data.Skills.AddRange(consultant.Skills.Select(x => x.Name));

                data.ProvidedServices.AddRange(providedServices.Select(x => new service()
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

                foreach (var grouped in weeklyAvailability.GroupBy(x => x.WeekDay))
                {
                    var newAvailability = new availability() { Weekday = grouped.Key };
                    newAvailability.Items.AddRange(grouped.Select(x =>
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
                    data.WeeklyAvailability.Add(newAvailability);
                }

                data.SporadicAvailability.AddRange(sporadicAvailability.Select(x =>
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
                reply.Error = new error() { Message = e.Message };
            }
            return reply;
        }

        public override async Task<defaultReply> RemoveConnection(RemoveConnectionRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                await _services.RemoveConnection(Guid.Parse(request.CandidateId), Guid.Parse(request.ConsultantId));
                reply.Statuscode = Constants.SuccessStatusCode;
            }
            catch (Exception e)
            {
                reply.Statuscode = Constants.FailStatusCode;
                reply.Error = new error() { Message = e.Message };
            }
            return reply;
        }

        public override async Task<defaultReply> ListConsultants(FilterRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                var response = await _services.Filter(request.Name, request.Services!);
                var profiles = (await _profileService.GetByIdsAsync(response.Select(x => x.ProfileId)))
                    .ToDictionary(x => x.Id);
                var data = new ListConsultantsReply();

                foreach (var item in response)
                {
                    var newConsultant = new consultant();
                    newConsultant.Id = item.Id.ToString();
                    newConsultant.Name = item.Name.FullName;

                    if (profiles.TryGetValue(item.ProfileId, out var profile))
                    {
                        newConsultant.PictureUrl = profile.PictureUrl;
                        if (!string.IsNullOrWhiteSpace(profile.BioText))
                            newConsultant.Bio = profile.BioText;
                    }

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
                    newConsultant.Skills.AddRange(item.Skills.Select(x => x.Name));
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
                await _services.Connect(
                    Guid.Parse(request.ConsultantId),
                    Guid.Parse(request.CandidateId),
                    request.CandidateFirstName,
                    request.CandidateLastName,
                    request.HasCandidateEmail ? request.CandidateEmail : null,
                    request.HasCandidatePictureUrl ? request.CandidatePictureUrl : null,
                    request.HasCandidateTelephoneCountryCode ? request.CandidateTelephoneCountryCode : null,
                    request.HasCandidateTelephoneAreaCode ? request.CandidateTelephoneAreaCode : null,
                    request.HasCandidateTelephoneNumber ? request.CandidateTelephoneNumber : null,
                    request.ShareSwotAnalysis);
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

        public override async Task<defaultReply> PublishResumeUploadedEvent(PublishResumeUploadedEventRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                await _talentServices.PublishResumeUploadedEvent(Guid.Parse(request.OwnerId), request.OwnerName, request.OwnerEmail, request.BlobName);
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

                await _talentServices.SetResumeData(Guid.Parse(request.OwnerId), resume);
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
                var response = await _talentServices.GetResumeData(Guid.Parse(request.OwnerId));
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

                if (response.StructuredExperience != null)
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

        public override async Task<defaultReply> EnsureProfile(EnsureProfileRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                await _profileService.EnsureProfileAsync(
                    Guid.Parse(request.Id),
                    new PersonName { FirstName = request.FirstName, LastName = request.LastName },
                    request.Email,
                    request.PictureUrl,
                    RoleType.Talent);
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

        public override async Task<defaultReply> GetTalentSkills(GetTalentSkillsRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                var skills = await _talentServices.GetTalentSkills(Guid.Parse(request.TalentProfileId));
                var data = new GetTalentSkillsReply();
                data.SkillNames.AddRange(skills.Select(x => x.Name));
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

        public override async Task<defaultReply> SetTalentSkills(SetTalentSkillsRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                await _talentServices.SetTalentSkills(Guid.Parse(request.TalentProfileId), request.SkillNames);
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

        public override async Task<defaultReply> SuggestSkills(GetTalentSkillsRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                var suggestedSkills = await _resumeSuggestionService.SuggestSkillsAsync(Guid.Parse(request.TalentProfileId));
                var data = new SuggestTalentSkillsReply();
                data.SuggestedSkills.AddRange(suggestedSkills);
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

        public override async Task<defaultReply> GenerateSwotAnalysis(GenerateSwotAnalysisRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                var ownerId = Guid.Parse(request.OwnerId);
                var analysisText = await _resumeSuggestionService.SuggestSwotAnalysisAsync(ownerId, request.VocationalSummary);
                await _swotAnalysisRepository.SaveAsync(ownerId, analysisText);

                reply.Data = Any.Pack(new SwotAnalysisReply { Found = true, AnalysisText = analysisText });
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

        public override async Task<defaultReply> GetSwotAnalysis(OwnerIdRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                var analysisText = await _swotAnalysisRepository.GetAsync(Guid.Parse(request.OwnerId));
                reply.Data = Any.Pack(new SwotAnalysisReply { Found = analysisText != null, AnalysisText = analysisText ?? string.Empty });
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

        // docs/specs/compra-agendamento-servico-consultor.md
        public override async Task<defaultReply> CreateServicePurchase(CreateServicePurchaseRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                var offeringId = Guid.Parse(request.OfferingId);
                var offering = await _services.GetOfferingForPayment(offeringId)
                    ?? throw new ArgumentException("Oferta de serviço não encontrada.");

                if (offering.ConsultantId != Guid.Parse(request.ConsultantId))
                    throw new ArgumentException("Esta oferta não pertence ao consultor informado.");

                var totalSessions = offering.Format == ServiceFormat.Package ? (offering.SessionCount ?? 1) : 1;
                var unitPrice = totalSessions > 0 ? offering.PriceInLemonCoins / totalSessions : offering.PriceInLemonCoins;

                var purchase = await _scheduleService.CreateServicePurchase(
                    Guid.Parse(request.CandidateId),
                    request.CandidateFirstName,
                    request.CandidateLastName,
                    request.HasCandidateEmail ? request.CandidateEmail : null,
                    request.HasCandidatePictureUrl ? request.CandidatePictureUrl : null,
                    offering.ConsultantId,
                    offeringId,
                    offering.ServiceItem?.Name ?? string.Empty,
                    offering.Format,
                    totalSessions,
                    unitPrice,
                    offering.PriceInLemonCoins,
                    Guid.Parse(request.WalletDebitTransactionId));

                reply.Data = Any.Pack(new CreateServicePurchaseReply { Id = purchase.Id.ToString(), TotalSessions = purchase.TotalSessions });
                reply.Statuscode = Constants.SuccessStatusCode;
            }
            catch (Exception e)
            {
                reply.Statuscode = Constants.FailStatusCode;
                reply.Error = new error() { Message = e.Message };
            }
            return reply;
        }

        public override async Task<defaultReply> ListMyServicePurchases(CandidateIdRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                var purchases = await _scheduleService.GetServicePurchasesForCandidate(Guid.Parse(request.CandidateId));
                var data = new ListMyServicePurchasesReply();
                data.Items.AddRange(await BuildServicePurchaseEntries(purchases));
                reply.Data = Any.Pack(data);
                reply.Statuscode = Constants.SuccessStatusCode;
            }
            catch (Exception e)
            {
                reply.Statuscode = Constants.FailStatusCode;
                reply.Error = new error() { Message = e.Message };
            }
            return reply;
        }

        public override async Task<defaultReply> ListServicePurchasesForConsultant(OwnerIdRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                var purchases = await _scheduleService.GetServicePurchasesForConsultant(Guid.Parse(request.OwnerId));
                var data = new ListServicePurchasesForConsultantReply();
                data.Items.AddRange(await BuildServicePurchaseEntries(purchases));
                reply.Data = Any.Pack(data);
                reply.Statuscode = Constants.SuccessStatusCode;
            }
            catch (Exception e)
            {
                reply.Statuscode = Constants.FailStatusCode;
                reply.Error = new error() { Message = e.Message };
            }
            return reply;
        }

        private async Task<List<servicePurchaseEntry>> BuildServicePurchaseEntries(List<ServicePurchase> purchases)
        {
            var consultantIds = purchases.Select(x => x.ConsultantId).Distinct().ToList();
            var consultants = new Dictionary<Guid, core.domain.Entities.Consultant>();
            foreach (var id in consultantIds)
            {
                var consultant = await _services.GetConsultantById(id);
                if (consultant is not null)
                    consultants[id] = consultant;
            }

            var entries = new List<servicePurchaseEntry>();
            foreach (var purchase in purchases)
            {
                var usedSessions = await _scheduleService.CountUsedSessions(purchase.Id);
                entries.Add(new servicePurchaseEntry
                {
                    Id = purchase.Id.ToString(),
                    ConsultantId = purchase.ConsultantId.ToString(),
                    ConsultantName = consultants.TryGetValue(purchase.ConsultantId, out var consultant) ? consultant.Name.FullName : string.Empty,
                    CandidateName = purchase.Candidate.Name.FullName,
                    OfferingId = purchase.OfferingId.ToString(),
                    ServiceName = purchase.ServiceName,
                    Format = purchase.Format.ToString(),
                    TotalSessions = purchase.TotalSessions,
                    UsedSessions = usedSessions,
                    UnitPriceInLemonCoins = purchase.UnitPriceInLemonCoins,
                    TotalPriceInLemonCoins = purchase.TotalPriceInLemonCoins,
                    PurchasedAt = purchase.PurchasedAt.ToString("O"),
                    Status = purchase.Status.ToString()
                });
            }
            return entries;
        }

        // Box de agendamentos do candidato no Feed (ajuste pedido pelo usuário) — Schedule não
        // guarda ConsultantId direto, resolvido via ServicePurchase.ConsultantId de cada agendamento.
        public override async Task<defaultReply> ListMySchedules(CandidateIdRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                var candidateId = Guid.Parse(request.CandidateId);
                var schedules = await _scheduleService.GetSchedulesForCandidate(candidateId);
                var purchases = await _scheduleService.GetServicePurchasesForCandidate(candidateId);
                var consultantIdByPurchaseId = purchases.ToDictionary(x => x.Id, x => x.ConsultantId);

                var consultants = new Dictionary<Guid, core.domain.Entities.Consultant>();
                foreach (var consultantId in purchases.Select(x => x.ConsultantId).Distinct())
                {
                    var consultant = await _services.GetConsultantById(consultantId);
                    if (consultant is not null)
                        consultants[consultantId] = consultant;
                }

                var data = new ListMySchedulesReply();
                data.Schedules.AddRange(schedules.Select(x =>
                {
                    var consultantName = string.Empty;
                    if (x.ServicePurchaseId.HasValue
                        && consultantIdByPurchaseId.TryGetValue(x.ServicePurchaseId.Value, out var consultantId)
                        && consultants.TryGetValue(consultantId, out var consultant))
                        consultantName = consultant.Name.FullName;

                    return new myScheduleEntry
                    {
                        Id = x.Id.ToString(),
                        ConsultantName = consultantName,
                        DateTime = $"{x.StartTime:t} - {x.EndTime:t}",
                        Date = x.StartTime.ToString("yyyy-MM-dd"),
                        Subject = x.Subject ?? string.Empty,
                        Status = x.Status.ToString()
                    };
                }));

                reply.Data = Any.Pack(data);
                reply.Statuscode = Constants.SuccessStatusCode;
            }
            catch (Exception e)
            {
                reply.Statuscode = Constants.FailStatusCode;
                reply.Error = new error() { Message = e.Message };
            }
            return reply;
        }

        public override async Task<defaultReply> GetScheduleIcs(GetScheduleIcsRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                var scheduleId = Guid.Parse(request.ScheduleId);
                var candidateId = Guid.Parse(request.CandidateId);

                var schedule = await _scheduleService.GetScheduleById(scheduleId)
                    ?? throw new ArgumentException("Agendamento não encontrado.");

                if (schedule.ServicePurchaseId is null)
                    throw new ArgumentException("Agendamento sem compra vinculada.");

                var purchase = await _scheduleService.GetServicePurchaseById(schedule.ServicePurchaseId.Value)
                    ?? throw new ArgumentException("Compra não encontrada.");

                if (purchase.CandidateId != candidateId)
                    throw new ArgumentException("Este agendamento não pertence a este candidato.");

                reply.Data = Any.Pack(new GetScheduleIcsReply
                {
                    FileName = $"agendamento-{scheduleId}.ics",
                    Content = BuildIcs(schedule, purchase)
                });
                reply.Statuscode = Constants.SuccessStatusCode;
            }
            catch (Exception e)
            {
                reply.Statuscode = Constants.FailStatusCode;
                reply.Error = new error() { Message = e.Message };
            }
            return reply;
        }

        // Formato mínimo iCalendar (RFC 5545) — StartTime/EndTime são gravados em UTC por
        // convenção neste projeto (mesmo padrão assumido em ProfileService.GetFullProfile pra
        // BirthDate/LastAccess).
        private static string BuildIcs(Schedule schedule, ServicePurchase purchase)
        {
            static string Fmt(DateTime dt) => DateTime.SpecifyKind(dt, DateTimeKind.Utc).ToString("yyyyMMdd'T'HHmmss'Z'");
            var summary = (schedule.Subject ?? purchase.ServiceName).Replace("\r", " ").Replace("\n", " ");

            return string.Join("\r\n",
            [
                "BEGIN:VCALENDAR",
                "VERSION:2.0",
                "PRODID:-//re.colocar.me//Agendamento//PT-BR",
                "BEGIN:VEVENT",
                $"UID:{schedule.Id}@re.colocar-me.com.br",
                $"DTSTAMP:{Fmt(DateTime.UtcNow)}",
                $"DTSTART:{Fmt(schedule.StartTime)}",
                $"DTEND:{Fmt(schedule.EndTime)}",
                $"SUMMARY:{summary}",
                "END:VEVENT",
                "END:VCALENDAR",
                ""
            ]);
        }

        public override async Task<defaultReply> GetBookedDatesForAvailability(AvailabilityIdRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                var dates = await _scheduleService.GetBookedDatesForAvailability(Guid.Parse(request.AvailabilityId));

                var data = new GetBookedDatesForAvailabilityReply();
                data.Dates.AddRange(dates.Select(x => x.ToString("yyyy-MM-dd")));

                reply.Data = Any.Pack(data);
                reply.Statuscode = Constants.SuccessStatusCode;
            }
            catch (Exception e)
            {
                reply.Statuscode = Constants.FailStatusCode;
                reply.Error = new error() { Message = e.Message };
            }
            return reply;
        }

        public override async Task<defaultReply> ScheduleServiceSession(ScheduleServiceSessionRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                DateOnly? weeklyDate = request.HasWeeklyDate ? DateOnly.Parse(request.WeeklyDate) : null;

                var schedule = await _scheduleService.ScheduleServiceSession(
                    Guid.Parse(request.ServicePurchaseId),
                    Guid.Parse(request.CandidateId),
                    request.HasSourceAvailabilityId ? Guid.Parse(request.SourceAvailabilityId) : null,
                    weeklyDate,
                    request.HasSourceSporadicAvailabilityId ? Guid.Parse(request.SourceSporadicAvailabilityId) : null);

                reply.Data = Any.Pack(new ScheduleServiceSessionReply { ScheduleId = schedule.Id.ToString() });
                reply.Statuscode = Constants.SuccessStatusCode;
            }
            catch (Exception e)
            {
                reply.Statuscode = Constants.FailStatusCode;
                reply.Error = new error() { Message = e.Message };
            }
            return reply;
        }

        public override async Task<defaultReply> GetServicePurchaseCancellationPreview(ServicePurchaseIdRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                var id = Guid.Parse(request.Id);
                // Levanta ArgumentException ("já cancelada") se a compra já foi estornada — barra
                // reestorno por retry/duplo clique (a RPC de estorno parcial não marca o grupo de
                // transações como Reversed, então essa checagem em GetServicePurchaseCancellationPreview
                // é o único guard contra chamar /cancel duas vezes).
                var preview = await _scheduleService.GetServicePurchaseCancellationPreview(id);

                reply.Data = Any.Pack(new GetServicePurchaseCancellationPreviewReply
                {
                    UnusedSessions = preview.UnusedSessions,
                    UnitPriceInLemonCoins = preview.UnitPriceInLemonCoins,
                    GrossAmountToRefundLemonCoins = preview.GrossAmountToRefundLemonCoins,
                    WalletDebitTransactionId = preview.WalletDebitTransactionId.ToString()
                });
                reply.Statuscode = Constants.SuccessStatusCode;
            }
            catch (Exception e)
            {
                reply.Statuscode = Constants.FailStatusCode;
                reply.Error = new error() { Message = e.Message };
            }
            return reply;
        }

        public override async Task<defaultReply> MarkServicePurchaseCanceled(ServicePurchaseIdRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                await _scheduleService.MarkServicePurchaseCanceled(Guid.Parse(request.Id));
                reply.Statuscode = Constants.SuccessStatusCode;
            }
            catch (Exception e)
            {
                reply.Statuscode = Constants.FailStatusCode;
                reply.Error = new error() { Message = e.Message };
            }
            return reply;
        }
    }
}
