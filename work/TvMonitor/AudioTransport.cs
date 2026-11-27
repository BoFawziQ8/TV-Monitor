using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

static partial class Network
{
    private static readonly SemaphoreSlim TalkGate = new(1);
    private const string TalkComponent = PopupPackage + "/.TalkService";
    private const string CaptureComponent = PopupPackage + "/.CaptureService";
    private const string CaptureActivity = PopupPackage + "/.CaptureActivity";

    internal static ProcessStartInfo Scrcpy(Tv tv)
    {
        var info = new ProcessStartInfo(Path.Combine(Engine, "scrcpy.exe"))
        {
            WorkingDirectory = Engine, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        info.Environment["ADB"] = Adb;
        info.Environment["ADB_SERVER_SOCKET"] = $"tcp:localhost:{AdbPort}";
        info.ArgumentList.Add("--serial=" + Serial(tv));
        return info;
    }

    internal static void AddScrcpyAudio(ProcessStartInfo info, int sdk, bool microphone = false)
    {
        info.ArgumentList.Add("--audio-codec=raw");
        info.ArgumentList.Add(microphone ? "--audio-source=mic" : sdk >= 33 ? "--audio-dup" : "--audio-source=output");
    }

    internal sealed class AudioConnection : IAsyncDisposable
    {
        private readonly Tv tv;
        private readonly int port;
        private readonly TcpClient client;
        private readonly string component;
        private readonly Action? release;
        private bool closed;

        internal AudioConnection(Tv tv, int port, TcpClient client, string component, Action? release = null)
        {
            this.tv = tv;
            this.port = port;
            this.client = client;
            this.component = component;
            this.release = release;
        }

        internal NetworkStream Stream => client.GetStream();

        public async ValueTask DisposeAsync()
        {
            if (closed) return;
            closed = true;
            client.Dispose();
            try { await CleanAudioTunnel(tv, port, component); }
            finally { release?.Invoke(); }
        }
    }

    internal static async Task<AudioConnection> OpenTalk(Tv tv, CancellationToken ct)
    {
        if (tv.Platform != Platform.Android) throw new IOException(T("Talk requires an Android TV."));
        await TalkGate.WaitAsync(ct);
        int port = 0;
        var requested = false;
        try
        {
            var state = await Read(tv, ct);
            if (state.Power != Power.On) throw new IOException(Tf($"Turn on and connect {tv.Name} before talking. {state.Detail}"));
            await EnsureHelper(tv, 2, ct);

            var serial = Serial(tv);
            var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            port = await ForwardAudio(tv, "tvmon_audio_" + secret);
            ct.ThrowIfCancellationRequested();
            var command = state.Sdk >= 26 ? "start-foreground-service" : "startservice";
            requested = true;
            var reply = await RunAdb(ct, "-s", serial, "shell", $"am {command} -n {TalkComponent} --es session {secret}");
            if (!reply.Contains("Starting service", StringComparison.OrdinalIgnoreCase) ||
                reply.Contains("Error:", StringComparison.OrdinalIgnoreCase))
                throw new IOException(Tf($"Android could not start the audio receiver. {ShortError(reply)}"));

            var client = await WaitForAudio(port, secret, 7500, ct)
                ?? throw new IOException(Tf($"{tv.Name} did not acknowledge its audio receiver."));
            return new(tv, port, client, TalkComponent, () => TalkGate.Release());
        }
        catch
        {
            try { await CleanAudioTunnel(tv, port, requested ? TalkComponent : null); }
            finally { TalkGate.Release(); }
            throw;
        }
    }

    internal static async Task<AudioConnection> OpenPlaybackCapture(Tv tv, Action prompt, CancellationToken ct)
    {
        await EnsureHelper(tv, 3, ct);
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var port = await ForwardAudio(tv, "tvmon_playback_" + secret);
        var requested = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            prompt();
            requested = true;
            try
            {
                var reply = await RunAdb(ct, 20000, "-s", Serial(tv), "shell",
                    $"am start -n {CaptureActivity} --es session {secret}");
                if (!reply.Contains("Starting:", StringComparison.OrdinalIgnoreCase) ||
                    reply.Contains("Error", StringComparison.OrdinalIgnoreCase))
                    throw new IOException(Tf($"The TV could not open the audio approval prompt. {ShortError(reply)}"));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The firmware can display the prompt even if its activity-start command times out.
            }
            var client = await WaitForAudio(port, secret, 62000, ct)
                ?? throw new IOException(T("TV audio did not start. Reopen Watch live and approve both prompts on the TV."));
            return new(tv, port, client, CaptureComponent);
        }
        catch
        {
            await CleanAudioTunnel(tv, port, requested ? CaptureComponent : null);
            throw;
        }
    }

    private static async Task<int> ForwardAudio(Tv tv, string socket)
    {
        var forward = await RunAdb(CancellationToken.None, "-s", Serial(tv), "forward", "tcp:0", "localabstract:" + socket);
        if (!int.TryParse(forward.Trim(), out var port) || port is < 1 or > 65535)
            throw new IOException(Tf($"Could not open the audio tunnel. {ShortError(forward)}"));
        return port;
    }

    private static async Task<TcpClient?> WaitForAudio(int port, string secret, int milliseconds, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(milliseconds);
        while (!deadline.IsCancellationRequested)
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            attempt.CancelAfter(2000);
            var client = new TcpClient { NoDelay = true };
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port, attempt.Token);
                var stream = client.GetStream();
                await stream.WriteAsync(Encoding.ASCII.GetBytes(secret), attempt.Token);
                var acknowledgment = new byte[1];
                await stream.ReadExactlyAsync(acknowledgment, attempt.Token);
                if (acknowledgment[0] != 1) throw new IOException(T("Unexpected Android audio response."));
                ct.ThrowIfCancellationRequested();
                return client;
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
            {
                client.Dispose();
                if (!deadline.IsCancellationRequested)
                    await Task.Delay(250, ct);
            }
        }
        ct.ThrowIfCancellationRequested();
        return null;
    }

    private static async Task CleanAudioTunnel(Tv tv, int port, string? component)
    {
        async Task Quiet(params string[] args)
        {
            try { await RunAdb(CancellationToken.None, args); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or System.ComponentModel.Win32Exception) { }
        }

        if (component is not null)
            await Quiet("-s", Serial(tv), "shell", $"am stopservice -n {component}");
        if (port is >= 1 and <= 65535)
            await Quiet("-s", Serial(tv), "forward", "--remove", $"tcp:{port}");
    }
}
