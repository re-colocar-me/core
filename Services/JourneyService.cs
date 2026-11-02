using System.Globalization;
using core.domain;
using core.domain.Entities;
using core.domain.Enums;
using core.domain.Interfaces.Services;
using core.domain.Services;
using Grpc.Core;
using Any = Google.Protobuf.WellKnownTypes.Any;

namespace core.Services
{
    /// <summary>
    /// RPCs do estado da jornada (docs/specs/estado-historico-jornada-talent.md, seção 6). As gravações montam o evento
    /// correspondente (produtor <c>core</c>) e o aplicam no mesmo pedido, sem passar pela fila.
    /// </summary>
    public class JourneyService : Journey.JourneyBase
    {
        private const string Producer = "core";

        private readonly IJourneyService _journey;
        private readonly TimeProvider _time;

        public JourneyService(IJourneyService journey, TimeProvider time)
        {
            _journey = journey;
            _time = time;
        }

        public override Task<defaultReply> GetJourneyState(JourneyProfileRequest request, ServerCallContext context)
            => Run(async () => await StateAsync(ParseGuid(request.ProfileId, "profileId")));

        public override Task<defaultReply> SetJourneySituation(SetJourneySituationRequest request, ServerCallContext context)
            => Run(async () =>
            {
                var situation = request.Situation?.Trim() ?? string.Empty;
                if (!JourneyNames.Situations.Contains(situation))
                    throw new ArgumentException($"Situação inválida: '{request.Situation}'.");

                var timeSince = request.TimeSinceLayoff?.Trim() ?? string.Empty;
                if (situation == "desligado")
                {
                    if (!JourneyNames.TimeSinceLayoff.Contains(timeSince))
                        throw new ArgumentException($"Faixa de tempo desde o desligamento inválida: '{request.TimeSinceLayoff}'.");
                }
                else
                {
                    timeSince = string.Empty;
                }

                return await ApplyThenStateAsync(request.ProfileId, request.EventId, JourneyEventType.SituationAnswered,
                    new JourneyEventData { Situation = situation, TimeSinceLayoff = timeSince.Length == 0 ? null : timeSince });
            });

        public override Task<defaultReply> PauseSearch(JourneyActionRequest request, ServerCallContext context)
            => Run(() => ApplyThenStateAsync(request.ProfileId, request.EventId, JourneyEventType.SearchPaused, null));

        public override Task<defaultReply> ResumeSearch(JourneyActionRequest request, ServerCallContext context)
            => Run(() => ApplyThenStateAsync(request.ProfileId, request.EventId, JourneyEventType.SearchResumed, null));

