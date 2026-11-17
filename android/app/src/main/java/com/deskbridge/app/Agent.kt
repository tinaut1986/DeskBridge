package com.deskbridge.app

import android.content.Context
import android.hardware.display.DisplayManager
import android.os.Handler
import android.os.Looper
import android.util.DisplayMetrics
import android.view.Display
import com.deskbridge.protocol.AppliedMessage
import com.deskbridge.protocol.ApplyProfileMessage
import com.deskbridge.protocol.ConfigSyncMessage
import com.deskbridge.protocol.ControlAuth
import com.deskbridge.protocol.ControlClient
import com.deskbridge.protocol.ControlException
import com.deskbridge.protocol.ControlMessage
import com.deskbridge.protocol.ControlSession
import com.deskbridge.protocol.DiagnosticsMessage
import com.deskbridge.protocol.DisplayConfig
import com.deskbridge.protocol.DisplaysMessage
import com.deskbridge.protocol.ErrorMessage
import com.deskbridge.protocol.GetDiagnosticsMessage
import com.deskbridge.protocol.GetDisplaysMessage
import com.deskbridge.protocol.OkMessage
import com.deskbridge.protocol.PingMessage
import com.deskbridge.protocol.ProtocolJson
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import kotlin.math.max
import kotlin.math.min
import kotlin.math.roundToInt

/** What the screen shows about the agent; updated on the main thread. */
object AgentState {
    enum class Status { Stopped, Connecting, Connected }

    @Volatile var status = Status.Stopped
        private set
    @Volatile var coordinator: String? = null
        private set
    @Volatile var lastError: String? = null
        private set

    private val lines = ArrayDeque<String>()
    private val listeners = mutableSetOf<() -> Unit>()
    private val main = Handler(Looper.getMainLooper())
    private val time = SimpleDateFormat("HH:mm:ss", Locale.ROOT)

    val log: List<String> get() = synchronized(lines) { lines.toList() }

    fun addListener(listener: () -> Unit) = main.post { listeners += listener }
    fun removeListener(listener: () -> Unit) = main.post { listeners -= listener }

    internal fun set(status: Status, coordinator: String? = null, error: String? = lastError) {
        this.status = status
        this.coordinator = coordinator
        this.lastError = error
        changed()
    }

    internal fun log(line: String) {
        synchronized(lines) {
            lines.addLast("${time.format(Date())}  $line")
            while (lines.size > 200) lines.removeFirst()
        }
        android.util.Log.i("DeskBridge", line)
        changed()
    }

    private fun changed() = main.post { listeners.toList().forEach { it() } }
}

/**
 * The DeskBridge agent of this device (C# AgentHost + ReverseLink): it connects to the coordinator, keeps that
 * session open (pinging it, reconnecting when it drops) and answers what the coordinator asks over it.
 */
class Agent(private val context: Context, private val settings: Settings) {
    companion object {
        private const val PING_EVERY_MS = 15_000L
        private const val PING_TIMEOUT_MS = 10_000L
        private const val MAX_BACKOFF_MS = 30_000L
    }

    @Volatile private var stopped = false
    @Volatile private var session: ControlSession? = null
    private var thread: Thread? = null
    private val wake = Object()

    fun start() {
        stopped = false
        thread = Thread(::run, "deskbridge-link").apply { isDaemon = true; start() }
    }

    fun stop() {
        stopped = true
        session?.close()
        synchronized(wake) { wake.notifyAll() }
        thread?.interrupt()
        AgentState.set(AgentState.Status.Stopped, error = null)
    }

    /** Try again now (the network came back) instead of waiting for the backoff. */
    fun retryNow() = synchronized(wake) { wake.notifyAll() }

    private fun sleep(ms: Long) = synchronized(wake) {
        try {
            wake.wait(ms)
        } catch (_: InterruptedException) {
        }
    }

