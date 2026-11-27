package local.tvmonitor.popup;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.Service;
import android.content.Intent;
import android.media.AudioAttributes;
import android.media.AudioFormat;
import android.media.AudioTrack;
import android.net.LocalServerSocket;
import android.net.LocalSocket;
import android.os.Build;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;
import android.util.Log;
import java.io.DataInputStream;
import java.io.EOFException;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;

public final class TalkService extends Service {
    private static final String CHANNEL = "tvmonitor_talk";
    private static final int SAMPLE_RATE = 16000;
    private final Handler handler = new Handler(Looper.getMainLooper());
    private final Runnable deadline = this::stopSelf;
    private LocalServerSocket server;
    private LocalSocket client;
    private boolean stopped;
    private Thread worker;

    @Override public IBinder onBind(Intent intent) { return null; }

    @Override public int onStartCommand(Intent intent, int flags, int startId) {
        if (worker != null) return START_NOT_STICKY;
        String secret = intent == null ? null : intent.getStringExtra("session");
        if (secret == null || !secret.matches("[0-9A-F]{32}")) {
            stopSelf();
            return START_NOT_STICKY;
        }

        Notification.Builder notification;
        if (Build.VERSION.SDK_INT >= 26) {
            getSystemService(NotificationManager.class).createNotificationChannel(
                new NotificationChannel(CHANNEL, "TV Monitor intercom", NotificationManager.IMPORTANCE_LOW));
            notification = new Notification.Builder(this, CHANNEL);
        } else {
            notification = new Notification.Builder(this).setPriority(Notification.PRIORITY_LOW);
        }
        startForeground(2, notification.setContentTitle("TV Monitor intercom")
            .setContentText("Audio from the connected PC").setSmallIcon(android.R.drawable.ic_btn_speak_now)
            .setOngoing(true).build());

        handler.postDelayed(deadline, 12000);
        worker = new Thread(() -> receive(secret), "TV Monitor audio");
        worker.start();
        return START_NOT_STICKY;
    }

    private void receive(String secret) {
        AudioTrack audio = null;
        int frames = 0;
        try {
            LocalServerSocket listener;
            synchronized (this) {
                if (stopped) return;
                listener = new LocalServerSocket("tvmon_audio_" + secret);
                server = listener;
            }
            LocalSocket connection = listener.accept();
            synchronized (this) {
                if (stopped) {
                    connection.close();
                    return;
                }
                client = connection;
                server.close();
                server = null;
            }

            connection.setSoTimeout(4000);
            DataInputStream input = new DataInputStream(connection.getInputStream());
            byte[] signature = new byte[32];
            input.readFully(signature);
            if (!MessageDigest.isEqual(signature, secret.getBytes(StandardCharsets.US_ASCII))) return;

            int minimum = AudioTrack.getMinBufferSize(SAMPLE_RATE, AudioFormat.CHANNEL_OUT_MONO, AudioFormat.ENCODING_PCM_16BIT);
            if (minimum <= 0) throw new IOException("16 kHz mono playback unavailable");
            audio = new AudioTrack(
                new AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_MEDIA)
                    .setContentType(AudioAttributes.CONTENT_TYPE_SPEECH).build(),
                new AudioFormat.Builder().setSampleRate(SAMPLE_RATE).setChannelMask(AudioFormat.CHANNEL_OUT_MONO)
                    .setEncoding(AudioFormat.ENCODING_PCM_16BIT).build(),
                Math.max(minimum, 3200), AudioTrack.MODE_STREAM, 0);
            if (audio.getState() != AudioTrack.STATE_INITIALIZED) throw new IOException("AudioTrack did not initialize");

            audio.play();
            handler.removeCallbacks(deadline);
            handler.postDelayed(deadline, 90000);
            connection.getOutputStream().write(1);
            byte[] pcm = new byte[640];
            while (true) {
                input.readFully(pcm);
                int offset = 0;
                while (offset < pcm.length) {
                    int written = audio.write(pcm, offset, pcm.length - offset);
                    if (written <= 0) throw new IOException("AudioTrack write failed");
                    offset += written;
                }
                frames++;
            }
        } catch (EOFException ended) {
            // The PC closed the stream.
        } catch (IOException | RuntimeException failure) {
            synchronized (this) {
                if (!stopped) Log.w("TvMonitorTalk", "Intercom transport ended", failure);
            }
        } finally {
            if (audio != null) audio.release();
            Log.i("TvMonitorTalk", "Playback frames accepted: " + frames);
            stopSelf();
        }
    }

    @Override public synchronized void onDestroy() {
        stopped = true;
        handler.removeCallbacks(deadline);
        try { if (server != null) server.close(); } catch (IOException ignored) { }
        try { if (client != null) client.close(); } catch (IOException ignored) { }
        stopForeground(true);
        super.onDestroy();
    }
}
