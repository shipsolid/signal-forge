using Grpc.Core;

namespace OrderApi.Tests;

/// <summary>Captures streamed messages in-memory for assertion in unit tests.</summary>
internal sealed class FakeServerStreamWriter<T> : IServerStreamWriter<T>
{
    public List<T> Written { get; } = [];

    public WriteOptions? WriteOptions { get; set; }

    public Task WriteAsync(T message)
    {
        Written.Add(message);
        return Task.CompletedTask;
    }

    public Task WriteAsync(T message, CancellationToken cancellationToken)
    {
        Written.Add(message);
        return Task.CompletedTask;
    }
}
