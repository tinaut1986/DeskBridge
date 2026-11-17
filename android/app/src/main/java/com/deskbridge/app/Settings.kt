package com.deskbridge.app

import android.content.Context
import android.os.Build
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/**
 * This device's DeskBridge settings: its machine id, the coordinator's address and the shared key. The key is
 * stored encrypted with a key of the Android Keystore, so a copy of the app's files does not reveal it.
 */
class Settings(context: Context) {
    private val prefs = context.getSharedPreferences("deskbridge", Context.MODE_PRIVATE)

    var machine: String
        get() = prefs.getString("machine", null) ?: defaultMachine()
        set(value) = prefs.edit().putString("machine", value.trim()).apply()

    /** "host" or "host:port" of the coordinator. */
    var connectTo: String
        get() = prefs.getString("connectTo", "") ?: ""
        set(value) = prefs.edit().putString("connectTo", value.trim()).apply()

    var sharedKey: String
        get() = prefs.getString("sharedKey", null)?.let(::decrypt) ?: ""
        set(value) = prefs.edit().putString("sharedKey", encrypt(value.trim())).apply()

    /** The agent should be running (restored when the app starts again). */
    var enabled: Boolean
        get() = prefs.getBoolean("enabled", false)
        set(value) = prefs.edit().putBoolean("enabled", value).apply()

    /** The last configuration the coordinator sent (JSON), kept for the next start. */
    var config: String?
        get() = prefs.getString("config", null)
        set(value) = prefs.edit().putString("config", value).apply()

    val isComplete: Boolean get() = machine.isNotBlank() && connectTo.isNotBlank() && sharedKey.isNotBlank()

    private fun defaultMachine(): String =
        (Build.MODEL ?: "android").lowercase().replace(Regex("[^a-z0-9]+"), "-").trim('-').ifEmpty { "android" }

    // Keystore encryption of the shared key.

    private val alias = "deskbridge-settings"

    private fun secretKey(): SecretKey {
        val store = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (store.getEntry(alias, null) as? KeyStore.SecretKeyEntry)?.let { return it.secretKey }
        return KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore").run {
            init(KeyGenParameterSpec.Builder(alias, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .build())
            generateKey()
        }
    }

    private fun encrypt(text: String): String {
        val cipher = Cipher.getInstance("AES/GCM/NoPadding").apply { init(Cipher.ENCRYPT_MODE, secretKey()) }
        val sealed = cipher.doFinal(text.toByteArray(Charsets.UTF_8))
        return Base64.encodeToString(cipher.iv + sealed, Base64.NO_WRAP)
    }

    private fun decrypt(stored: String): String? = try {
        val bytes = Base64.decode(stored, Base64.NO_WRAP)
        val cipher = Cipher.getInstance("AES/GCM/NoPadding").apply {
            init(Cipher.DECRYPT_MODE, secretKey(), GCMParameterSpec(128, bytes, 0, 12))
        }
        String(cipher.doFinal(bytes, 12, bytes.size - 12), Charsets.UTF_8)
    } catch (e: Exception) {
        null // the Keystore key is gone (restored backup, new device): the key has to be entered again
    }
}
