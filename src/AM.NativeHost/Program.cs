using System.IO.Pipes;
using AM.Bridge;

return await NativeHostProgram.RunAsync();

/// <summary>
/// Chrome 启动的中转进程：stdin/stdout（插件）↔ 命名管道（桌面端），按允许列表原样转发。
/// 来自旧仓库 AutoMagic.NativeHost，改了协议类型名。
/// </summary>
internal static class NativeHostProgram
{
    public static async Task<int> RunAsync()
    {
        await using var standardInput = Console.OpenStandardInput();
        await using var standardOutput = Console.OpenStandardOutput();
        await using var pipe = new NamedPipeClientStream(
            ".",
            BridgeProtocol.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough);

        try
        {
            using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await pipe.ConnectAsync(connectTimeout.Token);
        }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException or IOException)
        {
            var response = BridgeEnvelope.Failure(
                BridgeProtocol.Types.BridgeStatus,
                "DESKTOP_NOT_RUNNING",
                "AM 桌面端未启动或尚未准备好。");
            await LengthPrefixedJson.WriteAsync(standardOutput, response, CancellationToken.None);
            return 2;
        }

        using var shutdown = new CancellationTokenSource();
        try
        {
            var extensionToDesktop = ForwardAsync(standardInput, pipe, BridgeProtocol.FromExtension, shutdown.Token);
            var desktopToExtension = ForwardAsync(pipe, standardOutput, BridgeProtocol.FromDesktop, shutdown.Token);
            var completed = await Task.WhenAny(extensionToDesktop, desktopToExtension);
            await completed;
            shutdown.Cancel();
            return 0;
        }
        catch (Exception error)
        {
            await Console.Error.WriteLineAsync($"AM NativeHost error: {error.Message}");
            return 1;
        }
    }

    private static async Task ForwardAsync(
        Stream source,
        Stream destination,
        IReadOnlySet<string> allowedTypes,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var envelope = await LengthPrefixedJson.ReadAsync(source, cancellationToken);
            if (envelope is null)
            {
                return;
            }

            if (!allowedTypes.Contains(envelope.Type))
            {
                throw new InvalidDataException($"消息类型不在允许列表中：{envelope.Type}。");
            }

            await LengthPrefixedJson.WriteAsync(destination, envelope, cancellationToken);
        }
    }
}
