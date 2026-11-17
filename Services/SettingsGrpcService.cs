using core.domain;
using core.domain.Interfaces.Services;
using Grpc.Core;
using Any = Google.Protobuf.WellKnownTypes.Any;

namespace core.Services
{
    /// <summary>
    /// Os parâmetros da ponte para a equipe (docs/specs/ponte-talento-consultor.md, seção 39). <b>Sem autenticação própria</b>, como o resto do core: a porta é interna ao cluster e só o
    /// <c>core-backoffice</c> a chama, depois de conferir o JWT do backoffice e o perfil de administração. As regras (faixas, regras entre parâmetros, histórico) ficam em <c>SettingsService</c>.
    /// </summary>
    public class SettingsGrpcService : PlatformSettingsAdmin.PlatformSettingsAdminBase
    {
        private readonly ISettingsService _settings;
        private readonly ILogger<SettingsGrpcService> _logger;

        public SettingsGrpcService(ISettingsService settings, ILogger<SettingsGrpcService> logger)
        {
            _settings = settings;
            _logger = logger;
        }

        public override Task<defaultReply> ListSettings(SetEmptyRequest request, ServerCallContext context) => Run(async () =>
        {
            var reply = new SetListReply();
            reply.Items.AddRange((await _settings.ListAsync()).Select(Map));
            return Any.Pack(reply);
        });

        public override Task<defaultReply> UpdateSetting(SetUpdateRequest request, ServerCallContext context) => Run(async () =>
            Any.Pack(Map(await _settings.SetAsync(request.Key, request.Value, request.Actor, request.Note))));

        public override Task<defaultReply> ResetSetting(SetResetRequest request, ServerCallContext context) => Run(async () =>
            Any.Pack(Map(await _settings.ResetAsync(request.Key, request.Actor, request.Note))));

        public override Task<defaultReply> ListChanges(SetChangesRequest request, ServerCallContext context) => Run(async () =>
        {
            var reply = new SetChangesReply();
            reply.Items.AddRange((await _settings.ListChangesAsync(request.Take)).Select(x => new SetChangeItem
            {
                Id = x.Id.ToString(), Key = x.Key, Label = x.Label, HasOldValue = x.OldValue.HasValue, OldValue = x.OldValue ?? 0, HasNewValue = x.NewValue.HasValue, NewValue = x.NewValue ?? 0,
                ChangedAt = x.ChangedAt.ToString("O"), ChangedBy = x.ChangedBy, Note = x.Note ?? string.Empty
            }));
            return Any.Pack(reply);
        });

        private static SetItem Map(SettingItem x) => new()
        {
            Key = x.Key, Group = x.Group, Label = x.Label, Description = x.Description, Unit = x.Unit, Min = x.Min, Max = x.Max, Default = x.Default, Value = x.Value, IsCustom = x.IsCustom,
            UpdatedAt = x.UpdatedAt?.ToString("O") ?? string.Empty, UpdatedBy = x.UpdatedBy ?? string.Empty
        };

        private async Task<defaultReply> Run(Func<Task<Any>> action)
        {
            var reply = new defaultReply();
            try
            {
                reply.Data = await action();
                reply.Statuscode = Constants.SuccessStatusCode;
            }
            catch (Exception ex) when (ex is ArgumentException or ConversationRuleException)
            {
                // Faixa, regra entre parâmetros, parâmetro desconhecido ou falta de quem mudou: a mensagem é para a equipe e já diz o que corrigir.
                reply.Statuscode = 400;
                reply.Error = new error { Code = 400, Message = ex.Message };
            }
            catch (Exception ex)
            {
                // Falha inesperada (o banco, por exemplo): 500, para a tela distinguir de um erro de regra, e uma mensagem genérica, para o texto técnico não chegar à tela da equipe.
                _logger.LogError(ex, "Falha ao atender os parâmetros da ponte.");
                reply.Statuscode = 500;
                reply.Error = new error { Code = 500, Message = "Não foi possível concluir. Tente de novo." };
            }
            return reply;
        }
    }
}
