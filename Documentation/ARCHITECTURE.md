# LocalCast - Arquitetura Técnica

## 1. Visão Geral da Arquitetura

O **LocalCast** é projetado para transmissão de tela em tempo real ponto a ponto (peer-to-peer) exclusivamente em rede local (LAN), sem dependência de internet, servidores de nuvem, STUN ou TURN externos.

```
+-----------------------------------------------------------------------------------+
|                              REDE LOCAL (LAN / Wi-Fi)                             |
|                                                                                   |
|  [Windows Sender]                                           [Android TV Receiver] |
|  +--------------------+                                     +-------------------+ |
|  | Captura D3D11 / WGC|                                     | mDNS Announcer    | |
|  |         |          |                                     | (NsdManager)      | |
|  |         v          |                                     |         |         | |
|  | HW Encoder (MF MFT)|                                     |         v         | |
|  | (H.264 CBR, no B)  |                                     | Servidor WebSocket| |
|  |         |          |      mDNS Discovery (_localcast)    | (Ktor Local Port) | |
|  |         v          | <---------------------------------- |         |         | |
|  | mDNS Browser       |                                     |         v         | |
|  |         |          |     Sinalização & PIN Auth (WS)     | Pareamento PIN    | |
|  |         +--------> | ==================================> | (4 dígitos / lock)| |
|  |                    |                                     |         |         | |
|  | WebRTC PeerConnection                                    | WebRTC PeerConnection
|  | (Host Candidates)  |       Mídia H.264 via RTP / UDP     | (Host Candidates) | |
|  |                    | ----------------------------------> |         |         | |
|  | DataChannel Cursor | < - - - - - - - - - - - - - - - - > |         v         | |
|  | & Telemetria       |                                     | HW Decoder        | |
|  +--------------------+                                     | (MediaCodec)      | |
|                                                             |         |         | |
|                                                             |         v         | |
|                                                             | SurfaceView / UI  | |
|                                                             +-------------------+ |
+-----------------------------------------------------------------------------------+
```

---

## 2. Componentes e Decisões de Engenharia

### 2.1. Captura de Tela no Windows
* **Windows Graphics Capture (WGC - `Windows.Graphics.Capture`)**:
  * Captura direta na GPU via Direct3D11 (`ID3D11Texture2D`), eliminando cópia para a memória de sistema (RAM/CPU).
  * Controle de captura do cursor integrado (`IsCursorCaptureEnabled`) para validação comparativa entre cursor no frame vs. cursor via DataChannel.
  * Suporte a múltiplos monitores e janelas específicas com sincronização com a taxa de atualização do DWM.
* **Fallback DXGI Desktop Duplication API (DDAPI)**:
  * Acesso a `AcquireNextFrame` com metadados de dirty rects e `DXGI_OUTDUPL_POINTER_SHAPE_INFO`.

### 2.2. Codificação de Vídeo por Hardware (Windows)
* **Windows Media Foundation (MF H.264 MFT)**:
  * Utiliza hardware encoder do chip gráfico (Intel QuickSync, NVIDIA NVENC, AMD AMF) através de MFTs nativos do Windows.
  * Entrada: `IMFSample` vinculado diretamente à textura D3D11 (`ID3D11Texture2D`) via `IMFDXGIDeviceManager` (pipeline zero-copy).
  * Parâmetros de baixíssima latência:
    * `CODECAPI_AVLowLatencyMode` = `VARIANT_TRUE`.
    * `CODECAPI_AVEncCommonRateControlMode` = `eAVEncCommonRateControlMode_CBR`.
    * `CODECAPI_AVEncMPVDefaultBPictureCount` = `0` (zero B-frames para eliminar atraso de decodificação).
    * `CODECAPI_AVEncMPVGOPSize` = `0` ou grande, com emissão de IDR frames acionada sob demanda via RTCP PLI/FIR.

### 2.3. WebRTC Local e ICE Puro
* **Zero Cloud / Zero STUN-TURN**:
  * `RTCConfiguration.iceServers = []`.
  * Filtro de candidatos ICE restrito a candidatos do tipo `host` (IPv4 local).
  * Elimina latência e timeouts associados a consultas STUN/TURN externas.
* **Otimização de Jitter Buffer**:
  * Jitter buffer receptor configurado para atraso alvo mínimo (0 a 10 ms em rede local).
  * Mecanismo NACK habilitado para recuperação rápida de pacotes sem acúmulo de buffer.

