package local.tvmonitor.popup;

import android.app.Activity;
import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.Service;
import android.content.Intent;
import android.media.AudioAttributes;
import android.media.AudioFormat;
import android.media.AudioPlaybackCaptureConfiguration;
import android.media.AudioRecord;
import android.media.projection.MediaProjection;
import android.media.projection.MediaProjectionManager;
import android.net.LocalServerSocket;
import android.net.LocalSocket;
import android.os.Build;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;
import android.util.Log;
import java.io.DataInputStream;
import java.io.IOException;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;

public final class CaptureService extends Service {
    private static final String CHANNEL = "tvmonitor_playback";
    private final Handler handler = new Handler(Looper.getMainLooper());
    private final Runnable deadline = this::stopSelf;
    private MediaProjection projection;
    private LocalServerSocket listener;
    private LocalSocket client;
    private volatile boolean stopped;
    private Thread worker;

    @Override public IBinder onBind(Intent intent) { return null; }

    @Override public int onStartCommand(Intent intent, int flags, int startId) {
        if (worker != null) return START_NOT_STICKY;
        String session = intent == null ? null : intent.getStringExtra("session");
        Intent consent = intent == null ? null : intent.getParcelableExtra("projection");
        if (Build.VERSION.SDK_INT < 29 || consent == null || session == null || !session.matches("[0-9A-F]{32}")) {
            stopSelf();
            return START_NOT_STICKY;
        }

        getSystemService(NotificationManager.class).createNotificationChannel(
            new NotificationChannel(CHANNEL, "TV Monitor playback capture", NotificationManager.IMPORTANCE_LOW));
        startForeground(3, new Notification.Builder(this, CHANNEL)
            .setContentTitle("TV Monitor is sharing TV sound")
            .setContentText("Playback audio is being sent to your PC")
            .setSmallIcon(android.R.drawable.ic_lock_silent_mode_off)
            .setOngoing(true).build());

        projection = getSystemService(MediaProjectionManager.class).getMediaProjection(Activity.RESULT_OK, consent);
        if (projection == null) {
            stopSelf();
            return START_NOT_STICKY;
        }
        projection.registerCallback(new MediaProjection.Callback() {
            @Override public void onStop() { stopSelf(); }
        }, handler);

        handler.postDelayed(deadline, 15000);
        MediaProjection granted = projection;
        worker = new Thread(() -> stream(session, granted), "TV Monitor playback audio");
        worker.start();
        return START_NOT_STICKY;
    }

    private void stream(String session, MediaProjection granted) {
        AudioRecord record = null;
        try {
            LocalServerSocket server;
            synchronized (this) {
                if (stopped) return;
                server = new LocalServerSocket("tvmon_playback_" + session);
                listener = server;
            }
            LocalSocket socket = server.accept();
            synchronized (this) {
                if (stopped) {
                    socket.close();
                    return;
                }
                client = socket;
                listener.close();
                listener = null;
            }
            socket.setSoTimeout(4000);
            byte[] signature = new byte[32];
            new DataInputStream(socket.getInputStream()).readFully(signature);
            if (!MessageDigest.isEqual(signature, session.getBytes(StandardCharsets.US_ASCII))) return;

            AudioPlaybackCaptureConfiguration capture = new AudioPlaybackCaptureConfiguration.Builder(granted)
                .addMatchingUsage(AudioAttributes.USAGE_MEDIA)
                .addMatchingUsage(AudioAttributes.USAGE_GAME)
                .addMatchingUsage(AudioAttributes.USAGE_UNKNOWN)
                .build();
            AudioFormat format = new AudioFormat.Builder().setSampleRate(48000)
                .setChannelMask(AudioFormat.CHANNEL_IN_STEREO).setEncoding(AudioFormat.ENCODING_PCM_16BIT).build();
            int minimum = AudioRecord.getMinBufferSize(48000, AudioFormat.CHANNEL_IN_STEREO, AudioFormat.ENCODING_PCM_16BIT);
            if (minimum <= 0) throw new IOException("48 kHz stereo audio capture unavailable");
            record = new AudioRecord.Builder().setAudioPlaybackCaptureConfig(capture).setAudioFormat(format)
                .setBufferSizeInBytes(Math.max(minimum, 7680)).build();
            record.startRecording();
            if (record.getRecordingState() != AudioRecord.RECORDSTATE_RECORDING)
                throw new IOException("Playback capture did not start");

            handler.removeCallbacks(deadline);
            OutputStream output = socket.getOutputStream();
            output.write(1);
            byte[] buffer = new byte[3840];
            while (!stopped) {
                int count = record.read(buffer, 0, buffer.length, AudioRecord.READ_BLOCKING);
                if (count < 0) throw new IOException("Playback capture stopped: " + count);
                if (count > 0) output.write(buffer, 0, count);
            }
        } catch (IOException | RuntimeException error) {
            if (!stopped) Log.w("TvMonitorCapture", "Playback capture ended", error);
        } finally {
            if (record != null) record.release();
            stopSelf();
        }
    }

    @Override public synchronized void onDestroy() {
        stopped = true;
        handler.removeCallbacks(deadline);
        try { if (listener != null) listener.close(); } catch (IOException ignored) { }
        try { if (client != null) client.close(); } catch (IOException ignored) { }
        if (projection != null) projection.stop();
        stopForeground(true);
        super.onDestroy();
    }
}
