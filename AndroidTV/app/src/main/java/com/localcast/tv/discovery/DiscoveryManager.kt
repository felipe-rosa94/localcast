package com.localcast.tv.discovery

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.net.wifi.WifiManager
import android.util.Log
import org.json.JSONObject
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import kotlin.concurrent.thread

class DiscoveryManager(private val context: Context) {
    private val tag = "DiscoveryManager"
    private val serviceType = "_localcast._tcp"
    private val nsdManager = context.getSystemService(Context.NSD_SERVICE) as NsdManager
    private val wifiManager = context.applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
    private var multicastLock: WifiManager.MulticastLock? = null
    private var registrationListener: NsdManager.RegistrationListener? = null
    private var currentPort: Int = 8765
    private var currentTvName: String = "LocalCast TV"
    private var isRegistered = false
    private var isBusyState = false
    @Volatile
    private var isRunning = false
    private var beaconThread: Thread? = null

    fun registerService(port: Int, tvName: String, isBusy: Boolean = false) {
        currentPort = port
        currentTvName = tvName
        isBusyState = isBusy

        if (multicastLock == null) {
            try {
                multicastLock = wifiManager.createMulticastLock("LocalCastMulticastLock").apply {
                    setReferenceCounted(true)
                    acquire()
                }
            } catch (e: Exception) {
                Log.w(tag, "Multicast lock acquire error: ${e.message}")
            }
        }

        if (isRegistered) {
            unregisterService()
        }

        val serviceInfo = NsdServiceInfo().apply {
            serviceName = tvName
            serviceType = this@DiscoveryManager.serviceType
            this.port = port
            setAttribute("ver", "1")
            setAttribute("name", tvName)
            setAttribute("state", if (isBusy) "busy" else "available")
            setAttribute("port", port.toString())
        }

        registrationListener = object : NsdManager.RegistrationListener {
            override fun onServiceRegistered(serviceInfo: NsdServiceInfo) {
                isRegistered = true
                Log.i(tag, "NSD registered: ${serviceInfo.serviceName}")
            }

            override fun onRegistrationFailed(serviceInfo: NsdServiceInfo, errorCode: Int) {
                isRegistered = false
                Log.e(tag, "NSD registration failed: $errorCode")
            }

            override fun onServiceUnregistered(serviceInfo: NsdServiceInfo) {
                isRegistered = false
                Log.i(tag, "NSD unregistered")
            }

            override fun onUnregistrationFailed(serviceInfo: NsdServiceInfo, errorCode: Int) {
                Log.e(tag, "NSD unregistration failed: $errorCode")
            }
        }

        try {
            nsdManager.registerService(serviceInfo, NsdManager.PROTOCOL_DNS_SD, registrationListener)
        } catch (e: Exception) {
            Log.e(tag, "Failed to register NSD service", e)
        }

        startBroadcastBeacon()
    }

    private fun startBroadcastBeacon() {
        if (isRunning) return
        isRunning = true

        beaconThread = thread(isDaemon = true, name = "LocalCast-Beacon") {
            var socket: DatagramSocket? = null
            try {
                socket = DatagramSocket()
                socket.broadcast = true
                val broadcastAddr = InetAddress.getByName("255.255.255.255")

                while (isRunning) {
                    try {
                        val json = JSONObject().apply {
                            put("name", currentTvName)
                            put("port", currentPort)
                            put("state", if (isBusyState) "busy" else "available")
                            put("ver", "1")
                        }
                        val data = json.toString().toByteArray()
                        val packet = DatagramPacket(data, data.size, broadcastAddr, 8766)
                        socket.send(packet)
                    } catch (_: Exception) {}

                    Thread.sleep(2000)
                }
            } catch (e: Exception) {
                Log.w(tag, "Beacon socket error: ${e.message}")
            } finally {
                socket?.close()
            }
        }
    }

    fun updateStatus(isBusy: Boolean) {
        isBusyState = isBusy
        if (isRegistered) {
            registerService(currentPort, currentTvName, isBusy)
        }
    }

    fun unregisterService() {
        isRunning = false
        beaconThread?.interrupt()
        beaconThread = null

        if (isRegistered && registrationListener != null) {
            try {
                nsdManager.unregisterService(registrationListener)
            } catch (e: Exception) {
                Log.e(tag, "Error unregistering service", e)
            }
            isRegistered = false
            registrationListener = null
        }

        multicastLock?.let {
            if (it.isHeld) {
                it.release()
            }
        }
        multicastLock = null
    }
}
