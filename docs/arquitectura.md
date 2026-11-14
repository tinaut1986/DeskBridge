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

- **`LayoutPlanner`** (Core): lógica pura. A partir de la configuración y un perfil calcula las pantallas virtuales que debe crear cada equipo, los streams (origen → monitor de destino) y las rutas de entrada. Todos los agentes calculan el mismo plan a partir de la misma configuración.
- **`PlanApplier`** (Core): aplica la parte del plan del equipo local. Primero para los streams, luego ajusta las pantallas virtuales, emite y recibe.
- **`IVirtualDisplayDriver`**: crea y quita monitores virtuales. Implementación prevista: [Virtual Display Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver), un driver IDD firmado y open source. No vamos a escribir un driver propio: firmarlo es un infierno.
- **`IStreamSender` / `IStreamReceiver`**: captura, codificación y reproducción.
  - v1: orquestar **Sunshine** (emite) y **Moonlight** (recibe). Una instancia por pantalla, en puertos distintos.
  - v2 (opcional): pila propia con Desktop Duplication API, NVENC/AMF/QuickSync, UDP y Direct3D. Más control y menos latencia, pero meses de trabajo.
- **`IInputRouter`**: captura el teclado y el ratón en el coordinador y los reenvía cuando el cursor entra en una pantalla prestada, al estilo de Input Leap o Mouse Without Borders. Es la parte que no existe hecha y la que más valor aporta.

## Decisiones

- **Mismo binario en todos los equipos.** No hay servidor ni cliente fijos: cada agente emite y recibe a la vez según el perfil.
- **La pantalla virtual copia la resolución y la frecuencia del monitor físico** que la va a mostrar, para que la imagen sea exacta píxel a píxel.
- **Las pantallas no asignadas no existen.** Una pantalla virtual sin stream haría que Windows abriera ventanas donde no se ven.
- **El monitor prestado sigue activo en su dueño.** Lo necesita para mostrar el vídeo. El agente lo tapa con una ventana a pantalla completa y evita que el cursor y las ventanas locales entren en él.

## Hoja de ruta

1. **Fase 0, esqueleto** (hecho): configuración, planificador, CLI y tests.
2. **Prueba de concepto manual**: Virtual Display Driver + Sunshine en el portátil y Moonlight en el sobremesa. Medir latencia y nitidez por Wi-Fi 6 a 1080p60.
3. **`IVirtualDisplayDriver` real**: crear y quitar N pantallas de VDD desde el agente y saber qué id de Windows recibe cada una.
4. **Canal de control**: agente en cada equipo (servicio o tarea programada) que escucha en `controlPort`. El coordinador le envía "aplica el perfil X". Emparejamiento con clave compartida.
5. **Streaming v1**: lanzar y parar instancias de Sunshine y Moonlight por pantalla, con la ventana de Moonlight sin bordes en el monitor correcto.
6. **`IInputRouter`**: paso del ratón entre zonas, teclado, portapapeles y atajos para cambiar de perfil.
7. **App de bandeja**: elegir perfil con un clic o un atajo, y ver el estado de cada equipo.
8. **Opcional**: pila de streaming propia, audio y servicio SYSTEM para el escritorio seguro (UAC, Ctrl+Alt+Supr).
