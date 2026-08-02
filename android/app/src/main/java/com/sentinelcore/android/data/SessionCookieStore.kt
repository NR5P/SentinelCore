package com.sentinelcore.android.data

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import java.net.URL
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

// HttpURLConnection's CookieManager is process-memory only. Encrypt the server session cookie
// with a non-exportable Android Keystore key so a normal app restart does not force another
// login, while a restored preferences file is useless without this device's key.
class SessionCookieStore(context: Context) {
    private val preferences = context.getSharedPreferences("sentinel_core_sessions", Context.MODE_PRIVATE)

    fun load(url: URL): String? {
        val encoded = preferences.getString(storageKey(url), null) ?: return null
        return runCatching { decrypt(encoded) }.onFailure { clear(url) }.getOrNull()
    }

    fun save(url: URL, cookieHeader: String) {
        runCatching { encrypt(cookieHeader) }
            .onSuccess { preferences.edit().putString(storageKey(url), it).apply() }
    }

    fun clear(url: URL) {
        preferences.edit().remove(storageKey(url)).apply()
    }

    private fun storageKey(url: URL): String {
        val port = if (url.port >= 0) url.port else url.defaultPort
        return "${url.protocol.lowercase()}_${url.host.lowercase()}_$port"
    }

    private fun encrypt(value: String): String {
        val cipher = Cipher.getInstance(Transformation)
        cipher.init(Cipher.ENCRYPT_MODE, secretKey())
        val ciphertext = cipher.doFinal(value.toByteArray(Charsets.UTF_8))
        return Base64.encodeToString(cipher.iv + ciphertext, Base64.NO_WRAP)
    }

    private fun decrypt(value: String): String {
        val bytes = Base64.decode(value, Base64.NO_WRAP)
        require(bytes.size > IvSize)
        val cipher = Cipher.getInstance(Transformation)
        cipher.init(Cipher.DECRYPT_MODE, secretKey(), GCMParameterSpec(128, bytes.copyOfRange(0, IvSize)))
        return cipher.doFinal(bytes.copyOfRange(IvSize, bytes.size)).toString(Charsets.UTF_8)
    }

    private fun secretKey(): SecretKey {
        val keyStore = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (keyStore.getKey(KeyAlias, null) as? SecretKey)?.let { return it }
        return KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore").run {
            init(
                KeyGenParameterSpec.Builder(
                    KeyAlias,
                    KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT,
                )
                    .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                    .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                    .build(),
            )
            generateKey()
        }
    }

    private companion object {
        const val KeyAlias = "sentinel_core_session_cookie"
        const val Transformation = "AES/GCM/NoPadding"
        const val IvSize = 12
    }
}
