# Arquitectura

## Piezas

```
        ┌──────────── Coordinador (sobremesa: teclado, ratón, 3 monitores) ────────────┐
        │  Perfil → LayoutPlanner → LayoutPlan (el mismo en todos los equipos)          │
        │  Ganchos de teclado y ratón → CoordinatorHandover → a quién va la entrada      │
        │  VideoReceiver (monitor prestado) · AudioPlayer (sonido) · AudioSender (micro) │
        └───────────▲───────────────────────────────────────────────────────▲──────────┘
                    │ sesión de control (TCP, JSON cifrado)                  │ conexiones de vídeo/sonido
                    │ la abre el portátil (su cortafuegos bloquea entradas)  │ (binarias, cifradas)
        ┌───────────┴────────────────── Portátil ────────────────────────────┴──────────┐
        │  Pantalla virtual (Virtual Display Driver) → VideoSender (captura + H.264)     │
        │  InputReceiver → SendInput · AudioSender (lo que suena) · AudioPlayer (VB-CABLE) │
        └─────────────────────────────────────────────────────────────────────────────────┘
```

- **`AgentHost`** (Core): el agente, dentro de la aplicación. Atiende la sesión de control: aplicar un perfil,
  informar de los monitores, portapapeles, archivos, entrada, vídeo, mantenimiento.
- **`LayoutPlanner`** (Core): lógica pura. De la configuración y un perfil saca las pantallas virtuales de cada
  equipo y dónde van, los streams (origen → monitor de destino), las rutas de entrada y la pantalla principal
  de cada equipo. Todos los agentes calculan el mismo plan.
- **`MttVirtualDisplays`** (Platform): crea y coloca los monitores del
  [Virtual Display Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver) por su pipe. Solo cambia el
  número de monitores cuando hace falta (recargar el driver muchas veces lo deja en error).
- **Vídeo propio** (Platform/Windows/Video): DXGI Desktop Duplication → procesador de vídeo D3D11 (BGRA→NV12) →
  H.264 por hardware (Media Foundation; Quick Sync, NVENC…) → `VideoChannel` → DXVA → procesador (NV12→BGRA) →
  ventana con swap chain *flip model* a pantalla completa. Detalles que importan:
  - nunca esperar dentro de `AcquireNextFrame` (bloquea el dispositivo); `Present(0)` y latencia de cola 1;
  - Intel necesita conservar la petición de entrada tras `MF_E_NOTACCEPTING`;
  - el H.264 de 1080 líneas se decodifica en 1088: se recorta al tamaño enviado;
  - **espacios de color explícitos** en los dos extremos (RGB 0-255; YUV BT.709 16-235). Sin eso Intel y NVIDIA
    usan valores por defecto distintos y la imagen sale lavada;
  - la captura tiene **su propio dispositivo** D3D11 y espera dentro de `AcquireNextFrame` a que Windows tenga
    imagen nueva (esperar ahí bloquea su dispositivo, no el del codificador); el codificador la lee por una
    textura compartida con *keyed mutex*. Así cada imagen se codifica en cuanto Windows la compone;
  - **solo se codifican los fotogramas en que cambia la pantalla** (Desktop Duplication lo dice), más 100 ms
    después del último cambio (el codificador devuelve cada fotograma uno más tarde, y así afina la imagen) y
    uno por segundo con la pantalla quieta. El puntero va aparte: el receptor guarda la última imagen y la
    vuelve a pintar con el puntero en su sitio. Un cursor de texto parpadeando ya cuesta unos 13 fps;
  - en el receptor, un hilo decodifica y otro muestra la imagen más reciente; la espera al refresco del monitor
    se hace con un *swap chain* con espera (`FrameLatencyWaitableObject`), nunca dentro de `Present`, que bloqueaba
    todo el dispositivo (también el decodificador) y congelaba el vídeo al arrastrar una ventana;
  - **puntero local**: el coordinador hace las mismas cuentas que el otro equipo con cada movimiento
    (`PointerMirror`) y pinta el puntero al momento, como el Escritorio remoto. Cada tanda de movimientos va
    numerada y el otro equipo contesta (hasta 10 veces por segundo) dónde quedó de verdad el cursor tras una
    tanda (`InputCursorMessage`): el coordinador desplaza su copia esa diferencia (redondeos de Windows, la
    pantalla de bloqueo, un touchpad). La forma del puntero viaja con su punto activo;
  - opción para mostrar el vídeo sin esperar al refresco del monitor (*tearing*; `AllowTearing`);
  - los hilos de captura, codificación, vídeo y ganchos de ratón van en el planificador multimedia de Windows
    (MMCSS) y el proceso con prioridad superior a la normal, para que un equipo cargado no dé tirones;
  - probado sin ganancia (rama `feat/encoder-flush`): meter una copia tras cada imagen para que el codificador la
    suelte antes, y pedirle máxima velocidad. Quick Sync tarda ~10 ms por imagen en movimiento y no acepta la
    copia hasta acabar la anterior;
  - un fotograma no se pinta si ya ha llegado el siguiente, ni si va más de 80 ms por detrás del mejor de los
    últimos 30 s (`LateFrames`): así no se acumula cola y la deriva de los relojes no importa.
