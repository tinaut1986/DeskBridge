# Sonido, micrófono y cámara entre equipos

Caso típico: trabajas en el sobremesa, con sus cascos, micrófono y webcam, y en el monitor 3 ves el portátil.

| Dirección | Estado | Cómo |
|---|---|---|
| Sonido del portátil → cascos del sobremesa | **Hecho** | Captura *loopback* de la salida del portátil (que se silencia mientras tanto), Opus, reproducción en el sobremesa con volumen por equipo. Sin drivers. |
| Micrófono del sobremesa → portátil | **Hecho** (con VB-CABLE) | El sobremesa captura su micrófono de comunicaciones, Opus para voz, el portátil lo reproduce en "CABLE Input"; "CABLE Output" es su micrófono predeterminado mientras se usa. |
| Webcam del sobremesa → portátil | Pendiente | Ver abajo. |

## Por qué el micrófono necesita VB-CABLE

Windows no deja a una aplicación normal crear un micrófono. Las que lo consiguen lo hacen con un controlador:
el Escritorio remoto trae el suyo de serie, Parsec instala uno propio. TeamViewer y AnyDesk no pasan tu
micrófono como micrófono del otro equipo. VB-CABLE es gratuito y firmado; se instala una vez como
administrador (`VBCABLE_Setup_x64.exe`, *Install Driver*, reiniciar).

Cuidado: su instalador pone "CABLE Input" como **salida** predeterminada. Hay que devolverla a los altavoces;
DeskBridge avisa en el registro si la ve así, y nunca la silencia.

## Cámara (pendiente)

1. **Sobremesa:** captura de la webcam (Media Foundation) y H.264 por hardware, como una pantalla pequeña.
2. **Portátil:** Windows 11 tiene cámara virtual sin controlador (`MFCreateVirtualCamera`), pero exige un
   *media source* COM propio registrado en el sistema: la parte más delicada, y la que más choca con equipos
   gestionados. Alternativa: OBS Virtual Camera alimentada con el vídeo recibido.
3. Alternativa sin programar: compartir el USB de la webcam por red (VirtualHere, usbip-win), con controlador en
   el portátil y la cámara inutilizable en el sobremesa mientras tanto.
