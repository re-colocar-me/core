using core.domain;
using core.domain.Entities;
using core.domain.Interfaces.Services;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace core.Services
{
    public class ProfileService : Profile.ProfileBase
    {
        private IProfileService _service;
        private IResumeSuggestionService _resumeSuggestionService;

        public ProfileService(IProfileService service, IResumeSuggestionService resumeSuggestionService)
        {
            _service = service;
            _resumeSuggestionService = resumeSuggestionService;
        }

        public override async Task<defaultReply> SetBio(SetBioRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                await _service.SetBio(Guid.Parse(request.OwnerId), request.Text);
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

        public override async Task<defaultReply> GetFullProfile(GetFullProfileRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                var response = await _service.GetFullProfile(Guid.Parse(request.OwnerId));

                var data = new GetFullProfileReply()
                {
                    BioText = response.BioText ?? string.Empty,
                    BirthDate = response.BirthDate.HasValue ? Timestamp.FromDateTime(DateTime.SpecifyKind(response.BirthDate.Value, DateTimeKind.Utc)) : null,
                    Email = response.Email,
                    FirstName = response.Name?.FirstName,
                    LastName = response.Name?.LastName,
                    Id = response.Id.ToString(),
                    LastAccess = Timestamp.FromDateTime(DateTime.SpecifyKind(response.LastAccess, DateTimeKind.Utc)),
                    PictureUrl = response.PictureUrl,
                    Status = response.Status.ToString(),
                    TelephoneAreaCode = response.Telephone?.AreaCode ?? string.Empty,
                    TelephoneCountryCode = response.Telephone?.CountryCode ?? string.Empty,
                    TelephoneNumber = response.Telephone?.Number ?? string.Empty,
                    GenderCode = response.GenderCode ?? string.Empty,
                    Role = response.Role.ToString()
                };

                reply.Data = Any.Pack(data);
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

        public override async Task<defaultReply> SuggestBio(GetFullProfileRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                var suggestedText = await _resumeSuggestionService.SuggestBioAsync(Guid.Parse(request.OwnerId));
                reply.Data = Any.Pack(new SuggestBioReply { SuggestedText = suggestedText });
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

        public override async Task<defaultReply> AcceptTerms(AcceptTermsRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                await _service.AcceptTermsAsync(Guid.Parse(request.OwnerId), request.Version);
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

        public override async Task<defaultReply> SetAdditionalData(SetAdditionalDataRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                await _service.SetAdditionalData(Guid.Parse(request.OwnerId), new()
                {
                    birthDate = request.BirthDate.ToDateTime(),
                    genderCode = request.GenderCode,
                    telephone = new Telephone(request.TelephoneCountryCode, request.TelephoneAreaCode, request.TelephoneNumber)
                });
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
