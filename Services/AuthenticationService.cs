using core.domain.Interfaces.Services;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace core.Services;

public class AuthenticationService : Authentication.AuthenticationBase
{
    private readonly ILogger<AuthenticationService> _logger;
    private readonly IProfileService _service;

    public AuthenticationService(ILogger<AuthenticationService> logger,
                                 IProfileService service)
    {
        _logger = logger;
        _service = service;
    }

    public override async Task<AuthReply> Authenticate(AuthRequest request, ServerCallContext context)
    {
        var response = await _service.AuthenticateAsync(new domain.Authentication.AuthRequest()
        {
            Code = request.Code,
            Source = request.Source
        });

        return new AuthReply()
        {
            Token = response.Token,
            Profile = new basicprofile()
            {
                Email = response.Profile?.Email,
                Name = response.Profile?.Name,
                Id = response.Profile?.Id.ToString(),
                PictureUrl = response.Profile?.PictureUrl,
                Status = response.Profile?.Status,
                TermsAcceptedAt = response.Profile?.TermsAcceptedAt.HasValue == true
                    ? Timestamp.FromDateTime(DateTime.SpecifyKind(response.Profile.TermsAcceptedAt.Value, DateTimeKind.Utc))
                    : null,
                TermsAcceptedVersion = response.Profile?.TermsAcceptedVersion ?? string.Empty
            }
        };
    }
}
