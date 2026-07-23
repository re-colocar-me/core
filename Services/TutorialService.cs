using core.domain;
using core.domain.Entities;
using core.domain.Enums;
using core.domain.Interfaces.Services;
using Grpc.Core;
using Any = Google.Protobuf.WellKnownTypes.Any;

namespace core.Services
{
    public class TutorialService : Tutorial.TutorialBase
    {
        private readonly ITutorialService _service;

        public TutorialService(ITutorialService service)
        {
            _service = service;
        }

        public override async Task<defaultReply> GetPendingTutorial(GetPendingTutorialRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                var audience = Enum.Parse<TutorialAudience>(request.Audience, ignoreCase: true);
                var pending = await _service.GetPendingFlowAsync(Guid.Parse(request.OwnerId), audience);

                var data = new GetPendingTutorialReply { HasPending = pending != null };
                if (pending != null)
                {
                    data.Flow = MapFlow(pending.Flow);
                    if (pending.ResumeStepId != null)
                        data.ResumeStepId = pending.ResumeStepId;
                }

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

        public override async Task<defaultReply> ListTutorials(ListTutorialsRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                var audience = Enum.Parse<TutorialAudience>(request.Audience, ignoreCase: true);
                var flows = await _service.ListFlowsAsync(audience);

                var data = new ListTutorialsReply();
                data.Flows.AddRange(flows.Select(MapFlow));

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

        public override async Task<defaultReply> GetTutorialFlow(GetTutorialFlowRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                var flow = await _service.GetFlowAsync(request.FlowId);
                if (flow is null)
                    throw new ArgumentException($"Fluxo '{request.FlowId}' não encontrado ou inativo.");

                var data = new GetTutorialFlowReply { Flow = MapFlow(flow) };

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

        public override async Task<defaultReply> RecordTutorialProgress(RecordTutorialProgressRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                var eventType = Enum.Parse<TutorialEventType>(request.Event, ignoreCase: true);
                await _service.RecordProgressAsync(
                    Guid.Parse(request.OwnerId),
                    request.FlowId,
                    request.FlowVersion,
                    request.HasStepId ? request.StepId : null,
                    eventType,
                    request.HasMetadata ? request.Metadata : null);

                reply.Statuscode = Constants.SuccessStatusCode;
            }
            catch (Exception ex)
            {
                reply.Statuscode = Constants.FailStatusCode;
                reply.Error = new error { Message = ex.Message };
            }
            return reply;
        }

        private static tutorialFlow MapFlow(TutorialFlow flow)
        {
            var mapped = new tutorialFlow
            {
                FlowId = flow.FlowId,
                Label = flow.Label,
                Version = flow.Version,
                Trigger = flow.Trigger.ToString()
            };
            mapped.Steps.AddRange(flow.Steps.Select(MapStep));
            return mapped;
        }

        private static tutorialStep MapStep(TutorialStepConfig step)
        {
            var mapped = new tutorialStep
            {
                StepId = step.StepId,
                Order = step.Order,
                Type = step.Type,
                Title = step.Title,
                Body = step.Body,
                Placement = step.Placement,
                Route = step.Route
            };
            if (step.MediaUrl != null) mapped.MediaUrl = step.MediaUrl;
            if (step.Target != null) mapped.Target = step.Target;
            if (step.CtaLabel != null) mapped.CtaLabel = step.CtaLabel;
            if (step.CtaAction != null) mapped.CtaAction = step.CtaAction;
            if (step.CtaValue != null) mapped.CtaValue = step.CtaValue;
            mapped.DisplayConditions.AddRange(step.DisplayConditions.Select(c => new tutorialDisplayCondition { Type = c.Type, Value = c.Value }));
            return mapped;
        }
    }
}
