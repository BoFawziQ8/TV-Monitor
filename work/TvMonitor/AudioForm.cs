using System.Diagnostics;
using System.Net.Sockets;
using System.Threading.Channels;
using NAudio.Wave;

sealed class AudioForm : FluentForm
{
    private readonly Tv tv;
    private readonly Func<bool> liveViewHasAudio;
    private readonly Label support = new(), status = new();
    private readonly Button listen = new FluentButton(), listenMic = new FluentButton(), talk = new FluentButton();
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? talking;
    private Process? listener;
    private Task listenerCompletion = Task.CompletedTask;
    private Task listenOperation = Task.CompletedTask, talkOperation = Task.CompletedTask;
    private Task? shutdown;
    private bool listenStarting, stoppingListener, listeningToMic, closing, closed;
    private Reading reading;

    internal AudioForm(Tv tv, Reading reading, Func<bool> liveViewHasAudio)
    {
        this.tv = tv;
        this.reading = reading;
        this.liveViewHasAudio = liveViewHasAudio;
        Text = T("TV Audio");
        Icon = Program.AppIcon;
        RightToLeft = IsArabic ? RightToLeft.Yes : RightToLeft.No;
        RightToLeftLayout = IsArabic;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ClientSize = new Size(500, 290);
        MaximizeBox = MinimizeBox = false;
        Font = new Font("Segoe UI Variable Text", 10);
        BackColor = Color.FromArgb(15, 23, 42);
        ForeColor = Color.FromArgb(241, 245, 249);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(14) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var height in new[] { 34, 63, 44, 0 })
            layout.RowStyles.Add(height == 0 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.Absolute, height));
        Controls.Add(layout);
        layout.Controls.Add(new Label { Text = tv.Name, Dock = DockStyle.Fill, Font = new Font("Segoe UI", 15, FontStyle.Bold), AutoEllipsis = true });
        support.Dock = status.Dock = DockStyle.Fill;
        support.ForeColor = status.ForeColor = Color.LightSteelBlue;
        layout.Controls.Add(support);
        var buttons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
        for (int i = 0; i < 3; i++) buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
        foreach (var button in new[] { listen, listenMic, talk })
        {
            button.AutoSize = false;
            button.Dock = DockStyle.Fill;
            button.Margin = new Padding(0, 0, 6, 4);
            button.MinimumSize = Size.Empty;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.BackColor = Color.FromArgb(37, 99, 235);
            button.ForeColor = ForeColor;
            buttons.Controls.Add(button);
        }
        listen.Click += (_, _) => listenOperation = ToggleListen(false);
        listenMic.Click += (_, _) => listenOperation = ToggleListen(true);
        talk.Click += (_, _) =>
        {
            if (talking is { } current)
            {
                current.Cancel();
                status.Text = T("Stopping the microphone…");
            }
            else talkOperation = ToggleTalk();
        };
        layout.Controls.Add(buttons);
        layout.Controls.Add(status);
        status.Text = tv.Platform == Platform.Android
            ? T("Talk uses the default Windows microphone. The first use installs or updates the helper on the TV.")
            : T("Audio over this TV’s network connection is unavailable.");
        UpdateReading(reading);
        PrepareLayout();
    }

    internal bool Matches(Tv candidate) => candidate.Address == tv.Address && candidate.Port == tv.Port;
    internal bool Active => listener is not null || listenStarting || talking is not null;

    internal void UpdateReading(Reading updated)
    {
        reading = updated;
        if (closed) return;
        support.Text = tv.Platform != Platform.Android ? T("Audio controls require a compatible Android TV.")
            : reading.Sdk is > 0 and < 30 ? T("Talk is available. Listening to the TV or its microphone requires Android 11 or later.")
            : reading.Sdk is >= 30 and < 33 ? T("Listen on PC moves TV sound to Windows. Listen to TV mic uses the input selected by Android.")
            : T("Listen on PC plays TV sound. Listen to TV mic uses the input selected by Android.");
        listen.Text = listener is not null && !listeningToMic ? T("Stop listening") : T("Listen on PC");
        listenMic.Text = listener is not null && listeningToMic ? T("Stop TV mic") : T("Listen to TV mic");
        talk.Text = talking is null ? T("Start talking") : T("Stop talking");
        talk.BackColor = talking is null ? Color.FromArgb(37, 99, 235) : Color.FromArgb(153, 27, 27);
        var android = !closing && tv.Platform == Platform.Android;
        var canListen = android && talking is null && !listenStarting && !stoppingListener &&
            (listener is not null || reading.Sdk == 0 || reading.Sdk >= 30);
        listen.Enabled = canListen && (listener is null || !listeningToMic);
        listenMic.Enabled = canListen && (listener is null || listeningToMic);
        talk.Enabled = android && !listenStarting && !stoppingListener;
    }

    private async Task ToggleListen(bool microphone)
    {
        if (listener is not null)
        {
            await StopListening();
            if (!closed) status.Text = T("Listening stopped.");
            return;
        }
        if (liveViewHasAudio())
        {
            status.Text = microphone
                ? T("Stop the live view, then start TV mic. You can reopen the video while listening.")
                : T("The live view is already set to play this TV’s audio on the PC.");
            return;
        }

        listenStarting = true;
        UpdateReading(reading);
        status.Text = microphone ? T("Connecting to the TV microphone…") : T("Connecting to the TV’s audio…");
        var token = lifetime.Token;
        try
        {
            var state = await Network.Read(tv, token);
            token.ThrowIfCancellationRequested();
            if (state.Power != Power.On) throw new IOException(T("Turn on and connect the TV first."));
            if (state.Sdk < 30) throw new IOException(T("Listening requires Android 11 or later."));
            var info = Network.Scrcpy(tv);
            foreach (var option in new[] { "--no-window", "--no-control", "--no-power-on", "--require-audio" })
                info.ArgumentList.Add(option);
            Network.AddScrcpyAudio(info, state.Sdk, microphone);
            var process = Process.Start(info) ?? throw new IOException(T("Could not start the audio player."));
            listeningToMic = microphone;
            listener = process;
            status.Text = microphone ? T("TV microphone stream started. Click Stop TV mic to end.")
                : state.Sdk >= 33 ? T("Listening on PC. Compatible apps also keep playing on the TV.")
                : T("Listening on PC. Stop listening to restore audio on the TV.");
            listenerCompletion = TrackListening(process);
        }
        catch (Exception ex) when (AudioError(ex))
        {
            if (!closed) status.Text = ex.Message;
        }
        finally
        {
            listenStarting = false;
            UpdateReading(reading);
        }
    }

    private async Task TrackListening(Process process)
    {
        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var log = (await output) + "\n" + (await errors);
            listener = null;
            if (closed || stoppingListener) return;
            status.Text = process.ExitCode == 0 ? T("Listening ended.") :
                log.Split('\n', StringSplitOptions.TrimEntries).LastOrDefault(line => line.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
                    ?? T("The TV audio stream ended.");
            UpdateReading(reading);
        }
    }

    private async Task StopListening()
    {
        if (listener is not { } process) return;
        stoppingListener = true;
        UpdateReading(reading);
        try
        {
            if (!process.HasExited) process.Kill();
            await listenerCompletion;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            if (!closed) status.Text = ex.Message;
        }
        finally
        {
            stoppingListener = false;
            UpdateReading(reading);
        }
    }

    private async Task ToggleTalk()
    {
        if (WaveInEvent.DeviceCount == 0)
        {
            status.Text = T("Windows has no recording device available. Connect or enable a microphone to talk.");
            return;
        }
        if (liveViewHasAudio())
        {
            status.Text = T("Close the live view first, then start talking. You can reopen video while talking.");
            return;
        }

        using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        talking = request;
        request.CancelAfter(TimeSpan.FromSeconds(85));
        UpdateReading(reading);
        try
        {
            var wasListening = listener is not null;
            await StopListening();
            if (listener is not null) throw new IOException(T("Stop listening before starting the microphone."));
            if (wasListening) await Task.Delay(650, request.Token);
            request.Token.ThrowIfCancellationRequested();
            status.Text = T("Preparing the audio receiver on the TV…");
            await using var connection = await Network.OpenTalk(tv, request.Token);
            request.Token.ThrowIfCancellationRequested();
            status.Text = T("Microphone live. Click Stop talking to end.");
            await SendMicrophone(connection.Stream, request.Token);
            if (!closed) status.Text = T("The microphone stopped.");
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            if (!closed) status.Text = T("Microphone stopped. Click Listen on PC to hear the TV again.");
        }
        catch (Exception ex) when (AudioError(ex))
        {
            if (!closed) status.Text = ex.Message;
        }
        finally
        {
            talking = null;
            UpdateReading(reading);
        }
    }

    private static async Task SendMicrophone(Stream stream, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var mic = new WaveInEvent
        {
            DeviceNumber = -1, WaveFormat = new WaveFormat(16000, 16, 1),
            BufferMilliseconds = 20, NumberOfBuffers = 3
        };
        var frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(5)
        {
            FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true
        });
        mic.DataAvailable += (_, args) =>
        {
            if (args.BytesRecorded == 640 && !ct.IsCancellationRequested)
                frames.Writer.TryWrite(args.Buffer.AsSpan(0, 640).ToArray());
        };
        mic.RecordingStopped += (_, args) => frames.Writer.TryComplete(args.Exception);
        mic.StartRecording();
        try
        {
            await foreach (var frame in frames.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                await stream.WriteAsync(frame, ct).ConfigureAwait(false);
            }
        }
        finally { mic.StopRecording(); }
    }

    private static bool AudioError(Exception ex) => ex is IOException or SocketException or OperationCanceledException or
        UnauthorizedAccessException or System.ComponentModel.Win32Exception or NAudio.MmException or InvalidOperationException;

    internal Task Shutdown() => shutdown ??= StopAll();

    private async Task StopAll()
    {
        closing = true;
        lifetime.Cancel();
        UpdateReading(reading);
        await listenOperation;
        await talkOperation;
        await StopListening();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (Active)
        {
            e.Cancel = true;
            if (!closing) _ = CloseWhenStopped();
        }
        base.OnFormClosing(e);
    }

    private async Task CloseWhenStopped()
    {
        await Shutdown();
        if (!closed) Close();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        closed = true;
        lifetime.Cancel();
        lifetime.Dispose();
        base.OnFormClosed(e);
    }
}
