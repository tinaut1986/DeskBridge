# Desarrollo

## Ramas

Mismo esquema que NSA, sin PR (lo gestiona una sola persona):

- **`staging`**: línea de trabajo diaria. Cada cambio va en su rama (`feat/…`, `fix/…`) y se une con
  `git merge --no-ff` cuando funciona.
- **Publicar**: `staging` → `release` → `main` (merges `--no-ff`), tag `vX.Y.Z` en `main`, y después `main` →
  `staging`. Antes del tag, las notas en `docs/release-notes/` (inglés y los otros 10 idiomas; ver su README).
- Commits y código en inglés; documentación y textos para el usuario, en su idioma.

## Compilar una versión para los equipos

Las versiones que se instalan en los equipos llevan canal, para que *Buscar actualizaciones* funcione:

```bash
dotnet publish src/DeskBridge.Gui -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:Version=0.9.5 -p:DeskBridgeChannel=release -o <carpeta>
```

Sin `-p:DeskBridgeChannel` es una compilación local: nunca se actualiza sola ni se instala.

## Depurar los equipos a distancia

La CLI (`src/DeskBridge.App`, compilada en Debug) habla con el agente de este PC, que retransmite a los demás
por la sesión que ya tienen abierta (el portátil no acepta conexiones entrantes). Con `--local` apuntando al
`deskbridge.local.json` real (desde una consola que no esté virtualizada, por `\\localhost\C$\…`):

```
deskbridge remote <equipo> log [n]               últimas líneas del log de ese equipo
deskbridge remote <equipo> update <exe> [versión] instala esa versión y reinicia DeskBridge allí
deskbridge remote <equipo> displays | probe-vdd | probe-video
deskbridge remote <equipo> drag-test             mueve una ventana de prueba 10 s por la pantalla que envía
deskbridge remote <equipo> secure-log            log del servicio del escritorio seguro y su ayudante
deskbridge remote <equipo> ui <carpeta>          captura PNG de cada página de su ventana
deskbridge probe <equipo> [segundos]
deskbridge video-test | video-net-test
```

- `update` solo funciona si ese equipo tiene activado *Permitir que el coordinador instale nuevas versiones*.
  Instala siempre en `%LOCALAPPDATA%\DeskBridge\bin\DeskBridge.exe` y reinicia (si DeskBridge corría como
  administrador, sigue siéndolo).
- El log de cada equipo también está en `%LOCALAPPDATA%\DeskBridge\logs\` (el de la CLI remota se reinicia con
  cada arranque). Un cierre inesperado deja `crash.txt`, que se muestra en el siguiente arranque.
- Las cifras útiles del log: en el emisor, `capture to screen and back N ms` (ida y vuelta real),
  `frames/s encoded` y `encoder holds a picture N ms`; en el
  receptor, `fps shown`, `late frames` (por tiempo o con la siguiente esperando) y `decode and show`. `drag-test`
  da cifras de una ventana en movimiento sin nadie delante (sin pasar por el ratón); en el sonido, `kbps, loudest N dB`.
- Capturas de la interfaz sin tocar la instalación real: `DeskBridge.exe --snapshot <carpeta> --config <json> --local <json>`
  (con una clave falsa en el `local`).

## Pruebas

```bash
dotnet test
```

Los tests de traducciones fallan si a un idioma le falta un texto, si no coinciden los `{marcadores}` o si
sobra uno que ya no se usa. Las claves son el texto en inglés; los JSON de `src/DeskBridge.Core/Locales/` van
ordenados por orden ordinal. Para escribirlos, mejor un script de Python (`json.dumps(..., ensure_ascii=False)`)
que PowerShell 5.1, que estropea los caracteres no ASCII.

## Ficheros de estado en cada equipo (`%LOCALAPPDATA%\DeskBridge\`)

| Fichero | Para qué |
|---|---|
| `bin\DeskBridge.exe` | La versión instalada (y `.old` durante una actualización). |
| `original-primary.txt` | La pantalla principal del usuario mientras DeskBridge ha puesto otra. |
| `muted-outputs.txt` | Salidas de sonido que DeskBridge silenció (se devuelven al arrancar si se cerró de golpe). |
| `default-microphone.txt` | El micrófono predeterminado del usuario mientras "CABLE Output" lo sustituye. |
| `logs\` | Registro por día, y `crash.txt` con el último cierre inesperado. |

Con el servicio del escritorio seguro: su copia en `%ProgramFiles%\DeskBridge\DeskBridge.exe` y su log (y el del
ayudante) en `%ProgramData%\DeskBridge\logs\secure-*.log`.

En `%APPDATA%\DeskBridge\` están `deskbridge.json`, `deskbridge.local.json`, `settings.json` y
`applied-profile.json` (el perfil aplicado, que se vuelve a poner al arrancar).
