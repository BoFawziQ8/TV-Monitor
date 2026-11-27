using System.Net;
using System.Net.WebSockets;
using System.Text;

enum RemoteKey
{
    Up, Down, Left, Right, Ok, Back, Home, Input, Settings,
    VolumeDown, VolumeUp, Mute, ChannelDown, ChannelUp, Play, Pause,
    Zero, One, Two, Three, Four, Five, Six, Seven, Eight, Nine
}

static partial class Network
{
    internal sealed class RemoteSession(Tv tv) : IDisposable
    {
        private ClientWebSocket? pointer;
        private bool androidConnected;

        internal async Task Connect(CancellationToken ct)
        {
            if (tv.Platform == Platform.Android)
            {
                if (androidConnected) return;
                var state = await Read(tv, ct);
                if (state.Power != Power.On)
                    throw new IOException(Tf($"Turn on and connect {tv.Name}. {state.Detail}"));
                androidConnected = true;
                return;
            }

            if (tv.Platform != Platform.WebOS) throw new IOException(T("Remote control is unavailable for this TV type."));
            if (pointer?.State == WebSocketState.Open) return;
            pointer?.Dispose();
            pointer = null;
            var address = await WithWebOs(tv, async (socket, token) =>
            {
                var reply = await Exchange(socket, "remote", "com.webos.service.networkinput/getPointerInputSocket", new { }, token);
                if (Rejected(reply))
                    throw new IOException(Tf($"LG declined remote access. Try Pair LG again. {Rejection(reply)}"));
                var path = Str(Child(reply, "payload"), "socketPath");
                if (!Uri.TryCreate(path, UriKind.Absolute, out var uri) ||
                    uri.Scheme != "ws" || uri.UserInfo.Length != 0 ||
                    TvAddress.LocalUri(uri, IPAddress.Parse(tv.Address)) is not { } local)
                    throw new IOException(T("The TV did not provide a remote socket at its own IP address."));
                return local;
            }, ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(5000);
            var connection = new ClientWebSocket();
            connection.Options.Proxy = null;
            try
            {
                await connection.ConnectAsync(address, timeout.Token);
                ct.ThrowIfCancellationRequested();
                pointer = connection;
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        internal async Task Press(RemoteKey key, CancellationToken ct)
        {
            try
            {
                await Connect(ct);
                if (tv.Platform == Platform.Android)
                {
                    await AndroidKey(tv, AndroidName(key), ct);
                    return;
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(3000);
                var packet = Encoding.ASCII.GetBytes($"type:button\nname:{WebOsName(key)}\n\n");
                await pointer!.SendAsync(packet.AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            pointer?.Dispose();
            pointer = null;
            androidConnected = false;
        }

        private static string AndroidName(RemoteKey key) => "KEYCODE_" + (key switch
        {
            RemoteKey.Up => "DPAD_UP", RemoteKey.Down => "DPAD_DOWN",
            RemoteKey.Left => "DPAD_LEFT", RemoteKey.Right => "DPAD_RIGHT", RemoteKey.Ok => "DPAD_CENTER",
            RemoteKey.Input => "TV_INPUT", RemoteKey.VolumeDown => "VOLUME_DOWN", RemoteKey.VolumeUp => "VOLUME_UP",
            RemoteKey.Mute => "VOLUME_MUTE", RemoteKey.ChannelDown => "CHANNEL_DOWN", RemoteKey.ChannelUp => "CHANNEL_UP",
            RemoteKey.Play => "MEDIA_PLAY", RemoteKey.Pause => "MEDIA_PAUSE",
            >= RemoteKey.Zero and <= RemoteKey.Nine => ((int)(key - RemoteKey.Zero)).ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => key.ToString().ToUpperInvariant()
        });

        private static string WebOsName(RemoteKey key) => key switch
        {
            RemoteKey.Ok => "ENTER", RemoteKey.Settings => "ADVANCE_SETTING", RemoteKey.Input => "INPUT_HUB",
            >= RemoteKey.Zero and <= RemoteKey.Nine => ((int)(key - RemoteKey.Zero)).ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => key.ToString().ToUpperInvariant()
        };
    }
}
