package com.sentinelcore.android.data

import android.content.Context
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.ByteArrayInputStream
import java.net.HttpURLConnection
import java.net.URL
import java.security.cert.CertificateException
import java.security.cert.CertificateFactory
import java.security.cert.X509Certificate
import java.util.Base64
import javax.net.ssl.HostnameVerifier
import javax.net.ssl.HttpsURLConnection
import javax.net.ssl.SSLContext
import javax.net.ssl.X509TrustManager

// Recognising this app's own server when its HTTPS certificate isn't trusted. A port of
// ios/SentinelCore/Networking/ServerIdentity.swift.
//
// The server makes its own certificate authority, which no Android device trusts out of the box,
// so connect() falls back to the server's plain-HTTP port when the https:// address fails trust.
// Taken for anything that fails trust, that fallback hands the session cookie to whatever answers
// at the same private IP on someone else's network. So the server's authority is remembered the
// first time the fallback works (trust on first use - in practice at home, during setup), and
// after that the fallback only counts as this server when it completes a TLS handshake with a
// certificate that authority signed. Completing the handshake - not just showing the certificate
// - is what proves it holds the key. The check itself sends nothing: no cookies, and the handshake
// is abandoned before the request whenever the certificate doesn't verify.
//
// The server reissues its own certificate (with a new key) when its IP address changes but keeps
// its authority, which is why it's the authority that's remembered. A reinstalled server has a new
// one and is refused until its address is saved again in Settings (see forget).
class ServerIdentity(context: Context) {
    sealed interface Check {
        // Completed a handshake with a certificate the remembered authority signed.
        data object Verified : Check
        // Something answered with a certificate the remembered authority didn't sign.
        data object Mismatch : Check
        // Nothing remembered for this address yet; the certificate it showed, to check the
        // authority against once the fallback has worked.
        data class Unknown(val leaf: X509Certificate) : Check
        // No certificate to judge - the address stopped answering in between.
        data object Unavailable : Check
    }

    private val preferences = context.applicationContext
        .getSharedPreferences("sentinel_core_server_identity", Context.MODE_PRIVATE)

    suspend fun check(httpsAddress: String): Check = withContext(Dispatchers.IO) {
        val normalized = ServerAddress.normalize(httpsAddress) ?: return@withContext Check.Unavailable
        val url = runCatching { URL("$normalized/api/health") }.getOrNull()
        if (url == null || url.protocol != "https") return@withContext Check.Unavailable
        val authority = rememberedAuthority(url)

        var leaf: X509Certificate? = null
        var chained = false
        val trust = object : X509TrustManager {
            override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) =
                throw CertificateException("Not a server")

            // Finish the handshake only for our own authority's certificate; anything else is
            // abandoned before a single byte of the request is sent.
            override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
                val presented = chain?.firstOrNull() ?: throw CertificateException("No certificate")
                leaf = presented
                chained = authority != null && signs(authority, presented)
                if (!chained) throw CertificateException("Not signed by this server's authority")
            }

            override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
        }

        var connection: HttpsURLConnection? = null
        val answered = try {
            connection = (url.openConnection() as HttpsURLConnection).apply {
                sslSocketFactory = SSLContext.getInstance("TLS")
                    .apply { init(null, arrayOf(trust), null) }
                    .socketFactory
                // The host is already fixed by the address; whether the authority signed the
                // certificate is the whole question, and the trust manager has answered it.
                hostnameVerifier = HostnameVerifier { _, _ -> chained }
                connectTimeout = 4_000
                readTimeout = 4_000
                useCaches = false
                instanceFollowRedirects = false
            }
            connection.responseCode
            true
        } catch (error: Exception) {
            false
        } finally {
            connection?.disconnect()
        }

        val presented = leaf ?: return@withContext Check.Unavailable
        when {
            authority == null -> Check.Unknown(presented)
            chained -> if (answered) Check.Verified else Check.Unavailable
            else -> Check.Mismatch
        }
    }

    // Called once the fallback has actually worked with nothing remembered: keeps the server's
    // authority only if it really did sign the certificate the server presented.
    fun remember(authorityData: ByteArray, leaf: X509Certificate, httpsAddress: String): Boolean {
        val key = storageKey(httpsAddress) ?: return false
        val authority = parseCertificate(authorityData) ?: return false
        if (!signs(authority, leaf)) return false
        preferences.edit().putString(key, Base64.getEncoder().encodeToString(authority.encoded)).apply()
        return true
    }

    fun forget(httpsAddress: String) {
        val key = storageKey(httpsAddress) ?: return
        preferences.edit().remove(key).apply()
    }

    private fun rememberedAuthority(url: URL): X509Certificate? {
        val stored = preferences.getString(storageKey(url), null) ?: return null
        return runCatching { Base64.getDecoder().decode(stored) }.getOrNull()?.let(::parseCertificate)
    }

    companion object {
        // The server's certificate authority, from the plain-HTTP address the fallback just
        // reached. A bare connection, like check(): nothing of the app's own session goes with it.
        suspend fun fetchAuthority(httpAddress: String): ByteArray? = withContext(Dispatchers.IO) {
            val normalized = ServerAddress.normalize(httpAddress) ?: return@withContext null
            var connection: HttpURLConnection? = null
            try {
                connection = (URL("$normalized/api/certificate-authority").openConnection() as HttpURLConnection).apply {
                    connectTimeout = 5_000
                    readTimeout = 5_000
                    useCaches = false
                    instanceFollowRedirects = false
                }
                if (connection.responseCode != 200) null else connection.inputStream.use { it.readBytes() }
            } catch (error: Exception) {
                null
            } finally {
                connection?.disconnect()
            }
        }

        // The server signs its certificate with its authority directly and sends no chain, so
        // "did our authority sign this" is one signature check plus the names lining up. Not the
        // TLS policy: the host is already fixed by the address the certificate was fetched from.
        fun signs(authority: X509Certificate, leaf: X509Certificate): Boolean = runCatching {
            if (leaf.issuerX500Principal != authority.subjectX500Principal) return false
            leaf.verify(authority.publicKey)
            leaf.checkValidity()
            authority.checkValidity()
            true
        }.getOrDefault(false)

        // The server serves ca.crt as PEM; DER is accepted too - CertificateFactory reads both.
        fun parseCertificate(data: ByteArray): X509Certificate? = runCatching {
            CertificateFactory.getInstance("X.509")
                .generateCertificate(ByteArrayInputStream(data)) as X509Certificate
        }.getOrNull()

        // Per host and port - the same server whichever path or trailing slash it was saved with.
        fun storageKey(httpsAddress: String): String? {
            val normalized = ServerAddress.normalize(httpsAddress) ?: return null
            return runCatching { storageKey(URL(normalized)) }.getOrNull()
        }

        private fun storageKey(url: URL): String {
            val port = if (url.port >= 0) url.port else url.defaultPort
            return "${url.host.lowercase()}:$port"
        }
    }
}
