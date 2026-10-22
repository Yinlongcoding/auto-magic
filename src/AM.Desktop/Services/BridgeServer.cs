using System.IO;
using System.IO.Pipes;
using AM.Bridge;

namespace AM.Desktop.Services;

/// <summary>
/// 桌面端命名管道服务。NativeHost 连进来就代表插件已连接；同一时间只接受一个连接。
/// 事件在后台线程触发，订阅方自己切回界面线程。
/// </summary>
public sealed class BridgeServer : IAsyncDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private NamedPipeServerStream? _active;
    private Task? _loop;

    public event Action? Connected;
    public event Action? Disconnected;
    public event Action<BridgeEnvelope>? MessageReceived;

    public void Start() => _loop = Task.Run(() => RunAsync(_shutdown.Token));

    public async Task<bool> SendAsync(BridgeEnvelope envelope)
    {
        var pipe = _active;
        if (pipe is not { IsConnected: true })
        {
            return false;
        }

        await _writeLock.WaitAsync();
        try
        {
            await LengthPrefixedJson.WriteAsync(pipe, envelope, _shutdown.Token);
            return true;
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException)
        {
            return false;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = new NamedPipeServerStream(
                    BridgeProtocol.PipeName,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.WriteThrough | PipeOptions.CurrentUserOnly);
            }
            catch (IOException)
            {
                // 管道名被占用（例如另一个 AM 实例），稍后重试。
                await DelayAsync(TimeSpan.FromSeconds(5), cancellationToken);
                continue;
            }

            await using (pipe)
            {
                try
                {
                    await pipe.WaitForConnectionAsync(cancellationToken);
                    _active = pipe;
                    Connected?.Invoke();
                    while (await LengthPrefixedJson.ReadAsync(pipe, cancellationToken) is { } envelope)
                    {
                        // 探活直接在管道层回复，不依赖采集服务的状态。
                        // 走 SendAsync 是为了和其它消息共用 N 写锁，避免并发写坏帧。
                        if (envelope.Type == BridgeProtocol.Types.BridgePing)
                        {
                            await SendAsync(new BridgeEnvelope
                            {
                                RequestId = envelope.RequestId,
                                Type = BridgeProtocol.Types.BridgePong,
                            });
                            continue;
                        }

                        MessageReceived?.Invoke(envelope);
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException)
                {
                    // 连接断开或收到坏帧：丢弃这个连接，等下一个。
                }
                finally
                {
                    if (ReferenceEquals(_active, pipe))
                    {
                        _active = null;
                        Disconnected?.Invoke();
                    }
                }
            }
        }
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        if (_loop is not null)
        {
            await _loop;
        }

        _shutdown.Dispose();
        _writeLock.Dispose();
    }
}
