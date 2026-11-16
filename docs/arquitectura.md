# Arquitectura

## Piezas

```
             ┌──────────────── Coordinador (sobremesa) ────────────────┐
             │  Perfil activo → LayoutPlanner → LayoutPlan              │
             │  InputRouter: cursor en pantalla N → equipo X            │
             └──────────────┬───────────────────────────────────────────┘
                            │ canal de control (TCP, JSON)
        ┌───────────────────┴───────────────────┐
        ▼                                       ▼
   Agente sobremesa                        Agente portátil
   PlanApplier (su parte del plan)         PlanApplier (su parte del plan)
   ├ IVirtualDisplayDriver                 ├ IVirtualDisplayDriver
   ├ IStreamSender   ── vídeo ──────────►  ├ IStreamReceiver
   └ IStreamReceiver ◄────────── vídeo ──  └ IStreamSender
```

- **`AgentHost`** (Core): el agente. Escucha en el canal de control y ejecuta lo que pide el coordinador: aplicar un perfil o informar de sus monitores. Funciona siempre en segundo plano: lo lleva dentro la interfaz gráfica, o se arranca con `deskbridge agent`.
- **`Coordinator`** (Core): lo que hace el coordinador. Envía el perfil a todos los agentes, incluido el suyo, y pide los monitores a un equipo remoto.
- **`LayoutPlanner`** (Core): lógica pura. A partir de la configuración y un perfil calcula las pantallas virtuales que debe crear cada equipo, los streams (origen → monitor de destino) y las rutas de entrada. Todos los agentes calculan el mismo plan a partir de la misma configuración.
- **`PlanApplier`** (Core): aplica la parte del plan del equipo local. Primero para los streams, luego ajusta las pantallas virtuales, emite y recibe.
- **`IVirtualDisplayDriver`**: crea y quita monitores virtuales. Implementación prevista: [Virtual Display Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver), un driver IDD firmado y open source. No vamos a escribir un driver propio: firmarlo es un infierno.
- **`IStreamSender` / `IStreamReceiver`**: captura, codificación y reproducción.
  - v1: orquestar **Sunshine** (emite) y **Moonlight** (recibe). Una instancia por pantalla, en puertos distintos.
  - v2 (opcional): pila propia con Desktop Duplication API, NVENC/AMF/QuickSync, UDP y Direct3D. Más control y menos latencia, pero meses de trabajo.
- **`IInputRouter`**: captura el teclado y el ratón en el coordinador y los reenvía cuando el cursor entra en una pantalla prestada, al estilo de Input Leap o Mouse Without Borders. Es la parte que no existe hecha y la que más valor aporta.

## Canal de control

- TCP, un mensaje JSON por línea (`DeskBridge.Core/Control`), con un límite de 1 MiB por mensaje.
- Cada agente escucha. El coordinador se conecta a cada agente, también al suyo por `127.0.0.1`, y envía `applyProfile` con **la configuración completa** y el perfil. La configuración solo se edita en el coordinador.
- Autenticación por desafío y respuesta: el agente envía un nonce y el coordinador responde con HMAC-SHA256(clave, nonce + equipos). La clave compartida está en `deskbridge.local.json` y nunca viaja por la red.
- El agente anuncia su id y el coordinador lo comprueba, así se detecta una IP equivocada en la configuración.
- **Protocolo v2**: autenticación mutua (cada lado demuestra con un HMAC que tiene la clave, así un falso agente no recibe nada) y después cada mensaje va cifrado y autenticado con AES-GCM. Las claves de sesión se derivan con HKDF de la clave compartida y de los nonces de ambos lados, y el nonce es un contador por dirección, así que un mensaje alterado, repetido o reordenado corta la conexión.
- Varias peticiones por conexión, y cualquier equipo puede hablar con cualquier otro (portapapeles y archivos). Solo el coordinador puede aplicar perfiles.

## Decisiones

- **Mismo binario en todos los equipos.** No hay servidor ni cliente fijos: cada agente emite y recibe a la vez según el perfil.
- **Por defecto, la pantalla virtual copia la resolución y la frecuencia del monitor físico** que la va a mostrar, para que la imagen sea exacta píxel a píxel. Cada pantalla prestada puede cambiar la resolución, los Hz y el bitrate (`stream` en la asignación). El planificador avisa si la proporción no coincide, porque saldrían bandas negras, o si se envían más Hz de los que muestra el monitor.
- **Bitrate por defecto**: unos 0,16 bits por píxel y fotograma, que son ~20 Mbps para 1080p60.
- **Las pantallas no asignadas no existen.** Una pantalla virtual sin stream haría que Windows abriera ventanas donde no se ven.
- **El monitor prestado sigue activo en su dueño.** Lo necesita para mostrar el vídeo. El agente lo tapa con una ventana a pantalla completa y evita que el cursor y las ventanas locales entren en él.

## Hoja de ruta

1. **Fase 0, esqueleto** (hecho): configuración, planificador, CLI y tests.
2. **Prueba de concepto manual**: Virtual Display Driver + Sunshine en el portátil y Moonlight en el sobremesa. Medir latencia y nitidez por Wi-Fi 6 a 1080p60.
3. **`IVirtualDisplayDriver` real**: crear y quitar N pantallas de VDD desde el agente y colocarlas en la posición que calcula el planificador.
4. **Canal de control** (hecho): agente en cada equipo, cambio de perfil, lectura remota de monitores; cifrado y autenticación mutua.
5. **Interfaz gráfica** (hecho): mapa con las pantallas virtuales donde quedarán, asignación y ajustes por pantalla, equipos, idioma, modo noche, actualización, bandeja del sistema y arranque con Windows.
6. **Portapapeles y archivos** (hecho): texto e imágenes, y envío de archivos cifrado y verificado. Desactivado por defecto.
7. **Streaming v1**: lanzar y parar instancias de Sunshine y Moonlight por pantalla, con la ventana de Moonlight sin bordes en el monitor correcto y la resolución, los Hz y el bitrate del perfil (`--resolution`, `--fps`, `--bitrate`). El sonido del equipo emisor viaja con el stream.
8. **`IInputRouter`**: paso del ratón entre zonas, teclado y atajos para cambiar de perfil.
9. **Micrófono y cámara**: ver [microfono-camara.md](microfono-camara.md).
10. **Opcional**: pila de streaming propia y servicio SYSTEM para el escritorio seguro (UAC, Ctrl+Alt+Supr).
