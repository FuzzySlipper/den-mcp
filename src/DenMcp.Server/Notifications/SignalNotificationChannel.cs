using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using DenMcp.Core.Services;

namespace DenMcp.Server.Notifications;

public sealed class SignalNotificationChannel : INotificationChannel, IAsyncDisposable
{
    private const string ChannelName = "signal";
    private const string ApproveEmoji = "\u2705";
    private const string RejectEmoji = "\u274C";
    private const string DispatchIcon = "\U0001F4CB";
    private const string ActiveIcon = "\U0001F7E2";
    private const string FinishedIcon = "\U0001F3C1";
    private const string ReviewIcon = "\u23F3";
    private const string BlockedIcon = "\u26D4";
    private const string OfflineIcon = "\u26AA";
    private const string InfoIcon = "\u2139";
    private static readonly TimeSpan DaemonStartupTimeout = TimeSpan.FromSeconds(10);
    private readonly DenMcpOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IDispatchRepository _dispatches;
    private readonly INotificationMessageRepository _messageLinks;
    private readonly ILogger<SignalNotificationChannel> _logger;
    private SemaphoreSlim? _daemonLock = new(1, 1);

    private Process? _daemonProcess;
    private bool _ownsDaemonProcess;
    private bool _availabilityWarningLogged;
    private bool _missingConfigWarningLogged;
    private int _rpcIdCounter;
    private int _disposeState;

    public SignalNotificationChannel(
        DenMcpOptions options,
        IHttpClientFactory httpClientFactory,
        IDispatchRepository dispatches,
        INotificationMessageRepository messageLinks,
        ILogger<SignalNotificationChannel> logger)
    {
        _options = options;
        _httpClientFactory = httpClientFactory;
        _dispatches = dispatches;
        _messageLinks = messageLinks;
        _logger = logger;
    }

    public async Task SendDispatchNotificationAsync(
        DispatchEntry dispatch,
        string summary,
        CancellationToken cancellationToken = default)
    {
        if (!ShouldSendDispatchNotifications())
            return;

        var timestamp = await SendMessageAsync(BuildDispatchMessage(dispatch, summary), cancellationToken);
        if (timestamp is null)
            return;

        await _messageLinks.LinkDispatchMessageAsync(
            ChannelName,
            timestamp.Value.ToString(CultureInfo.InvariantCulture),
            dispatch.Id,
            _options.Signal.RecipientNumber);
    }

    public async Task SendAgentStatusAsync(
        string projectId,
        string agent,
        string status,
        int? taskId = null,
        CancellationToken cancellationToken = default)
    {
        if (!ShouldSendAgentStatusNotifications())
            return;

        await SendMessageAsync(BuildAgentStatusMessage(projectId, agent, status, taskId), cancellationToken);
    }

    public async Task StartListeningAsync(CancellationToken cancellationToken)
    {
        if (!_options.Signal.Enabled)
            return;

        if (!HasConfiguredRecipient())
        {
            LogMissingRecipientWarning();
            return;
        }

        if (!await EnsureDaemonAvailableAsync(cancellationToken))
            return;

        var client = CreateClient();
        var path = HasConfiguredAccount()
            ? $"/api/v1/events?account={Uri.EscapeDataString(_options.Signal.Account!)}"
            : "/api/v1/events";

        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            LogDaemonUnavailable($"Signal event stream responded with {(int)response.StatusCode}.");
            return;
        }

        ResetAvailabilityWarning();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var currentEvent = "";
        var dataLines = new List<string>();

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
                break;

            if (line.Length == 0)
            {
                if (dataLines.Count > 0)
                {
                    var payload = string.Join("\n", dataLines);
                    dataLines.Clear();
                    await ProcessEventPayloadAsync(currentEvent, payload, cancellationToken);
                }

                currentEvent = "";
                continue;
            }