- **Entrada** (Core/Input + Gui/Sharing): ganchos de bajo nivel en el coordinador. `InputTarget` describe cada
  pantalla de otro equipo vista desde el coordinador (en el escritorio, con escala por DPI, o prestada en un
  monitor). El cursor se mueve de verdad en el otro equipo (para que se arrastren ventanas) dentro de su "área";
  entre sus pantallas sigue la geometría del escritorio del coordinador (`AreaDesk`). Las ventanas del
  coordinador encima del vídeo son "agujeros" por los que vuelve. Arrastrando (botón pulsado allí), el cursor
  salta los huecos vacíos del escritorio hasta la pantalla más cercana de ese equipo, y si vuelve al
  coordinador el otro equipo mantiene el botón pulsado (`HoldsDrag`): el arrastre sigue al volver a entrar, y si
  se suelta en el coordinador, este le manda soltarlo (un poco dentro del borde, para que Windows no maximice
  la ventana). Si se pierde la conexión, el otro equipo lo suelta solo. El puntero que no se usa se oculta. Si
  Windows descarta el movimiento simulado (una app de administrador en primer plano), el cursor se coloca con
  `SetCursorPos`.
- **Sonido** (Platform/Windows/Audio, NAudio + Concentus): captura WASAPI *loopback* de la salida
  predeterminada (sigue funcionando aunque la salida esté silenciada, así que el portátil se silencia mientras
  envía), Opus en tramas de 10 ms (128 kbps; voz a 48 kbps), reproducción con búfer corto que se vacía si se
  acumula. En una salida 7.1 se reproduce en estéreo (un formato de más de 2 canales sin máscara se rechaza).
  El micrófono va al revés y se reproduce en "CABLE Input" de VB-CABLE; mientras tanto "CABLE Output" es el
  micrófono predeterminado (`IPolicyConfig`, la interfaz interna que usa el panel de sonido) y luego se devuelve
  el del usuario.
- **Pantalla principal**: el coordinador hace principal otra pantalla mientras la suya muestra otro equipo; cada
  equipo puede elegir la suya por perfil (en el portátil, incluso su pantalla virtual). Cambiar la principal
  mueve el origen de Windows: las disposiciones guardan las coordenadas de la colocación original y
  `DeskOffset` convierte (entrada, vídeo, *Detectar*). La original se guarda en un fichero y se devuelve al cerrar.