### 2.4. Descoberta e Sinalização Local
* **Descoberta via mDNS / DNS-SD**:
  * Tipo de serviço: `_localcast._tcp.local.`.
  * Parâmetros TXT:
    * `name`: Nome legível da TV (ex: "LocalCast TV - Sala de Reuniões").
    * `state`: `available` ou `busy`.
    * `port`: Porta do servidor de sinalização WebSocket.
    * `ver`: Versão do protocolo (`1`).
* **Sinalização WebSocket Integrada**:
  * Executada pelo aplicativo Android TV em porta local (padrão `8765`).
  * Troca de mensagens JSON leves: autenticação, SDP Offer/Answer, ICE Candidates e encerramento.
* **Fallback Manual**:
  * Conexão direta por IP:Porta ou código de pareamento curto para redes que bloqueiam multicast/mDNS.

### 2.5. Pareamento, Segurança e Concorrência Multiusuário
* **Pareamento por PIN Temporário**:
  * PIN aleatório de 4 dígitos gerado com `SecureRandom`.
  * Tempo de vida de 120 segundos com renovação automática após expiração ou desconexão.
  * Limite de 5 tentativas consecutivas incorretas antes de bloquear temporariamente o cliente.
* **Bloqueio de Sessão Única (Single Active Host)**:
  * Uma TV só aceita uma transmissão ativa por vez.
  * Durante a transmissão, o estado mDNS é atualizado para `busy`.
  * Tentativas de conexão adicionais recebem resposta imediata `BUSY` informando o nome do computador transmissor.

### 2.6. Decodificação e Exibição no Android TV
* **WebRTC Native Android + MediaCodec**:
  * Decodificador H.264 por hardware (`MediaCodecVideoDecoderFactory` / `DefaultVideoDecoderFactory`).
  * Modo de baixa latência ativado (`MediaFormat.KEY_LOW_LATENCY = 1`).
  * Renderização direta em hardware via `SurfaceViewRenderer` conectado ao contexto EGL compartilhado.

### 2.7. Pipeline do Cursor do Mouse
1. **Modo Vídeo Integrado**: Cursor desenhado diretamente na superfície D3D11 pelo subsistema de captura WGC/DXGI (sincronização perfeita de quadros).
2. **Modo Canal de Dados Dedicado (WebRTC DataChannel)**:
   * Coordenadas $(X, Y)$ normalizadas $(0.0 - 1.0)$ enviadas em alta frequência (60-120 Hz) com timestamp do emissor.
   * Sprite do cursor renderizado em camada de sobreposição (overlay) no Android TV.
   * Suporte a medição comparativa de fluidez e latência visual.

---

## 3. Protocolo de Sinalização (JSON sobre WebSocket)

### 3.1. Autenticação e Pareamento
```json
// Cliente -> TV
{
  "type": "auth",
  "pin": "1234",
  "clientName": "Dev-Notebook-01",
  "version": "1"
}

// TV -> Cliente (Sucesso)
{
  "type": "auth_ok",
  "sessionToken": "d8f3e2...",
  "tvName": "LocalCast TV - Sala de Reuniões"
}

// TV -> Cliente (Erro / Ocupada)
{
  "type": "error",
  "code": "BUSY",
  "message": "TV ocupada em transmissão por Dev-Notebook-02"
}
```

### 3.2. Troca de SDP e ICE
```json
// Cliente -> TV (SDP Offer)
{
  "type": "sdp_offer",
  "sdp": "v=0\r\no=..."
}

// TV -> Cliente (SDP Answer)
{
  "type": "sdp_answer",
  "sdp": "v=0\r\no=..."
}

// Bidirecional (ICE Candidate)
{
  "type": "ice_candidate",
  "candidate": "candidate:1 1 UDP 2122260223 192.168.1.100 50000 typ host",
  "sdpMid": "0",
  "sdpMLineIndex": 0
}
```

### 3.3. Controle de Sessão e Telemetria
```json
// Cliente / TV (Encerramento)
{
  "type": "disconnect",
  "reason": "user_stop"
}

// Cliente -> TV (Canal de Dados - Cursor)
{
  "x": 0.542,
  "y": 0.321,
  "visible": true,
  "t": 1731154000123
}
```
