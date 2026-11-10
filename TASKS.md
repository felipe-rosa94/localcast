# Tasks

- [x] Fase 0: spike de viabilidade (captura, encoder HW, WebRTC, decoder)
- [x] Fase 1: estrutura dos projetos (arquitetura consolidada, compilação validada, suíte de testes ativa)
- [x] Fase 2: Android TV com descoberta mDNS (NsdManager) e sinalização local WebSocket (Java-WebSocket)
- [x] Fase 3: Windows com descoberta automática (MdnsDiscoveryService) e fallback manual
- [x] Fase 4: pareamento por PIN temporário (PinManager/SignalingClient) e validação de concorrência
- [ ] Fase 5: captura e transmissão da tela — reimplementada em 2026-11-10 (ffmpeg ddagrab + encoder HW → RTP → SIPSorcery; PeerConnection na TV). Código escrito, falta compilar e validar em hardware
      (a versão anterior era simulada: SimulatedScreenCapture + NAL falso, sem WebRTC)
- [ ] Fase 6: latência e aceleração por hardware — CBR, B-frames=0 e cursor no vídeo configurados nos args do ffmpeg; medir latência real na TV TCL
- [x] Fase 7: interfaces definitivas (WPF Windows 11 Fluent Design / Android TV Jetpack Compose)
- [x] Fase 8: testes, diagnóstico e documentação (9 testes automatizados passando, documentação completa)
