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

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED)
            requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), 1)

        if (settings.enabled && settings.isComplete && AgentState.status == AgentState.Status.Stopped)
            AgentService.start(this)
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

    private fun refresh() {
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
