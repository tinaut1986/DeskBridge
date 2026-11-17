# Tablet o móvil Android como un equipo más

Estado: **diseño**, sin código todavía.

Caso típico: el sobremesa (coordinador, con teclado y ratón) y una tablet Android al lado. La tablet entra en la
Disposición como cualquier otro equipo y cada perfil decide qué hace su pantalla:

| Modo | Qué muestra la tablet | Teclado y ratón del sobremesa | En la Disposición |
|---|---|---|---|
| **1. Pantalla extra** | Una pantalla virtual del sobremesa | Llegan solos: esa pantalla es parte del escritorio del sobremesa | Pantalla de la tablet asignada al sobremesa (`DisplayAssignment` con `UsedBy` = coordinador) |
| **2. Su propia pantalla** | Android, normal | Pasan a la tablet al cruzar el borde, como con el portátil | Pantalla de la tablet colocada en el escritorio (`DeskPositions`), sin asignar |
| *3. Android en un monitor del PC* (más adelante) | — | Como en el modo 2 | Monitor del sobremesa asignado a la tablet |

Los modos 1 y 2 encajan en el modelo actual sin conceptos nuevos: son las dos formas que ya tiene un monitor de
otro equipo (prestado o en el escritorio). Lo nuevo es un equipo que no es Windows.

## Piezas

```
┌──────────────────── Sobremesa (coordinador) ─────────────────────┐
│ Modo 1: pantalla virtual propia → captura → H.264 → VideoChannel │
│ Modo 2: ganchos de teclado y ratón → InputEvents a la tablet      │
│ Inyecta los toques que vuelven de la tablet (modo 1)             │
└───────────▲──────────────────────────────────────▲───────────────┘
            │ control (TCP, JSON cifrado)          │ vídeo (binario, cifrado)
            │ lo abre la tablet                    │ lo abre la tablet
┌───────────┴──────────── App Android ─────────────┴───────────────┐
│ Agente (servicio en primer plano): sesión de control, pantallas  │
│ Modo 1: MediaCodec → SurfaceView a pantalla completa + puntero   │
│         toques → de vuelta al sobremesa                          │
│ Modo 2: servidor de entrada con permisos de shell (ADB):          │
│         teclado UHID + ratón inyectado                           │
└──────────────────────────────────────────────────────────────────┘
```

La tablet abre siempre las conexiones, como hoy el portátil. Así funciona aunque Android no acepte conexiones
entrantes (redes de invitados, datos móviles, ahorro de batería).

## Protocolo: lo que la app tiene que hablar

Se porta tal cual a Kotlin; nada cambia en el lado Windows salvo lo marcado como nuevo.

- **Saludo**: `challenge` → `auth` → `welcome` en JSON por líneas; pruebas HMAC-SHA256 con las cadenas de
  `ControlAuth` (`deskbridge/v{Version}/client|…`). Claves de sesión con HKDF-SHA256. Android no trae HKDF:
  son dos HMAC, se escribe a mano.
- **Sesión de control**: `SessionFrame` cifrados con AES-GCM. El nonce son 12 bytes con el contador por
  dirección en little-endian y la etiqueta 16 bytes. Discriminador JSON `type` con los mismos nombres
  (`startVideo`, `displays`, `inputEvents`…).
- **Vídeo**: `VideoChannel` (longitud de 4 bytes + AES-GCM). La tablet recibe `Format`, `Frame`, `Pointer` y
  `PointerShape`, y envía `KeyFrameRequest`, `Shown` y `Stop`.
- **Vectores de prueba compartidos**: un test de C# genera, con una clave fija, el saludo, las claves de sesión
  y unos paquetes cifrados en un JSON. Los tests de la app Android lo leen. Así un cambio en el protocolo rompe
  los dos lados a la vez.

### Cambios del lado Windows

