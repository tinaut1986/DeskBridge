# DeskBridge

Usa varios equipos con un solo teclado y ratón, y **reparte las pantallas físicas entre ellos** como quieras.

Ejemplo: un sobremesa con 3 pantallas y un portátil.

| Perfil | Qué pasa |
|---|---|
| `personal` | Cada equipo con sus pantallas. El teclado y el ratón del sobremesa también controlan el portátil. |
| `cuatro-pantallas` | La pantalla del portátil pasa a ser la 4ª pantalla del sobremesa. |
| `trabajo` | El portátil trabaja en las pantallas 2 y 3 del sobremesa, y la pantalla del portátil muestra una del sobremesa. |

## Cómo funciona (resumen)

Cuando un equipo "presta" una pantalla física a otro:

1. El equipo que la **usa** crea una **pantalla virtual** de la misma resolución. Windows cree que tiene un monitor más.
2. Esa pantalla virtual se captura, se codifica con la GPU (H.264/HEVC) y se envía por la red.
3. El equipo **dueño** del monitor muestra el vídeo a pantalla completa en él.
4. El teclado y el ratón del equipo coordinador se reenvían al equipo correspondiente según la pantalla en la que esté el cursor.

Solo se transmite lo que se presta. En el perfil `trabajo` son 3 streams 1080p60, unos 20–30 Mbps cada uno.

Más detalle en [docs/arquitectura.md](docs/arquitectura.md).

## ¿Servidor o cliente?

**Ninguno: el mismo programa en todos los equipos**, y cada uno emite y recibe a la vez según el perfil (en `trabajo` el sobremesa emite 1 pantalla y recibe 2). Solo hay un rol especial:

- **Coordinador** (`"hasInput": true`): el equipo con el teclado y el ratón. Decide el perfil activo y enruta la entrada.

## Estado

Fase 0: esqueleto. Está hecho el modelo de configuración, el planificador de perfiles (con tests) y una CLI que muestra el plan y simula aplicarlo. Las piezas reales (pantallas virtuales, streaming, entrada) son interfaces con implementaciones simuladas.

Ver la [hoja de ruta](docs/arquitectura.md#hoja-de-ruta).

## Compilar y probar

Requiere el SDK de .NET 10.

```bash
dotnet test
```

```bash
dotnet run --project src/DeskBridge.App -- plan trabajo --config deskbridge.example.json
```

```bash
dotnet run --project src/DeskBridge.App -- aplicar trabajo portatil --config deskbridge.example.json
```

Para tu configuración real, copia `deskbridge.example.json` a `deskbridge.json` (está en `.gitignore`) y pon las IPs de tus equipos.

## Estructura

```
src/DeskBridge.Core        Lógica sin dependencias de Windows: modelo, planificador, interfaces, aplicador
src/DeskBridge.App         CLI (más adelante, app de bandeja del sistema)
tests/DeskBridge.Core.Tests
docs/                      Arquitectura y hoja de ruta
```
