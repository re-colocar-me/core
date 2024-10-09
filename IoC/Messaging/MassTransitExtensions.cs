using core.domain.Interfaces.EventPublishers;
using core.infrastructure.Publishers;
using MassTransit;

namespace core.IoC.Messaging
{
    public static class MassTransitIoCExtensions
    {
        public static void RegisterMasstransit(this IServiceCollection services, IConfiguration configuration)
        {
            RabbitMQConfiguration rabbitMQOptions = new RabbitMQConfiguration();

            rabbitMQOptions.Host = configuration["RabbitMQ__Host"];
            rabbitMQOptions.UserName = configuration["RabbitMQ__UserName"];
            rabbitMQOptions.Password = configuration["RabbitMQ__Password"];

            configuration.GetSection(RabbitMQConfiguration.RabbitMQOptions).Bind(rabbitMQOptions);

            services.AddMassTransit(config =>
            {

                config.UsingRabbitMq((ctx, config) =>
                {
                    config.Host(rabbitMQOptions?.Host, 5672, "/", h =>
                    {
                        h.Username(rabbitMQOptions?.UserName!);
                        h.Password(rabbitMQOptions?.Password!);
                    });
                    config.ConfigureEndpoints(ctx);
                    config.UseMessageRetry(r => r.Interval(100, TimeSpan.FromSeconds(25)));

                });
            });

            services.AddScoped<IConsultantEventPublisher, ConsultantEventPublisher>();
        }
    }
}
