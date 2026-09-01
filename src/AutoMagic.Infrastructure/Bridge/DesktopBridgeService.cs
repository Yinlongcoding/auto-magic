using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using AutoMagic.Application.Search;
using AutoMagic.Contracts.Protocol;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AutoMagic.Infrastructure.Bridge;

public sealed class DesktopBridgeService : BackgroundService, ISearchBridge
{
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromMinutes(3);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<SearchResultPayload>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private NamedPipeServerStream? _connection;
    private int _connectionState;
    private readonly ILogger<DesktopBridgeService> _logger;
    private readonly string _pipeName;

    public DesktopBridgeService(ILogger<DesktopBridgeService> logger)
        : this(logger, BridgeProtocol.PipeName)
    {
    }

    public DesktopBridgeService(
        ILogger<DesktopBridgeService> logger,
        string pipeName)
    {
        _logger = logger;
        _pipeName = string.IsNullOrWhiteSpace(pipeName)
            ? throw new ArgumentException("Pipe name is required.", nameof(pipeName))
            : pipeName;
    }

    public bool IsExtensionConnected => Volatile.Read(ref _connectionState) == 1;

    public event EventHandler<bool>? ConnectionChanged;

    public async Task<SearchResultPayload> SearchAsync(
        string keyword,
        int maxItems,
        decimal procurementMinimumCny,
        decimal procurementMaximumCny,
        string sortMode,
        bool includeDetailFacts,
        CancellationToken cancellationToken)
    {
        keyword = keyword.Trim();
        if (keyword.Length is < 1 or > 100)
        {
            throw new ArgumentException("搜索关键词长度必须在 1 到 100 个字符之间。", nameof(keyword));
        }

        if (!IsExtensionConnected)
        {
            throw new InvalidOperationException("Chrome 插件尚未连接，请确认桌面端已注册且插件已启用。");
        }

        if (procurementMinimumCny < 0 || procurementMaximumCny < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(procurementMinimumCny),
                "采购成本区间不能为负数。");
        }

        if (procurementMinimumCny > procurementMaximumCny)
        {
            throw new ArgumentException("采购成本下限不能高于上限。", nameof(procurementMinimumCny));
        }

        if (!ProductSortModes.IsSupported(sortMode))
        {
            throw new ArgumentException("商品排序模式无效。", nameof(sortMode));
        }

        var requestId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<SearchResultPayload>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(requestId, completion))
        {
            throw new InvalidOperationException("无法创建搜索请求。");
        }

        try
        {
            var envelope = ProtocolEnvelope.Create(
                requestId,
                BridgeProtocol.MessageTypes.SearchStart,
                new SearchStartPayload(
                    keyword,
                    Math.Clamp(maxItems, 1, 60),
                    procurementMinimumCny,
                    procurementMaximumCny,
                    sortMode,
                    includeDetailFacts));
            await SendAsync(envelope, cancellationToken);
            return await completion.Task.WaitAsync(SearchTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("等待 Chrome 插件返回搜索结果超时。");
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await using var pipe = CreatePipe();
            try
            {
                _logger.LogInformation("等待 Chrome 原生消息宿主连接。Pipe={PipeName}", _pipeName);
                await pipe.WaitForConnectionAsync(stoppingToken);
                Volatile.Write(ref _connection, pipe);
                await ReadLoopAsync(pipe, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                _logger.LogError(error, "Chrome 桥接连接异常，将重新等待连接。");
            }
            finally
            {
                Volatile.Write(ref _connection, null);
                SetConnected(false);
                FailPending("Chrome 插件连接已断开。");
            }
        }
    }

    private NamedPipeServerStream CreatePipe() =>
        new(
            _pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough | PipeOptions.CurrentUserOnly);

    private async Task ReadLoopAsync(Stream pipe, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var envelope = await LengthPrefixedJson.ReadEnvelopeAsync(pipe, cancellationToken);
            if (envelope is null)
            {
                return;
            }

            HandleEnvelope(envelope);
        }
    }

    private void HandleEnvelope(ProtocolEnvelope envelope)
    {
        switch (envelope.Type)
        {
            case BridgeProtocol.MessageTypes.ExtensionReady:
                SetConnected(true);
                _logger.LogInformation("Chrome 插件已连接。Version={Version}",
                    Deserialize<ExtensionReadyPayload>(envelope)?.ExtensionVersion ?? "unknown");
                break;

            case BridgeProtocol.MessageTypes.SearchAccepted:
                _logger.LogInformation("Chrome 已接受搜索请求。RequestId={RequestId}", envelope.RequestId);
                break;

            case BridgeProtocol.MessageTypes.SearchCompleted:
                var result = Deserialize<SearchResultPayload>(envelope);
                if (result is null)
                {
                    CompleteWithError(envelope.RequestId, "Chrome 返回的搜索结果格式无效。");
                }
                else if (_pending.TryGetValue(envelope.RequestId, out var completion))
                {
                    completion.TrySetResult(result);
                }
                break;

            case BridgeProtocol.MessageTypes.SearchFailed:
            case BridgeProtocol.MessageTypes.BridgeStatus:
                CompleteWithError(
                    envelope.RequestId,
                    envelope.Error?.Message ?? "Chrome 桥接返回了未知错误。");
                break;
        }
    }

    private static T? Deserialize<T>(ProtocolEnvelope envelope) =>
        envelope.Payload is { } payload
            ? payload.Deserialize<T>(BridgeJson.Options)
            : default;

    private async Task SendAsync(ProtocolEnvelope envelope, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var pipe = Volatile.Read(ref _connection);
            if (pipe is null || !pipe.IsConnected)
            {
                throw new InvalidOperationException("Chrome 插件连接已断开。");
            }

            await LengthPrefixedJson.WriteEnvelopeAsync(pipe, envelope, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void CompleteWithError(string requestId, string message)
    {
        if (_pending.TryGetValue(requestId, out var completion))
        {
            completion.TrySetException(new InvalidOperationException(message));
        }
    }

    private void FailPending(string message)
    {
        foreach (var completion in _pending.Values)
        {
            completion.TrySetException(new IOException(message));
        }
    }

    private void SetConnected(bool value)
    {
        var next = value ? 1 : 0;
        if (Interlocked.Exchange(ref _connectionState, next) == next)
        {
            return;
        }

        ConnectionChanged?.Invoke(this, value);
    }
}
