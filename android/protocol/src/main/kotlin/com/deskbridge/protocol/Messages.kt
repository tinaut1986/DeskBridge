package com.deskbridge.protocol

import kotlinx.serialization.ExperimentalSerializationApi
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonElement

/**
 * Messages of the control channel (C# Messages.cs): newline-delimited JSON, camelCase, "type" as the
 * discriminator. Only what the Android app uses is here; a message of another type arrives as null and is
 * answered with an error, as the C# side does with types it does not know.
 */
@Serializable
sealed class ControlMessage

@OptIn(ExperimentalSerializationApi::class)
val ProtocolJson: Json = Json {
    classDiscriminator = "type"
    ignoreUnknownKeys = true
    explicitNulls = false
    encodeDefaults = true
    isLenient = false
}

// Handshake.

@Serializable @SerialName("challenge")
data class ChallengeMessage(val version: Int, val machine: String, val nonce: String) : ControlMessage()

@Serializable @SerialName("auth")
data class AuthMessage(
    val machine: String,
    val nonce: String,
    val proof: String,
    val link: Boolean = false,
    val video: String? = null,
) : ControlMessage()

@Serializable @SerialName("welcome")
data class WelcomeMessage(val proof: String) : ControlMessage()

@Serializable @SerialName("ok")
data object OkMessage : ControlMessage()

@Serializable @SerialName("error")
data class ErrorMessage(val message: String) : ControlMessage()

@Serializable @SerialName("ping")
data object PingMessage : ControlMessage()

// Displays and diagnostics.

/** A physical display (C# DisplayConfig). [scale] in percent. */
@Serializable
data class DisplayConfig(
    val id: String,
    val width: Int,
    val height: Int,
    val refreshHz: Int = 60,
    val x: Int = 0,
    val y: Int = 0,
    val scale: Int = 100,
)

@Serializable
data class DisplayRect(val x: Int, val y: Int, val width: Int, val height: Int)

@Serializable @SerialName("getDisplays")
data object GetDisplaysMessage : ControlMessage()

@Serializable @SerialName("displays")
data class DisplaysMessage(val machine: String, val displays: List<DisplayConfig>) : ControlMessage()

@Serializable @SerialName("getDiagnostics")
data object GetDiagnosticsMessage : ControlMessage()

@Serializable @SerialName("diagnostics")
data class DiagnosticsMessage(
    val machine: String,
    val version: String,
    val connectTo: String?,
    val controlPort: Int,
    val isCoordinator: Boolean,
    val config: JsonElement? = null,
    val log: List<String>,
    val appPath: String? = null,
    val acceptsUpdates: Boolean = false,
) : ControlMessage()

// Configuration and profiles (the configuration is kept as JSON: the coordinator owns it).

@Serializable @SerialName("configSync")
data class ConfigSyncMessage(val config: JsonElement) : ControlMessage()

@Serializable @SerialName("applyProfile")
data class ApplyProfileMessage(val config: JsonElement, val profile: String) : ControlMessage()

@Serializable @SerialName("applied")
data class AppliedMessage(val machine: String, val profile: String, val warnings: List<String>) : ControlMessage()

// Video.

@Serializable @SerialName("startVideo")
data class StartVideoMessage(
    val key: String,
    val send: Boolean,
    val display: String,
    val fps: Int,
    val bitrateMbps: Int,
) : ControlMessage()

@Serializable @SerialName("videoStarted")
data class VideoStartedMessage(
    val x: Int,
    val y: Int,
    val width: Int,
    val height: Int,
    val mirror: Boolean = false,
) : ControlMessage()

@Serializable @SerialName("stopVideo")
data class StopVideoMessage(val key: String? = null) : ControlMessage()

// Keyboard and mouse.

@Serializable
enum class PlacementSide {
    @SerialName("right") Right,
    @SerialName("left") Left,
    @SerialName("above") Above,
    @SerialName("below") Below,
}

@Serializable
enum class InputKind {
    @SerialName("mouseMove") MouseMove,
    @SerialName("mouseButton") MouseButton,
    @SerialName("wheel") Wheel,
    @SerialName("key") Key,
}

/**
 * MouseMove: x/y relative pixels. MouseButton: code 0 left, 1 right, 2 middle, 3 X1, 4 X2. Wheel: x is the delta
 * (120 per notch), extended = horizontal. Key: code is the scan code, extended the E0 prefix.
 */
@Serializable
data class InputEvent(
    val kind: InputKind,
    val x: Int = 0,
    val y: Int = 0,
    val code: Int = 0,
    val down: Boolean = false,
    val extended: Boolean = false,
)

@Serializable @SerialName("inputEnter")
data class InputEnterMessage(
    val side: PlacementSide,
    val fraction: Double,
    val x: Int? = null,
    val y: Int? = null,
    val peers: List<DisplayRect>? = null,
    val area: List<DisplayRect>? = null,
    val speedX: Double? = null,
    val speedY: Double? = null,
    val holes: List<DisplayRect>? = null,
    val areaDesk: List<DisplayRect>? = null,
    val holdsDrag: Boolean = false,
) : ControlMessage()

@Serializable @SerialName("inputEvents")
data class InputEventsMessage(val events: List<InputEvent>, val seq: Long? = null) : ControlMessage()

@Serializable @SerialName("inputCursor")
data class InputCursorMessage(val seq: Long, val x: Int, val y: Int) : ControlMessage()

@Serializable @SerialName("inputLeave")
data class InputLeaveMessage(val fraction: Double, val x: Int? = null, val y: Int? = null) : ControlMessage()

@Serializable @SerialName("inputRelease")
data object InputReleaseMessage : ControlMessage()

// Clipboard.

@Serializable @SerialName("clipboard")
data class ClipboardMessage(val text: String? = null, val pngBase64: String? = null) : ControlMessage()
