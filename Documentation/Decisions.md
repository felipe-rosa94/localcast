# Decisões

| Data | Decisão |
| --- | --- |
| 2026-11-09 | Motor WebRTC no Windows: libwebrtc nativo em C++ (DLL) com encoder H.264 via Media Foundation |
| 2026-11-09 | UI Windows: WPF sobre .NET 8 |
| 2026-11-09 | TV de referência: TCL com Android TV; versão do Android e perfis H.264 a ler na Fase 0 |
| 2026-11-09 | Rede: multicast liberado; mDNS é o caminho principal, código manual é reserva |
| 2026-11-09 | Cursor: canal de dados separado, a validar na Fase 6 contra o cursor embutido |
| 2026-11-10 | Windows: troca da DLL libwebrtc por SIPSorcery (WebRTC em C#) + ffmpeg (ddagrab + NVENC/QSV/AMF, fallback x264) emitindo RTP H.264 local; motivo: o pipeline de mídia anterior era simulado e libwebrtc não é compilável/testável neste fluxo |
| 2026-11-10 | Cursor: desenhado no vídeo pelo ddagrab/gdigrab (draw_mouse=1); canal de dados fica para depois |
