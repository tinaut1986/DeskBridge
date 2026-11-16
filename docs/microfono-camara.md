# Micrófono, cámara y sonido entre equipos

Caso típico: trabajas sentado en el sobremesa, con su cascos, micrófono y webcam, y haces una
videollamada (Teams...) que corre en el portátil, en las pantallas 2 y 3 del sobremesa.

| Dirección | Qué hace falta | Cómo |
|---|---|---|
| Sonido del portátil → cascos del sobremesa | Reproducir en el sobremesa el audio del portátil | **Ya viene con el streaming v1**: Sunshine envía el audio del equipo emisor y Moonlight lo reproduce. Sin trabajo extra. |
| Micrófono del sobremesa → portátil | Que Teams en el portátil vea un micrófono que en realidad es el del sobremesa | Capturar, enviar y meterlo en un **micrófono virtual** del portátil. |
| Webcam del sobremesa → portátil | Que el portátil vea una cámara que en realidad es la del sobremesa | Capturar, enviar y alimentar una **cámara virtual** del portátil. |

El problema es el lado del portátil: Windows no deja a una aplicación normal crear un micrófono,
y una cámara solo con condiciones. Hacen falta dispositivos virtuales, y en un portátil de empresa
eso puede chocar con permisos y políticas (Teams gestionado, controladores bloqueados).

## Opción A: compartir los USB por red (para validar antes de programar)

Programas de "USB over IP" hacen que la webcam o los cascos USB del sobremesa aparezcan en el
portátil como si estuvieran enchufados ahí: VirtualHere (servidor gratis para 1 dispositivo) o
usbip-win.

- **A favor:** micrófono y cámara a la vez, sin programar nada, y Teams los ve como dispositivos reales.
- **En contra:**
  - mientras están "prestados", el sobremesa no puede usarlos;
  - la webcam por Wi-Fi puede ir justa, porque USB isócrono no tolera bien la latencia variable;
  - instalar su controlador en el portátil necesita permisos de administrador.

Recomendación: probar esto primero. Si basta, DeskBridge solo tendría que conectar y desconectar
esos dispositivos al cambiar de perfil.

## Opción B: implementación propia en DeskBridge

### Micrófono (esfuerzo moderado: 1-2 semanas)

1. **Sobremesa:** captura WASAPI del micrófono (NAudio), Opus a 48 kHz y 20 ms (Concentus, en C#),
   y envío por UDP cifrado. La clave se deriva de la sesión del canal de control, igual que ahora.
2. **Portátil:** decodificación, búfer de jitter pequeño (40-60 ms) y reproducción en la entrada de
   un cable de audio virtual (**VB-CABLE**, gratuito, requiere instalar su controlador). En Teams
   se elige "CABLE Output" como micrófono.
3. En el perfil: "micrófono de X → Y".

Latencia esperada por LAN: 40-80 ms, aceptable para llamadas.

### Cámara (esfuerzo alto: varias semanas)

1. **Sobremesa:** captura de la webcam (Media Foundation), H.264 por hardware y envío, igual que
   una pantalla pequeña.
2. **Portátil:** Windows 11 tiene **cámara virtual sin controlador** (`MFCreateVirtualCamera`), pero
   exige un *media source* COM propio (C++ o C# con mucho interop) registrado en el sistema. Es la
   parte más delicada y la que más choca con equipos gestionados.
3. Alternativa más simple: **OBS Virtual Camera** en el portátil, alimentada con el vídeo recibido.
   Funciona, pero depende de tener OBS instalado.

## Qué necesito saber para seguir

- ¿Qué aplicación usarías (Teams, Zoom, Meet en navegador)? Teams gestionado por la empresa puede
  ocultar dispositivos virtuales.
- ¿Puedes instalar controladores en el portátil (VB-CABLE, VirtualHere)?
- ¿Webcam y cascos del sobremesa son USB?
