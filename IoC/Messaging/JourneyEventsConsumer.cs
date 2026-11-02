using System.Text;
using core.domain.Interfaces.Services;
using core.domain.Services;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace core.IoC.Messaging
{
    /// <summary>Configuração (seção <c>Journey</c>) do consumidor da fila <c>journey-events</c>.</summary>
    public class JourneyConsumerOptions
    {
        public const string Section = "Journey";

        /// <summary>Desliga o consumidor (por exemplo, num ambiente sem RabbitMQ).</summary>
        public bool ConsumerEnabled { get; set; } = true;

        /// <summary>Atrasos entre tentativas, em segundos. Mudar os valores cria filas de retry novas (o nome leva o atraso).</summary>
        /// <remarks>Sem valor padrão de propósito: o binder do .NET SOMA um array configurado ao inicializador da propriedade.</remarks>
        public int[]? RetryDelaysSeconds { get; set; }

        public ushort Prefetch { get; set; } = 10;
    }

    /// <summary>
    /// Consome os eventos da jornada publicados por quem está fora do <c>core</c> (docs/specs/estado-historico-jornada-talent.md,
    /// seção 5.3). Diferente do <c>QueueConsumerBackgroundService</c> do <c>core_subscribers</c>, que dá ack sempre e perde
    /// eventos: aqui a mensagem só é confirmada depois de aplicada, ou depois de entregue com segurança à fila de retry ou à de mortos.
    /// </summary>
    /// <remarks>
    /// Topologia (declarada na subida, idempotente): <c>journey-events</c> (durável), uma fila <c>journey-events.retry.{N}s</c> por
    /// atraso (TTL de N segundos; ao expirar a mensagem volta para <c>journey-events</c> pelo exchange padrão) e
    /// <c>journey-events.dead</c>. O produtor publica no exchange padrão com a chave <c>journey-events</c>, mensagem persistente.
    /// </remarks>
    public class JourneyEventsConsumer : BackgroundService
    {
        public const string Queue = "journey-events";
        public const string DeadQueue = "journey-events.dead";
        public const string AttemptHeader = "x-attempt";
        public const string ErrorHeader = "x-error";

        public static string RetryQueue(int delaySeconds) => $"{Queue}.retry.{delaySeconds}s";

        private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(10);

        private readonly IServiceScopeFactory _scopes;
        private readonly IConfiguration _configuration;
        private readonly JourneyConsumerOptions _options;
        private readonly JourneyRetryPolicy _policy;
        private readonly ILogger<JourneyEventsConsumer> _logger;

        public JourneyEventsConsumer(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<JourneyEventsConsumer> logger)
        {
            _scopes = scopes;
            _configuration = configuration;
            _logger = logger;
            _options = configuration.GetSection(JourneyConsumerOptions.Section).Get<JourneyConsumerOptions>() ?? new JourneyConsumerOptions();
            _policy = new JourneyRetryPolicy(_options.RetryDelaysSeconds is { Length: > 0 } configured ? configured : null);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_options.ConsumerEnabled)
            {
                _logger.LogWarning("Consumidor da fila {Queue} desligado (Journey:ConsumerEnabled=false).", Queue);
                return;
            }

            // Uma exceção solta num BackgroundService derruba o host inteiro; o RabbitMQ fora do ar não pode derrubar o core.
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunConnectionAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Consumidor da fila {Queue} perdeu a conexão com o RabbitMQ; nova tentativa em {Seconds}s.", Queue, ReconnectDelay.TotalSeconds);
                }

                try { await Task.Delay(ReconnectDelay, stoppingToken); }
                catch (OperationCanceledException) { return; }
            }
        }

        private async Task RunConnectionAsync(CancellationToken stop)
        {
            var factory = new ConnectionFactory
            {
                HostName = _configuration["RabbitMQ:Host"],
                UserName = _configuration["RabbitMQ:UserName"],
                Password = _configuration["RabbitMQ:Password"],
                DispatchConsumersAsync = true,
                // A reconexão é do laço acima, que também refaz a topologia e o consumo.
                AutomaticRecoveryEnabled = false
            };

            using var connection = factory.CreateConnection("core-journey-events");
            using var channel = connection.CreateModel();

            var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.ConnectionShutdown += (_, args) => lost.TrySetResult();

            DeclareTopology(channel);
            channel.ConfirmSelect();
            channel.BasicQos(0, _options.Prefetch, global: false);

            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.Received += async (_, delivery) =>
            {
                try
                {
                    await HandleAsync(channel, delivery, stop);
                }
                catch (Exception ex)
                {
                    // Não deu nem para entregar à fila de retry ou de mortos: devolve à fila principal para o broker reentregar.
                    _logger.LogError(ex, "Falha ao tratar a mensagem {DeliveryTag} de {Queue}; devolvida à fila.", delivery.DeliveryTag, Queue);
                    try { channel.BasicNack(delivery.DeliveryTag, multiple: false, requeue: true); } catch { /* canal já caiu: o broker reentrega */ }
                }
            };
            channel.BasicConsume(Queue, autoAck: false, consumerTag: "core-journey-events", consumer);
            _logger.LogInformation("Consumindo a fila {Queue} (atrasos de retry: {Delays}s).", Queue, string.Join(",", _policy.DelaysSeconds));

            using var stopped = stop.Register(() => lost.TrySetResult());
            await lost.Task;
            stop.ThrowIfCancellationRequested();
            throw new IOException("Conexão com o RabbitMQ encerrada.");
        }

        private void DeclareTopology(IModel channel)
        {
            channel.QueueDeclare(Queue, durable: true, exclusive: false, autoDelete: false);
            channel.QueueDeclare(DeadQueue, durable: true, exclusive: false, autoDelete: false);
            foreach (var seconds in _policy.DelaysSeconds.Distinct())
            {
                channel.QueueDeclare(RetryQueue(seconds), durable: true, exclusive: false, autoDelete: false, arguments: new Dictionary<string, object>
                {
                    ["x-message-ttl"] = seconds * 1000,
                    // Ao expirar, volta para a fila principal pelo exchange padrão.
                    ["x-dead-letter-exchange"] = string.Empty,
                    ["x-dead-letter-routing-key"] = Queue
                });
            }
        }

        private async Task HandleAsync(IModel channel, BasicDeliverEventArgs delivery, CancellationToken stop)
        {
            var attempt = ReadAttempt(delivery.BasicProperties);

            JourneyProcessResult result;
            await using (var scope = _scopes.CreateAsyncScope())
            {
                var processor = new JourneyEventProcessor(scope.ServiceProvider.GetRequiredService<IJourneyService>());
                result = await processor.ProcessAsync(delivery.Body, stop);
            }

            switch (result.Outcome)
            {
                case JourneyProcessing.Done:
                    channel.BasicAck(delivery.DeliveryTag, multiple: false);
                    return;

                case JourneyProcessing.Retry when _policy.DelayAfterFailure(attempt) is { } delay:
                    _logger.LogWarning("Evento da jornada adiado em {Delay}s (tentativa {Attempt} de {Max}): {Reason}", delay, attempt, _policy.MaxAttempts, result.Reason);
                    Republish(channel, RetryQueue(delay), delivery, attempt + 1, result.Reason);
                    channel.BasicAck(delivery.DeliveryTag, multiple: false);
                    return;

                default:
                    // Mensagem inválida, ou as tentativas acabaram. O texto fixo é o que o alerta procura.
                    _logger.LogError("journey-events: mensagem enviada para {DeadQueue} depois de {Attempt} tentativa(s): {Reason}. Corpo: {Body}",
                        DeadQueue, attempt, result.Reason, Encoding.UTF8.GetString(delivery.Body.Span));
                    Republish(channel, DeadQueue, delivery, attempt, result.Reason);
                    channel.BasicAck(delivery.DeliveryTag, multiple: false);
                    return;
            }
        }

        /// <summary>Publica a cópia e espera a confirmação do broker ANTES de o chamador confirmar a original: não há janela em que a mensagem não exista.</summary>
        private static void Republish(IModel channel, string queue, BasicDeliverEventArgs original, int attempt, string? reason)
        {
            var properties = channel.CreateBasicProperties();
            properties.Persistent = true;
            properties.ContentType = original.BasicProperties?.ContentType ?? "application/json";
            properties.Headers = new Dictionary<string, object>
            {
                [AttemptHeader] = attempt,
                [ErrorHeader] = Truncate(reason ?? string.Empty, 500)
            };

            channel.BasicPublish(exchange: string.Empty, routingKey: queue, mandatory: false, basicProperties: properties, body: original.Body);
            channel.WaitForConfirmsOrDie(TimeSpan.FromSeconds(10));
        }

        /// <summary>A primeira entrega é a tentativa 1; cada passagem pela fila de retry soma 1.</summary>
        private static int ReadAttempt(IBasicProperties? properties)
        {
            if (properties?.Headers is { } headers && headers.TryGetValue(AttemptHeader, out var value))
            {
                var attempt = value switch
                {
                    int i => i,
                    long l => (int)l,
                    byte[] bytes when int.TryParse(Encoding.UTF8.GetString(bytes), out var parsed) => parsed,
                    _ => 1
                };
                return Math.Max(1, attempt);
            }
            return 1;
        }

        private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];
    }
}
