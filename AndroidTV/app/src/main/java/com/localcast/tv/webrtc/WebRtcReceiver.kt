package com.localcast.tv.webrtc

import android.content.Context
import android.util.Log
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
    return PeerConnectionFactory.builder()
        .setVideoDecoderFactory(DefaultVideoDecoderFactory(eglBase.eglBaseContext))
        .setVideoEncoderFactory(DefaultVideoEncoderFactory(eglBase.eglBaseContext, true, true))
        .createPeerConnectionFactory()
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
                        pc.setLocalDescription(object : LoggingSdpObserver("setLocal") {
                            override fun onSetSuccess() {
                                onAnswer(description.description)
                            }
                        }, description)
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
