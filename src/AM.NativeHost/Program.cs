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
        Log($"start pid={Environment.ProcessId} args={string.Join(' ', Environment.GetCommandLineArgs().Skip(1))}");

        await using var standardInput = Console.OpenStandardInput();
        await using var standardOutput = Console.OpenStandardOutput();
        await using var pipe = new NamedPipeClientStream(
            ".",
            BridgeProtocol.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough);

        try
        {
            await ConnectWithRetryAsync(pipe);
        }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException or IOException)
        {
            Log($"desktop-not-reachable {error.GetType().Name}: {error.Message}");
            var response = BridgeEnvelope.Failure(
                BridgeProtocol.Types.BridgeStatus,
                "DESKTOP_NOT_RUNNING",
                "AM 桌面端未启动或尚未准备好。");
            await LengthPrefixedJson.WriteAsync(standardOutput, response, CancellationToken.None);
            return 2;
        }

        Log("connected to desktop");
        using var shutdown = new CancellationTokenSource();
        try
        {
            var extensionToDesktop = ForwardAsync(standardInput, pipe, BridgeProtocol.FromExtension, shutdown.Token);
            var desktopToExtension = ForwardAsync(pipe, standardOutput, BridgeProtocol.FromDesktop, shutdown.Token);
            var completed = await Task.WhenAny(extensionToDesktop, desktopToExtension);
            await completed;
            shutdown.Cancel();
            Log($"stopped: {(completed == extensionToDesktop ? "extension closed stdin" : "desktop closed pipe")}");
            return 0;
        }
        catch (Exception error)
        {
            Log($"error {error.GetType().Name}: {error.Message}");
            await Console.Error.WriteLineAsync($"AM NativeHost error: {error.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Chrome 启动的进程没有可见控制台，出错信息只能进日志。文件在 LocalAppData/AM/native-host.log。
    /// 追加写，超过 1 MB 时轮换一次。
    /// </summary>
    private static void Log(string message)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AM");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "native-host.log");
            var info = new FileInfo(path);
            if (info.Exists && info.Length > 1024 * 1024)
            {
                File.Move(path, path + ".1", overwrite: true);
            }

            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // 日志失败不能影响转发。
        }
    }

    /// <summary>
    /// 桌面端管道在客户端断开后要重建，重建期间新连接会失败。总共等约 5 秒再放弃。
    /// </summary>
    private static async Task ConnectWithRetryAsync(NamedPipeClientStream pipe)
    {
        const int attempts = 10;
        for (var attempt = 1; ; attempt++)
        {
            using var attemptTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
            try
            {
                await pipe.ConnectAsync(attemptTimeout.Token);
                Log($"connected to desktop after {attempt} attempt(s)");
                return;
            }
            catch (Exception error) when (attempt < attempts && error is TimeoutException or OperationCanceledException or IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200));
            }
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
            BridgeEnvelope? envelope;
            try
            {
                envelope = await LengthPrefixedJson.ReadAsync(source, cancellationToken);
            }
            catch (Exception error) when (error is System.Text.Json.JsonException or InvalidDataException)
            {
                // 帧长度是好的、内容坏了（例如旧版插件少发 type）。跳过这一帧继续转发，
                // 不要因为一条消息把整条连接和桌面端会话一起废掉。
                Log($"skip bad frame {error.GetType().Name}: {error.Message}");
                await Console.Error.WriteLineAsync($"AM NativeHost: 跳过一条无效消息：{error.Message}");
                continue;
            }

            if (envelope is null)
            {
                return;
            }

            if (!allowedTypes.Contains(envelope.Type))
            {
                // 丢弃而不是断开：插件版本和桌面端不一致时，不该把整条连接一起废掉。
                await Console.Error.WriteLineAsync($"AM NativeHost: 忽略未知消息类型 {envelope.Type}。");
                continue;
            }

            await LengthPrefixedJson.WriteAsync(destination, envelope, cancellationToken);
        }
    }
}
