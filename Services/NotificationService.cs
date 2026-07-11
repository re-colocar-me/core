using core.domain;
using core.domain.Enums;
using core.domain.Interfaces.Services;
using Grpc.Core;
using Any = Google.Protobuf.WellKnownTypes.Any;

namespace core.Services
{
    public class NotificationService : Notification.NotificationBase
    {
        private INotificationService _service;

        public NotificationService(INotificationService service)
        {
            _service = service;
        }

        public override async Task<defaultReply> GetCountByOwner(GetCountByOwnerRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();

            try
            {
                var count = await _service.GetCountBy(Guid.Parse(request.Ownerid));
                var data = new GetCountByOwnerReply { Count = count };

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

        public override async Task<defaultReply> GetNotificationListByOwner(GetNotificationListByOwnerRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();

            try
            {
                var list = await _service.GetListBy(Guid.Parse(request.Ownerid));

                var data = new GetNotificationListByOwnerReply();
                data.Notifications.AddRange(list.Select(x => new notificationentity()
                {
                    Id = x.Id.ToString(),
                    Body = x.Body,
                    Title = x.Title,
                    Status = System.Enum.GetName(typeof(NotificationStatus), x.Status),
                    Viewedat = x.ViewedAt?.ToString("dd/MM/yyyy hh:mm:ss") ?? string.Empty,
                    Createdat = x.CreatedAt.ToString("dd/MM/yyyy HH:mm:ss")
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

        public override async Task<defaultReply> SetNotificationRead(SetNotificationReadRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                await _service.SetAsRead(Guid.Parse(request.Ownerid), Guid.Parse(request.Messageid));
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
        public override async Task<defaultReply> CreateNotification(NotificationRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                await _service.CreateNotification(request.Email, request.Role, request.Title, request.Body);
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

        public override async Task<defaultReply> RemoveNotification(RemoveNotificationRequst request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                await _service.DeleteNotification(Guid.Parse(request.NotificationId));
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
