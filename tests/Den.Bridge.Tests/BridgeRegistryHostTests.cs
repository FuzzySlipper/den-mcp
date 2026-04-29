using Den.Bridge.Abstractions;
using Den.Bridge.Hosting;
using Den.Bridge.Protocol;
using Den.Bridge.Registry;
using Microsoft.Extensions.DependencyInjection;

namespace Den.Bridge.Tests;

public class BridgeRegistryHostTests
{
    [Fact]
    public void Registries_RejectDuplicateCommandsAndEvents()
    {
        var builder = new BridgeRegistryBuilder()
            .RegisterCommand<EchoRequest, EchoResponse, EchoHandler>("sample.echo")
            .RegisterCommand<EchoRequest, EchoResponse, EchoHandler>("sample.echo")
            .RegisterEvent<EchoEventPayload>("sample.echoed")
            .RegisterEvent<EchoEventPayload>("sample.echoed");

        Assert.Throws<InvalidOperationException>(() => builder.BuildCommandRegistry());
        Assert.Throws<InvalidOperationException>(() => builder.BuildEventRegistry());
    }

    [Fact]
    public void Builder_BuildMethodsReturnRepeatableSnapshots()
    {
        var builder = new BridgeRegistryBuilder()
            .RegisterCommand<EchoRequest, EchoResponse, EchoHandler>("sample.echo")
            .RegisterEvent<EchoEventPayload>("sample.echoed");

        var firstCommandRegistry = builder.BuildCommandRegistry();
        var secondCommandRegistry = builder.BuildCommandRegistry();
        var firstEventRegistry = builder.BuildEventRegistry();
        var secondEventRegistry = builder.BuildEventRegistry();

        Assert.NotSame(firstCommandRegistry, secondCommandRegistry);
        Assert.NotSame(firstEventRegistry, secondEventRegistry);
        Assert.Equal(new[] { "sample.echo" }, firstCommandRegistry.Commands.Select(descriptor => descriptor.Command));
        Assert.Equal(new[] { "sample.echo" }, secondCommandRegistry.Commands.Select(descriptor => descriptor.Command));
        Assert.Equal(new[] { "sample.echoed" }, firstEventRegistry.Events.Select(descriptor => descriptor.Event));
        Assert.Equal(new[] { "sample.echoed" }, secondEventRegistry.Events.Select(descriptor => descriptor.Event));

        builder
            .RegisterCommand<EchoRequest, EchoResponse, EchoHandler>("sample.echo_again")
            .RegisterEvent<EchoEventPayload>("sample.echoed_again");

        Assert.Equal(new[] { "sample.echo" }, firstCommandRegistry.Commands.Select(descriptor => descriptor.Command));
        Assert.Equal(new[] { "sample.echoed" }, firstEventRegistry.Events.Select(descriptor => descriptor.Event));
        Assert.Equal(
            new[] { "sample.echo", "sample.echo_again" },
            builder.BuildCommandRegistry().Commands.Select(descriptor => descriptor.Command));
        Assert.Equal(
            new[] { "sample.echoed", "sample.echoed_again" },
            builder.BuildEventRegistry().Events.Select(descriptor => descriptor.Event));
    }

    [Fact]
    public void CapabilitiesProvider_ExposesDefaultSchemaNames()
    {
        using var provider = BuildProvider(builder => builder
            .RegisterCommand<EchoRequest, EchoResponse, EchoHandler>("sample.echo")
            .RegisterEvent<EchoEventPayload>("sample.echoed"));

        var capabilities = provider.GetRequiredService<IBridgeCapabilitiesProvider>().CreateCapabilitiesFrame();
        var command = Assert.Single(capabilities.Commands);
        var evt = Assert.Single(capabilities.Events);

        Assert.Equal("sample.echo.request", command.RequestSchema);
        Assert.Equal("sample.echo.response", command.ResponseSchema);
        Assert.Equal("sample.echoed.payload", evt.PayloadSchema);
    }

