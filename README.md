# DeskBridge

Usa varios equipos con un solo teclado y ratón, y **reparte las pantallas físicas entre ellos**: un monitor
del sobremesa puede mostrar una pantalla del portátil, y el ratón pasa de uno a otro como si fueran un solo
escritorio.

Ejemplo real: un sobremesa con 3 monitores y un portátil de empresa.

| Perfil | Qué pasa |
|---|---|
| `personal` | El monitor 3 del sobremesa muestra una pantalla virtual del portátil; el ratón y el teclado del sobremesa manejan los dos equipos, el sonido del portátil sale por los cascos del sobremesa. |
| `four-screens` | La pantalla del propio portátil queda colocada debajo de los monitores del sobremesa, como una pantalla más. |

## Cómo funciona (resumen)

1. El equipo que **presta** su imagen crea una **pantalla virtual** (Virtual Display Driver) colocada donde dice el perfil.
2. Esa pantalla se captura, se codifica en H.264 con la GPU y se envía cifrada por la red.
3. El monitor del otro equipo muestra el vídeo a pantalla completa, con un retraso de unos 20-30 ms ida y vuelta.
4. El teclado y el ratón del **coordinador** (el equipo con teclado y ratón) pasan al otro equipo al entrar en su pantalla.
5. Opcionalmente, el sonido de un equipo se oye en el coordinador y el micrófono del coordinador se usa en el otro.

Más detalle en [docs/arquitectura.md](docs/arquitectura.md); para trabajar en el código, [docs/desarrollo.md](docs/desarrollo.md).

## Instalación

`DeskBridge.exe` es la misma aplicación en todos los equipos; no necesita .NET instalado.
- Al abrirla por primera vez se copia a `%LOCALAPPDATA%\DeskBridge\bin\`, crea su acceso directo en el menú
  Inicio y su entrada en **Configuración → Aplicaciones** (desde donde se desinstala). No pide permisos de administrador.
- Se actualiza sola desde las [releases de GitHub](https://github.com/tinaut1986/DeskBridge/releases) (canal estable o beta).
- Todos los equipos necesitan la **misma clave compartida** (página *Conexión y seguridad*). Un equipo que no
  acepta conexiones entrantes (cortafuegos de empresa) pone además la dirección del coordinador en *Conectar a*:
  él abre todas las conexiones.

## La aplicación

Barra lateral con estas páginas:

- **Inicio**: este PC, estado del agente, perfil aplicado y equipos.
- **Disposición** (en el coordinador): mapa con *tu escritorio* (los monitores del coordinador) y las pantallas de
  los demás equipos.
  - Arrastra una pantalla de otro equipo al escritorio para usarla desde allí; el ratón pasa por donde se tocan.
  - Selecciona una pantalla (en el mapa o en la lista) para elegir qué muestra, la resolución, los Hz y el bitrate.
  - **Clic derecho** en una pantalla: hacerla la **pantalla principal** de su equipo (menú Inicio, barra de
    tareas, ventanas nuevas), o hacer principal del otro equipo lo que muestra. Las principales llevan "★".
    Si el coordinador presta su pantalla principal, por defecto pasa a ser principal la más cercana.
  - Menú "⋯" del perfil: nuevo, duplicar, cambiar nombre, eliminar. **Aplicar perfil** lo pone en marcha en todos los equipos.
- **Equipos**: equipos, direcciones y monitores (*Detectar* los lee de Windows, también de otro equipo por la red).
- **Compartir**, cada opción por equipo y casi todas desactivadas por defecto (uno de los equipos puede ser del trabajo):
  - teclado y ratón (en el coordinador: llevarlos a otros equipos; en el otro: dejarse controlar). **Ctrl+Alt+Inicio** en el coordinador los recupera siempre;
  - **sonido**: un equipo envía todo lo que reproduce al coordinador (y silencia sus altavoces); el coordinador lo
    reproduce, con un volumen por equipo;
  - **micrófono**: el coordinador presta su micrófono; el otro equipo lo recibe por **VB-CABLE** (hay que
    instalarlo), y mientras se usa "CABLE Output" es su micrófono predeterminado;
  - portapapeles (texto e imágenes) y archivos (se sueltan en la ventana o *Enviar archivos a*).
- **Conexión y seguridad**: nombre del equipo, clave compartida (oculta), puerto, *Conectar a*, y permitir que el
  coordinador instale versiones nuevas en este equipo (mantenimiento a distancia).
- **Actualizaciones**: versión instalada, canal y buscar ahora.
- **Registro**: el log, diagnósticos de todos los equipos y prueba de vídeo.
- **General**: idioma (11), tema, iniciar con Windows, seguir en el área de notificación al cerrar,
  **marcar cada pantalla con el color de su equipo** (un borde fino) y **ejecutar como administrador**.

**Ejecutar como administrador** (en el equipo controlado): Windows no deja que un programa normal envíe teclas o
clics a una aplicación abierta como administrador. Con esta opción DeskBridge arranca elevado al iniciar sesión
(una tarea programada; Windows pide permiso una vez). Los avisos de UAC (escritorio seguro) siguen sin poder
manejarse a distancia: eso necesitaría un servicio de Windows.

La configuración vive en `%APPDATA%\DeskBridge\`: `deskbridge.json` (común, se edita en el coordinador y se
reparte sola), `deskbridge.local.json` (id del equipo y clave) y `settings.json` (preferencias de este usuario).

## Estado

Funciona a diario entre un sobremesa (NVIDIA) y un portátil (Intel Arc): pantallas virtuales, vídeo propio de
baja latencia, teclado y ratón, sonido, micrófono, portapapeles, archivos, actualizaciones remotas.

Pendiente, por orden: enviar vídeo solo cuando la pantalla cambia; servicio de Windows para los avisos de UAC y
la pantalla de bloqueo; mostrar el sobremesa en el portátil (fase 4); color a resolución completa (4:4:4);
cámara. Ver la [hoja de ruta](docs/arquitectura.md#hoja-de-ruta).

## Compilar y probar

Requiere el SDK de .NET 10 y Windows.

```bash
dotnet test
```

```bash
dotnet run --project src/DeskBridge.Gui
```

La línea de comandos (`src/DeskBridge.App`, `deskbridge.exe`) sirve para pruebas y para depurar otros equipos a
distancia; ver [docs/desarrollo.md](docs/desarrollo.md).

## Publicar

Un tag `vX.Y.Z` compila `DeskBridge-X.Y.Z-win-x64.exe` (autocontenido, un solo archivo) y publica la release con
su `SHA256SUMS.txt` (`.github/workflows/build-release.yml`). Si el commit está en `main` es estable; en una rama
`release/*` sin fusionar, beta. Las notas, en los 11 idiomas, van antes en `docs/release-notes/`.

## Estructura

```
src/DeskBridge.Core        Sin dependencias de Windows: modelo, planificador, canal de control y de vídeo,
                           agente, entrada (lógica), Opus, traducciones (Locales/), releases
src/DeskBridge.Platform    Windows: monitores, pantallas virtuales, captura/codificación/vídeo (Direct3D 11,
                           Media Foundation), entrada (ganchos, SendInput), sonido (WASAPI)
src/DeskBridge.Gui         Aplicación WPF (DeskBridge.exe) con el agente dentro
src/DeskBridge.App         CLI (deskbridge.exe)
tests/DeskBridge.Core.Tests
scripts/i18n-keys.cs       Lista los textos traducibles (o los que faltan en un idioma)
docs/                      Arquitectura, desarrollo, micrófono y cámara, notas de versión
```