        public override Task<defaultReply> ConfirmRecolocation(ConfirmRecolocationRequest request, ServerCallContext context)
            => Run(async () =>
            {
                var outcomeType = request.OutcomeType?.Trim() ?? string.Empty;
                if (!JourneyNames.OutcomeTypes.Contains(outcomeType))
                    throw new ArgumentException($"Tipo de desfecho inválido: '{request.OutcomeType}'.");

                DateTime? outcomeDate = null;
                if (!string.IsNullOrWhiteSpace(request.OutcomeDate))
                {
                    if (!DateTime.TryParse(request.OutcomeDate, CultureInfo.InvariantCulture,
                            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
                        throw new ArgumentException($"Data do desfecho inválida: '{request.OutcomeDate}'.");
                    // A data é aproximada e declarada pela pessoa; só se recusa o que está claramente no futuro.
                    if (parsed > _time.GetUtcNow().UtcDateTime.AddDays(1))
                        throw new ArgumentException("A data do desfecho não pode estar no futuro.");
                    outcomeDate = parsed;
                }

                return await ApplyThenStateAsync(request.ProfileId, request.EventId, JourneyEventType.RecolocationConfirmed,
                    new JourneyEventData { OutcomeType = outcomeType, OutcomeDate = outcomeDate, AreaChange = request.AreaChange });
            });

        public override Task<defaultReply> StartNewCycle(StartNewCycleRequest request, ServerCallContext context)
            => Run(async () =>
            {
                var profileId = ParseGuid(request.ProfileId, "profileId");
                var origin = request.Origin?.Trim().ToLowerInvariant() switch
                {
                    "continue_searching" => JourneyCycleOrigin.ContinueSearching,
                    "reactivation" => JourneyCycleOrigin.Reactivation,
                    _ => throw new ArgumentException($"Origem inválida: '{request.Origin}' (use continue_searching ou reactivation).")
                };

                await _journey.StartNewCycleAsync(profileId, origin, _time.GetUtcNow().UtcDateTime);
                return await StateAsync(profileId);
            });

        // ------------------------------------------------------------------

        private async Task<JourneyStateReply> ApplyThenStateAsync(string profileIdText, string eventIdText, JourneyEventType type, JourneyEventData? data)
        {
            var profileId = ParseGuid(profileIdText, "profileId");
            var eventId = string.IsNullOrWhiteSpace(eventIdText) ? Guid.NewGuid() : ParseGuid(eventIdText, "eventId");

            var result = await _journey.ApplyAsync(new JourneyEvent
            {
                EventId = eventId,
                Type = type,
                ProfileId = profileId,
                OccurredAt = _time.GetUtcNow().UtcDateTime,
                Producer = Producer,
                Data = data
            });

            // Duplicate: o mesmo pedido repetido (mesmo eventId) é sucesso. Ignored: a ação já estava feita (pausar quem já está em pausa).
            if (result == JourneyApplyResult.NoOpenCycle)
                throw new InvalidOperationException("A pessoa não tem um ciclo aberto na jornada.");

            return await StateAsync(profileId);
        }

        private async Task<JourneyStateReply> StateAsync(Guid profileId)
        {
            var cycles = await _journey.GetCyclesAsync(profileId);
            var reply = new JourneyStateReply { HasCycle = cycles.Count > 0 };
            if (cycles.Count == 0)
                return reply;

            var shown = cycles.FirstOrDefault(x => x.IsOpen) ?? cycles[0];
            reply.Cycle = MapCycle(shown);
            reply.IsOpen = shown.IsOpen;
            if (shown.IsOpen)
            {
                reply.WeekOfSearch = shown.WeekOfSearch(_time.GetUtcNow().UtcDateTime);
                if (shown.CurrentStep is { } current)
                {
                    reply.CurrentStep = JourneyNames.Of(current.StepKey);
                    reply.CurrentStepSince = Iso(current.BecameCurrentAt);
                }
            }
            reply.History.AddRange(cycles.Select(c => new JourneyCycleSummary
            {
                Number = c.Number,
                Origin = Origin(c.Origin),
                Status = Status(c.Status),
                StartedAt = Iso(c.StartedAt),
                ClosedAt = Iso(c.ClosedAt),
                OutcomeType = c.OutcomeType ?? string.Empty,
                OutcomeDate = Iso(c.OutcomeDate),
                HasAreaChange = c.AreaChange.HasValue,
                AreaChange = c.AreaChange ?? false
            }));
            return reply;
        }

        private static JourneyCycleState MapCycle(JourneyCycle c)
        {
            var state = new JourneyCycleState
            {
                Id = c.Id.ToString(),
                Number = c.Number,
                Origin = Origin(c.Origin),
                Status = Status(c.Status),
                StartedAt = Iso(c.StartedAt),
                PausedAt = Iso(c.PausedAt),
                ResumedAt = Iso(c.ResumedAt),
                Situation = c.Situation ?? string.Empty,
                TimeSinceLayoff = c.TimeSinceLayoff ?? string.Empty,
                OutcomeType = c.OutcomeType ?? string.Empty,
                OutcomeDate = Iso(c.OutcomeDate),
                HasAreaChange = c.AreaChange.HasValue,
                AreaChange = c.AreaChange ?? false,
                ClosedAt = Iso(c.ClosedAt)
            };
            state.Steps.AddRange(c.Steps.OrderBy(s => s.StepKey).Select(s => new JourneyStepState
            {
                StepKey = JourneyNames.Of(s.StepKey),
                BecameCurrentAt = Iso(s.BecameCurrentAt),
                CompletedAt = Iso(s.CompletedAt),
                Inherited = s.Inherited,
                Count = s.Count,
                StartedAt = Iso(s.StartedAt)
            }));
            return state;
        }

        private static string Iso(DateTime? value) => value is { } v ? v.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) : string.Empty;

        private static string Origin(JourneyCycleOrigin origin) => origin switch
        {
            JourneyCycleOrigin.FirstAccess => "first_access",
            JourneyCycleOrigin.ContinueSearching => "continue_searching",
            _ => "reactivation"
        };

        private static string Status(JourneyCycleStatus status) => status switch
        {
            JourneyCycleStatus.Active => "active",
            JourneyCycleStatus.Paused => "paused",
            JourneyCycleStatus.Recolocated => "recolocated",
            _ => "superseded"
        };

        private static Guid ParseGuid(string? text, string field)
            => Guid.TryParse(text, out var id) && id != Guid.Empty
                ? id
                : throw new ArgumentException($"{field} inválido.");

        /// <summary>O padrão do projeto: nunca lança para o cliente; erro vira <c>statuscode</c> de falha com a mensagem.</summary>
        private static async Task<defaultReply> Run(Func<Task<JourneyStateReply>> action)
        {
            var reply = new defaultReply();
            try
            {
                reply.Data = Any.Pack(await action());
                reply.Statuscode = Constants.SuccessStatusCode;
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
