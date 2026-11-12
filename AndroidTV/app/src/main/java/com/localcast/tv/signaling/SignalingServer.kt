package com.localcast.tv.signaling

import android.util.Log
import com.localcast.tv.pairing.PinManager
import org.java_websocket.WebSocket
import org.java_websocket.handshake.ClientHandshake
import org.java_websocket.server.WebSocketServer
import org.json.JSONObject
import java.net.InetSocketAddress
import java.util.UUID

class SignalingServer(
    port: Int = 8765,
    @Volatile var tvName: String,
    private val pinManager: PinManager,
    private val listener: Listener
) : WebSocketServer(InetSocketAddress(port)) {

    private val tag = "SignalingServer"

    init {
        isReuseAddr = true
    }

    interface Listener {
        fun onAuthSuccess(clientName: String)
        fun onAuthFailed(reason: String)
        fun onSdpOffer(sdp: String)
        fun onIceCandidate(candidate: String, sdpMid: String?, sdpMLineIndex: Int?)
        fun onClientDisconnected()
        fun onCursorUpdate(x: Double, y: Double, visible: Boolean, timestamp: Long)
    }

    private var activeSocket: WebSocket? = null
    var activeClientName: String? = null
        private set
    val isBusy: Boolean
        get() = activeSocket != null && activeSocket?.isOpen == true

    override fun onStart() {
        Log.i(tag, "Signaling server started on port $port")
    }

    override fun onOpen(conn: WebSocket, handshake: ClientHandshake) {
        Log.i(tag, "New client connection from ${conn.remoteSocketAddress}")
        if (isBusy) {
            val error = JSONObject().apply {
                put("type", "error")
                put("code", "BUSY")
                put("message", "TV ocupada em transmissão por $activeClientName")
            }
            conn.send(error.toString())
            conn.close()
        }
    }

    override fun onClose(conn: WebSocket, code: Int, reason: String, remote: Boolean) {
        Log.i(tag, "Connection closed from ${conn.remoteSocketAddress}: code=$code, reason=$reason")
        if (conn == activeSocket) {
            activeSocket = null
            activeClientName = null
            listener.onClientDisconnected()
        }
    }

    override fun onError(conn: WebSocket?, ex: Exception) {
        Log.e(tag, "WebSocket error: ${ex.message}", ex)
    }

    override fun onMessage(conn: WebSocket, message: String) {
        try {
            val json = JSONObject(message)
            val type = json.optString("type")

            when (type) {
                "auth" -> handleAuth(conn, json)
                "sdp_offer" -> {
                    if (conn == activeSocket) {
                        val sdp = json.optString("sdp")
                        listener.onSdpOffer(sdp)
                    }
                }
                "ice_candidate" -> {
                    if (conn == activeSocket) {
                        val candidate = json.optString("candidate")
                        val sdpMid = if (json.has("sdpMid")) json.getString("sdpMid") else null
                        val sdpMLineIndex = if (json.has("sdpMLineIndex")) json.getInt("sdpMLineIndex") else null
                        listener.onIceCandidate(candidate, sdpMid, sdpMLineIndex)
                    }
                }
                "cursor" -> {
                    if (conn == activeSocket) {
                        val x = json.optDouble("x", 0.0)
                        val y = json.optDouble("y", 0.0)
                        val visible = json.optBoolean("visible", true)
                        val t = json.optLong("t", 0L)
                        listener.onCursorUpdate(x, y, visible, t)
                    }
                }
                "disconnect" -> {
                    if (conn == activeSocket) {
                        conn.close()
                    }
                }
                else -> Log.w(tag, "Unknown message type: $type")
            }
        } catch (e: Exception) {
            Log.e(tag, "Failed to parse signaling message: $message", e)
        }
    }

    private fun handleAuth(conn: WebSocket, json: JSONObject) {
        if (isBusy && conn != activeSocket) {
            val err = JSONObject().apply {
                put("type", "error")
                put("code", "BUSY")
                put("message", "TV ocupada em transmissão por $activeClientName")
            }
            conn.send(err.toString())
            conn.close()
            return
        }

        val pin = json.optString("pin")
        val clientName = json.optString("clientName", "Computador")

        when (val result = pinManager.validate(pin)) {
            is PinManager.ValidationResult.Success -> {
                activeSocket = conn
                activeClientName = clientName
                val sessionToken = UUID.randomUUID().toString()

                val response = JSONObject().apply {
                    put("type", "auth_ok")
                    put("sessionToken", sessionToken)
                    put("tvName", tvName)
                }
                conn.send(response.toString())
                listener.onAuthSuccess(clientName)
            }
            is PinManager.ValidationResult.Expired -> {
                val err = JSONObject().apply {
                    put("type", "error")
                    put("code", "PIN_EXPIRED")
                    put("message", "PIN expirado. Use o novo PIN exibido na TV.")
                }
                conn.send(err.toString())
                listener.onAuthFailed("PIN expirado")
            }
            is PinManager.ValidationResult.InvalidPin -> {
                val err = JSONObject().apply {
                    put("type", "error")
                    put("code", "INVALID_PIN")
                    put("message", "PIN incorreto. Tentativas restantes: ${result.remainingAttempts}")
                }
                conn.send(err.toString())
                listener.onAuthFailed("PIN incorreto (${result.remainingAttempts} restantes)")
            }
            is PinManager.ValidationResult.MaxAttemptsExceeded -> {
                val err = JSONObject().apply {
                    put("type", "error")
                    put("code", "MAX_ATTEMPTS_EXCEEDED")
                    put("message", "Número máximo de tentativas excedido. Conexão bloqueada.")
                }
                conn.send(err.toString())
                conn.close()
                listener.onAuthFailed("Máximo de tentativas excedido")
            }
        }
    }

    fun sendSdpAnswer(sdp: String) {
        activeSocket?.let {
            if (it.isOpen) {
                val msg = JSONObject().apply {
                    put("type", "sdp_answer")
                    put("sdp", sdp)
                }
                it.send(msg.toString())
            }
        }
    }

    fun sendIceCandidate(candidate: String, sdpMid: String?, sdpMLineIndex: Int?) {
        activeSocket?.let {
            if (it.isOpen) {
                val msg = JSONObject().apply {
                    put("type", "ice_candidate")
                    put("candidate", candidate)
                    sdpMid?.let { mid -> put("sdpMid", mid) }
                    sdpMLineIndex?.let { idx -> put("sdpMLineIndex", idx) }
                }
                it.send(msg.toString())
            }
        }
    }

    fun disconnectActive() {
        activeSocket?.let {
            if (it.isOpen) {
                val msg = JSONObject().apply {
                    put("type", "disconnect")
                    put("reason", "tv_terminated")
                }
                it.send(msg.toString())
                it.close()
            }
        }
        activeSocket = null
        activeClientName = null
    }
}
