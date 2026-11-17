package com.deskbridge.protocol

import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.long
import kotlinx.serialization.json.put
import java.io.Closeable
import java.io.IOException
import java.net.Socket
import java.util.concurrent.CompletableFuture
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.ExecutionException
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.TimeoutException
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicLong

/** Answers a request the other side sent; [from] is its authenticated machine id. */
fun interface RequestHandler {
    fun handle(from: String, request: ControlMessage): ControlMessage
}

/**
 * An authenticated, encrypted connection where both sides can send requests (C# ControlSession). Requests
 * and replies are numbered session frames, so they can cross on the wire; frame id 0 is a notification
 * (handled in order, never answered: input events).
 */
class ControlSession internal constructor(
    private val channel: LineChannel,
    private val socket: Socket,
    val remoteMachine: String,
    private val handler: RequestHandler?,
    private val log: (String) -> Unit,
) : Closeable {
    private val sendLock = Any()
    private val pending = ConcurrentHashMap<Long, CompletableFuture<ControlMessage>>()
    private val nextId = AtomicLong()
    private val closed = AtomicBoolean()
    private val requests = Executors.newCachedThreadPool { r -> Thread(r, "deskbridge-request").apply { isDaemon = true } }
    private val done = CompletableFuture<Unit>()
    private var onClosed: (() -> Unit)? = null

    val isOpen: Boolean get() = !closed.get()

    /** Called once, from the reading thread, when the connection is gone. */
    fun setOnClosed(action: () -> Unit) {
        onClosed = action
        if (!isOpen) action()
    }

    internal fun start() {
        Thread(::readLoop, "deskbridge-session-$remoteMachine").apply { isDaemon = true }.start()
    }

    fun awaitClosed(timeoutMs: Long): Boolean = try {
        done.get(timeoutMs, TimeUnit.MILLISECONDS); true
    } catch (e: TimeoutException) {
        false
    }

    fun request(message: ControlMessage, timeoutMs: Long = 30_000): ControlMessage {
        if (!isOpen) throw lost()
        val id = nextId.incrementAndGet()
        val reply = CompletableFuture<ControlMessage>()
        pending[id] = reply
        try {
            sendFrame(id, false, message)
        } catch (e: IOException) {
            pending.remove(id)
            close()
            throw lost()
        }
        return try {
            reply.get(timeoutMs, TimeUnit.MILLISECONDS)
        } catch (e: TimeoutException) {
            pending.remove(id)
            throw ControlException("$remoteMachine did not answer in time.")
        } catch (e: ExecutionException) {
            throw e.cause ?: e
        }
    }

    /** A message that gets no reply (frame id 0). */
    fun notify(message: ControlMessage) {
        if (!isOpen) throw lost()
        try {
            sendFrame(0, false, message)
        } catch (e: IOException) {
            close()
            throw lost()
        }
    }

    private fun sendFrame(id: Long, isReply: Boolean, body: ControlMessage) {
        val json = buildJsonObject {
            put("type", "frame")
            put("id", id)
            put("isReply", isReply)
            put("body", ProtocolJson.encodeToJsonElement(ControlMessage.serializer(), body))
        }.toString()
        synchronized(sendLock) { channel.sendJson(json) }
    }

    private class Frame(val id: Long, val isReply: Boolean, val type: String, val body: ControlMessage?)

    private fun readFrame(json: String): Frame {
        val root = try {
            ProtocolJson.parseToJsonElement(json).jsonObject
        } catch (e: Exception) {
            throw ProtocolException("Malformed message.", e)
        }
        if ((root["type"] as? JsonPrimitive)?.content != "frame" || root["id"] == null || root["body"] !is JsonObject)
            throw ProtocolException("Unexpected message outside a session frame.")
        val body = root["body"]!!.jsonObject
        val type = (body["type"] as? JsonPrimitive)?.content ?: "?"
        val message = try {
            ProtocolJson.decodeFromJsonElement(ControlMessage.serializer(), body)
        } catch (e: Exception) {
            null
        }
        return Frame(root["id"]!!.jsonPrimitive.long, root["isReply"]?.jsonPrimitive?.booleanOrNull == true, type, message)
    }

    private fun readLoop() {
        try {
            while (true) {
                val json = channel.receiveJson() ?: break
                val frame = readFrame(json)
                when {
                    frame.isReply -> pending.remove(frame.id)?.let { reply ->
                        if (frame.body != null) reply.complete(frame.body)
                        else reply.completeExceptionally(ControlException(unknown(frame.type)))
                    }
                    frame.id == 0L -> {
                        val body = frame.body
                        if (body != null && handler != null) {
                            try {
                                handler.handle(remoteMachine, body)
                            } catch (e: Exception) {
                                log("Request ${body::class.simpleName} failed: ${e.message}")
                            }
                        }
                    }
                    frame.body == null -> requests.execute { reply(frame.id, ErrorMessage(unknown(frame.type))) }
                    else -> requests.execute { handle(frame.id, frame.body) }
                }
            }
        } catch (e: IOException) {
            if (isOpen) log("Connection with $remoteMachine closed: ${e.message}")
        } finally {
            close()
        }
    }

    private fun handle(id: Long, request: ControlMessage) {
        val reply = try {
            when {
                request is PingMessage -> OkMessage
                handler == null -> ErrorMessage("Unexpected request: ${request::class.simpleName}.")
                else -> handler.handle(remoteMachine, request)
            }
        } catch (e: Exception) {
            log("Request ${request::class.simpleName} failed: ${e.message}")
            ErrorMessage(e.message ?: e.toString())
        }
        reply(id, reply)
    }

    private fun reply(id: Long, message: ControlMessage) {
        try {
            sendFrame(id, true, message)
        } catch (e: IOException) {
            close()
        }
    }

    private fun unknown(type: String) =
        "The other machine sent '$type', which this version of DeskBridge does not know. Update both machines."

    private fun lost() = ControlException("The connection with $remoteMachine was lost.")

    override fun close() {
        if (!closed.compareAndSet(false, true)) return
        try {
            socket.close()
        } catch (_: IOException) {
        }
        requests.shutdown()
        for (id in pending.keys.toList()) pending.remove(id)?.completeExceptionally(lost())
        done.complete(Unit)
        onClosed?.invoke()
    }
}
