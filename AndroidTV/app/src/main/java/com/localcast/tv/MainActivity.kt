package com.localcast.tv

import android.content.Context
import android.os.Build
import android.os.Bundle
import android.provider.Settings
import android.os.Handler
import android.os.Looper
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.focusable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsFocusedAsState
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
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
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.window.Dialog
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
import com.localcast.tv.update.UpdateInfo
import com.localcast.tv.update.UpdateManager
import com.localcast.tv.webrtc.WebRtcReceiver
import com.localcast.tv.webrtc.createPeerConnectionFactory
import org.webrtc.EglBase
import org.webrtc.PeerConnectionFactory
import org.webrtc.RendererCommon
import org.webrtc.SurfaceViewRenderer
import kotlinx.coroutines.delay
import java.io.File
import java.net.Inet4Address
import kotlin.concurrent.thread
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
    private val tvName = mutableStateOf("")
    private lateinit var updateManager: UpdateManager
    private var availableUpdate: UpdateInfo? = null
    private var downloadedApk: File? = null
    private val updateLabel = mutableStateOf<String?>(null)
    @Volatile private var updating = false
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

        tvName.value = loadTvName()
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
        updateManager = UpdateManager(this)
        checkForUpdate()

        setContent {
            MaterialTheme {
                MainContent(
                    state = screenState.value,
                    tvName = tvName.value,
                    serverPort = serverPort,
                    onRename = ::renameTv,
                    onResetPin = ::resetPin,
                    updateLabel = updateLabel.value,
                    onUpdate = ::startUpdate,
                    renderer = videoRenderer,
                    onDisconnectRequested = {
                        signalingServer?.disconnectActive()
                    }
                )
            }
        }
    }

    private fun loadTvName(): String {
        val saved = getSharedPreferences("localcast", Context.MODE_PRIVATE).getString("tv_name", null)
        if (!saved.isNullOrBlank()) return saved
        val deviceName = Settings.Global.getString(contentResolver, "device_name")
        return "LocalCast TV - " + (if (deviceName.isNullOrBlank()) Build.MODEL else deviceName)
    }

    private fun renameTv(name: String) {
        val newName = name.trim().take(60)
        if (newName.isEmpty() || newName == tvName.value) return
        getSharedPreferences("localcast", Context.MODE_PRIVATE).edit().putString("tv_name", newName).apply()
        tvName.value = newName
        signalingServer?.tvName = newName
        discoveryManager.updateName(newName)
    }

    private fun checkForUpdate() {
        thread(isDaemon = true, name = "LocalCast-UpdateCheck") {
            val info = updateManager.checkForUpdate() ?: return@thread
            mainHandler.post {
                availableUpdate = info
                updateLabel.value = "Atualizar para v${info.version}"
            }
        }
    }

    private fun startUpdate() {
        val info = availableUpdate ?: return
        if (updating) return
        if (!updateManager.canInstall()) {
            updateManager.openInstallPermissionSettings()
            return
        }
        downloadedApk?.takeIf { it.exists() }?.let {
            updateManager.install(it)
            return
        }
        updating = true
        updateLabel.value = "Baixando atualização..."
        thread(isDaemon = true, name = "LocalCast-UpdateDownload") {
            try {
                val file = updateManager.download(info) { pct ->
                    mainHandler.post { updateLabel.value = "Baixando atualização... $pct%" }
                }
                mainHandler.post {
                    downloadedApk = file
                    updateLabel.value = "Instalar v${info.version}"
                    updateManager.install(file)
                }
            } catch (_: Exception) {
                mainHandler.post { updateLabel.value = "Falha no download - tentar de novo" }
            } finally {
                updating = false
            }
        }
    }

    private fun resetPin() {
        pinManager.generateNewPin()
        if (screenState.value is ScreenState.Waiting) {
            screenState.value = ScreenState.Waiting(
                pin = pinManager.currentPin,
                tvIp = getDeviceIpAddress(),
                remainingSeconds = pinManager.remainingSeconds()
            )
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
            tvName = tvName.value,
            pinManager = pinManager,
            listener = this
        ).apply {
            start()
        }

        discoveryManager.registerService(
            port = serverPort,
            tvName = tvName.value,
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
    onRename: (String) -> Unit,
    onResetPin: () -> Unit,
    updateLabel: String?,
    onUpdate: () -> Unit,
    renderer: SurfaceViewRenderer,
    onDisconnectRequested: () -> Unit
) {
    when (state) {
        is ScreenState.Waiting -> WaitingScreen(
            tvName = tvName,
            tvIp = state.tvIp,
            serverPort = serverPort,
            pin = state.pin,
            remainingSeconds = state.remainingSeconds,
            onRename = onRename,
            onResetPin = onResetPin,
            updateLabel = updateLabel,
            onUpdate = onUpdate
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
fun WaitingScreen(tvName: String, tvIp: String, serverPort: Int, pin: String, remainingSeconds: Long, onRename: (String) -> Unit, onResetPin: () -> Unit, updateLabel: String?, onUpdate: () -> Unit) {
    var editing by remember { mutableStateOf(false) }
    if (editing) {
        RenameDialog(
            initial = tvName,
            onSave = { onRename(it); editing = false },
            onDismiss = { editing = false }
        )
    }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .background(BgColor),
        verticalArrangement = Arrangement.Center,
        horizontalAlignment = Alignment.CenterHorizontally
    ) {
        Text("LocalCast", color = Color(0xFFFAFAFA), fontSize = 34.sp, fontWeight = FontWeight.SemiBold)
        Spacer(Modifier.height(8.dp))
        FocusButton(text = "$tvName  ✎", fontSize = 18.sp, color = MutedColor) { editing = true }

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

        Spacer(Modifier.height(14.dp))
        FocusButton(text = "↻  Gerar novo PIN", fontSize = 15.sp, color = FaintColor, onClick = onResetPin)

        Spacer(Modifier.height(36.dp))

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

        if (updateLabel != null) {
            Spacer(Modifier.height(24.dp))
            FocusButton(text = "⬇  $updateLabel", fontSize = 15.sp, color = AccentColor, onClick = onUpdate)
        }
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

@Composable
fun FocusButton(text: String, fontSize: androidx.compose.ui.unit.TextUnit = 16.sp, color: Color = Color(0xFFFAFAFA), onClick: () -> Unit) {
    val interaction = remember { MutableInteractionSource() }
    val focused by interaction.collectIsFocusedAsState()
    Box(
        modifier = Modifier
            .background(if (focused) SurfaceColor else Color.Transparent, RoundedCornerShape(10.dp))
            .border(1.dp, if (focused) AccentColor else Color.Transparent, RoundedCornerShape(10.dp))
            .clickable(interactionSource = interaction, indication = null, onClick = onClick)
            .focusable(interactionSource = interaction)
            .padding(horizontal = 14.dp, vertical = 6.dp)
    ) {
        Text(text, color = if (focused) Color(0xFFFAFAFA) else color, fontSize = fontSize)
    }
}

@Composable
fun RenameDialog(initial: String, onSave: (String) -> Unit, onDismiss: () -> Unit) {
    var value by remember { mutableStateOf(initial) }
    val focus = remember { FocusRequester() }
    LaunchedEffect(Unit) { focus.requestFocus() }
    Dialog(onDismissRequest = onDismiss) {
        Column(
            modifier = Modifier
                .width(480.dp)
                .background(SurfaceColor, RoundedCornerShape(18.dp))
                .border(1.dp, LineColor, RoundedCornerShape(18.dp))
                .padding(28.dp)
        ) {
            Text("Nome do dispositivo", color = Color(0xFFFAFAFA), fontSize = 20.sp, fontWeight = FontWeight.SemiBold)
            Spacer(Modifier.height(18.dp))
            BasicTextField(
                value = value,
                onValueChange = { value = it.take(60) },
                singleLine = true,
                textStyle = TextStyle(color = Color(0xFFFAFAFA), fontSize = 18.sp),
                cursorBrush = SolidColor(AccentColor),
                keyboardOptions = KeyboardOptions(imeAction = ImeAction.Done),
                keyboardActions = KeyboardActions(onDone = { onSave(value) }),
                modifier = Modifier
                    .focusRequester(focus)
                    .fillMaxWidth()
                    .background(BgColor, RoundedCornerShape(10.dp))
                    .border(1.dp, AccentColor, RoundedCornerShape(10.dp))
                    .padding(14.dp)
            )
            Spacer(Modifier.height(22.dp))
            Row(modifier = Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End) {
                FocusButton("Cancelar", color = MutedColor, onClick = onDismiss)
                Spacer(Modifier.width(8.dp))
                FocusButton("Salvar", color = AccentColor) { onSave(value) }
            }
        }
    }
}
