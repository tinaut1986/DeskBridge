package com.deskbridge.app

import android.graphics.Bitmap
import android.media.MediaCodec
import android.media.MediaFormat
import android.os.Build
import android.os.Handler
import android.os.HandlerThread
import android.view.Surface
import com.deskbridge.protocol.VideoBodies
import com.deskbridge.protocol.VideoChannel
import com.deskbridge.protocol.VideoFormat
import com.deskbridge.protocol.VideoFrame
import com.deskbridge.protocol.VideoPacket
import java.io.IOException
import java.util.concurrent.ConcurrentHashMap

/**
 * One screen of the coordinator shown on this device (C# VideoReceiver): packets come in on a thread of their own,
 * H.264 frames go to the hardware decoder, which draws straight onto the surface of [VideoActivity]; the pointer
 * goes to the activity, which draws it on top. Frames that arrive while there is no surface (the activity is
 * starting, or in the background) are dropped, and a key frame is asked for once there is one again.
 */
class VideoStream(
    val key: String,
    val from: String,
    private val channel: VideoChannel,
    private val log: (String) -> Unit,
) {
    /** The screen showing it (main thread calls only). */
    interface View {
        fun onFormat(width: Int, height: Int)
        fun onPointer(x: Int, y: Int, visible: Boolean)
        fun onPointerShape(picture: Bitmap)
        fun onEnded()
    }

    @Volatile var view: View? = null
        set(value) {
            field = value
            // A screen that opens late gets what it missed.
            if (value != null) main.post {
                format?.let { value.onFormat(it.width, it.height) }
                shape?.let { value.onPointerShape(it) }
                pointer?.let { value.onPointer(it.x, it.y, it.visible) }
            }
        }

    @Volatile private var format: VideoFormat? = null
    @Volatile private var shape: Bitmap? = null
    @Volatile private var pointer: VideoBodies.Pointer? = null
    @Volatile private var closed = false

    private val main = android.os.Handler(android.os.Looper.getMainLooper())
    private val lock = Any()
    private var surface: Surface? = null
    private var codec: MediaCodec? = null
    private val freeInputs = ArrayDeque<Int>()
    private val pending = ArrayDeque<VideoFrame>()
    private var waitingForKey = true
    private var lastKeyRequest = 0L
    private val decoderThread = HandlerThread("deskbridge-decoder").apply { start() }

    // For the log, every 10 s (like the coordinator's).
    private var shown = 0L
    private var shownSince = 0L
    private var bytesSince = 0L
    private var dropped = 0L
    private var reportAt = 0L

    fun start() = Thread(::receive, "deskbridge-video").apply { isDaemon = true }.start()

    /** The activity's surface is ready: decode onto it. */
    fun attach(surface: Surface) = synchronized(lock) {
        this.surface = surface
        restartDecoder()
    }

    /** The surface is going away (the activity stops): nothing is decoded until there is one again. */
    fun detach() = synchronized(lock) {
        stopDecoder()
        surface = null
    }

    /** Stops showing it: the coordinator is told, and asks for it again only if the profile wants it. */
    fun close() {
        if (closed) return
        closed = true
        try {
            channel.send(VideoPacket.Stop)
        } catch (_: IOException) {
        }
        channel.close()
    }

    private fun receive() {
        log("[video] showing $from's screen")
        reportAt = System.currentTimeMillis() + 10_000
        try {
            while (!closed) {
                val (type, body) = channel.receive() ?: break
                when (type) {
                    VideoPacket.Stop -> break
                    VideoPacket.Format -> onFormat(VideoFormat.parse(body))
                    VideoPacket.Frame -> onFrame(VideoBodies.parseFrame(body))
                    VideoPacket.Pointer -> VideoBodies.parsePointer(body).let { p ->
                        pointer = p
                        main.post { view?.onPointer(p.x, p.y, p.visible) }
                    }
                    VideoPacket.PointerShape -> VideoBodies.parsePointerShape(body)?.let { image ->
                        val picture = toBitmap(image.width, image.height, image.bgra)
                        shape = picture
                        main.post { view?.onPointerShape(picture) }
                    }
                    else -> {}
                }
                report()
            }
        } catch (e: Exception) {
            if (!closed) log("[video] $from: ${e.message ?: e}")
        } finally {
            closed = true
            channel.close()
            synchronized(lock) { stopDecoder() }
            decoderThread.quitSafely()
            VideoStreams.ended(this)
            log("[video] $from's screen is no longer shown")
            main.post { view?.onEnded() }
        }
    }

    private fun onFormat(f: VideoFormat) {
        format = f
        log("[video] $from: ${f.width}x${f.height}@${f.fps}, ${f.gpu}, ${f.encoder}")
        main.post { view?.onFormat(f.width, f.height) }
        synchronized(lock) { restartDecoder() }
    }

    private fun onFrame(frame: VideoFrame) {
        bytesSince += frame.size
        synchronized(lock) {
            if (codec == null) {
                waitingForKey = true // nowhere to show it: from the next key frame once there is
                return
            }
            if (waitingForKey) {
                if (!frame.keyFrame) {
                    requestKeyFrame()
                    return
                }
                waitingForKey = false
            }
            // The decoder is not keeping up (or stuck): start over from a fresh key frame.
            if (pending.size > 30) {
                dropped += pending.size
                pending.clear()
                waitingForKey = true
                requestKeyFrame()
                return
            }
            pending.addLast(frame)
            pump()
        }
    }

    /** Under [lock]: frames into the decoder's free input buffers. */
    private fun pump() {
        val c = codec ?: return
        while (freeInputs.isNotEmpty() && pending.isNotEmpty()) {
            val frame = pending.removeFirst()
            val index = freeInputs.removeFirst()
            try {
                val buffer = c.getInputBuffer(index) ?: continue
                if (frame.size > buffer.capacity()) {
                    log("[video] a frame of ${frame.size} bytes does not fit the decoder (${buffer.capacity()})")
                    c.queueInputBuffer(index, 0, 0, 0, 0)
                    waitingForKey = true
                    requestKeyFrame()
                    continue
                }
                buffer.clear()
                buffer.put(frame.data, frame.offset, frame.size)
                c.queueInputBuffer(index, 0, frame.size, frame.time / 10, 0) // 100 ns units → µs
            } catch (e: IllegalStateException) {
                return // the decoder is being stopped
            }
        }
    }

    /** Under [lock]: a decoder for the current format on the current surface (none while either is missing). */
    private fun restartDecoder() {
        stopDecoder()
        val f = format ?: return
        val s = surface ?: return
        if (!s.isValid) return
        try {
            val mediaFormat = MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, f.width, f.height).apply {
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) setInteger(MediaFormat.KEY_LOW_LATENCY, 1)
                setInteger(MediaFormat.KEY_PRIORITY, 0) // real time
                setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, f.width * f.height * 3 / 2)
            }
            val c = MediaCodec.createDecoderByType(MediaFormat.MIMETYPE_VIDEO_AVC)
            c.setCallback(callback, Handler(decoderThread.looper))
            c.configure(mediaFormat, s, null, 0)
            c.setVideoScalingMode(MediaCodec.VIDEO_SCALING_MODE_SCALE_TO_FIT)
            c.start()
            codec = c
            waitingForKey = true
            requestKeyFrame(force = true)
            log("[video] decoding on ${c.name}")
        } catch (e: Exception) {
            log("[video] the decoder could not start: ${e.message ?: e}")
            codec = null
        }
    }

    /** Under [lock]. */
    private fun stopDecoder() {
        val c = codec ?: return
        codec = null
        freeInputs.clear()
        pending.clear()
        try {
            c.stop()
        } catch (_: IllegalStateException) {
        }
        c.release()
    }

    private val callback = object : MediaCodec.Callback() {
        override fun onInputBufferAvailable(c: MediaCodec, index: Int) {
            synchronized(lock) {
                if (c !== codec) return
                freeInputs.addLast(index)
                pump()
            }
        }

        override fun onOutputBufferAvailable(c: MediaCodec, index: Int, info: MediaCodec.BufferInfo) {
            synchronized(lock) {
                if (c !== codec) return
                try {
                    c.releaseOutputBuffer(index, true) // onto the surface, at once
                } catch (_: IllegalStateException) {
                    return
                }
            }
            shown++
            shownSince++
            // Now and then, when this picture was captured: the coordinator works out the delay from it.
            if (shown % 15 == 0L) send(VideoPacket.Shown, VideoBodies.timeBody(info.presentationTimeUs * 10))
        }

        override fun onError(c: MediaCodec, e: MediaCodec.CodecException) {
            log("[video] decoder error, starting it again: ${e.diagnosticInfo}")
            synchronized(lock) { if (c === codec) restartDecoder() }
        }

        override fun onOutputFormatChanged(c: MediaCodec, format: MediaFormat) {}
    }

    /** At most one a second, unless [force]d (a new decoder). */
    private fun requestKeyFrame(force: Boolean = false) {
        val now = System.currentTimeMillis()
        if (!force && now - lastKeyRequest < 1000) return
        lastKeyRequest = now
        send(VideoPacket.KeyFrameRequest)
    }

    private fun send(type: VideoPacket, body: ByteArray = ByteArray(0)) {
        if (closed) return
        // Never on the decoder's or the receiving thread for long: a small packet, sent as is.
        try {
            channel.send(type, body)
        } catch (_: IOException) {
        }
    }

    private fun report() {
        val now = System.currentTimeMillis()
        if (now < reportAt) return
        val seconds = (now - reportAt + 10_000) / 1000.0
        log(String.format(java.util.Locale.ROOT, "[video] %s: %.1f fps shown, %.1f Mbps%s", from,
            shownSince / seconds, bytesSince * 8 / seconds / 1_000_000, if (dropped > 0) ", $dropped frames dropped" else ""))
        shownSince = 0
        bytesSince = 0
        dropped = 0
        reportAt = now + 10_000
    }

    /** Straight-alpha BGRA (the sender's pointer picture) to a bitmap. */
    private fun toBitmap(width: Int, height: Int, bgra: ByteArray): Bitmap {
        val pixels = IntArray(width * height)
        for (i in pixels.indices) {
            val b = bgra[i * 4].toInt() and 0xFF
            val g = bgra[i * 4 + 1].toInt() and 0xFF
            val r = bgra[i * 4 + 2].toInt() and 0xFF
            val a = bgra[i * 4 + 3].toInt() and 0xFF
            pixels[i] = (a shl 24) or (r shl 16) or (g shl 8) or b
        }
        return Bitmap.createBitmap(pixels, width, height, Bitmap.Config.ARGB_8888)
    }
}

/** The coordinator's screens shown on this device, by stream key. */
object VideoStreams {
    private val streams = ConcurrentHashMap<String, VideoStream>()

    fun add(stream: VideoStream) {
        streams.put(stream.key, stream)?.close()
        stream.start()
    }

    fun get(key: String): VideoStream? = streams[key]

    /** Stops [key], or every one (null). */
    fun stop(key: String? = null) {
        for ((k, stream) in streams) if (key == null || key == k) stream.close()
    }

    internal fun ended(stream: VideoStream) {
        streams.remove(stream.key, stream)
    }
}