1. **`MachineConfig.Platform`** (`"windows"` por defecto, `"android"`). La interfaz y el planificador saben así
   que ese equipo no crea pantallas virtuales ni recibe `SendInput`, y no ofrecen lo que no puede hacer
   (sonido con VB-CABLE, ejecutar como administrador, mantenimiento remoto con el `.exe`, escritorio seguro).
2. **El coordinador como emisor de vídeo** (modo 1). Hoy el que presta su pantalla es siempre el otro equipo.
   Aquí el coordinador crea la pantalla virtual, la captura y la envía. Es lo mismo que la **Fase 4** de la hoja
   de ruta (pantalla del sobremesa en el portátil): hecho una vez, sirve para los dos. El `StartVideoMessage` ya
   tiene `Send`; a la tablet le llega con `Send = false`.
3. **`DisplaysMessage` desde Android**: un único monitor con su resolución en píxeles, los Hz y la escala
   (`densityDpi / 160 × 100`). *Detectar* funciona igual.
4. **Toques de vuelta** (modo 1): mensaje nuevo `touchEvents` (id de dedo, abajo/mover/arriba, x, y, presión,
   lápiz o dedo) en coordenadas de la pantalla virtual. El coordinador lo inyecta en su propio escritorio con
   `InjectSyntheticPointerInput` (Windows 10 1809+), que da multitáctil y lápiz con presión. Opción "toques como
   ratón" para apps que no se llevan bien con el táctil.
5. **Emparejar con QR**: la página *Conexión y seguridad* muestra un QR con dirección, puerto y clave
   compartida, y la app lo escanea. La clave se guarda cifrada con el Android Keystore.

## Modo 1: pantalla extra

**En el sobremesa**: el planificador crea una `VirtualDisplaySpec` en el coordinador con la resolución y los Hz
de la tablet, y un `StreamSpec` del coordinador a la tablet. La pantalla virtual va donde la pongas en la
Disposición, y el ratón llega a ella como a cualquier monitor propio: no hay `InputRoute`.

**En la tablet**:
- `MediaCodec` H.264 sobre la `Surface` de un `SurfaceView`, con `KEY_LOW_LATENCY` (Android 11+) y
  `KEY_PRIORITY = 0`, decodificando en cuanto llega cada fotograma. Se usa el descodificador por hardware; uno
  por software no aguanta 60 fps.
- Las mismas reglas que el receptor de Windows: no pintar un fotograma si ya llegó el siguiente, descartar los
  que van tarde (`LateFrames`, portado), y pedir un fotograma clave al (re)arrancar el descodificador.
- **Recorte**: el H.264 de 1080 líneas llega en 1088. Se recorta con `KEY_CROP_*` / `setCrop` del `SurfaceView`.
- **Color**: BT.709 de rango limitado, explícito (`KEY_COLOR_STANDARD`, `KEY_COLOR_RANGE`), igual que en el
  emisor. Si no, cada fabricante elige el suyo y la imagen sale lavada.
- **Puntero**: una capa encima del vídeo con la forma (`PointerShape`) en la posición (`Pointer`). Desktop
  Duplication no lo incluye en la imagen.
- Pantalla completa inmersiva, sin apagarse (`FLAG_KEEP_SCREEN_ON`), y orientación fija según el perfil.

**Orientación**: girar la tablet cambiaría la resolución de la pantalla virtual, y recargar muchas veces el
Virtual Display Driver lo deja en error. La orientación se fija en el perfil (horizontal o vertical) y la app la
bloquea mientras lo muestra.

**Retraso esperado**: 30-50 ms con Wi-Fi a 5 GHz en una tablet decente, depende sobre todo del descodificador del
fabricante. Por USB (más abajo) se gana estabilidad, no mucho retraso.

## Modo 2: su propia pantalla

El coordinador trata la tablet como al portátil: al cruzar el borde manda `inputEnter`, `inputEvents`,
`inputLeave` e `inputRelease`. La diferencia está en cómo se aplican en Android.