            if (line[0] == ':')
                continue;

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                currentEvent = line[6..].Trim();
                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                dataLines.Add(line.Length > 5 ? line[5..].TrimStart() : string.Empty);
            }
        }

        if (dataLines.Count > 0)
            await ProcessEventPayloadAsync(currentEvent, string.Join("\n", dataLines), cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            return;

        var daemonLock = Interlocked.Exchange(ref _daemonLock, null);
        if (daemonLock is null)
            return;

        await daemonLock.WaitAsync();
        try
        {
            if (_ownsDaemonProcess && _daemonProcess is { HasExited: false })
            {
                try
                {
                    _daemonProcess.Kill(entireProcessTree: true);
                    await _daemonProcess.WaitForExitAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to stop managed signal-cli process cleanly.");
                }
            }

            _daemonProcess?.Dispose();
            _daemonProcess = null;
            _ownsDaemonProcess = false;
        }
        finally
        {
            daemonLock.Release();
            daemonLock.Dispose();
        }
    }

    private bool ShouldSendDispatchNotifications()
    {
        if (!_options.Signal.Enabled || !_options.Signal.NotifyOnDispatch)
            return false;

        if (HasConfiguredRecipient())
            return true;

        LogMissingRecipientWarning();
        return false;
    }

    private bool ShouldSendAgentStatusNotifications()
    {
        if (!_options.Signal.Enabled || !_options.Signal.NotifyOnAgentStatus)
            return false;

        if (HasConfiguredRecipient())
            return true;

        LogMissingRecipientWarning();
        return false;
    }

    private bool HasConfiguredRecipient() =>
        !string.IsNullOrWhiteSpace(_options.Signal.RecipientNumber);

    private bool HasConfiguredAccount() =>
        !string.IsNullOrWhiteSpace(_options.Signal.Account);

    private void LogMissingRecipientWarning()
    {
        if (_missingConfigWarningLogged)
            return;

        _logger.LogWarning("Signal notifications are enabled but DenMcp:Signal:RecipientNumber is not configured.");
        _missingConfigWarningLogged = true;
    }

    private HttpClient CreateClient()
        => _httpClientFactory.CreateClient("signal-daemon");

    private async Task<long?> SendMessageAsync(string message, CancellationToken cancellationToken)
    {
        if (!await EnsureDaemonAvailableAsync(cancellationToken))
            return null;

        var parameters = new Dictionary<string, object?>
        {
            ["message"] = message,
            ["recipient"] = new[] { _options.Signal.RecipientNumber! }
        };

        if (HasConfiguredAccount())
            parameters["account"] = _options.Signal.Account;

        var response = await SendRpcAsync("send", parameters, cancellationToken);
        if (response is null || !response.Value.TryGetProperty("timestamp", out var timestampEl))
        {
            _logger.LogWarning("Signal send response did not include a timestamp.");
            return null;
        }

        return timestampEl.ValueKind switch
        {
            JsonValueKind.Number when timestampEl.TryGetInt64(out var timestamp) => timestamp,
            JsonValueKind.String when long.TryParse(timestampEl.GetString(), out var timestamp) => timestamp,
            _ => null
        };
    }

    private async Task<JsonElement?> SendRpcAsync(
        string method,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken)
    {
        try
        {
            var client = CreateClient();
            var requestBody = new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["method"] = method,
                ["id"] = Interlocked.Increment(ref _rpcIdCounter).ToString(CultureInfo.InvariantCulture),
                ["params"] = parameters
            };

            using var response = await client.PostAsJsonAsync("/api/v1/rpc", requestBody, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                LogDaemonUnavailable($"Signal RPC {method} failed with HTTP {(int)response.StatusCode}.");
                return null;
            }

            ResetAvailabilityWarning();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                _logger.LogWarning("Signal RPC {Method} returned error: {Error}", method, error.ToString());
                return null;
            }

            if (!document.RootElement.TryGetProperty("result", out var result))
            {
                _logger.LogWarning("Signal RPC {Method} returned no result payload.", method);
                return null;
            }

            return result.Clone();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogDaemonUnavailable($"Signal RPC {method} failed: {ex.Message}");
            return null;
        }
    }

    private async Task<bool> EnsureDaemonAvailableAsync(CancellationToken cancellationToken)
    {
        if (!_options.Signal.Enabled)
            return false;

        if (await CheckDaemonAsync(cancellationToken))
        {
            ResetAvailabilityWarning();
            return true;
        }

        if (!_options.Signal.AutoStart)
        {
            LogDaemonUnavailable("Signal daemon is unavailable and auto-start is disabled.");
            return false;
        }

        var daemonLock = _daemonLock ?? throw new ObjectDisposedException(nameof(SignalNotificationChannel));
        await daemonLock.WaitAsync(cancellationToken);
        try
        {
            if (await CheckDaemonAsync(cancellationToken))
            {
                ResetAvailabilityWarning();
                return true;
            }

            if (_daemonProcess is null || _daemonProcess.HasExited)
            {
                if (!TryStartDaemonProcess())
                    return false;
            }
        }
        finally
        {
            daemonLock.Release();
        }

        var startedAt = DateTime.UtcNow;
        while (DateTime.UtcNow - startedAt < DaemonStartupTimeout && !cancellationToken.IsCancellationRequested)
        {
            if (await CheckDaemonAsync(cancellationToken))
            {
                ResetAvailabilityWarning();
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        LogDaemonUnavailable("Timed out waiting for signal-cli daemon to start.");
        return false;
    }

    private async Task<bool> CheckDaemonAsync(CancellationToken cancellationToken)
    {
        try
        {
            var client = CreateClient();
            using var response = await client.GetAsync("/api/v1/check", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private bool TryStartDaemonProcess()
    {
        var signalOptions = _options.Signal;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = signalOptions.SignalCliPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            if (HasConfiguredAccount())
            {
                startInfo.ArgumentList.Add("-a");
                startInfo.ArgumentList.Add(signalOptions.Account!);
            }

            startInfo.ArgumentList.Add("daemon");
            startInfo.ArgumentList.Add($"--http={signalOptions.HttpHost}:{signalOptions.HttpPort}");

            var process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };

            process.OutputDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                    _logger.LogDebug("signal-cli: {Line}", args.Data);
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                    _logger.LogWarning("signal-cli: {Line}", args.Data);
            };
            process.Exited += (_, _) =>
            {
                _logger.LogWarning("Managed signal-cli daemon exited with code {ExitCode}.", process.ExitCode);
            };

            if (!process.Start())
            {
                LogDaemonUnavailable("signal-cli could not be started.");
                return false;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            _daemonProcess = process;
            _ownsDaemonProcess = true;
            _logger.LogInformation("Started managed signal-cli daemon on {Host}:{Port}.",
                signalOptions.HttpHost, signalOptions.HttpPort);
            return true;
        }
        catch (Win32Exception ex)
        {
            LogDaemonUnavailable($"signal-cli executable not found: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            LogDaemonUnavailable($"Failed to start signal-cli daemon: {ex.Message}");
            return false;
        }
    }

    private void ResetAvailabilityWarning()
    {
        _availabilityWarningLogged = false;
    }

    private void LogDaemonUnavailable(string message)
    {
        if (_availabilityWarningLogged)
            return;

        _logger.LogWarning("{Message}", message);
        _availabilityWarningLogged = true;
    }

    private async Task ProcessEventPayloadAsync(string eventName, string payload, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(eventName) && !string.Equals(eventName, "receive", StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            using var document = JsonDocument.Parse(payload);
            await ProcessInboundPayloadAsync(document.RootElement, cancellationToken);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Ignoring malformed Signal SSE payload: {Payload}", payload);
        }
    }

    private async Task ProcessInboundPayloadAsync(JsonElement root, CancellationToken cancellationToken)
    {
        if (!TryGetEnvelope(root, out var envelope))
            return;

        var source = GetOptionalString(envelope, "source");
        var sourceNumber = GetOptionalString(envelope, "sourceNumber");
        var sourceUuid = GetOptionalString(envelope, "sourceUuid");
        var sourceName = GetOptionalString(envelope, "sourceName");
        if (!MatchesConfiguredRecipient(source, sourceNumber, sourceUuid))
            return;

        if (!TryGetDataMessage(envelope, out var dataMessage))
            return;

        if (TryGetReaction(dataMessage, out var reaction))
        {
            await HandleReactionAsync(reaction, source, sourceNumber, sourceUuid, sourceName, cancellationToken);
        }
        else
        {
            await HandleCommandAsync(dataMessage, source, sourceNumber, sourceUuid, sourceName, cancellationToken);
        }
    }

    private async Task HandleReactionAsync(
        SignalReaction reaction,
        string? source,
        string? sourceNumber,
        string? sourceUuid,
        string? sourceName,
        CancellationToken cancellationToken)
    {
        if (reaction.IsRemove)
            return;

        if (!IsApprovalReaction(reaction.Emoji) && !IsRejectReaction(reaction.Emoji))
            return;

        var dispatchId = await _messageLinks.FindDispatchIdAsync(
            ChannelName,
            reaction.TargetSentTimestamp.ToString(CultureInfo.InvariantCulture));
        if (dispatchId is null)
            return;

        DispatchEntry? updated;
        try
        {
            updated = IsApprovalReaction(reaction.Emoji)
                ? await _dispatches.ApproveAsync(dispatchId.Value, BuildSignalActor(source, sourceNumber, sourceUuid, sourceName))
                : await _dispatches.RejectAsync(dispatchId.Value, BuildSignalActor(source, sourceNumber, sourceUuid, sourceName));
        }
        catch (InvalidOperationException)
        {
            return;
        }

        var confirmation = IsApprovalReaction(reaction.Emoji)
            ? BuildDispatchConfirmationMessage(updated, approved: true)
            : BuildDispatchConfirmationMessage(updated, approved: false);
        await SendMessageAsync(confirmation, cancellationToken);
    }

    private async Task HandleCommandAsync(
        JsonElement dataMessage,
        string? source,
        string? sourceNumber,
        string? sourceUuid,
        string? sourceName,
        CancellationToken cancellationToken)
    {
        var message = GetOptionalString(dataMessage, "message");
        if (string.IsNullOrWhiteSpace(message))
            return;

        var normalized = message.Trim().ToLowerInvariant();
        if (normalized is "approve all")
        {
            var pending = await _dispatches.ListAsync(statuses: [DispatchStatus.Pending]);
            var approved = new List<DispatchEntry>();
            foreach (var entry in pending)
            {
                try
                {
                    approved.Add(await _dispatches.ApproveAsync(
                        entry.Id,
                        BuildSignalActor(source, sourceNumber, sourceUuid, sourceName)));
                }
                catch (InvalidOperationException)
                {
                    // Another actor may have decided it while this command was in flight.
                }
            }

            var response = approved.Count > 0
                ? $"Approved {approved.Count} pending dispatches."
                : "There are no pending dispatches to approve.";
            await SendMessageAsync(response, cancellationToken);
            return;
        }

        if (normalized is not ("details" or "d"))
            return;

        if (!TryGetQuotedTimestamp(dataMessage, out var quotedTimestamp))
            return;

        var dispatchId = await _messageLinks.FindDispatchIdAsync(
            ChannelName,
            quotedTimestamp.ToString(CultureInfo.InvariantCulture));
        if (dispatchId is null)
            return;

        var dispatch = await _dispatches.GetByIdAsync(dispatchId.Value);
        if (dispatch is null)
            return;

        await SendMessageAsync(BuildDispatchDetailsMessage(dispatch), cancellationToken);
    }

    private static bool TryGetEnvelope(JsonElement root, out JsonElement envelope)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("envelope", out envelope))
                return true;

            if (root.TryGetProperty("params", out var parameters))
            {
                if (parameters.TryGetProperty("envelope", out envelope))
                    return true;

                if (parameters.TryGetProperty("result", out var result) && result.TryGetProperty("envelope", out envelope))
                    return true;
            }
        }

        envelope = default;
        return false;
    }

    private static bool TryGetDataMessage(JsonElement envelope, out JsonElement dataMessage)
    {
        if (envelope.ValueKind == JsonValueKind.Object && envelope.TryGetProperty("dataMessage", out dataMessage))
            return true;

        if (envelope.ValueKind == JsonValueKind.Object &&
            envelope.TryGetProperty("syncMessage", out var syncMessage) &&
            syncMessage.ValueKind == JsonValueKind.Object &&
            syncMessage.TryGetProperty("sentMessage", out var sentMessage) &&
            sentMessage.ValueKind == JsonValueKind.Object &&
            sentMessage.TryGetProperty("dataMessage", out dataMessage))
        {
            return true;
        }

        dataMessage = default;
        return false;
    }

    private static bool TryGetReaction(JsonElement dataMessage, out SignalReaction reaction)
    {
        if (dataMessage.ValueKind == JsonValueKind.Object &&
            dataMessage.TryGetProperty("reaction", out var reactionEl) &&
            reactionEl.ValueKind == JsonValueKind.Object &&
            TryGetInt64(reactionEl, "targetSentTimestamp", out var targetSentTimestamp))
        {
            reaction = new SignalReaction(
                GetOptionalString(reactionEl, "emoji"),
                targetSentTimestamp,
                GetOptionalBool(reactionEl, "isRemove"));
            return reaction.Emoji is not null;
        }

        reaction = default;
        return false;
    }

    private static bool TryGetQuotedTimestamp(JsonElement dataMessage, out long timestamp)
    {
        if (dataMessage.ValueKind == JsonValueKind.Object &&
            dataMessage.TryGetProperty("quote", out var quote) &&
            quote.ValueKind == JsonValueKind.Object)
        {
            return TryGetInt64(quote, "id", out timestamp);
        }

        timestamp = default;
        return false;
    }

    private static bool TryGetInt64(JsonElement element, string propertyName, out long value)
    {
        value = default;
        if (!element.TryGetProperty(propertyName, out var property))
            return false;

        return property.ValueKind switch
        {
            JsonValueKind.Number => property.TryGetInt64(out value),
            JsonValueKind.String => long.TryParse(property.GetString(), out value),
            _ => false
        };
    }

    private static string? GetOptionalString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
            return null;

        return property.ValueKind == JsonValueKind.String ? property.GetString() : property.ToString();
    }

    private static bool GetOptionalBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return false;

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(property.GetString(), out var value) && value,
            _ => false
        };
    }

    private bool MatchesConfiguredRecipient(params string?[] candidates)
    {
        var configuredRecipient = _options.Signal.RecipientNumber?.Trim();
        if (string.IsNullOrWhiteSpace(configuredRecipient))
            return false;

        return candidates.Any(candidate =>
            !string.IsNullOrWhiteSpace(candidate) &&
            string.Equals(candidate.Trim(), configuredRecipient, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsApprovalReaction(string? emoji) =>
        string.Equals(emoji, ApproveEmoji, StringComparison.Ordinal);

    private static bool IsRejectReaction(string? emoji) =>
        string.Equals(emoji, RejectEmoji, StringComparison.Ordinal);

    private static string BuildSignalActor(string? source, string? sourceNumber, string? sourceUuid, string? sourceName)
    {
        if (!string.IsNullOrWhiteSpace(sourceNumber))
            return $"signal:{sourceNumber}";
        if (!string.IsNullOrWhiteSpace(sourceUuid))
            return $"signal:{sourceUuid}";
        if (!string.IsNullOrWhiteSpace(source))
            return $"signal:{source}";
        if (!string.IsNullOrWhiteSpace(sourceName))
            return $"signal:{sourceName}";
        return "signal";
    }

    private static string BuildDispatchMessage(DispatchEntry dispatch, string summary)
    {
        var lines = new List<string>
        {
            $"{DispatchIcon} {dispatch.ProjectId} -> {dispatch.TargetAgent}"
        };

        if (dispatch.TaskId is int taskId)
            lines[0] += $" (task #{taskId})";

        lines.Add(string.IsNullOrWhiteSpace(summary) ? "Pending dispatch awaiting approval." : summary.Trim());
        lines.Add($"React {ApproveEmoji} to approve, {RejectEmoji} to reject.");
        lines.Add("Reply \"details\" for the full prompt.");
        return string.Join("\n", lines);
    }

    private static string BuildDispatchDetailsMessage(DispatchEntry dispatch)
    {
        var lines = new List<string>
        {
            $"Dispatch #{dispatch.Id}",
            $"Project: {dispatch.ProjectId}",
            $"Target agent: {dispatch.TargetAgent}",
            $"Status: {dispatch.Status.ToDbValue()}"
        };

        if (dispatch.TaskId is int taskId)
            lines.Add($"Task: #{taskId}");

        if (!string.IsNullOrWhiteSpace(dispatch.Summary))
        {
            lines.Add("");
            lines.Add("Summary:");
            lines.Add(dispatch.Summary.Trim());
        }

        if (!string.IsNullOrWhiteSpace(dispatch.ContextPrompt))
        {
            lines.Add("");
            lines.Add("Context prompt:");
            lines.Add(dispatch.ContextPrompt.Trim());
        }

        return string.Join("\n", lines);
    }

    private static string BuildDispatchConfirmationMessage(DispatchEntry dispatch, bool approved)
    {
        var verb = approved ? "Dispatched" : "Rejected";
        var subject = dispatch.TaskId is int taskId
            ? $"{dispatch.ProjectId} task #{taskId}"
            : dispatch.ProjectId;
        var icon = approved ? ApproveEmoji : RejectEmoji;
        return $"{icon} {verb} {dispatch.TargetAgent} on {subject}.";
    }

    private static string BuildAgentStatusMessage(string projectId, string agent, string status, int? taskId)
    {
        var taskLabel = taskId is int id ? $" #{id}" : "";
        return status switch
        {
            "checked_in" => $"{ActiveIcon} {projectId}: {agent} checked in",
            "checked_out" => $"{OfflineIcon} {projectId}: {agent} checked out",
            "in_progress" => $"{ActiveIcon} {projectId}: {agent} started task{taskLabel}",
            "review" => $"{ReviewIcon} {projectId}: {agent} reviewing{taskLabel}",
            "done" => $"{FinishedIcon} {projectId}: {agent} finished task{taskLabel}",
            "blocked" => $"{BlockedIcon} {projectId}: {agent} blocked on task{taskLabel}",
            _ => $"{InfoIcon} {projectId}: {agent} status {status}{taskLabel}"
        };
    }

    private readonly record struct SignalReaction(string? Emoji, long TargetSentTimestamp, bool IsRemove);
}
