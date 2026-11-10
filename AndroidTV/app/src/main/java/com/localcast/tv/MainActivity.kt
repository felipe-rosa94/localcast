package com.localcast.tv

import android.os.Bundle
import android.os.Handler
import android.os.Looper
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.tv.material3.MaterialTheme
import androidx.tv.material3.Text
import com.localcast.tv.discovery.DiscoveryManager
import com.localcast.tv.pairing.PinManager
import com.localcast.tv.signaling.SignalingServer
import com.localcast.tv.webrtc.WebRtcReceiver
import com.localcast.tv.webrtc.createPeerConnectionFactory
import org.webrtc.EglBase
import org.webrtc.PeerConnectionFactory
import org.webrtc.RendererCommon
import org.webrtc.SurfaceViewRenderer
import kotlinx.coroutines.delay
import java.net.Inet4Address
import java.net.NetworkInterface

sealed class ScreenState {
    data class Waiting(val pin: String, val tvIp: String, val remainingSeconds: Long) : ScreenState()
    data class Connecting(val clientName: String) : ScreenState()
    data class Streaming(val clientName: String) : ScreenState()
}

class MainActivity : ComponentActivity(), SignalingServer.Listener {

    private lateinit var pinManager: PinManager
    private lateinit var discoveryManager: DiscoveryManager
    private var signalingServer: SignalingServer? = null
    private val mainHandler = Handler(Looper.getMainLooper())

    private lateinit var eglBase: EglBase
    private lateinit var peerConnectionFactory: PeerConnectionFactory
    private lateinit var videoRenderer: SurfaceViewRenderer
    @Volatile private var receiver: WebRtcReceiver? = null

    private val screenState = mutableStateOf<ScreenState>(ScreenState.Waiting("----", "Localizando IP...", 120))
    private val tvName = "LocalCast TV - Sala de reuniões"
    private val serverPort = 8765

