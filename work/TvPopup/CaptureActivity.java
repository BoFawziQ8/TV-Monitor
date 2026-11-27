package local.tvmonitor.popup;

import android.Manifest;
import android.app.Activity;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.media.projection.MediaProjectionManager;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;

public final class CaptureActivity extends Activity {
    private static final int AUDIO = 1;
    private static final int PROJECTION = 2;
    private final Handler handler = new Handler(Looper.getMainLooper());
    private String session;

    @Override protected void onCreate(Bundle state) {
        super.onCreate(state);
        session = getIntent().getStringExtra("session");
        if (Build.VERSION.SDK_INT < 29 || session == null || !session.matches("[0-9A-F]{32}")) {
            finish();
            return;
        }
        handler.postDelayed(this::finish, 60000);
        if (state != null) return;
        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED)
            requestPermissions(new String[] { Manifest.permission.RECORD_AUDIO }, AUDIO);
        else requestProjection();
    }

    private void requestProjection() {
        MediaProjectionManager manager = getSystemService(MediaProjectionManager.class);
        startActivityForResult(manager.createScreenCaptureIntent(), PROJECTION);
    }

    @Override public void onRequestPermissionsResult(int request, String[] permissions, int[] results) {
        super.onRequestPermissionsResult(request, permissions, results);
        if (request == AUDIO && results.length == 1 && results[0] == PackageManager.PERMISSION_GRANTED)
            requestProjection();
        else finish();
    }

    @Override protected void onActivityResult(int request, int result, Intent data) {
        super.onActivityResult(request, result, data);
        if (request == PROJECTION && result == RESULT_OK && data != null && !isFinishing()) {
            startForegroundService(new Intent(this, CaptureService.class)
                .putExtra("session", session).putExtra("projection", data));
        }
        finish();
    }

    @Override protected void onDestroy() {
        handler.removeCallbacksAndMessages(null);
        super.onDestroy();
    }
}
