using System.IO.Pipes;
using AutoMagic.Contracts.Protocol;

return await NativeHostProgram.RunAsync();

internal static class NativeHostProgram
{
    private static readonly HashSet<string> ExtensionMessageTypes =
    [
        BridgeProtocol.MessageTypes.ExtensionReady,
        BridgeProtocol.MessageTypes.SearchAccepted,
        BridgeProtocol.MessageTypes.SearchCompleted,
        BridgeProtocol.MessageTypes.SearchFailed,
        BridgeProtocol.MessageTypes.BridgeStatus,
    ];

    private static readonly HashSet<string> DesktopMessageTypes =
    [
        BridgeProtocol.MessageTypes.SearchStart,
    ];

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
            var response = ProtocolEnvelope.Failure(
                Guid.NewGuid().ToString("N"),
                BridgeProtocol.MessageTypes.BridgeStatus,
                "DESKTOP_NOT_RUNNING",
                "Auto Magic 桌面端未启动或尚未准备好。");
            await LengthPrefixedJson.WriteEnvelopeAsync(standardOutput, response, CancellationToken.None);
            return 2;
        }

        using var shutdown = new CancellationTokenSource();
        try
        {
            var extensionToDesktop = ForwardAsync(
                standardInput,
                pipe,
                ExtensionMessageTypes,
                shutdown.Token);
            var desktopToExtension = ForwardAsync(
                pipe,
                standardOutput,
                DesktopMessageTypes,
                shutdown.Token);
            var completed = await Task.WhenAny(extensionToDesktop, desktopToExtension);
            await completed;
            shutdown.Cancel();
            return 0;
        }
        catch (Exception error)
        {
            await Console.Error.WriteLineAsync($"Auto Magic NativeHost error: {error.Message}");
            return 1;
        }
    }

    private static async Task ForwardAsync(
        Stream source,
        Stream destination,
        ISet<string> allowedTypes,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var envelope = await LengthPrefixedJson.ReadEnvelopeAsync(source, cancellationToken);
            if (envelope is null)
            {
                return;
            }

            if (!allowedTypes.Contains(envelope.Type))
            {
                throw new InvalidDataException($"消息类型不在允许列表中：{envelope.Type}。");
            }

            await LengthPrefixedJson.WriteEnvelopeAsync(destination, envelope, cancellationToken);
        }
    }
}
