using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using DotNet.Testcontainers.Networks;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Xunit;

namespace IntegrationTests;

// Stands up the full 5-hop path for real: Postgres + RabbitMQ + Redis + Jaeger,
// plus order-api and notification-svc built from their actual Dockerfiles (not
// project references) — the same two images deploy-local.sh builds for k3d,
// built here instead for a real broker + a real second-language consumer to
// exercise, without needing a cluster. See
// https://shipsolid.github.io/notes/documentation/app-signal-forge/testing/ for how to invoke
// this project; it is NOT part of the default `dotnet test` run (no Docker
// dependency assumed there).
public sealed class CrossLanguageTraceFixture : IAsyncLifetime
{
    private const string PostgresPassword = "inttest";
    private const string RabbitMqUser = "signalforge";
    private const string RabbitMqPassword = "inttest";

    private INetwork _network = null!;
    private PostgreSqlContainer _postgres = null!;
    private RabbitMqContainer _rabbitMq = null!;
    private IContainer _redis = null!;
    private IContainer _jaeger = null!;
    private IFutureDockerImage _orderApiImage = null!;
    private IFutureDockerImage _notificationSvcImage = null!;
    private IContainer _orderApi = null!;
    private IContainer _notificationSvc = null!;

    public string OrderApiGrpcAddress => $"http://localhost:{_orderApi.GetMappedPublicPort(5002)}";
    public string NotificationSvcAddress => $"http://localhost:{_notificationSvc.GetMappedPublicPort(8000)}";
    public string JaegerQueryAddress => $"http://localhost:{_jaeger.GetMappedPublicPort(16686)}";

    public async Task<string> GetOrderApiLogsAsync()
    {
        var (stdout, stderr) = await _orderApi.GetLogsAsync();
        return stdout + stderr;
    }

    public async Task<string> GetNotificationSvcLogsAsync()
    {
        var (stdout, stderr) = await _notificationSvc.GetLogsAsync();
        return stdout + stderr;
    }

    public async Task<string> GetRabbitMqLogsAsync()
    {
        var (stdout, stderr) = await _rabbitMq.GetLogsAsync();
        return stdout + stderr;
    }

    public async Task InitializeAsync()
    {
        var repoRoot = FindRepoRoot();
        var orderApiCtx = Path.Combine(repoRoot, "src", "order-api");
        var notificationSvcCtx = Path.Combine(repoRoot, "src", "notification-svc");
        var protoDir = Path.Combine(repoRoot, "src", "proto");
        var caPath = Path.Combine(repoRoot, "zcert.crt");

        _network = new NetworkBuilder().Build();
        await _network.CreateAsync();

        _postgres = new PostgreSqlBuilder("postgres:16.4")
            .WithNetwork(_network)
            .WithNetworkAliases("postgres")
            .WithDatabase("orderdb")
            .WithUsername("orderuser")
            .WithPassword(PostgresPassword)
            .Build();

        _rabbitMq = new RabbitMqBuilder("rabbitmq:3.13.7-management")
            .WithNetwork(_network)
            .WithNetworkAliases("rabbitmq")
            .WithUsername(RabbitMqUser)
            .WithPassword(RabbitMqPassword)
            .Build();

        _redis = new ContainerBuilder("redis:7.4-alpine")
            .WithNetwork(_network)
            .WithNetworkAliases("redis")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Ready to accept connections"))
            .Build();

        _jaeger = new ContainerBuilder("jaegertracing/all-in-one:1.55")
            .WithNetwork(_network)
            .WithNetworkAliases("jaeger")
            .WithEnvironment("COLLECTOR_OTLP_ENABLED", "true")
            .WithPortBinding(4317, true)
            .WithPortBinding(16686, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(16686).ForPath("/")))
            .Build();

        await Task.WhenAll(
            _postgres.StartAsync(),
            _rabbitMq.StartAsync(),
            _redis.StartAsync(),
            _jaeger.StartAsync());

        // Stage the same build-context workarounds deploy-local.sh's
        // build_images() applies for a real cluster build — the Dockerfiles
        // COPY zcert.crt (Zscaler CA, empty placeholder off corporate
        // networks) and, for order-api, COPY proto/ (src/proto/ lives
        // outside this Docker build context; see src/order-api/Dockerfile's
        // own comment on why).
        StageFile(caPath, Path.Combine(orderApiCtx, "zcert.crt"));
        StageFile(caPath, Path.Combine(notificationSvcCtx, "zcert.crt"));
        var stagedProtoDir = Path.Combine(orderApiCtx, "proto");
        Directory.CreateDirectory(stagedProtoDir);
        File.Copy(Path.Combine(protoDir, "orders.proto"), Path.Combine(stagedProtoDir, "orders.proto"), overwrite: true);
        File.Copy(Path.Combine(protoDir, "OrderValidation.cs"), Path.Combine(stagedProtoDir, "OrderValidation.cs"), overwrite: true);