### El problema

Android no deja que una app normal mueva el puntero ni escriba en otras apps. Hay tres vías:

| Vía | Qué permite | Qué cuesta |
|---|---|---|
| **Permisos de shell (ADB)**, como scrcpy | Inyección real de ratón y teclado en todo el sistema; teclado UHID con la distribución que elijas en Android | Activar la *depuración inalámbrica* y emparejar una vez; algunos fabricantes la apagan al reiniciar |
| Servicio de accesibilidad | Toques y gestos simulados (`dispatchGesture`) | Sin hover ni rueda real, puntero dibujado, teclado solo a través de un IME propio y solo en campos de texto |
| Root | Todo | No es razonable pedirlo |

**Decisión: ADB como vía principal; accesibilidad como modo reducido** para quien no quiera activar la
depuración. La app dice claramente qué falta en ese modo.

### Servidor de entrada

Un `.jar`/dex que la app lleva dentro y que se lanza con `app_process` con el usuario `shell`, igual que el
servidor de scrcpy. Habla con la app por un socket local (`LocalSocket`, nombre abstracto), solo con la app de
DeskBridge (comprobando el UID del otro extremo).

- **Teclado**: dispositivo **UHID** (`/dev/uhid`) con descriptor de teclado HID estándar. El `Code` de
  `InputEvent` es el *scan code* de Windows (más `Extended` para E0), que se traduce directamente a *usage*
  HID. Android aplica la distribución configurada para teclados físicos, así que la ñ, los acentos y
  Alt Gr salen bien sin traducir caracteres.
- **Ratón**: el servidor guarda la posición del cursor, como el `InputReceiver` de Windows. Suma los movimientos
  relativos de `MouseMove`, la limita a la pantalla y, al empujar contra el borde que da al coordinador, manda
  `inputLeave`. Inyecta eventos absolutos (`InputManager.injectInputEvent`, `SOURCE_MOUSE`, `HOVER_MOVE`,
  botones, `AXIS_VSCROLL`). Así el coordinador sabe siempre dónde está el cursor y el paso del borde es exacto.
  `inputCursor` se contesta igual que en Windows.
- **Puntero visible**: falta comprobar si Android dibuja el puntero con eventos inyectados de `SOURCE_MOUSE`.
  Si no, la app lo dibuja en una capa (`TYPE_APPLICATION_OVERLAY`, permiso "mostrar sobre otras apps").
  Alternativa si esa capa da problemas: un ratón UHID relativo (puntero nativo de Android), pero entonces la
  posición la calcula Android con su aceleración y el paso del borde es aproximado.
- **Ctrl+Alt+Inicio** sigue funcionando: lo atiende el coordinador.

### Quién lanza el servidor

| Opción | Cómo | Pros / contras |
|---|---|---|
| **A. El sobremesa con `adb`** | DeskBridge usa `adb` (platform-tools de Google; se descarga o se indica su ruta), empareja por depuración inalámbrica y ejecuta `app_process` | Lo que hace scrcpy, probado. Necesita `adb` en el PC y volver a lanzarlo si la tablet se reinicia |
| **B. La propia tablet** | La app se empareja consigo misma por depuración inalámbrica (como Shizuku o LADB) o usa Shizuku si está instalado | Sin nada en el PC. Más código (cliente ADB con TLS dentro de la app) |

**Decisión: A para el prototipo, B después.** El servidor de entrada es el mismo en las dos.

### Que Android no la mate

En el modo 2 la app de DeskBridge no está en primer plano: estás usando otras apps. Hace falta:
- un **servicio en primer plano** (`foregroundServiceType="connectedDevice"`) con su notificación fija, que
  mantiene la sesión de control;
- pedir que se excluya de la **optimización de batería**; si no, varios fabricantes cortan la red a los pocos
  minutos;
- reconectar sola al volver la Wi-Fi (`ConnectivityManager.NetworkCallback`).