- **Escritorio seguro** (pantalla de bloqueo, avisos de UAC), opcional: un programa normal no puede capturarlo
  ni escribir en él. El servicio `DeskBridgeHelper` (SYSTEM) mantiene un ayudante (`--secure-helper`) en la
  sesión de la consola, con su token; el ayudante sigue el escritorio de entrada (`OpenInputDesktop`,
  `SetThreadDesktop`). DeskBridge le habla por una *named pipe* (asíncrona: en una síncrona una escritura esperaba
  detrás de la lectura) que solo abren SYSTEM y Administradores con el token elevado: un programa sin permisos
  no puede usarlo para pulsar "Sí" en un aviso de UAC. Mientras el escritorio seguro tiene la entrada, DeskBridge
  le pasa teclado y ratón, y el ayudante captura y codifica la pantalla enviada y la manda por una segunda pipe;
  DeskBridge la reenvía por el canal cifrado mientras su propio emisor calla (después reenvía formato y fotograma
  clave). El servicio ejecuta una copia en `Program Files` (un archivo que el usuario pudiera cambiar daría SYSTEM
  a cualquier programa suyo), que DeskBridge como administrador pone al día en cada actualización.
- **Rebordes de color**: la ventana de vídeo dibuja el suyo en la GPU (`ClearView`); el resto de pantallas
  llevan cuatro tiras Win32 opacas de 3 px. Una ventana transparente a pantalla completa hacía que Windows
  recompusiera todos los monitores y el vídeo bajaba a 20-30 fps.

## Canales

- **Control**: TCP, un mensaje JSON por línea. Autenticación mutua con HMAC de la clave compartida, claves de
  sesión por HKDF y cada mensaje cifrado con AES-GCM (nonce = contador por dirección). La clave nunca viaja.
- **Vídeo y sonido** (`VideoChannel`): la misma autenticación y luego paquetes binarios cifrados (longitud,
  AES-GCM). Los abre siempre el equipo que no es el coordinador (clave de stream: `laptop/v1->skynet/3`,
  `audio`, `mic`). Tipos: formato, fotograma, puntero, forma del puntero, sonido, petición de fotograma clave,
  "mostrado" (para medir la latencia) y fin.
- **Mantenimiento remoto** (con la opción activada en el equipo): el coordinador puede instalar una versión nueva
  (siempre en `%LOCALAPPDATA%\DeskBridge\bin\DeskBridge.exe`, así el cortafuegos no vuelve a preguntar), pedir el
  log, sondear el driver, capturar la interfaz…

## Decisiones

- **Mismo binario en todos los equipos**, sin servidor ni cliente fijos; el coordinador es el que tiene el
  teclado y el ratón.
- **La pantalla virtual copia la resolución y la frecuencia del monitor que la muestra**; cada pantalla prestada
  puede cambiar resolución, Hz y bitrate (por defecto ~20 Mbps para 1080p60; una pantalla quieta no envía casi nada).
- **Lo que comparte un portátil de empresa está desactivado por defecto** (portapapeles, archivos, sonido,
  micrófono, actualizaciones remotas).
- **Sin drivers propios**: VDD y VB-CABLE son de terceros, firmados; DeskBridge se instala por usuario. El
  único servicio es el opcional del escritorio seguro.

## Hoja de ruta

Hecho: configuración y planificador; canal de control y de vídeo cifrados (vídeo solo cuando cambia la
pantalla); interfaz con barra lateral en 11
idiomas; portapapeles y archivos; teclado y ratón; pantallas virtuales; vídeo propio portátil → sobremesa;
sonido y micrófono; ejecutar como administrador; pantalla principal por equipo; rebordes de color; instalación
por usuario; actualizaciones (GitHub y remotas); puntero local; captura sincronizada; pantalla de bloqueo y
avisos de UAC (servicio opcional).

Pendiente:
1. **Fase 4**: una pantalla del sobremesa mostrada en el portátil (el sentido contrario).
2. **Color 4:4:4** para el texto de colores (HEVC/H.264 4:4:4 donde la GPU lo permita).
3. **Cámara**: ver [microfono-camara.md](microfono-camara.md).
4. **Android**: tablet o móvil como pantalla extra o como equipo controlado; ver [android.md](android.md).
5. Linux / Avalonia: en pausa.
