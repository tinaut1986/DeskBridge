package com.deskbridge.app

import android.app.Activity
import android.content.Context
import android.content.Intent
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Paint
import android.graphics.Rect
import android.os.Build
import android.os.Bundle
import android.view.Gravity
import android.view.SurfaceHolder
import android.view.SurfaceView
import android.view.View
import android.view.ViewGroup
import android.view.WindowInsets
import android.view.WindowInsetsController
import android.view.WindowManager
import android.widget.FrameLayout

/**
 * A screen of the coordinator, full screen on this device (mode 1, "extra display"): the picture fitted to the
 * screen with its shape kept, and the coordinator's pointer drawn over it. Closing it (Back) stops the stream.
 */
class VideoActivity : Activity(), SurfaceHolder.Callback, VideoStream.View {
    companion object {
        private const val EXTRA_KEY = "key"

        fun start(context: Context, key: String) = context.startActivity(
            Intent(context, VideoActivity::class.java)
                .putExtra(EXTRA_KEY, key)
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_NO_ANIMATION))
    }

    private var stream: VideoStream? = null
    private var ended = false
    private lateinit var root: FrameLayout
    private lateinit var picture: SurfaceView
    private lateinit var pointer: PointerView
    private var videoWidth = 0
    private var videoHeight = 0

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        root = FrameLayout(this).apply { setBackgroundColor(android.graphics.Color.BLACK) }
        picture = SurfaceView(this)
        pointer = PointerView(this)
        root.addView(picture, FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT, Gravity.CENTER))
        root.addView(pointer, FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT))
        setContentView(root)
        root.addOnLayoutChangeListener { _, _, _, _, _, _, _, _, _ -> fit() }
        hideSystemBars()
        picture.holder.addCallback(this)
        show(intent)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        show(intent) // another stream (the profile changed): this window shows that one now
    }

    private fun show(intent: Intent) {
        val next = intent.getStringExtra(EXTRA_KEY)?.let(VideoStreams::get)
        if (next == null) {
            if (stream == null) finish()
            return
        }
        if (next === stream) return
        stream?.let {
            it.view = null
            it.detach()
        }
        stream = next
        next.view = this
        if (picture.holder.surface.isValid) next.attach(picture.holder.surface)
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) hideSystemBars()
    }

    private fun hideSystemBars() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            window.insetsController?.apply {
                hide(WindowInsets.Type.systemBars())
                systemBarsBehavior = WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
            }
        } else {
            @Suppress("DEPRECATION")
            window.decorView.systemUiVisibility = (View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY or View.SYSTEM_UI_FLAG_FULLSCREEN
                or View.SYSTEM_UI_FLAG_HIDE_NAVIGATION or View.SYSTEM_UI_FLAG_LAYOUT_STABLE
                or View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION or View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN)
        }
    }

    /** The picture as large as the screen allows with its shape kept (black bars otherwise). */
    private fun fit() {
        val w = root.width
        val h = root.height
        if (w == 0 || h == 0 || videoWidth == 0 || videoHeight == 0) return
        val scale = minOf(w.toFloat() / videoWidth, h.toFloat() / videoHeight)
        val pw = (videoWidth * scale).toInt()
        val ph = (videoHeight * scale).toInt()
        val params = picture.layoutParams as FrameLayout.LayoutParams
        if (params.width != pw || params.height != ph) {
            params.width = pw
            params.height = ph
            picture.layoutParams = params
        }
        pointer.place(Rect((w - pw) / 2, (h - ph) / 2, (w - pw) / 2 + pw, (h - ph) / 2 + ph), videoWidth, videoHeight)
    }

    // The surface the decoder draws on.

    override fun surfaceCreated(holder: SurfaceHolder) {
        stream?.attach(holder.surface)
    }

    override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) {}

    override fun surfaceDestroyed(holder: SurfaceHolder) {
        stream?.detach()
    }

    override fun onDestroy() {
        stream?.let {
            it.view = null
            it.detach()
            if (isFinishing && !ended) VideoStreams.stop(it.key) // closed here: the stream stops
        }
        super.onDestroy()
    }

    // From the stream (main thread).

    override fun onFormat(width: Int, height: Int) {
        videoWidth = width
        videoHeight = height
        fit()
    }

    override fun onPointer(x: Int, y: Int, visible: Boolean) = pointer.move(x, y, visible)

    override fun onPointerShape(picture: Bitmap) = pointer.shape(picture)

    override fun onEnded() {
        ended = true
        finish()
    }
}

/** The coordinator's pointer over the picture: at its position in the picture, scaled like the picture. */
private class PointerView(context: Context) : View(context) {
    private var area = Rect()
    private var videoWidth = 1
    private var videoHeight = 1
    private var x = 0
    private var y = 0
    private var visible = false
    private var picture: Bitmap? = null
    private val paint = Paint(Paint.FILTER_BITMAP_FLAG)
    private val target = Rect()

    fun place(area: Rect, videoWidth: Int, videoHeight: Int) {
        this.area = area
        this.videoWidth = videoWidth
        this.videoHeight = videoHeight
        invalidate()
    }

    fun move(x: Int, y: Int, visible: Boolean) {
        if (x == this.x && y == this.y && visible == this.visible) return
        this.x = x
        this.y = y
        this.visible = visible
        invalidate()
    }

    fun shape(picture: Bitmap) {
        this.picture = picture
        invalidate()
    }

    override fun onDraw(canvas: Canvas) {
        val p = picture ?: return
        if (!visible || area.isEmpty) return
        val scale = area.width().toFloat() / videoWidth
        val left = area.left + (x * scale).toInt()
        val top = area.top + (y * area.height().toFloat() / videoHeight).toInt()
        target.set(left, top, left + (p.width * scale).toInt().coerceAtLeast(1), top + (p.height * scale).toInt().coerceAtLeast(1))
        canvas.save()
        canvas.clipRect(area)
        canvas.drawBitmap(p, null, target, paint)
        canvas.restore()
    }
}
