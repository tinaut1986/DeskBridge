package com.deskbridge.protocol

import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.security.MessageDigest
import java.security.SecureRandom
import java.util.Base64
import javax.crypto.AEADBadTagException
import javax.crypto.Cipher
import javax.crypto.Mac
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

/** Thrown for anything the other side sent that breaks the protocol (closes the connection). */
class ProtocolException(message: String, cause: Throwable? = null) : java.io.IOException(message, cause)

/** Thrown when a connection or request fails in a way to show to the user. */
class ControlException(message: String) : Exception(message)

internal object Crypto {
    const val TAG_BYTES = 16
    private val random = SecureRandom()

    fun randomBytes(count: Int): ByteArray = ByteArray(count).also(random::nextBytes)

    fun hmacSha256(key: ByteArray, data: ByteArray): ByteArray =
        Mac.getInstance("HmacSHA256").run {
            init(SecretKeySpec(key, "HmacSHA256"))
            doFinal(data)
        }

    /** RFC 5869 HKDF-SHA256, as .NET's HKDF.DeriveKey (extract then expand). */
    fun hkdfSha256(ikm: ByteArray, length: Int, salt: ByteArray, info: ByteArray): ByteArray {
        val prk = hmacSha256(if (salt.isEmpty()) ByteArray(32) else salt, ikm)
        val out = java.io.ByteArrayOutputStream()
        var previous = ByteArray(0)
        var counter = 1
        while (out.size() < length) {
            previous = hmacSha256(prk, previous + info + byteArrayOf(counter.toByte()))
            out.write(previous)
            counter++
        }
        return out.toByteArray().copyOf(length)
    }

    fun constantTimeEquals(a: String, b: String?): Boolean =
        MessageDigest.isEqual(a.toByteArray(Charsets.US_ASCII), (b ?: "").toByteArray(Charsets.US_ASCII))

    fun base64(bytes: ByteArray): String = Base64.getEncoder().encodeToString(bytes)

    fun unbase64(text: String): ByteArray = Base64.getDecoder().decode(text)
}

/**
 * AES-GCM with a per-direction counter as nonce (12 bytes, the counter little-endian in the first 8): the same
 * scheme as the C# LineChannel and VideoChannel. One instance per direction; not thread-safe.
 */
internal class CounterGcm(private val key: ByteArray) {
    private var counter = 0L

    private fun nonce(): ByteArray =
        ByteBuffer.allocate(12).order(ByteOrder.LITTLE_ENDIAN).putLong(counter++).array()

    private fun cipher(mode: Int): Cipher =
        Cipher.getInstance("AES/GCM/NoPadding").apply {
            init(mode, SecretKeySpec(key, "AES"), GCMParameterSpec(Crypto.TAG_BYTES * 8, nonce()))
        }

    /** Ciphertext followed by the 16-byte tag. */
    fun seal(plain: ByteArray): ByteArray = cipher(Cipher.ENCRYPT_MODE).doFinal(plain)

    fun open(sealed: ByteArray): ByteArray {
        if (sealed.size < Crypto.TAG_BYTES) throw ProtocolException("Malformed encrypted message.")
        return try {
            cipher(Cipher.DECRYPT_MODE).doFinal(sealed)
        } catch (e: AEADBadTagException) {
            throw ProtocolException("A message failed authentication (altered, replayed or wrong key).", e)
        }
    }
}

/** The shared-key handshake of DeskBridge (C# ControlAuth): the key never travels. */
object ControlAuth {
    const val PROTOCOL_VERSION = 3
    private const val KEY_BYTES = 32

    fun newKey(): String = Crypto.base64(Crypto.randomBytes(KEY_BYTES))

    fun decodeKey(key: String): ByteArray {
        val bytes = try {
            Crypto.unbase64(key.trim())
        } catch (e: IllegalArgumentException) {
            throw ControlException("The shared key is not valid.")
        }
        if (bytes.size < 16) throw ControlException("The shared key is too short.")
        return bytes
    }

    fun newNonce(): String = Crypto.base64(Crypto.randomBytes(32))

    fun clientProof(key: ByteArray, serverNonce: String, clientNonce: String, client: String, server: String): String =
        mac(key, "deskbridge/v$PROTOCOL_VERSION/client|$serverNonce|$clientNonce|$client|$server")

    fun serverProof(key: ByteArray, serverNonce: String, clientNonce: String, client: String, server: String): String =
        mac(key, "deskbridge/v$PROTOCOL_VERSION/server|$serverNonce|$clientNonce|$client|$server")

    fun matches(expected: String, actual: String?): Boolean = Crypto.constantTimeEquals(expected, actual)

    /** Session keys, one per direction: (client → server, server → client). */
    fun sessionKeys(key: ByteArray, serverNonce: String, clientNonce: String): Pair<ByteArray, ByteArray> {
        val salt = Crypto.unbase64(serverNonce) + Crypto.unbase64(clientNonce)
        fun derive(direction: String) =
            Crypto.hkdfSha256(key, 32, salt, "deskbridge/v$PROTOCOL_VERSION $direction".toByteArray(Charsets.UTF_8))
        return derive("client->server") to derive("server->client")
    }

    private fun mac(key: ByteArray, text: String): String =
        Crypto.base64(Crypto.hmacSha256(key, text.toByteArray(Charsets.UTF_8)))
}
