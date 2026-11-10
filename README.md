<div align="center">

# LocalCast

**Transmita a tela do Windows para a Android TV pela rede local.**

Sem internet, sem contas e sem nuvem.

![Windows](https://img.shields.io/badge/Windows-WPF%20.NET%208-0078D4?logo=windows&logoColor=white)
![Android TV](https://img.shields.io/badge/Android%20TV-Kotlin%20%2B%20Compose-3DDC84?logo=android&logoColor=white)
![WebRTC](https://img.shields.io/badge/WebRTC-H.264-333333?logo=webrtc&logoColor=white)
![Status](https://img.shields.io/badge/status-em%20desenvolvimento-orange)

</div>

---

## Recursos

- **Descoberta automática** da TV na rede via mDNS, com opção de IP manual.
- **Pareamento por PIN** temporário de 4 dígitos.
- **Vídeo P2P** por WebRTC (H.264) direto entre os dois aparelhos, sem servidor.
- **Aceleração por hardware** no encoder: NVENC (NVIDIA), QSV (Intel) ou AMF (AMD), com x264 como reserva.
- **Baixa latência**: CBR, sem B-frames e cursor desenhado no vídeo.
- **Decodificação por hardware** na TV e exibição em tela cheia.

## Como funciona

```
┌────────────────────┐   mDNS + WebSocket (PIN)   ┌────────────────────┐
│      Windows       │ ◄────────────────────────► │     Android TV     │
│                    │                            │                    │
│ ffmpeg (ddagrab)   │                            │  NsdManager        │
│  └─ H.264 (HW)     │      WebRTC (RTP/H.264)    │  PeerConnection    │
│     └─ RTP local   │ ─────────────────────────► │   └─ MediaCodec    │
│        └─ SIPSorcery                            │      └─ tela cheia │
└────────────────────┘                            └────────────────────┘
```

| Módulo | Pasta | Tecnologia |
| --- | --- | --- |
| Transmissor | [`Windows/`](Windows) | WPF .NET 8, ffmpeg, [SIPSorcery](https://github.com/sipsorcery-org/sipsorcery) |
| Receptor | [`AndroidTV/`](AndroidTV) | Kotlin, Jetpack Compose for TV, WebRTC |
| Documentação | [`Documentation/`](Documentation) | Arquitetura, decisões e plano de desenvolvimento |

## Como usar

1. Instale o app da TV (`AndroidTV/`, pelo Android Studio). Ele mostra o IP e o PIN.
2. No Windows, instale o `LocalCast-Setup.exe`, escolha a TV e digite o PIN.
3. A tela do PC aparece na TV.

> Os dois aparelhos precisam estar na mesma rede local.

## Compilar

**Requisitos:** .NET 8 SDK, Android Studio e, para o instalador, o [Inno Setup](https://jrsoftware.org/isinfo.php).

```powershell
cd Windows
powershell -ExecutionPolicy Bypass -File tools\get-ffmpeg.ps1   # baixa o ffmpeg
dotnet build
dotnet test
```

Gerar o instalador (`Windows\dist\LocalCast-Setup.exe`):

```powershell
powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1
```

## Estrutura

```
localcast/
├── AndroidTV/        App da TV (Kotlin + Compose)
├── Windows/          App do PC (WPF), testes, instalador e scripts
├── Documentation/    Arquitetura, decisões e plano
└── TASKS.md          Estado das fases do projeto
```

## Status

Fases 0 a 4, 7 e 8 concluídas. A captura e transmissão da tela (fase 5) e a otimização de latência (fase 6) estão implementadas e aguardam validação em hardware. Detalhes em [`TASKS.md`](TASKS.md).

## Notas

- O ffmpeg é baixado de [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds) (build GPL) e não é distribuído neste repositório.
- `Windows/native/` é um esqueleto da antiga ideia de DLL com libwebrtc e não é usado hoje.
