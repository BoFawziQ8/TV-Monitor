package local.tvmonitor.popup;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.Intent;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.util.Base64;
import android.view.View;
import android.widget.TextView;
import java.nio.charset.StandardCharsets;

public final class PopupActivity extends Activity {
    private final Handler handler = new Handler(Looper.getMainLooper());
    private AlertDialog popup;
    private final Runnable expire = this::finish;

    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        display(getIntent());
    }

    @Override protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        setIntent(intent);
        display(intent);
    }

    private void display(Intent intent) {
        String encoded = intent.getStringExtra("message64");
        if (encoded == null || encoded.length() > 2200) {
            finish();
            return;
        }
        String message;
        try {
            message = new String(Base64.decode(encoded, Base64.DEFAULT), StandardCharsets.UTF_8);
        } catch (IllegalArgumentException invalid) {
            finish();
            return;
        }
        if (message.trim().isEmpty() || message.length() > 400) {
            finish();
            return;
        }
        closePopup();
        popup = new AlertDialog.Builder(this, android.R.style.Theme_Material_Dialog_Alert)
            .setTitle("Message from TV Monitor")
            .setMessage(message)
            .setPositiveButton(android.R.string.ok, (dialog, which) -> finish())
            .create();
        popup.setOnDismissListener(dialog -> finish());
        popup.show();
        TextView body = popup.findViewById(android.R.id.message);
        if (body != null) body.setTextDirection(View.TEXT_DIRECTION_FIRST_STRONG);
        popup.getButton(AlertDialog.BUTTON_POSITIVE).requestFocus();
        handler.postDelayed(expire, 30000);
    }

    private void closePopup() {
        handler.removeCallbacks(expire);
        if (popup != null) {
            popup.setOnDismissListener(null);
            popup.dismiss();
            popup = null;
        }
    }

    @Override protected void onDestroy() {
        closePopup();
        super.onDestroy();
    }
}
