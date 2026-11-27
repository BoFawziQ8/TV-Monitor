using System.IO.Pipes;
using NAudio.Wave;

using var server = new AnonymousPipeServerStream(PipeDirection.Out);
using var client = new AnonymousPipeClientStream(PipeDirection.In, server.GetClientHandleAsString());
using var ct = new CancellationTokenSource(3500);
var feeder = Task.Run(async () =>
{
    var frame = new byte[3840];
    while (!ct.IsCancellationRequested)
    {
        await server.WriteAsync(frame, ct.Token);
        await Task.Delay(20, ct.Token);
    }
}, ct.Token);
try { await PlaybackAudio.Play(client, ct.Token); }
catch (OperationCanceledException) when (ct.IsCancellationRequested) { Console.WriteLine("PASS Windows audio output survived the silent PCM test"); }
