using core.domain;
using core.domain.Entities;
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
        private readonly ITalentServices _talentServices;
        private readonly IResumeSuggestionService _resumeSuggestionService;

        public TalentService(IConsultantServices services, IProfileService profileService, ITalentServices talentServices, IResumeSuggestionService resumeSuggestionService)
        {
            _services = services;
            _profileService = profileService;
            _talentServices = talentServices;
            _resumeSuggestionService = resumeSuggestionService;
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
                    request.HasCandidateTelephoneNumber ? request.CandidateTelephoneNumber : null);
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

    }
}
