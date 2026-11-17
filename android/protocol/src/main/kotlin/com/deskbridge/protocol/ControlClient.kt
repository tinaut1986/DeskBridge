package com.deskbridge.protocol

import java.io.IOException
import java.net.InetSocketAddress
import java.net.Socket

/** Opens sessions to other machines' agents (C# ControlClient). Blocking: call it off the main thread. */
object ControlClient {
    const val CONNECT_TIMEOUT_MS = 5_000
    const val DEFAULT_PORT = 47900

    /** "host" or "host:port" (default port when absent). */
    fun parseAddress(address: String, defaultPort: Int = DEFAULT_PORT): Pair<String, Int> {
        val a = address.trim()
        val colon = a.lastIndexOf(':')
        if (colon > 0 && a.indexOf(':') == colon) {
            val port = a.substring(colon + 1).toIntOrNull()
            if (port != null && port in 1..65535) return a.substring(0, colon) to port
        }
        return a to defaultPort
    }

    /**
     * Connects, authenticates both ways and starts an encrypted session.
     * @param asLink keep it open as this machine's link to a coordinator (requests come back over it).
     */
    fun open(
        host: String,
        port: Int,
        localMachine: String,
        key: ByteArray,
        handler: RequestHandler?,
        log: (String) -> Unit = {},
        asLink: Boolean = false,
        expectedMachine: String? = null,
    ): ControlSession {
        val h = handshake(host, port, localMachine, expectedMachine, key, asLink, video = null)
        h.channel.encrypt(sendKey = h.clientToServer, receiveKey = h.serverToClient)
        return ControlSession(h.channel, h.socket, h.machine, handler, log).also { it.start() }
    }

    /** Opens a video connection for stream [streamKey] (same authentication, then binary packets). */
    fun openVideo(host: String, port: Int, localMachine: String, key: ByteArray, streamKey: String, expectedMachine: String? = null): VideoChannel {
        val h = handshake(host, port, localMachine, expectedMachine, key, asLink = false, video = streamKey)
        val input = java.io.SequenceInputStream(h.channel.leftover().inputStream(), h.socket.getInputStream())
        return VideoChannel(h.socket, input, h.socket.getOutputStream(), h.machine, streamKey,
            sendKey = h.clientToServer, receiveKey = h.serverToClient)
    }

    private class Handshake(
        val socket: Socket,
        val channel: LineChannel,
        val machine: String,
        val clientToServer: ByteArray,
        val serverToClient: ByteArray,
    )

    private fun handshake(
        host: String, port: Int, localMachine: String, expectedMachine: String?, key: ByteArray, asLink: Boolean, video: String?,
    ): Handshake {
        val socket = Socket()
        try {
            socket.connect(InetSocketAddress(host, port), CONNECT_TIMEOUT_MS)
        } catch (e: IOException) {
            socket.close()
            throw ControlException("Could not connect to $host:$port (${e.message}). Is DeskBridge running there and the port open in the firewall?")
        }
        try {
            socket.keepAlive = true
            socket.tcpNoDelay = true
            socket.soTimeout = CONNECT_TIMEOUT_MS
            val channel = LineChannel(socket.getInputStream(), socket.getOutputStream())

            val challenge = channel.receive() as? ChallengeMessage
                ?: throw ControlException("$host:$port does not look like a DeskBridge agent.")
            if (challenge.version != ControlAuth.PROTOCOL_VERSION)
                throw ControlException("Protocol version mismatch with $host (${challenge.version}, here ${ControlAuth.PROTOCOL_VERSION}). Update both.")
            if (expectedMachine != null && challenge.machine != expectedMachine)
                throw ControlException("'${challenge.machine}' answers at $host, not '$expectedMachine'.")

            val clientNonce = ControlAuth.newNonce()
            val proof = ControlAuth.clientProof(key, challenge.nonce, clientNonce, localMachine, challenge.machine)
            channel.send(AuthMessage(localMachine, clientNonce, proof, asLink, video))

            when (val answer = channel.receive()) {
                is WelcomeMessage -> {
                    val expected = ControlAuth.serverProof(key, challenge.nonce, clientNonce, localMachine, challenge.machine)
                    if (!ControlAuth.matches(expected, answer.proof))
                        throw ControlException("$host could not prove it has the shared key.")
                }
                is ErrorMessage -> throw ControlException(answer.message)
                else -> throw ControlException("Unexpected reply from $host during authentication.")
            }
            socket.soTimeout = 0
            val (c2s, s2c) = ControlAuth.sessionKeys(key, challenge.nonce, clientNonce)
            return Handshake(socket, channel, challenge.machine, c2s, s2c)
        } catch (e: IOException) {
            socket.close()
            throw ControlException("Communication error with $host: ${e.message}")
        } catch (e: Throwable) {
            socket.close()
            throw e
        }
    }
}
