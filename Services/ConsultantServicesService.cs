using core.domain;
using core.domain.Entities;
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

        private static service MapService(ConsultantServiceItem x, string? categoryName = null) => new()
        {
            Id = x.Id.ToString(),
            Categoryname = categoryName ?? x.Category?.Name ?? string.Empty,
            Name = x.Name,
            Description = x.Description,
            IsActive = x.IsActive,
            SkillNames = { x.Skills.Select(s => s.Name) }
        };

        private static category MapCategory(ServiceCategory x) => new()
        {
            Id = x.Id.ToString(),
            Name = x.Name,
            Description = x.Description,
            IsActive = x.IsActive,
            SkillNames = { x.Skills.Select(s => s.Name) },
            Services = { x.Services.Select(s => MapService(s, x.Name)) }
        };

        public override async Task<defaultReply> ListAllServices(Empty request, ServerCallContext context)
        {
            var reply = new defaultReply();

            try
            {
                var response = await _services.ListAllServices();
                var data = new ListAllServicesReply();
                data.Services.AddRange(response.Select(x => MapService(x)));

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

        public override async Task<defaultReply> ListCategories(Empty request, ServerCallContext context)
        {
            var reply = new defaultReply();

            try
            {
                var response = await _services.ListAllCategories();
                var data = new ListCategoriesReply();
                data.Categories.AddRange(response.Select(MapCategory));

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
                data.Services.AddRange(response.Select(x => MapService(x)));

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

        public override async Task<defaultReply> CreateCategory(CreateCategoryRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                var category = await _services.CreateCategory(request.Name, request.Description);
                reply.Data = Any.Pack(MapCategory(category));
                reply.Statuscode = Constants.SuccessStatusCode;
            }
            catch (Exception ex)
            {
                reply.Statuscode = Constants.FailStatusCode;
                reply.Error = new error { Message = ex.Message };
            }
            return reply;
        }

        public override async Task<defaultReply> UpdateCategory(UpdateCategoryRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                var category = await _services.UpdateCategory(Guid.Parse(request.Id), request.Name, request.Description);
                reply.Data = Any.Pack(MapCategory(category));
                reply.Statuscode = Constants.SuccessStatusCode;
            }
            catch (Exception ex)
            {
                reply.Statuscode = Constants.FailStatusCode;
                reply.Error = new error { Message = ex.Message };
            }
            return reply;
        }

        public override async Task<defaultReply> SetCategoryActive(SetCategoryActiveRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                await _services.SetCategoryActive(Guid.Parse(request.Id), request.IsActive);
                reply.Statuscode = Constants.SuccessStatusCode;
            }
            catch (Exception ex)
            {
                reply.Statuscode = Constants.FailStatusCode;
                reply.Error = new error { Message = ex.Message };
            }
            return reply;
        }

        public override async Task<defaultReply> CreateService(CreateServiceRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                var item = await _services.CreateService(Guid.Parse(request.CategoryId), request.Name, request.Description);
                reply.Data = Any.Pack(MapService(item));
                reply.Statuscode = Constants.SuccessStatusCode;
            }
            catch (Exception ex)
            {
                reply.Statuscode = Constants.FailStatusCode;
                reply.Error = new error { Message = ex.Message };
            }
            return reply;
        }

        public override async Task<defaultReply> UpdateService(UpdateServiceRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                var item = await _services.UpdateService(Guid.Parse(request.Id), request.Name, request.Description);
                reply.Data = Any.Pack(MapService(item));
                reply.Statuscode = Constants.SuccessStatusCode;
            }
            catch (Exception ex)
            {
                reply.Statuscode = Constants.FailStatusCode;
                reply.Error = new error { Message = ex.Message };
            }
            return reply;
        }

        public override async Task<defaultReply> SetServiceActive(SetServiceActiveRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                await _services.SetServiceActive(Guid.Parse(request.Id), request.IsActive);
                reply.Statuscode = Constants.SuccessStatusCode;
            }
            catch (Exception ex)
            {
                reply.Statuscode = Constants.FailStatusCode;
                reply.Error = new error { Message = ex.Message };
            }
            return reply;
        }

        public override async Task<defaultReply> SetCategorySkills(SetCategorySkillsRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                await _services.SetCategorySkills(Guid.Parse(request.CategoryId), request.SkillNames);
                reply.Statuscode = Constants.SuccessStatusCode;
            }
            catch (Exception ex)
            {
                reply.Statuscode = Constants.FailStatusCode;
                reply.Error = new error { Message = ex.Message };
            }
            return reply;
        }

        public override async Task<defaultReply> SetServiceSkills(SetServiceSkillsRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                await _services.SetServiceSkills(Guid.Parse(request.ServiceItemId), request.SkillNames);
                reply.Statuscode = Constants.SuccessStatusCode;
            }
            catch (Exception ex)
            {
                reply.Statuscode = Constants.FailStatusCode;
                reply.Error = new error { Message = ex.Message };
            }
            return reply;
        }

        public override async Task<defaultReply> SearchSkills(SearchSkillsRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                var skills = await _services.SearchSkills(request.Query);
                var data = new SearchSkillsReply();
                data.SkillNames.AddRange(skills.Select(x => x.Name));
                reply.Data = Any.Pack(data);
                reply.Statuscode = Constants.SuccessStatusCode;
            }
            catch (Exception ex)
            {
                reply.Statuscode = Constants.FailStatusCode;
                reply.Error = new error { Message = ex.Message };
            }
            return reply;
        }

        public override async Task<defaultReply> GetEffectiveServiceSkills(GetEffectiveServiceSkillsRequest request, ServerCallContext context)
        {
            var reply = new defaultReply();
            try
            {
                var skills = await _services.GetEffectiveServiceSkills(Guid.Parse(request.ServiceItemId));
                var data = new GetEffectiveServiceSkillsReply();
                data.SkillNames.AddRange(skills.Select(x => x.Name));
                reply.Data = Any.Pack(data);
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
