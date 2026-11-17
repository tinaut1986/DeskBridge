package com.deskbridge.protocol

import java.io.ByteArrayOutputStream
import java.io.InputStream
import java.io.OutputStream

/**
 * One JSON message per line (C# LineChannel). After [encrypt], each line is base64(AES-GCM(json)) with a
 * per-direction counter as nonce. Reads and writes may happen on different threads, but each from one at a time.
 */
internal class LineChannel(private val input: InputStream, private val output: OutputStream) {
    companion object {
        const val HANDSHAKE_LIMIT = 64 * 1024
        const val SESSION_LIMIT = 64 * 1024 * 1024
        private const val NEW_LINE = 0x0A
    }

    private val buffer = ByteArray(64 * 1024)
    private var start = 0
    private var end = 0
    private var maxLineBytes = HANDSHAKE_LIMIT
    private var send: CounterGcm? = null
    private var receive: CounterGcm? = null

    /** Bytes read past the last line (a binary protocol that follows the handshake starts with them). */
    fun leftover(): ByteArray = buffer.copyOfRange(start, end)

    fun encrypt(sendKey: ByteArray, receiveKey: ByteArray) {
        send = CounterGcm(sendKey)
        receive = CounterGcm(receiveKey)
        maxLineBytes = SESSION_LIMIT
    }

    fun send(message: ControlMessage) = sendJson(ProtocolJson.encodeToString(ControlMessage.serializer(), message))

    fun sendJson(json: String) {
        val bytes = json.toByteArray(Charsets.UTF_8)
        val line = send?.let { Crypto.base64(it.seal(bytes)).toByteArray(Charsets.US_ASCII) } ?: bytes
        output.write(line + NEW_LINE.toByte())
        output.flush()
    }

    /** The next message; null if the peer closed the connection cleanly. */
    fun receive(): ControlMessage? = receiveJson()?.let {
        try {
            ProtocolJson.decodeFromString(ControlMessage.serializer(), it)
        } catch (e: kotlinx.serialization.SerializationException) {
            throw ProtocolException("Unexpected message: ${e.message}", e)
        } catch (e: IllegalArgumentException) {
            throw ProtocolException("Unexpected message: ${e.message}", e)
        }
    }

    /** The next message as JSON text, decrypted but not interpreted; null when the peer closed cleanly. */
    fun receiveJson(): String? {
        val line = readLine() ?: return null
        val receive = receive ?: return String(line, Charsets.UTF_8)
        val sealed = try {
            Crypto.unbase64(String(line, Charsets.US_ASCII))
        } catch (e: IllegalArgumentException) {
            throw ProtocolException("Malformed encrypted message.", e)
        }
        return String(receive.open(sealed), Charsets.UTF_8)
    }

    private fun readLine(): ByteArray? {
        val line = ByteArrayOutputStream()
        while (true) {
            for (i in start until end) {
                if (buffer[i].toInt() == NEW_LINE) {
                    line.write(buffer, start, i - start)
                    start = i + 1
                    return line.toByteArray()
                }
            }
            line.write(buffer, start, end - start)
            start = 0
            end = 0
            if (line.size() > maxLineBytes) throw ProtocolException("Message too large.")
            val read = input.read(buffer)
            if (read <= 0) {
                if (line.size() == 0) return null
                throw ProtocolException("Connection closed in the middle of a message.")
            }
            end = read
        }
    }
}
