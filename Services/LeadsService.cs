using core.domain;
using core.domain.Enums;
using core.domain.Interfaces.Services;
using Grpc.Core;
using Any = Google.Protobuf.WellKnownTypes.Any;

namespace core.Services
{
    /// <summary>Entrada e listagem dos leads (docs/specs/leads-consultores-landing.md). Quem chama (consultant-bff) é uma rota pública: a mensagem de erro de dado inválido é a do domínio, a de falha de infraestrutura é genérica.</summary>
    public class LeadsService(ILeadService service, ILogger<LeadsService> logger) : Leads.LeadsBase
    {
        public const string UnavailableMessage = "Não foi possível registrar agora. Tente de novo em instantes.";

        public override async Task<defaultReply> CreateLead(CreateLeadRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                if (!Enum.TryParse<LeadKind>(request.Kind, ignoreCase: true, out var kind))
                    throw new ArgumentException("Tipo de lead inválido.");

                await service.CreateAsync(new NewLead(kind, request.Name, request.Email, request.Phone, request.Consent, request.ConsentTextVersion, request.ConsentText, request.Source));
                reply.Statuscode = Constants.SuccessStatusCode;
            }
            catch (ArgumentException ex)
            {
                reply.Statuscode = Constants.FailStatusCode;
                reply.Error = new error { Message = ex.Message };
            }
            catch (Exception ex)
            {
                // Dado do lead nunca vai para o log: só o tipo e a exceção.
                logger.LogError(ex, "Falha ao gravar lead do tipo {Kind}", request.Kind);
                reply.Statuscode = Constants.FailStatusCode;
                reply.Error = new error { Message = UnavailableMessage };
            }
            return reply;
        }

        public override async Task<defaultReply> ListLeads(ListLeadsRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                if (!Enum.TryParse<LeadKind>(request.Kind, ignoreCase: true, out var kind))
                    throw new ArgumentException("Tipo de lead inválido.");

                var leads = await service.ListAsync(kind);
                var data = new ListLeadsReply();
                data.Items.AddRange(leads.Select(x => new leadEntry
                {
                    Id = x.Id.ToString(),
                    Kind = x.Kind.ToString(),
                    Name = x.Name,
                    Email = x.Email,
                    Phone = x.Phone,
                    ConsentedAt = x.ConsentedAt.ToString("o"),
                    ConsentTextVersion = x.ConsentTextVersion,
                    Source = x.Source,
                    CreatedAt = x.CreatedAt.ToString("o"),
                    NotifiedAt = x.NotifiedAt?.ToString("o") ?? string.Empty
                }));

                reply.Statuscode = Constants.SuccessStatusCode;
                reply.Data = Any.Pack(data);
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