        try
        {
            _orderApiImage = new ImageFromDockerfileBuilder()
                .WithDockerfileDirectory(orderApiCtx)
                .WithDockerfile("Dockerfile")
                .WithName("order-api:inttest")
                .WithDeleteIfExists(false)
                .Build();
            await _orderApiImage.CreateAsync();

            _notificationSvcImage = new ImageFromDockerfileBuilder()
                .WithDockerfileDirectory(notificationSvcCtx)
                .WithDockerfile("Dockerfile")
                .WithName("notification-svc:inttest")
                .WithDeleteIfExists(false)
                .Build();
            await _notificationSvcImage.CreateAsync();
        }
        finally
        {
            File.Delete(Path.Combine(orderApiCtx, "zcert.crt"));
            File.Delete(Path.Combine(notificationSvcCtx, "zcert.crt"));
            Directory.Delete(stagedProtoDir, recursive: true);
        }

        _orderApi = new ContainerBuilder(_orderApiImage)
            .WithNetwork(_network)
            .WithNetworkAliases("order-api")
            .WithPortBinding(5001, true)
            .WithPortBinding(5002, true)
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Production")
            // No ASPNETCORE_URLS — Program.cs's ConfigureKestrel binds 5001
            // (HTTP/1.1, /healthz) and 5002 (HTTP/2-only, gRPC) explicitly;
            // see that file's "gRPC server" comment for why they're split.
            // Wildcarded AllowedHosts deliberately: this is a disposable
            // per-test-run container reached only via Testcontainers' own
            // mapped port from the test process, not the hardened k8s
            // deployment (see k8s/app/order/deployment.yaml for the real,
            // narrow allow-list).
            .WithEnvironment("AllowedHosts", "*")
            .WithEnvironment(
                "ConnectionStrings__DefaultConnection",
                $"Host=postgres;Port=5432;Database=orderdb;Username=orderuser;Password={PostgresPassword}")
            .WithEnvironment("RabbitMQ__Host", "rabbitmq")
            .WithEnvironment("RabbitMQ__Port", "5672")
            .WithEnvironment("RabbitMQ__User", RabbitMqUser)
            .WithEnvironment("RabbitMQ__Password", RabbitMqPassword)
            .WithEnvironment("OTEL_SERVICE_NAME", "order-api")
            .WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", "http://jaeger:4317")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(5001).ForPath("/healthz")))
            .Build();
        await _orderApi.StartAsync();

        _notificationSvc = new ContainerBuilder(_notificationSvcImage)
            .WithNetwork(_network)
            .WithNetworkAliases("notification-svc")
            .WithPortBinding(8000, true)
            .WithEnvironment("RABBITMQ_HOST", "rabbitmq")
            .WithEnvironment("RABBITMQ_PORT", "5672")
            .WithEnvironment("RABBITMQ_USER", RabbitMqUser)
            .WithEnvironment("RABBITMQ_PASSWORD", RabbitMqPassword)
            .WithEnvironment("REDIS_HOST", "redis")
            .WithEnvironment("REDIS_PORT", "6379")
            .WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", "http://jaeger:4317")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8000).ForPath("/healthz")))
            .Build();
        await _notificationSvc.StartAsync();
    }

    public async Task DisposeAsync()
    {
        async Task SafeDispose(Func<Task> action)
        {
            try { await action(); } catch { /* best-effort cleanup */ }
        }

        await SafeDispose(() => _notificationSvc?.DisposeAsync().AsTask() ?? Task.CompletedTask);
        await SafeDispose(() => _orderApi?.DisposeAsync().AsTask() ?? Task.CompletedTask);
        await SafeDispose(() => _jaeger?.DisposeAsync().AsTask() ?? Task.CompletedTask);
        await SafeDispose(() => _redis?.DisposeAsync().AsTask() ?? Task.CompletedTask);
        await SafeDispose(() => _rabbitMq?.DisposeAsync().AsTask() ?? Task.CompletedTask);
        await SafeDispose(() => _postgres?.DisposeAsync().AsTask() ?? Task.CompletedTask);
        await SafeDispose(() => _network?.DeleteAsync() ?? Task.CompletedTask);
    }

    private static void StageFile(string source, string dest)
    {
        if (File.Exists(source))
            File.Copy(source, dest, overwrite: true);
        else
            File.WriteAllBytes(dest, Array.Empty<byte>());
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        var current = new DirectoryInfo(dir);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "deploy-local.sh")))
                return current.FullName;
            current = current.Parent;
        }
        throw new InvalidOperationException(
            $"Could not locate repo root (deploy-local.sh) walking up from {dir}");
    }
}

[CollectionDefinition("CrossLanguageTrace")]
public class CrossLanguageTraceCollection : ICollectionFixture<CrossLanguageTraceFixture>
{
}