    private fun run() {
        var backoff = 2_000L
        var lastError: String? = null
        while (!stopped) {
            val (host, port) = ControlClient.parseAddress(settings.connectTo)
            AgentState.set(AgentState.Status.Connecting, error = lastError)
            try {
                val key = ControlAuth.decodeKey(settings.sharedKey)
                val s = ControlClient.open(host, port, settings.machine, key, ::handle, AgentState::log, asLink = true)
                session = s
                lastError = null
                backoff = 2_000L
                AgentState.log(context.getString(R.string.log_connected, s.remoteMachine, "$host:$port"))
                AgentState.set(AgentState.Status.Connected, s.remoteMachine, error = null)
                keepAlive(s)
                s.close()
                session = null
                if (!stopped) AgentState.log(context.getString(R.string.log_lost, s.remoteMachine))
            } catch (e: ControlException) {
                if (e.message != lastError) {
                    lastError = e.message
                    AgentState.log(e.message ?: e.toString())
                }
            } catch (e: Exception) {
                lastError = e.toString()
                AgentState.log(lastError)
            }
            if (stopped) break
            AgentState.set(AgentState.Status.Connecting, error = lastError)
            sleep(backoff)
            backoff = min(backoff * 2, MAX_BACKOFF_MS)
        }
    }

    private fun keepAlive(s: ControlSession) {
        while (s.isOpen && !stopped) {
            if (s.awaitClosed(PING_EVERY_MS) || stopped) return
            try {
                s.request(PingMessage, PING_TIMEOUT_MS)
            } catch (e: Exception) {
                return
            }
        }
    }

    // Requests from the coordinator.

    private fun handle(from: String, request: ControlMessage): ControlMessage = when (request) {
        is GetDisplaysMessage -> DisplaysMessage(settings.machine, listOf(display()))
        is GetDiagnosticsMessage -> DiagnosticsMessage(
            machine = settings.machine,
            version = "android-" + BuildInfo.versionName(context),
            connectTo = settings.connectTo,
            controlPort = ControlClient.parseAddress(settings.connectTo).second,
            isCoordinator = false,
            config = settings.config?.let { ProtocolJson.parseToJsonElement(it) },
            log = AgentState.log.takeLast(100),
        )
        is ConfigSyncMessage ->
            if (coordinatorOf(request.config) != from) ErrorMessage("Only the coordinator can change the configuration.")
            else {
                settings.config = request.config.toString()
                OkMessage
            }
        is ApplyProfileMessage -> {
            if (coordinatorOf(request.config) != from)
                ErrorMessage("Only the coordinator ($from) can apply profiles.")
            else {
                settings.config = request.config.toString()
                AgentState.log(context.getString(R.string.log_profile, request.profile))
                // Nothing to set up here yet: the modes (extra display, own screen) come in later phases.
                AppliedMessage(settings.machine, request.profile, listOf(context.getString(R.string.warning_profiles_pending)))
            }
        }
        else -> ErrorMessage("This Android device does not handle ${request::class.simpleName} yet.")
    }

    /** The machine with HasInput in a configuration. */
    private fun coordinatorOf(config: JsonElement): String? = try {
        config.jsonObject["machines"]?.jsonArray
            ?.map { it.jsonObject }
            ?.firstOrNull { (it["hasInput"] as? JsonPrimitive)?.booleanOrNull == true }
            ?.let { (it["id"] as? JsonPrimitive)?.content }
    } catch (e: IllegalArgumentException) {
        null
    }

    /**
     * This device's screen as a DeskBridge display: in landscape (the orientation the modes use for now), its
     * refresh rate, and its density as a Windows-like scale (160 dpi = 100 %).
     */
    private fun display(): DisplayConfig {
        val display = context.getSystemService(DisplayManager::class.java).getDisplay(Display.DEFAULT_DISPLAY)
        val mode = display.mode
        val w = max(mode.physicalWidth, mode.physicalHeight)
        val h = min(mode.physicalWidth, mode.physicalHeight)
        val dpi = context.resources.displayMetrics.densityDpi
        val scale = (dpi * 100.0 / DisplayMetrics.DENSITY_DEFAULT / 25).roundToInt() * 25 // in 25 % steps, like Windows
        return DisplayConfig("1", w, h, mode.refreshRate.roundToInt(), 0, 0, scale.coerceIn(100, 500))
    }
}

object BuildInfo {
    fun versionName(context: Context): String =
        context.packageManager.getPackageInfo(context.packageName, 0).versionName ?: "?"
}
