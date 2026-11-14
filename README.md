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

La configuración se guarda en `%APPDATA%\DeskBridge\`:
- `deskbridge.json` es común y solo se edita en el coordinador;
- `deskbridge.local.json` es de cada equipo y lleva su id y la clave;
- `settings.json` guarda las preferencias de la interfaz (idioma y canal).

Las dos primeras se crean solas la primera vez, a partir de los monitores detectados.

### Compartir portapapeles y archivos

En **This PC > Sharing**, por equipo y desactivado por defecto, porque uno de los equipos puede ser del trabajo:
- **Enviar lo que copio:** lo que copies en este PC (texto o imágenes) llega a los demás.
- **Recibir lo que se copia:** lo que copien los demás aparece en el portapapeles de este PC.
- **Aceptar archivos:** se guardan en la carpeta elegida, por defecto `Descargas\DeskBridge`. Para enviar, suelta los archivos en la ventana y elige el equipo, o usa **File > Send files to**.

Todo viaja cifrado. Los archivos se verifican con SHA-256 y nunca sobrescriben a otro.

### Bandeja del sistema y modo noche

- Cerrar la ventana deja DeskBridge en el área de notificación, con el agente funcionando; **File > Quit** lo cierra de verdad.
- **Settings > Start with Windows** lo arranca minimizado, que es lo que conviene en el portátil.
- **Settings > Theme**: System (sigue a Windows), Light o Dark.

### Idioma

**Settings > Language**: "System" sigue el idioma de Windows, o eliges uno de los 11 disponibles, los mismos que NSA y llm-browser. Cambia al momento, sin reiniciar. Las claves son el texto en inglés y el inglés es el respaldo.
- Los textos traducibles van en `L.T("...")` (código) y `{l:T '...'}` (XAML), y cada idioma es un JSON en `src/DeskBridge.Core/Locales/`.
- Para ver qué falta en un idioma:
  ```bash
  dotnet run scripts/i18n-keys.cs -- --missing es
  ```
- Los tests fallan si a algún idioma le falta un texto, si los `{marcadores}` no coinciden o si sobra una clave que ya no se usa.

### Actualizaciones

DeskBridge se actualiza solo desde las GitHub Releases, como llm-browser:
- comprueba al arrancar y cada 6 horas, o con **Settings > Check for updates now**;
- descarga el nuevo `DeskBridge.exe`, lo verifica con el `SHA256SUMS.txt` de la release y lo cambia al reiniciar. Si cierras sin reiniciar, lo cambia al salir;
- canal **Stable** o **Beta** en **Settings > Update channel**;
- las novedades salen en tu idioma (`docs/release-notes/`; ver [su README](docs/release-notes/README.md)).

Solo se actualizan las versiones publicadas; una compilación local nunca. Para ver la ventana sin publicar nada: `DESKBRIDGE_UPDATER_DEMO=<carpeta con vX.Y.Z*.md>`.

> Mientras el repositorio sea **privado**, el actualizador no puede leer las releases (GitHub responde 404 sin autenticación). Hay que hacerlo público o añadir un token antes de la primera versión.

## Estado

- Hecho:
  - configuración y planificador de perfiles, con ajustes de stream por pantalla y colocación de las pantallas virtuales;
  - canal de control cifrado y con autenticación mutua;
  - detección de monitores (local y remota);
  - interfaz gráfica con agente integrado, en 11 idiomas, con modo noche, bandeja del sistema, arranque con Windows y actualización automática;
  - portapapeles compartido (texto e imágenes) y envío de archivos entre equipos;
  - CLI;
  - tests.
- Diseñado, pendiente de decidir: micrófono y cámara ([docs/microfono-camara.md](docs/microfono-camara.md)).
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

## Publicar

Mismo esquema que llm-browser y los ports de 3DS (`.github/workflows/build-release.yml`):
- un tag `vX.Y.Z` compila `DeskBridge-X.Y.Z-win-x64.exe` (autocontenido y de un solo archivo: no necesita .NET instalado) y publica la release con su `SHA256SUMS.txt`;
- si el commit del tag está en `main` es **Release**; si está en una rama `release/*` sin fusionar, es **Beta**;
- antes del tag, escribe las notas en `docs/release-notes/`.

## Estructura

```
src/DeskBridge.Core        Lógica sin dependencias de Windows: modelo, planificador, canal de control, agente,
                           traducciones (Locales/) y lectura de releases
src/DeskBridge.Windows     Piezas específicas de Windows (detección de monitores; luego driver virtual, entrada...)
src/DeskBridge.Gui         Aplicación WPF (DeskBridge.exe)
src/DeskBridge.App         CLI (deskbridge.exe)
tests/DeskBridge.Core.Tests
scripts/i18n-keys.cs       Lista los textos traducibles (o los que faltan en un idioma)
docs/                      Arquitectura, hoja de ruta y notas de versión
```
