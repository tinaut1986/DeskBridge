package com.deskbridge.app

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.net.ConnectivityManager
import android.net.Network
import android.os.IBinder

/**
 * Keeps the agent running while other apps are in front (the "own screen" mode needs that), with the
 * notification Android requires for it. Stopped from the app.
 */
class AgentService : Service() {
    companion object {
        private const val CHANNEL = "agent"
        private const val NOTIFICATION_ID = 1

        fun start(context: Context) = context.startForegroundService(Intent(context, AgentService::class.java))
        fun stop(context: Context) = context.stopService(Intent(context, AgentService::class.java))
    }

    private var agent: Agent? = null
    private val listener = { updateNotification() }
    private val network = object : ConnectivityManager.NetworkCallback() {
        override fun onAvailable(network: Network) {
            agent?.retryNow()
        }
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        getSystemService(NotificationManager::class.java).createNotificationChannel(
            NotificationChannel(CHANNEL, getString(R.string.notification_channel), NotificationManager.IMPORTANCE_LOW))
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val notification = notification()
        startForeground(NOTIFICATION_ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE)

        if (agent == null) {
            val settings = Settings(this)
            if (!settings.isComplete) {
                stopSelf()
                return START_NOT_STICKY
            }
            agent = Agent(this, settings).also { it.start() }
            AgentState.addListener(listener)
            getSystemService(ConnectivityManager::class.java).registerDefaultNetworkCallback(network)
        }
        return START_STICKY
    }

    override fun onDestroy() {
        agent?.stop()
        agent = null
        AgentState.removeListener(listener)
        try {
            getSystemService(ConnectivityManager::class.java).unregisterNetworkCallback(network)
        } catch (_: IllegalArgumentException) {
        }
        super.onDestroy()
    }

    private fun notification(): Notification {
        val open = PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE)
        val text = when (AgentState.status) {
            AgentState.Status.Connected -> getString(R.string.status_connected, AgentState.coordinator)
            AgentState.Status.Connecting -> getString(R.string.status_connecting)
            AgentState.Status.Stopped -> getString(R.string.status_stopped)
        }
        return Notification.Builder(this, CHANNEL)
            .setSmallIcon(R.drawable.ic_deskbridge)
            .setContentTitle(getString(R.string.app_name))
            .setContentText(text)
            .setContentIntent(open)
            .setOngoing(true)
            .build()
    }

    private fun updateNotification() =
        getSystemService(NotificationManager::class.java).notify(NOTIFICATION_ID, notification())
}
