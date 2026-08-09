using System.Diagnostics;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Serilog;

namespace core.Middleware;

public class LoggingInterceptor : Interceptor
{
    private readonly Serilog.ILogger _logger = Log.ForContext<LoggingInterceptor>();

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        var stopwatch = Stopwatch.StartNew();
        var statusCode = StatusCode.OK;

        try
        {
            return await continuation(request, context);
        }
        catch (RpcException ex)
        {
            statusCode = ex.StatusCode;
            throw;
        }
        catch (Exception)
        {
            statusCode = StatusCode.Unknown;
            throw;
        }
        finally
        {
            stopwatch.Stop();
            var logDetails = new
            {
                Method = context.Method,
                StatusCode = statusCode.ToString(),
                stopwatch.ElapsedMilliseconds
            };

            _logger.Information("Request handled: {@LogDetails}", logDetails);
        }
    }
}
