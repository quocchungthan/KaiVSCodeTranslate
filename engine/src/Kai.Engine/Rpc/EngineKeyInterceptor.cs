using System.Security.Cryptography;
using System.Text;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Kai.Engine.Configuration;
using Microsoft.Extensions.Options;

namespace Kai.Engine.Rpc;

/// <summary>Rejects every call that does not carry the shared secret in <c>x-engine-key</c> (constant-time compare).</summary>
public sealed class EngineKeyInterceptor : Interceptor
{
    public const string HeaderName = "x-engine-key";

    private readonly byte[] _expectedHash;
    private readonly ILogger<EngineKeyInterceptor> _logger;

    public EngineKeyInterceptor(IOptions<EngineOptions> options, ILogger<EngineKeyInterceptor> logger)
    {
        _expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(options.Value.SecretKey));
        _logger = logger;
    }

    public override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
    {
        Authorize(context);
        return continuation(request, context);
    }

    public override Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(IAsyncStreamReader<TRequest> requestStream, ServerCallContext context, ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Authorize(context);
        return continuation(requestStream, context);
    }

    public override Task ServerStreamingServerHandler<TRequest, TResponse>(TRequest request, IServerStreamWriter<TResponse> responseStream, ServerCallContext context, ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Authorize(context);
        return continuation(request, responseStream, context);
    }

    public override Task DuplexStreamingServerHandler<TRequest, TResponse>(IAsyncStreamReader<TRequest> requestStream, IServerStreamWriter<TResponse> responseStream, ServerCallContext context, DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Authorize(context);
        return continuation(requestStream, responseStream, context);
    }

    private void Authorize(ServerCallContext context)
    {
        var supplied = context.RequestHeaders.GetValue(HeaderName);
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied ?? string.Empty));
        if (supplied is null || !CryptographicOperations.FixedTimeEquals(suppliedHash, _expectedHash))
        {
            _logger.LogWarning("Rejected unauthenticated call to {Method} from {Peer}", context.Method, context.Peer);
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Missing or invalid engine key."));
        }
    }
}
