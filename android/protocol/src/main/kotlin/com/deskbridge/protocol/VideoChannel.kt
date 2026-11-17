package com.deskbridge.protocol

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import java.io.Closeable
import java.io.DataInputStream
import java.io.EOFException
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.net.Socket
import java.nio.ByteBuffer
import java.nio.ByteOrder

/** What a video packet carries (C# VideoPacket). */
enum class VideoPacket(val code: Int) {
    Format(1), Frame(2), Pointer(3), PointerShape(4), AudioFormat(6), Audio(7), KeyFrameRequest(10), Shown(11), Stop(20);

    companion object {
        private val byCode = entries.associateBy { it.code }
        fun of(code: Int): VideoPacket? = byCode[code]
    }
}

/** The stream's picture as the sender produces it. On the wire in PascalCase (C# default serializer options). */
@Serializable
data class VideoFormat(
    @SerialName("Width") val width: Int,
    @SerialName("Height") val height: Int,
    @SerialName("Fps") val fps: Int,
    @SerialName("BitrateMbps") val bitrateMbps: Int,
    @SerialName("Gpu") val gpu: String = "",
    @SerialName("Encoder") val encoder: String = "",
) {
    companion object {
        private val json = Json { ignoreUnknownKeys = true }
        fun parse(body: ByteArray): VideoFormat = json.decodeFromString(serializer(), String(body, Charsets.UTF_8))
        fun encode(format: VideoFormat): ByteArray = json.encodeToString(serializer(), format).toByteArray(Charsets.UTF_8)
    }
}

/** One H.264 frame: capture time (100 ns units), key frame flag, Annex B bytes. */
class VideoFrame(val time: Long, val keyFrame: Boolean, val data: ByteArray, val offset: Int) {
    val size: Int get() = data.size - offset
}

/** Pointer picture: straight-alpha BGRA, width × height, with its hot spot. */
class PointerImage(val width: Int, val height: Int, val bgra: ByteArray, val hotX: Int, val hotY: Int)

object VideoBodies {
    private fun le(body: ByteArray) = ByteBuffer.wrap(body).order(ByteOrder.LITTLE_ENDIAN)

    fun frameHeader(time: Long, keyFrame: Boolean): ByteArray =
        ByteBuffer.allocate(9).order(ByteOrder.LITTLE_ENDIAN).putLong(time).put(if (keyFrame) 1 else 0).array()

    fun parseFrame(body: ByteArray): VideoFrame {
        if (body.size < 9) throw ProtocolException("Short video frame.")
        return VideoFrame(le(body).getLong(0), (body[8].toInt() and 1) != 0, body, 9)
    }

    data class Pointer(val x: Int, val y: Int, val visible: Boolean)

    fun parsePointer(body: ByteArray): Pointer {
        if (body.size < 9) throw ProtocolException("Short pointer packet.")
        val b = le(body)
        return Pointer(b.getInt(0), b.getInt(4), body[8].toInt() != 0)
    }

    /** Null when the body does not hold a sensible picture. Older senders send no hot spot. */
    fun parsePointerShape(body: ByteArray): PointerImage? {
        if (body.size < 8) return null
        val b = le(body)
        val width = b.getInt(0)
        val height = b.getInt(4)
        if (width !in 1..512 || height !in 1..512) return null
        val pixels = width * height * 4
        if (body.size < 8 + pixels) return null
        val bgra = body.copyOfRange(8, 8 + pixels)
        val hasHot = body.size >= 16 + pixels
        return PointerImage(width, height, bgra,
            if (hasHot) b.getInt(8 + pixels) else 0, if (hasHot) b.getInt(12 + pixels) else 0)
    }

    fun timeBody(time: Long): ByteArray = ByteBuffer.allocate(8).order(ByteOrder.LITTLE_ENDIAN).putLong(time).array()
}

/**
 * A video connection (C# VideoChannel): after the control handshake, binary packets of a 4-byte little-endian
 * length, then AES-GCM ciphertext (type byte + body) and tag, with a per-direction counter as nonce.
 * [send] may be called from any thread; [receive] from one thread.
 */
class VideoChannel internal constructor(
    private val socket: Socket?,
    input: InputStream,
    private val output: OutputStream,
    val remoteMachine: String,
    val streamKey: String,
    sendKey: ByteArray,
    receiveKey: ByteArray,
) : Closeable {
    companion object {
        const val MAX_PACKET = 16 * 1024 * 1024

        /** For tests: a channel over plain streams. */
        fun overStreams(input: InputStream, output: OutputStream, sendKey: ByteArray, receiveKey: ByteArray) =
            VideoChannel(null, input, output, "", "", sendKey, receiveKey)
    }

    private val input = DataInputStream(input)
    private val sendGcm = CounterGcm(sendKey)
    private val receiveGcm = CounterGcm(receiveKey)
    private val sendLock = Any()
    private val header = ByteArray(4)

    init {
        socket?.apply {
            tcpNoDelay = true
            receiveBufferSize = 256 * 1024
            sendBufferSize = 128 * 1024
        }
    }

    fun send(type: VideoPacket, body: ByteArray = ByteArray(0)) {
        val length = 1 + body.size
        if (length > MAX_PACKET) throw ProtocolException("Video packet too large ($length bytes).")
        val plain = ByteArray(length)
        plain[0] = type.code.toByte()
        body.copyInto(plain, 1)
        synchronized(sendLock) {
            val sealed = sendGcm.seal(plain)
            val packet = ByteBuffer.allocate(4 + sealed.size).order(ByteOrder.LITTLE_ENDIAN).putInt(length).put(sealed).array()
            output.write(packet)
            output.flush()
        }
    }

    /** The next packet (an unknown type is skipped), or null when the other side closed the connection. */
    fun receive(): Pair<VideoPacket, ByteArray>? {
        while (true) {
            if (!readFully(header, allowEnd = true)) return null
            val length = ByteBuffer.wrap(header).order(ByteOrder.LITTLE_ENDIAN).int
            if (length < 1 || length > MAX_PACKET) throw ProtocolException("Invalid video packet length $length.")
            val sealed = ByteArray(length + Crypto.TAG_BYTES)
            readFully(sealed, allowEnd = false)
            val plain = receiveGcm.open(sealed)
            val type = VideoPacket.of(plain[0].toInt() and 0xFF) ?: continue
            return type to plain.copyOfRange(1, plain.size)
        }
    }

    private fun readFully(buffer: ByteArray, allowEnd: Boolean): Boolean {
        var read = 0
        while (read < buffer.size) {
            val n = input.read(buffer, read, buffer.size - read)
            if (n < 0) {
                if (read == 0 && allowEnd) return false
                throw EOFException("The video connection closed in the middle of a packet.")
            }
            read += n
        }
        return true
    }

    override fun close() {
        try {
            socket?.close() ?: input.close()
        } catch (_: IOException) {
        }
    }
}
