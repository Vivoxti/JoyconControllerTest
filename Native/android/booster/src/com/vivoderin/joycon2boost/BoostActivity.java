package com.vivoderin.joycon2boost;

import android.Manifest;
import android.app.Activity;
import android.bluetooth.BluetoothAdapter;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.TextView;

/** Starts a BLE parameter helper; contains no Nintendo handshake or controller input code. */
public final class BoostActivity extends Activity {
    private final Handler handler = new Handler();
    private TextView status;
    private boolean fromBrowser;
    private final Runnable refresh = new Runnable() {
        @Override public void run() { status.setText(BoostService.status); handler.postDelayed(this, 1000); }
    };
    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        configureLaunch();
        LinearLayout layout = new LinearLayout(this);
        layout.setOrientation(LinearLayout.VERTICAL);
        int padding = (int)(20 * getResources().getDisplayMetrics().density);
        layout.setPadding(padding, padding, padding, padding);
        TextView title = new TextView(this);
        title.setText("Joy-Con 2 Boost"); title.setTextSize(24); layout.addView(title);
        TextView help = new TextView(this);
        help.setText("In the game, tap Boost before connecting your Joy-Con. This app starts and returns you to Chrome automatically.\n\nFirst use: allow Nearby devices. A notification stays visible while boost is running; use its Stop action when finished.");
        help.setTextSize(17); layout.addView(help);
        Button start = new Button(this); start.setText("Start boost"); layout.addView(start);
        start.setOnClickListener(v -> startBoost());
        Button stop = new Button(this); stop.setText("Stop boost"); layout.addView(stop);
        stop.setOnClickListener(v -> stopService(new Intent(this, BoostService.class)));
        status = new TextView(this); status.setTextSize(15); layout.addView(status);
        setContentView(layout);
        if (fromBrowser) startBoost();
    }
    private void configureLaunch() {
        android.net.Uri data = getIntent().getData();
        fromBrowser = data != null && "joycon2boost".equals(data.getScheme()) && "start".equals(data.getHost());
        if (fromBrowser && data.getQueryParameter("service") != null) {
            try {
                String service = java.util.UUID.fromString(data.getQueryParameter("service")).toString();
                // Generic target service from the game's C# core; no Nintendo protocol in this APK.
                getSharedPreferences("boost", MODE_PRIVATE).edit().putString("service", service).putString("controllers", "").apply();
            } catch (IllegalArgumentException ignored) { fromBrowser = false; }
        }
        String configured = getIntent().getStringExtra("controllers");
        if (configured != null) {
            StringBuilder valid = new StringBuilder();
            for (String address : configured.split(",")) {
                address = address.trim().toUpperCase(java.util.Locale.ROOT);
                if (!BluetoothAdapter.checkBluetoothAddress(address)) continue;
                if (valid.length() > 0) valid.append(',');
                valid.append(address);
            }
            getSharedPreferences("boost", MODE_PRIVATE).edit().putString("controllers", valid.toString()).apply();
        }
    }
    @Override protected void onNewIntent(Intent intent) { super.onNewIntent(intent); setIntent(intent); configureLaunch(); if (fromBrowser) startBoost(); }
    private void startBoost() {
        if (Build.VERSION.SDK_INT >= 31 && checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(new String[] { Manifest.permission.BLUETOOTH_CONNECT }, 1); return;
        }
        try { startForegroundService(new Intent(this, BoostService.class)); if (fromBrowser) finish(); }
        catch (RuntimeException error) { BoostService.status = "Could not start: " + error.getMessage(); }
    }
    @Override public void onRequestPermissionsResult(int request, String[] permissions, int[] results) {
        super.onRequestPermissionsResult(request, permissions, results);
        if (request == 1 && results.length > 0 && results[0] == PackageManager.PERMISSION_GRANTED) startBoost();
        else BoostService.status = "Nearby devices permission denied. Tap Start to retry or allow it in app settings.";
    }
    @Override protected void onResume() { super.onResume(); handler.post(refresh); }
    @Override protected void onPause() { handler.removeCallbacks(refresh); super.onPause(); }
}
