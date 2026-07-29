package com.sentinelcore.android.net

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.util.Log
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update

// A SentinelCore server found on the local network via NSD/mDNS - see ServerDiscovery below.
// Purely a convenience for prefilling ServerSetupScreen's manual address field; nothing here is
// persisted directly.
data class DiscoveredServer(
    val id: String,
    val name: String,
    val host: String,
    val port: Int,
    val scheme: String,
) {
    val addressString: String get() = "$scheme://$host:$port"
}

// Browses for other SentinelCore servers on the LAN via NsdManager ("_sentinelcore._tcp." - the
// same dedicated service type the server advertises over mDNS/DNS-SD, see
// MdnsAdvertiserService.cs on the server side), so ServerSetupScreen can list them instead of the
// user typing an IP address. Purely an assist: manual entry must keep working regardless of what
// (if anything) this finds.
class ServerDiscovery(context: Context) {
    private val nsdManager =
        context.applicationContext.getSystemService(Context.NSD_SERVICE) as NsdManager

    private val _servers = MutableStateFlow<List<DiscoveredServer>>(emptyList())
    val servers: StateFlow<List<DiscoveredServer>> = _servers.asStateFlow()

    private var discoveryListener: NsdManager.DiscoveryListener? = null

    fun start() {
        if (discoveryListener != null) return

        val listener = object : NsdManager.DiscoveryListener {
            override fun onDiscoveryStarted(serviceType: String) {
                Log.i(TAG, "Local-network discovery started for $serviceType")
            }

            override fun onServiceFound(serviceInfo: NsdServiceInfo) {
                resolve(serviceInfo)
            }

            override fun onServiceLost(serviceInfo: NsdServiceInfo) {
                _servers.update { list -> list.filterNot { it.id == serviceInfo.serviceName } }
            }

            override fun onDiscoveryStopped(serviceType: String) {
                Log.i(TAG, "Local-network discovery stopped")
            }

            override fun onStartDiscoveryFailed(serviceType: String, errorCode: Int) {
                Log.w(TAG, "Local-network discovery failed to start (error $errorCode)")
                discoveryListener = null
            }

            override fun onStopDiscoveryFailed(serviceType: String, errorCode: Int) {
                Log.w(TAG, "Local-network discovery failed to stop (error $errorCode)")
            }
        }

        try {
            nsdManager.discoverServices(SERVICE_TYPE, NsdManager.PROTOCOL_DNS_SD, listener)
            discoveryListener = listener
        } catch (error: Exception) {
            // A device with NSD unavailable (e.g. disabled by device policy) must degrade to "no
            // local-network discovery" rather than crash setup - manual address entry still works.
            Log.w(TAG, "Could not start local-network discovery", error)
        }
    }

    fun stop() {
        val listener = discoveryListener ?: return
        discoveryListener = null
        try {
            nsdManager.stopServiceDiscovery(listener)
        } catch (error: Exception) {
            Log.w(TAG, "Could not stop local-network discovery", error)
        }
        _servers.value = emptyList()
    }

    // NsdManager.resolveService is deprecated from API 34 in favor of a newer callback-based API,
    // but this app's minSdk is 26 - using the one classic path here covers every supported version
    // uniformly rather than branching on Build.VERSION.SDK_INT for a convenience feature.
    @Suppress("DEPRECATION")
    private fun resolve(serviceInfo: NsdServiceInfo) {
        nsdManager.resolveService(
            serviceInfo,
            object : NsdManager.ResolveListener {
                override fun onResolveFailed(serviceInfo: NsdServiceInfo, errorCode: Int) {
                    Log.w(TAG, "Could not resolve ${serviceInfo.serviceName} (error $errorCode)")
                }

                override fun onServiceResolved(serviceInfo: NsdServiceInfo) {
                    val host = serviceInfo.host?.hostAddress ?: return
                    val scheme = serviceInfo.attributes["scheme"]?.toString(Charsets.UTF_8) ?: "http"
                    val server = DiscoveredServer(
                        id = serviceInfo.serviceName,
                        name = serviceInfo.serviceName,
                        host = host,
                        port = serviceInfo.port,
                        scheme = scheme,
                    )
                    _servers.update { list ->
                        if (list.any { it.id == server.id }) list else list + server
                    }
                }
            },
        )
    }

    private companion object {
        const val TAG = "ServerDiscovery"
        const val SERVICE_TYPE = "_sentinelcore._tcp."
    }
}