    private val pinTicker = object : Runnable {
        override fun run() {
            if (screenState.value is ScreenState.Waiting) {
                if (pinManager.isExpired()) {
                    pinManager.generateNewPin()
                }
                screenState.value = ScreenState.Waiting(
                    pin = pinManager.currentPin,
                    tvIp = getDeviceIpAddress(),
                    remainingSeconds = pinManager.remainingSeconds()
                )
            }
            mainHandler.postDelayed(this, 1000)
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        pinManager = PinManager()
        discoveryManager = DiscoveryManager(this)

        eglBase = EglBase.create()
        peerConnectionFactory = createPeerConnectionFactory(this, eglBase)
        videoRenderer = SurfaceViewRenderer(this).apply {
            init(eglBase.eglBaseContext, null)
            setScalingType(RendererCommon.ScalingType.SCALE_ASPECT_FIT)
            setEnableHardwareScaler(true)
            keepScreenOn = true
        }

        startServer()
        mainHandler.postDelayed(pinTicker, 1000)

        setContent {
            MaterialTheme {
                MainContent(
                    state = screenState.value,
                    tvName = tvName,
                    serverPort = serverPort,
                    renderer = videoRenderer,
                    onDisconnectRequested = {
                        signalingServer?.disconnectActive()
                    }
                )
            }
        }
    }

    private fun getDeviceIpAddress(): String {
        try {
            val interfaces = NetworkInterface.getNetworkInterfaces()
            for (intf in interfaces) {
                if (intf.isLoopback || !intf.isUp) continue
                val addrs = intf.inetAddresses
                for (addr in addrs) {
                    if (addr is Inet4Address && !addr.isLoopbackAddress) {
                        val ip = addr.hostAddress
                        if (ip != null && !ip.startsWith("127.")) {
                            return ip
                        }
                    }
                }
            }
        } catch (_: Exception) {}
        return "Conectar à rede local"
    }

    private fun startServer() {
        val tvIp = getDeviceIpAddress()

        signalingServer = SignalingServer(
            port = serverPort,
            tvName = tvName,
            pinManager = pinManager,
            listener = this
        ).apply {
            start()
        }

        discoveryManager.registerService(
            port = serverPort,
            tvName = tvName,
            isBusy = false
        )

        screenState.value = ScreenState.Waiting(
            pin = pinManager.currentPin,
            tvIp = tvIp,
            remainingSeconds = pinManager.remainingSeconds()
        )
    }

    override fun onAuthSuccess(clientName: String) {
        receiver?.close()
        receiver = WebRtcReceiver(
            factory = peerConnectionFactory,
            renderer = videoRenderer,
            onAnswer = { sdp -> signalingServer?.sendSdpAnswer(sdp) },
            onLocalIceCandidate = { candidate, mid, index ->
                signalingServer?.sendIceCandidate(candidate, mid, index)
            }
        )

        mainHandler.post {
            discoveryManager.updateStatus(isBusy = true)
            screenState.value = ScreenState.Streaming(clientName)
        }
    }

    override fun onAuthFailed(reason: String) {
        mainHandler.post {
            // Keep on waiting screen
        }
    }

    override fun onSdpOffer(sdp: String) {
        receiver?.handleOffer(sdp)
    }

    override fun onIceCandidate(candidate: String, sdpMid: String?, sdpMLineIndex: Int?) {
        receiver?.addRemoteIceCandidate(candidate, sdpMid, sdpMLineIndex)
    }

    override fun onClientDisconnected() {
        receiver?.close()
        receiver = null

        mainHandler.post {
            videoRenderer.clearImage()
            pinManager.generateNewPin()
            discoveryManager.updateStatus(isBusy = false)
            screenState.value = ScreenState.Waiting(
                pin = pinManager.currentPin,
                tvIp = getDeviceIpAddress(),
                remainingSeconds = pinManager.remainingSeconds()
            )
        }
    }

    override fun onCursorUpdate(x: Double, y: Double, visible: Boolean, timestamp: Long) {
        // Cursor Telemetry
    }

    override fun onDestroy() {
        super.onDestroy()
        mainHandler.removeCallbacks(pinTicker)
        discoveryManager.unregisterService()
        try {
            signalingServer?.stop()
        } catch (_: Exception) {}
        receiver?.close()
        receiver = null
        videoRenderer.release()
        peerConnectionFactory.dispose()
        eglBase.release()
    }
}

@Composable
fun MainContent(
    state: ScreenState,
    tvName: String,
    serverPort: Int,
    renderer: SurfaceViewRenderer,
    onDisconnectRequested: () -> Unit
) {
    when (state) {
        is ScreenState.Waiting -> WaitingScreen(
            tvName = tvName,
            tvIp = state.tvIp,
            serverPort = serverPort,
            pin = state.pin,
            remainingSeconds = state.remainingSeconds
        )
        is ScreenState.Connecting -> ConnectingScreen(
            clientName = state.clientName
        )
        is ScreenState.Streaming -> StreamingScreen(
            clientName = state.clientName,
            renderer = renderer,
            onStop = onDisconnectRequested
        )
    }
}

private val BgColor = Color(0xFF0A0A0B)
private val SurfaceColor = Color(0xFF131316)
private val LineColor = Color(0xFF26262B)
private val MutedColor = Color(0xFF8A8A93)
private val FaintColor = Color(0xFF55555C)
private val AccentColor = Color(0xFF5B8CFF)

@Composable
fun WaitingScreen(tvName: String, tvIp: String, serverPort: Int, pin: String, remainingSeconds: Long) {
    Column(
        modifier = Modifier
            .fillMaxSize()
            .background(BgColor),
        verticalArrangement = Arrangement.Center,
        horizontalAlignment = Alignment.CenterHorizontally
    ) {
        Text("LocalCast", color = Color(0xFFFAFAFA), fontSize = 34.sp, fontWeight = FontWeight.SemiBold)
        Spacer(Modifier.height(8.dp))
        Text(tvName, color = MutedColor, fontSize = 18.sp)

        Spacer(Modifier.height(64.dp))

        Text(
            text = stringResource(R.string.pin_label).uppercase(),
            color = FaintColor,
            fontSize = 13.sp,
            fontWeight = FontWeight.Medium,
            letterSpacing = 3.sp
        )
        Spacer(Modifier.height(18.dp))

        Row(horizontalArrangement = Arrangement.spacedBy(14.dp)) {
            pin.forEach { char ->
                Box(
                    modifier = Modifier
                        .width(92.dp)
                        .height(118.dp)
                        .background(SurfaceColor, shape = RoundedCornerShape(18.dp))
                        .border(1.dp, LineColor, RoundedCornerShape(18.dp)),
                    contentAlignment = Alignment.Center
                ) {
                    Text(
                        text = char.toString(),
                        color = Color(0xFFFAFAFA),
                        fontSize = 60.sp,
                        fontWeight = FontWeight.Medium,
                        fontFamily = FontFamily.Monospace
                    )
                }
            }
        }

        Spacer(Modifier.height(22.dp))

        val progress = (remainingSeconds / 120f).coerceIn(0f, 1f)
        Box(
            modifier = Modifier
                .width(404.dp)
                .height(3.dp)
                .background(LineColor, shape = RoundedCornerShape(2.dp))
        ) {
            Box(
                modifier = Modifier
                    .width((404 * progress).dp)
                    .height(3.dp)
                    .background(AccentColor, shape = RoundedCornerShape(2.dp))
            )
        }

        Spacer(Modifier.height(56.dp))

        Text(
            text = "Abra o LocalCast no Windows e digite o PIN",
            color = MutedColor,
            fontSize = 18.sp
        )
        Spacer(Modifier.height(10.dp))
        Text(
            text = "$tvIp:$serverPort",
            color = FaintColor,
            fontSize = 15.sp,
            fontFamily = FontFamily.Monospace
        )
    }
}

@Composable
fun ConnectingScreen(clientName: String) {
    Column(
        modifier = Modifier
            .fillMaxSize()
            .background(BgColor),
        verticalArrangement = Arrangement.Center,
        horizontalAlignment = Alignment.CenterHorizontally
    ) {
        Text("LocalCast", color = Color(0xFFFAFAFA), fontSize = 34.sp, fontWeight = FontWeight.SemiBold)
        Spacer(Modifier.height(20.dp))
        Text("Conectando com $clientName…", color = MutedColor, fontSize = 20.sp)
    }
}

@Composable
fun StreamingScreen(clientName: String, renderer: SurfaceViewRenderer, onStop: () -> Unit) {
    var showBadge by remember(clientName) { mutableStateOf(true) }
    LaunchedEffect(clientName) {
        delay(4000)
        showBadge = false
    }

    Box(
        modifier = Modifier
            .fillMaxSize()
            .background(Color.Black),
        contentAlignment = Alignment.TopStart
    ) {
        AndroidView(
            factory = { renderer },
            modifier = Modifier.fillMaxSize()
        )

        if (showBadge) {
            Box(
                modifier = Modifier
                    .padding(32.dp)
                    .background(Color(0xCC0A0A0B), shape = RoundedCornerShape(24.dp))
                    .padding(horizontal = 20.dp, vertical = 10.dp)
            ) {
                Text(
                    text = clientName,
                    color = Color(0xFFFAFAFA),
                    fontSize = 16.sp,
                    fontWeight = FontWeight.Medium
                )
            }
        }
    }
}
