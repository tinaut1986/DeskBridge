package com.deskbridge.app

import android.Manifest
import android.app.Activity
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.widget.Button
import android.widget.EditText
import android.widget.TextView
import android.widget.Toast
import com.deskbridge.protocol.ControlAuth
import com.deskbridge.protocol.ControlException

/** Settings (machine id, coordinator, shared key), start/stop, state and log. */
class MainActivity : Activity() {
    private lateinit var settings: Settings
    private lateinit var machine: EditText
    private lateinit var connectTo: EditText
    private lateinit var sharedKey: EditText
    private lateinit var toggle: Button
    private lateinit var status: TextView
    private lateinit var log: TextView
    private val listener = { refresh() }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)
        settings = Settings(this)
        machine = findViewById(R.id.machine)
        connectTo = findViewById(R.id.connect_to)
        sharedKey = findViewById(R.id.shared_key)
        toggle = findViewById(R.id.toggle)
        status = findViewById(R.id.status)
        log = findViewById(R.id.log)

        machine.setText(settings.machine)
        connectTo.setText(settings.connectTo)
        sharedKey.setText(settings.sharedKey)
        toggle.setOnClickListener { if (settings.enabled) stopAgent() else startAgent() }
        findViewById<Button>(R.id.overlay_allow).setOnClickListener {
            startActivity(android.content.Intent(android.provider.Settings.ACTION_MANAGE_OVERLAY_PERMISSION,
                android.net.Uri.parse("package:$packageName")))
        }

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED)
            requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), 1)

        findViewById<Button>(R.id.scan).setOnClickListener { scan() }

        if (!join(intent) && settings.enabled && settings.isComplete && AgentState.status == AgentState.Status.Stopped)
            AgentService.start(this)
    }

    override fun onNewIntent(intent: android.content.Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        join(intent)
    }

    /** Google's scanner (Play services); what it reads goes through [join] like a link opened from the camera. */
    private fun scan() {
        val options = com.google.mlkit.vision.codescanner.GmsBarcodeScannerOptions.Builder()
            .setBarcodeFormats(com.google.mlkit.vision.barcode.common.Barcode.FORMAT_QR_CODE)
            .build()
        com.google.mlkit.vision.codescanner.GmsBarcodeScanning.getClient(this, options).startScan()
            .addOnSuccessListener { code ->
                val text = code.rawValue
                if (text == null || !join(android.content.Intent(android.content.Intent.ACTION_VIEW, android.net.Uri.parse(text))))
                    Toast.makeText(this, R.string.qr_invalid, Toast.LENGTH_LONG).show()
            }
            .addOnFailureListener { e -> Toast.makeText(this, getString(R.string.qr_failed, e.message), Toast.LENGTH_LONG).show() }
    }

    /**
     * A deskbridge://join?c=<coordinator>&k=<shared key> link (the coordinator's QR): the fields are filled in and
     * the agent connects with them. False when the intent is not such a link.
     */
    private fun join(intent: android.content.Intent?): Boolean {
        val uri = intent?.data ?: return false
        if (uri.scheme != "deskbridge" || uri.host != "join") return false
        val coordinator = uri.getQueryParameter("c")?.trim().orEmpty()
        val key = uri.getQueryParameter("k")?.trim().orEmpty()
        try {
            ControlAuth.decodeKey(key)
        } catch (e: ControlException) {
            Toast.makeText(this, R.string.qr_invalid, Toast.LENGTH_LONG).show()
            return false
        }
        if (coordinator.isEmpty()) return false
        intent.data = null // handled once (not again on rotation)
        if (settings.enabled) stopAgent()
        connectTo.setText(coordinator)
        sharedKey.setText(key)
        Toast.makeText(this, R.string.qr_loaded, Toast.LENGTH_LONG).show()
        startAgent()
        return true
    }

    override fun onStart() {
        super.onStart()
        AgentState.addListener(listener)
        refresh()
    }

    override fun onStop() {
        AgentState.removeListener(listener)
        super.onStop()
    }

    private fun startAgent() {
        val key = sharedKey.text.toString().trim()
        try {
            ControlAuth.decodeKey(key)
        } catch (e: ControlException) {
            Toast.makeText(this, R.string.error_key, Toast.LENGTH_LONG).show()
            return
        }
        if (machine.text.isBlank() || connectTo.text.isBlank()) {
            Toast.makeText(this, R.string.error_missing, Toast.LENGTH_LONG).show()
            return
        }
        settings.machine = machine.text.toString()
        settings.connectTo = connectTo.text.toString()
        settings.sharedKey = key
        settings.enabled = true
        AgentService.start(this)
        refresh()
    }

    private fun stopAgent() {
        settings.enabled = false
        AgentService.stop(this)
        refresh()
    }

    override fun onResume() {
        super.onResume()
        refresh() // back from granting "Display over other apps"
    }

    private fun refresh() {
        findViewById<android.view.View>(R.id.overlay).visibility =
            if (android.provider.Settings.canDrawOverlays(this)) android.view.View.GONE else android.view.View.VISIBLE
        val running = settings.enabled
        toggle.setText(if (running) R.string.stop else R.string.start)
        listOf(machine, connectTo, sharedKey).forEach { it.isEnabled = !running }
        status.text = when (AgentState.status) {
            AgentState.Status.Connected -> getString(R.string.status_connected, AgentState.coordinator)
            AgentState.Status.Connecting ->
                getString(R.string.status_connecting) + (AgentState.lastError?.let { "\n$it" } ?: "")
            AgentState.Status.Stopped -> getString(R.string.status_stopped)
        }
        log.text = AgentState.log.asReversed().joinToString("\n")
    }
}
