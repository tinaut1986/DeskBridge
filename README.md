# DeskBridge

Usa varios equipos con un solo teclado y ratón, y **reparte las pantallas físicas entre ellos** como quieras.

Ejemplo: un sobremesa con 3 pantallas y un portátil.

| Perfil | Qué pasa |
|---|---|
| `personal` | Cada equipo con sus pantallas. El teclado y el ratón del sobremesa también controlan el portátil. |
| `four-screens` | La pantalla del portátil pasa a ser la 4ª pantalla del sobremesa. |
| `work` | El portátil trabaja en las pantallas 2 y 3 del sobremesa, y la pantalla del portátil muestra una del sobremesa. |

## Cómo funciona (resumen)

Cuando un equipo "presta" una pantalla física a otro:

1. El equipo que la **usa** crea una **pantalla virtual**. Por defecto tiene la misma resolución que la física; se puede cambiar.
2. Esa pantalla virtual se captura, se codifica con la GPU (H.264/HEVC) y se envía por la red.
3. El equipo **dueño** del monitor muestra el vídeo a pantalla completa en él.
4. El teclado y el ratón del coordinador se reenvían al equipo correspondiente según la pantalla en la que esté el cursor.

Solo se transmite lo que se presta. En el perfil `work` son 3 streams 1080p60, unos 20 Mbps cada uno.

Más detalle en [docs/arquitectura.md](docs/arquitectura.md).

## La aplicación

`DeskBridge.exe` es **la misma aplicación en todos los equipos** y lleva dentro el **agente**: la parte que funciona en segundo plano y hace el trabajo real (pantallas virtuales, streams, entrada).

- **En el coordinador** (el equipo con teclado y ratón, normalmente el sobremesa):
  - **Layout**: mapa visual de las pantallas de cada equipo. Para cada pantalla física eliges qué equipo la usa y, si se presta, con qué resolución, Hz y bitrate se envía. Abajo se resume cuántas pantallas virtuales crea cada equipo, el ancho de banda total y los avisos. Con *Apply profile* se aplica en todos los equipos.
  - **Machines**: equipos, sus direcciones y sus monitores. *Detect* los lee de Windows en este PC, o se los pide al agente del otro equipo por la red. Cada equipo puede tener los monitores que sean.
- **En el portátil**: en la pestaña **This PC** eliges `laptop`, pegas la clave compartida y guardas. El agente queda esperando órdenes del coordinador.

La configuración se guarda en `%APPDATA%\DeskBridge\`: `deskbridge.json` es común y solo se edita en el coordinador, y `deskbridge.local.json` es de cada equipo y lleva su id y la clave. La primera vez se crean solas a partir de los monitores detectados.

## Estado

- Hecho:
  - configuración y planificador de perfiles, con ajustes de stream por pantalla;
  - canal de control autenticado;
  - detección de monitores (local y remota);
  - interfaz gráfica con agente integrado;
  - CLI;
  - tests.
- Simulado: las piezas reales (pantallas virtuales, streaming, entrada) son interfaces cuyas implementaciones de momento solo escriben en el log lo que harían.

Ver la [hoja de ruta](docs/arquitectura.md#hoja-de-ruta).

## Compilar y probar

Requiere el SDK de .NET 10 y Windows.

```bash
dotnet test
```

```bash
dotnet run --project src/DeskBridge.Gui
```

### Línea de comandos

`deskbridge` (proyecto `src/DeskBridge.App`) sirve para pruebas y scripts:

```
deskbridge validate | profiles | plan <profile> | simulate <profile> <machine>
deskbridge displays [<machine>] | keygen | agent | switch <profile>
```

Con `--config` y `--local` se usan otros ficheros. Por ejemplo, para probar en un solo PC: `"address": "127.0.0.1"` y un `"controlPort"` distinto en cada equipo, y dos agentes con `--local` apuntando a dos ficheros locales.

## Estructura

```
src/DeskBridge.Core        Lógica sin dependencias de Windows: modelo, planificador, canal de control, agente
src/DeskBridge.Windows     Piezas específicas de Windows (detección de monitores; luego driver virtual, entrada...)
src/DeskBridge.Gui         Aplicación WPF (DeskBridge.exe)
src/DeskBridge.App         CLI (deskbridge.exe)
tests/DeskBridge.Core.Tests
docs/                      Arquitectura y hoja de ruta
```