    [Fact]
    public async Task Invoker_MapsUnknownCommandToUnsupportedCapabilityError()
    {
        using var provider = BuildProvider(builder => { });
        var router = provider.GetRequiredService<IBridgeCommandRouter>();

        var response = await router.DispatchAsync(new BridgeRequestFrame
        {
            RequestId = "req_missing",
            Command = "sample.missing",
        });

        Assert.Null(response.Result);
        Assert.NotNull(response.Error);
        Assert.Equal(BridgeErrorCodes.CommandUnsupported, response.Error.Code);
        Assert.Equal(BridgeErrorCategories.UnsupportedCapability, response.Error.Category);
    }

    [Fact]
    public async Task Invoker_ResolvesTypedHandlerThroughDiAndMapsSuccessResponse()
    {
        using var provider = BuildProvider(
            builder => builder.RegisterCommand<EchoRequest, EchoResponse, EchoHandler>("sample.echo"),
            services => services.AddSingleton(new EchoDependency("prefix")));
        var router = provider.GetRequiredService<IBridgeCommandRouter>();

        var response = await router.DispatchAsync(new BridgeRequestFrame
        {
            RequestId = "req_echo",
            Command = "sample.echo",
            Payload = BridgeJson.ToElement(new { Message = "hello" }),
            Correlation = new BridgeCorrelation { TraceId = "tr_echo" },
        });

        Assert.Null(response.Error);
        Assert.NotNull(response.Result);
        Assert.Equal("prefix:hello", response.Result.Value.GetProperty("echo").GetString());
        Assert.Equal("req_echo", response.Result.Value.GetProperty("request_id").GetString());
        Assert.Equal("tr_echo", response.Correlation.TraceId);
    }

    [Fact]
    public async Task Invoker_MapsBridgeHandlerExceptionToBridgeError()
    {
        using var provider = BuildProvider(builder => builder.RegisterCommand<EchoRequest, EchoResponse, FailingHandler>("sample.fail"));
        var router = provider.GetRequiredService<IBridgeCommandRouter>();

        var response = await router.DispatchAsync(new BridgeRequestFrame
        {
            RequestId = "req_fail",
            Command = "sample.fail",
            Payload = BridgeJson.ToElement(new { Message = "hello" }),
        });

        Assert.Null(response.Result);
        Assert.NotNull(response.Error);
        Assert.Equal("sample.failed", response.Error.Code);
        Assert.Equal(BridgeErrorCategories.Transient, response.Error.Category);
        Assert.True(response.Error.Retryable);
        Assert.Equal(250, response.Error.Details!.Value.GetProperty("retry_after_ms").GetInt32());
    }

    [Fact]
    public async Task Invoker_PropagatesCancellationTokenToHandlerAndMapsCancelledResponse()
    {
        var probe = new CancellationProbe();
        using var provider = BuildProvider(
            builder => builder.RegisterCommand<EchoRequest, EchoResponse, CancellableHandler>(
                "sample.wait",
                registration => registration.SupportsCancellation = true),
            services => services.AddSingleton(probe));
        var router = provider.GetRequiredService<IBridgeCommandRouter>();
        using var cts = new CancellationTokenSource();

        var responseTask = router.DispatchAsync(new BridgeRequestFrame
        {
            RequestId = "req_wait",
            Command = "sample.wait",
            Payload = BridgeJson.ToElement(new { Message = "hello" }),
        }, cts.Token).AsTask();

        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();
        var response = await responseTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(probe.HandlerSawCancellableToken);
        Assert.NotNull(response.Error);
        Assert.Equal(BridgeErrorCodes.RequestCancelled, response.Error.Code);
        Assert.Equal(BridgeErrorCategories.Cancelled, response.Error.Category);
    }

