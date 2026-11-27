using NAudio.Wave;

static class PlaybackAudio
{
    internal static async Task Play(Stream pcm, CancellationToken ct, Action? firstSound = null)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var buffer = new BufferedWaveProvider(new WaveFormat(48000, 16, 2))
        {
            BufferDuration = TimeSpan.FromMilliseconds(220), DiscardOnBufferOverflow = true
        };
        using var output = new WaveOutEvent { DeviceNumber = -1, DesiredLatency = 90 };
        Exception? failure = null;
        void OnStopped(object? sender, StoppedEventArgs args)
        {
            failure = args.Exception;
            try { lifetime.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        output.PlaybackStopped += OnStopped;
        output.Init(buffer);
        output.Play();
        try
        {
            var frame = new byte[3840];
            while (true)
            {
                await pcm.ReadExactlyAsync(frame, lifetime.Token);
                buffer.AddSamples(frame, 0, frame.Length);
                if (firstSound is not null && System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(frame)
                    .ContainsAnyExcept((short)0))
                {
                    firstSound();
                    firstSound = null;
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException(failure is null ? T("Windows stopped the audio output.")
                : T("Windows audio output failed. ") + failure.Message, failure);
        }
        catch (EndOfStreamException)
        {
            throw new IOException(T("The TV stopped sharing sound. Reopen Watch live to start a new audio session."));
        }
        finally
        {
            output.PlaybackStopped -= OnStopped;
            output.Stop();
        }
    }
}
