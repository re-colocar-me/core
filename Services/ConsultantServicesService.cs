using core.domain;
using core.domain.Interfaces.Services;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace core.Services
{
    public class ConsultantServicesService : ConsultantServices.ConsultantServicesBase
    {
        private IConsultantServiceServices _services;

        public ConsultantServicesService(IConsultantServiceServices services)
        {
            _services = services;
        }

        public override async Task<defaultReply> ListAllServices(Empty request, ServerCallContext context)
        {
            var reply = new defaultReply();

            try
            {
                var response = await _services.ListAllServices();
                var data = new ListAllServicesReply();
                data.Services.AddRange(response.Select(x => new service()
                {
                    Id = x.Id.ToString(),
                    Categoryname = x.Category.Name,
                    Name = x.Name,
                    Description = x.Description
                }));


                reply.Statuscode = Constants.SuccessStatusCode;
                reply.Data = Any.Pack(reply);              

            }
            catch (Exception ex)
            {
                reply.Statuscode =Constants.FailStatusCode;
                reply.Error = new error()
                {
                    Message = ex.Message
                };

            }
            return reply;
        }

        public override async Task<defaultReply> ListCategories(Empty request, ServerCallContext context)
        {
            var reply = new defaultReply();

            try
            {
                var response = await _services.ListAllCategories();
                var data = new ListCategoriesReply();

                foreach (var item in response)
                {
                    var newCategory = new category()
                    {
                        Id = item.Id.ToString(),
                        Name = item.Name,
                        Description = item.Description
                    };

                    newCategory.Services.AddRange(item.Services.Select(x => new service()
                    {
                        Id = x.Id.ToString(),
                        Categoryname = item.Name,
                        Name = x.Name,
                        Description = x.Description
                    }));

                    data.Categories.Add(newCategory);
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

        public override async Task<defaultReply> ListServicesByCategory(ListServicesByCategoryRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();

            try
            {
                var response = await _services.ListServicesByCategory(Guid.Parse(request.CategoryId));
                var data = new ListServicesByCategoryReply();
                data.Services.AddRange(response.Select(x => new service()
                {
                    Id = x.Id.ToString(),
                    Name = x.Name,
                    Description = x.Description
                }));
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