    [Fact]
    public async Task Invoker_EmitsProgressThroughConfiguredPublisher()
    {
        var publisher = new CapturingProgressPublisher();
        using var provider = BuildProvider(
            builder => builder.RegisterCommand<EchoRequest, EchoResponse, ProgressHandler>(
                "sample.progress",
                registration => registration.SupportsProgress = true),
            services => services.AddSingleton<IBridgeProgressPublisher>(publisher));
        var router = provider.GetRequiredService<IBridgeCommandRouter>();

        var response = await router.DispatchAsync(new BridgeRequestFrame
        {
            RequestId = "req_progress",
            Command = "sample.progress",
            Payload = BridgeJson.ToElement(new { Message = "hello" }),
            Correlation = new BridgeCorrelation { TraceId = "tr_progress" },
            ExpectsProgress = true,
        });

        Assert.Null(response.Error);
        var progress = Assert.Single(publisher.Frames);
        Assert.Equal("req_progress", progress.RequestId);
        Assert.Equal("working", progress.Stage);
        Assert.Equal(50, progress.Percent);
        Assert.Equal("tr_progress", progress.Correlation.TraceId);
    }

    [Fact]
    public async Task CapabilitiesProvider_ExposesCommandEventMetadataAndCapabilityGateCanDenyInvocation()
    {
        using var provider = BuildProvider(
            builder => builder
                .RegisterCommand<EchoRequest, EchoResponse, EchoHandler>(
                    "sample.restricted",
                    registration =>
                    {
                        registration.SupportsCancellation = true;
                        registration.SupportsProgress = true;
                        registration.RequiredCapabilities = new[] { "sample.admin" };
                        registration.RequestSchema = "sample.restricted.request.v1";
                        registration.ResponseSchema = "sample.restricted.response.v1";
                    })
                .RegisterEvent<EchoEventPayload>(
                    "sample.restricted_changed",
                    registration =>
                    {
                        registration.PayloadSchema = "sample.restricted_changed.v1";
                        registration.RequiredCapabilities = new[] { "sample.read" };
                    }),
            services =>
            {
                services.AddSingleton(new EchoDependency("prefix"));
                services.AddSingleton<IBridgeCapabilityGate>(new DenyingCapabilityGate());
            },
            host =>
            {
                host.AppId = "sample-app";
                host.AppVersion = "1.2.3";
                host.SchemaBundleId = "sample@1";
                host.SupportedTransports = new[] { "in_memory" };
                host.FeatureFlags = new[] { "sample.feature" };
            });

        var commandDescriptor = Assert.Single(provider.GetRequiredService<IBridgeCommandRegistry>().Commands);
        var eventDescriptor = Assert.Single(provider.GetRequiredService<IBridgeEventRegistry>().Events);
        var capabilities = provider.GetRequiredService<IBridgeCapabilitiesProvider>().CreateCapabilitiesFrame();
        var command = Assert.Single(capabilities.Commands);
        var evt = Assert.Single(capabilities.Events);

        Assert.Equal(typeof(EchoRequest), commandDescriptor.RequestType);
        Assert.Equal(typeof(EchoResponse), commandDescriptor.ResponseType);
        Assert.Equal(typeof(EchoEventPayload), eventDescriptor.PayloadType);
        Assert.Equal("sample-app", capabilities.AppId);
        Assert.Equal("sample@1", capabilities.SchemaBundleId);
        Assert.Equal("sample.restricted", command.Command);
        Assert.Equal("sample.restricted.request.v1", command.RequestSchema);
        Assert.Equal("sample.restricted.response.v1", command.ResponseSchema);
        Assert.True(command.SupportsCancellation);
        Assert.True(command.SupportsProgress);
        Assert.Equal(new[] { "sample.admin" }, command.RequiredCapabilities);
        Assert.Equal("sample.restricted_changed", evt.Event);
        Assert.Equal("sample.restricted_changed.v1", evt.PayloadSchema);
        Assert.Equal(new[] { "sample.read" }, evt.RequiredCapabilities);

        var router = provider.GetRequiredService<IBridgeCommandRouter>();
        var response = await router.DispatchAsync(new BridgeRequestFrame
        {
            RequestId = "req_restricted",
            Command = "sample.restricted",
            Payload = BridgeJson.ToElement(new { Message = "hello" }),
        });

        Assert.Null(response.Result);
        Assert.NotNull(response.Error);
        Assert.Equal(BridgeErrorCodes.CapabilityUnsupported, response.Error.Code);
        Assert.Equal(BridgeErrorCategories.UnsupportedCapability, response.Error.Category);
    }

