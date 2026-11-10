package com.localcast.tv.webrtc

import android.content.Context
import android.media.AudioAttributes
import android.util.Log
import org.webrtc.AudioTrack
import org.webrtc.audio.JavaAudioDeviceModule
import org.webrtc.DefaultVideoDecoderFactory
import org.webrtc.DefaultVideoEncoderFactory
import org.webrtc.EglBase
import org.webrtc.IceCandidate
import org.webrtc.MediaConstraints
import org.webrtc.MediaStream
import org.webrtc.PeerConnection
import org.webrtc.PeerConnectionFactory
import org.webrtc.RtpTransceiver
import org.webrtc.SdpObserver
import org.webrtc.SessionDescription
import org.webrtc.SurfaceViewRenderer
import org.webrtc.VideoTrack
import org.webrtc.DataChannel

fun createPeerConnectionFactory(context: Context, eglBase: EglBase): PeerConnectionFactory {
    PeerConnectionFactory.initialize(
        PeerConnectionFactory.InitializationOptions.builder(context.applicationContext)
            .setFieldTrials("WebRTC-ForcePlayoutDelay/min_ms:0,max_ms:40/")
            .createInitializationOptions()
    )
    val audioDeviceModule = JavaAudioDeviceModule.builder(context.applicationContext)
        .setAudioAttributes(
            AudioAttributes.Builder()
                .setUsage(AudioAttributes.USAGE_MEDIA)
                .setContentType(AudioAttributes.CONTENT_TYPE_MUSIC)
                .build()
        )
        .setUseStereoOutput(true)
        .setUseHardwareAcousticEchoCanceler(false)
        .setUseHardwareNoiseSuppressor(false)
        .createAudioDeviceModule()
    val factory = PeerConnectionFactory.builder()
        .setAudioDeviceModule(audioDeviceModule)
        .setVideoDecoderFactory(DefaultVideoDecoderFactory(eglBase.eglBaseContext))
        .setVideoEncoderFactory(DefaultVideoEncoderFactory(eglBase.eglBaseContext, true, true))
        .createPeerConnectionFactory()
    audioDeviceModule.release()
    return factory
}

private val opusRtpmap = Regex("""a=rtpmap:(\d+) opus/48000""", RegexOption.IGNORE_CASE)

internal fun enableOpusStereo(sdp: String): String {
    val pt = opusRtpmap.find(sdp)?.groupValues?.get(1) ?: return sdp
    return sdp.split("\r\n").joinToString("\r\n") { line ->
        if (line.startsWith("a=fmtp:$pt ") && !line.contains("stereo=1")) "$line;stereo=1;sprop-stereo=1" else line
    }
}

class WebRtcReceiver(
    factory: PeerConnectionFactory,
    private val renderer: SurfaceViewRenderer,
    private val onAnswer: (String) -> Unit,
    private val onLocalIceCandidate: (String, String?, Int) -> Unit,
    private val onStateChanged: (String) -> Unit = {}
) {
    private val tag = "WebRtcReceiver"
    private val lock = Any()
    private val pendingRemoteIce = ArrayList<IceCandidate>()
    private var remoteDescriptionSet = false
    private var videoTrack: VideoTrack? = null
    private var closed = false

    private val observer = object : PeerConnection.Observer {
        override fun onSignalingChange(state: PeerConnection.SignalingState?) {}

        override fun onIceConnectionChange(state: PeerConnection.IceConnectionState?) {
            Log.i(tag, "ICE connection: $state")
            onStateChanged("ice:$state")
        }

        override fun onConnectionChange(newState: PeerConnection.PeerConnectionState?) {
            Log.i(tag, "Peer connection: $newState")
            onStateChanged("pc:$newState")
        }

        override fun onIceConnectionReceivingChange(receiving: Boolean) {}
        override fun onIceGatheringChange(state: PeerConnection.IceGatheringState?) {}

        override fun onIceCandidate(candidate: IceCandidate) {
            onLocalIceCandidate(candidate.sdp, candidate.sdpMid, candidate.sdpMLineIndex)
        }

        override fun onIceCandidatesRemoved(candidates: Array<out IceCandidate>?) {}
        override fun onAddStream(stream: MediaStream?) {}
        override fun onRemoveStream(stream: MediaStream?) {}
        override fun onDataChannel(channel: DataChannel?) {}
        override fun onRenegotiationNeeded() {}

        override fun onTrack(transceiver: RtpTransceiver) {
            val track = transceiver.receiver.track()
            if (track is VideoTrack) {
                Log.i(tag, "Video track received")
                synchronized(lock) {
                    if (closed) return
                    videoTrack = track
                    track.setEnabled(true)
                    track.addSink(renderer)
                }
            } else if (track is AudioTrack) {
                Log.i(tag, "Audio track received")
                track.setEnabled(true)
            }
        }
    }

    private val peerConnection: PeerConnection? = factory.createPeerConnection(
        PeerConnection.RTCConfiguration(emptyList()).apply {
            sdpSemantics = PeerConnection.SdpSemantics.UNIFIED_PLAN
            continualGatheringPolicy = PeerConnection.ContinualGatheringPolicy.GATHER_CONTINUALLY
        },
        observer
    )

    fun handleOffer(sdp: String) {
        val pc = peerConnection ?: return
        pc.setRemoteDescription(object : LoggingSdpObserver("setRemote") {
            override fun onSetSuccess() {
                synchronized(lock) {
                    remoteDescriptionSet = true
                    pendingRemoteIce.forEach { pc.addIceCandidate(it) }
                    pendingRemoteIce.clear()
                }
                pc.createAnswer(object : LoggingSdpObserver("createAnswer") {
                    override fun onCreateSuccess(description: SessionDescription?) {
                        if (description == null) return
                        val answer = SessionDescription(description.type, enableOpusStereo(description.description))
                        pc.setLocalDescription(object : LoggingSdpObserver("setLocal") {
                            override fun onSetSuccess() {
                                onAnswer(answer.description)
                            }
                        }, answer)
                    }
                }, MediaConstraints())
            }
        }, SessionDescription(SessionDescription.Type.OFFER, sdp))
    }

    fun addRemoteIceCandidate(candidate: String, sdpMid: String?, sdpMLineIndex: Int?) {
        val pc = peerConnection ?: return
        val ice = IceCandidate(sdpMid ?: "0", sdpMLineIndex ?: 0, candidate)
        synchronized(lock) {
            if (!remoteDescriptionSet) {
                pendingRemoteIce.add(ice)
                return
            }
        }
        pc.addIceCandidate(ice)
    }

    fun close() {
        synchronized(lock) {
            if (closed) return
            closed = true
            videoTrack?.removeSink(renderer)
            videoTrack = null
        }
        try {
            peerConnection?.close()
            peerConnection?.dispose()
        } catch (e: Exception) {
            Log.w(tag, "close failed: ${e.message}")
        }
    }

    private open class LoggingSdpObserver(private val label: String) : SdpObserver {
        override fun onCreateSuccess(description: SessionDescription?) {}
        override fun onSetSuccess() {}
        override fun onCreateFailure(error: String?) {
            Log.e("WebRtcReceiver", "$label create failure: $error")
        }
        override fun onSetFailure(error: String?) {
            Log.e("WebRtcReceiver", "$label set failure: $error")
        }
    }
}
