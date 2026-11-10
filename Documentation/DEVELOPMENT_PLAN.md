# LocalCast - Plano de Desenvolvimento

Este plano detalha o roteiro de implementação incremental do LocalCast, estruturado em 8 fases com critérios de validação rigorosos focados em **Latência Mínima**, **Estabilidade** e **Facilidade de Uso**.

---

## Fase 1: Estrutura dos Projetos e Ambiente
* **Objetivo**: Padronizar as soluções do Windows e Android TV, validar compilação limpa e dependências de build.
* **Tarefas**:
  - Validar estrutura da solução .NET 8 no Windows (`LocalCast.sln`, `LocalCast.Core`, `LocalCast.App`, `LocalCast.Core.Tests`).
  - Ajustar configuração do Gradle e SDK Android TV com dependências do WebRTC, Ktor e Compose for TV.
  - Implementar biblioteca compartilhada de mensagens de protocolo e constantes em C# e Kotlin.
* **Entregável**: Build e testes unitários executando com sucesso em ambas as plataformas.

---

## Fase 2: Aplicativo Android TV - Descoberta e Sinalização Local
* **Objetivo**: Tornar a TV localmente descobrível via mDNS e fornecer o servidor de sinalização WebSocket.
* **Tarefas**:
  - Implementar anúncio de serviço via `NsdManager` (`_localcast._tcp.local`) com registros TXT de status e porta.
  - Implementar servidor WebSocket local embutido (Ktor HTTP/WebSocket engine) ouvindo na porta local `8765`.
  - Criar gerador de PIN criptográfico de 4 dígitos com timer de expiração (120 segundos) e ciclo de vida visual na tela.
  - Tratamento de concorrência: bloquear conexões simultâneas quando o estado estiver em `busy`.
* **Entregável**: Aplicativo Android TV executando, exibindo nome e PIN, anunciando via mDNS e respondendo a conexões WebSocket.

---

## Fase 3: Aplicativo Windows - Descoberta Automática e Fallback
* **Objetivo**: Localizar TVs disponíveis na rede sem intervenção manual do usuário.
* **Tarefas**:
  - Implementar resolvedor mDNS no Windows para descobrir serviços `_localcast._tcp.local`.
  - Mecanismo de reativação periódica e detecção de saída/offline de TVs.
  - Mecanismo de conexão manual de contingência (endereço IP ou código direto) para redes corporativas com bloqueio de multicast.
  - Modelos de dados e testes de integração de descoberta de rede.
* **Entregável**: Aplicação Windows listando TVs na rede com status de disponibilidade em tempo real.

---

## Fase 4: Pareamento e Negociação WebRTC
* **Objetivo**: Concluir o handshake de segurança com PIN e estabelecer a conexão WebRTC ponto a ponto na LAN.
* **Tarefas**:
  - Implementar fluxo de autenticação por PIN no cliente Windows com envio ao servidor WebSocket da TV.
  - Validação do PIN no Android TV, geração de token de sessão e bloqueio de outras tentativas.
  - Inicialização de `PeerConnection` sem servidores STUN/TURN (apenas candidatos do tipo `host`).
  - Troca de SDP Offer/Answer e ICE candidates via WebSocket.
  - Gerenciamento de estado da conexão (`connecting`, `connected`, `disconnected`, `failed`).
* **Entregável**: Conexão WebRTC ponto a ponto ativa entre o Windows e a Android TV na rede local.

---

## Fase 5: Captura e Transmissão de Vídeo
* **Objetivo**: Capturar a tela do Windows e transmiti-la com reprodução em tela cheia na Android TV.
* **Tarefas**:
  - Implementar captura de tela no Windows utilizando Windows Graphics Capture (WGC) e Desktop Duplication API (DXGI).
  - Configurar pipeline de codificação de vídeo H.264 e empacotamento em `VideoTrack` WebRTC.
  - Implementar decodificador e renderizador de vídeo no Android TV com `SurfaceViewRenderer`.
  - Suporte a resoluções 720p e 1080p a 30 e 60 FPS.
* **Entregável**: Transmissão contínua da tela do Windows para a televisão com baixa latência.

---

## Fase 6: Otimização Extrema de Latência e Aceleração por Hardware
* **Objetivo**: Atingir latência mínima de ponta a ponta sem degradação visual.
* **Tarefas**:
  - Integrar codificador H.264 por hardware no Windows via Media Foundation MFT (modo Low Latency, CBR, zero B-frames, zero-copy D3D11).
  - Ativar decodificação por hardware no Android TV via MediaCodec com `KEY_LOW_LATENCY = 1`.
  - Otimização do jitter buffer receptor (atraso de buffer alvo de 0 a 10 ms).
  - Implementação e comparação de renderização do cursor do mouse: modo vídeo acelerado vs. canal de dados (WebRTC DataChannel).
* **Entregável**: Movimento do mouse e resposta interativa fluidos sem atraso perceptível (< 30-50 ms ponta a ponta na LAN).

---

## Fase 7: Interfaces Definitivas e Experiência do Usuário
* **Objetivo**: Entregar interface profissional, minimalista e moderna em ambas as pontas.
* **Tarefas**:
  - **Windows**: Interface WPF moderna inspirada no Windows 11 (Fluent Design, cantos arredondados, visualizador de status, controle de transmissão e tempo decorrido).
  - **Android TV**: Tela inicial limpa com suporte total a controle remoto D-pad, transição automática para reprodução em tela cheia e overlays discretos de reconexão/aviso.
  - Diálogos intuitivos de inserção de PIN e gerenciamento de erros de conexão.
* **Entregável**: Interfaces finais operando de ponta a ponta com navegação fluida e design consistente.

---

## Fase 8: Testes, Métricas de Diagnóstico e Documentação
* **Objetivo**: Validar estabilidade sob estresse, métricas em tempo real e documentar todo o ciclo de vida da solução.
* **Tarefas**:
  - Implementar painel de diagnóstico em tempo real (FPS de captura/exibição, bitrate, RTT, jitter, pacotes perdidos).
  - Criar suite de testes de estabilidade (transmissões longas de 60+ minutos, desconexão súbita e reconexão rápida).
  - Testes de alternância entre múltiplos computadores (concorrência e liberação de TV).
  - Finalizar documentação técnica e guias de build para ambiente de desenvolvimento.
* **Entregável**: Sistema estável, testado e documentado para uso em produção.