    private static ServiceProvider BuildProvider(
        Action<BridgeRegistryBuilder> configureRegistry,
        Action<IServiceCollection>? configureServices = null,
        Action<BridgeHostMetadata>? configureHost = null)
    {
        var services = new ServiceCollection();
        configureServices?.Invoke(services);
        services.AddBridgeHost(configureRegistry, configureHost);
        return services.BuildServiceProvider(validateScopes: true);
    }

    private sealed record EchoRequest
    {
        public required string Message { get; init; }
    }

    private sealed record EchoResponse
    {
        public required string Echo { get; init; }

        public required string RequestId { get; init; }
    }

    private sealed record EchoEventPayload
    {
        public required string Message { get; init; }
    }

    private sealed record EchoDependency(string Prefix);

    private sealed class EchoHandler(EchoDependency? dependency = null) : IBridgeCommandHandler<EchoRequest, EchoResponse>
    {
        public ValueTask<EchoResponse?> HandleAsync(
            EchoRequest request,
            BridgeRequestContext context,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<EchoResponse?>(new EchoResponse
            {
                Echo = $"{dependency?.Prefix ?? "echo"}:{request.Message}",
                RequestId = context.RequestId,
            });
        }
    }

    private sealed class FailingHandler : IBridgeCommandHandler<EchoRequest, EchoResponse>
    {
        public ValueTask<EchoResponse?> HandleAsync(
            EchoRequest request,
            BridgeRequestContext context,
            CancellationToken cancellationToken)
        {
            throw new BridgeHandlerException(
                "sample.failed",
                "Sample failed",
                BridgeErrorCategories.Transient,
                retryable: true,
                BridgeJson.ToElement(new { RetryAfterMs = 250 }));
        }
    }

    private sealed class CancellationProbe
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool HandlerSawCancellableToken { get; set; }
    }

    private sealed class CancellableHandler(CancellationProbe probe) : IBridgeCommandHandler<EchoRequest, EchoResponse>
    {
        public async ValueTask<EchoResponse?> HandleAsync(
            EchoRequest request,
            BridgeRequestContext context,
            CancellationToken cancellationToken)
        {
            probe.HandlerSawCancellableToken = cancellationToken.CanBeCanceled;
            probe.Started.SetResult();
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return new EchoResponse { Echo = request.Message, RequestId = context.RequestId };
        }
    }

    private sealed class ProgressHandler : IBridgeCommandHandler<EchoRequest, EchoResponse>
    {
        public async ValueTask<EchoResponse?> HandleAsync(
            EchoRequest request,
            BridgeRequestContext context,
            CancellationToken cancellationToken)
        {
            await context.ReportProgressAsync(
                "working",
                "Working",
                percent: 50,
                payload: BridgeJson.ToElement(new { request.Message }),
                cancellationToken);

            return new EchoResponse { Echo = request.Message, RequestId = context.RequestId };
        }
    }

    private sealed class CapturingProgressPublisher : IBridgeProgressPublisher
    {
        public List<BridgeProgressFrame> Frames { get; } = [];

        public ValueTask PublishAsync(BridgeProgressFrame frame, CancellationToken cancellationToken = default)
        {
            Frames.Add(frame);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DenyingCapabilityGate : IBridgeCapabilityGate
    {
        public ValueTask<bool> IsAllowedAsync(
            IReadOnlyList<string> requiredCapabilities,
            BridgeRequestFrame request,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(requiredCapabilities.Count == 0);
        }
    }
}