## Modo 3 (más adelante): Android en un monitor del PC

La tablet envía su pantalla y el receptor del sobremesa la muestra sin cambios, porque es H.264 por el mismo
`VideoChannel`.
- **Captura**: con el servidor de shell, una pantalla virtual espejo (`SurfaceControl` / `DisplayManager`, como
  scrcpy) sin aviso cada vez. Sin shell, `MediaProjection`, que en Android 14+ pide permiso en cada sesión.
- **Codificación**: `MediaCodec` H.264 en modo baja latencia sobre una `Surface` de entrada. La pantalla
  virtual solo entrega fotogramas cuando algo cambia, así que una pantalla quieta tampoco gasta aquí.
- Es un espejo: Android no deja que una app cree un segundo escritorio usable como hace el Virtual Display Driver.

## Lo demás, por orden de interés

- **Portapapeles** (texto): con el servidor de shell se lee y escribe sin las restricciones de Android 10+ a las
  apps en segundo plano. Imágenes después.
- **Archivos**: recibir a `Descargas` con `MediaStore`; enviar con "Compartir → DeskBridge".
- **Sonido de la tablet al PC**: `AudioPlaybackCapture` (Android 10+, necesita `MediaProjection`). Las apps que
  lo prohíben no suenan. Mismo Opus en tramas de 10 ms.
- **USB**: `adb reverse tcp:47900 tcp:47900` y la tablet conecta a `127.0.0.1`. Menos variación que la Wi-Fi y
  la tablet se carga a la vez. Solo con la opción A.
- **Actualizaciones**: APK en las mismas releases de GitHub. La app avisa y abre el instalador (permiso
  "instalar apps desconocidas"). Las actualizaciones remotas desde el coordinador no aplican: Android siempre
  pide confirmar.

## Proyecto

- Carpeta `android/`: Gradle, Kotlin, `minSdk 29` (Android 10: `AudioPlaybackCapture`, servicios en primer plano
  con tipo). `KEY_LOW_LATENCY` se usa cuando está (Android 11+).
- Módulos: `protocol` (saludo, cifrado, mensajes, `VideoChannel`; JVM puro, con los vectores de prueba de C#),
  `app` (interfaz, agente, vídeo), `inputserver` (lo que corre con `app_process`).
- Mismos idiomas que la app de Windows. Las cadenas se generan desde `src/DeskBridge.Core/Locales/*.json` para no
  traducir dos veces.
- CI: un job más en `build-release.yml` que firma y adjunta el APK a la release.

## Fases

1. **Protocolo en Kotlin** con los vectores de prueba de C#. Una app mínima que se empareja y aparece en
   *Equipos* con su pantalla (*Detectar*).
2. **Coordinador como emisor** (Fase 4 de la hoja de ruta), probada antes con el portátil como receptor.
3. **Modo 1**: la tablet recibe y muestra, con puntero. Medir el retraso con `Shown`.
4. **Toques de vuelta** en el modo 1 (`touchEvents`, multitáctil y lápiz).
5. **Modo 2** con la opción A: servidor de entrada, teclado UHID, ratón, paso de bordes, servicio en primer plano.
6. Portapapeles, USB, opción B (sin PC), modo reducido por accesibilidad.
7. Modo 3 y sonido.

## Por decidir o comprobar en el prototipo

- ¿Android dibuja el puntero con `SOURCE_MOUSE` inyectado, o hace falta la capa propia? (decide el diseño del
  ratón en el modo 2)
- Retraso real del descodificador en las tablets que vayamos a usar, y si `KEY_LOW_LATENCY` cambia algo.
- ¿`adb` se descarga automáticamente de Google o se pide al usuario? (licencia de platform-tools)
- ¿Merece la pena HEVC para tablets con pantalla de mucha densidad? (menos bitrate a igual nitidez, si el
  coordinador lo codifica por hardware)
