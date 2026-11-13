# PLAN — Demo «Rover en Marte» (Unity 6.6 / URP)

## Contexto

Se pide una demo pequeña: terreno marciano ≤ 300 × 300 m con montañas y cráteres, un rover que va de un origen a un destino elegidos con clics, esquivando obstáculos por la ruta óptima y moviéndose con física de Marte (no de la Tierra). Prioridad: funcionalidad sobre estética. Este documento es la Fase 1; no se escribe código hasta su aprobación.

**Estado verificado del entorno (solo lectura, con Unity CLI 1.0.0-beta.17):**

| Dato | Valor comprobado |
|---|---|
| Proyecto | Ya existe en `C:\Users\E\Desktop\proyectos\Ares\PruebaFable\PruebaFable` (plantilla URP). **Se reutiliza**, no se crea otro. |
| Editor | 6000.6.8f1 (Unity 6.6), **abierto y en estado `ready`** (puerto 7800) |
| Render pipeline | URP 17.6.0 (`com.unity.render-pipelines.universal`) |
| Entrada | Input System 1.20.1 con `activeInputHandler: 1` → la clase antigua `UnityEngine.Input` está **desactivada**; hay que usar `Mouse.current` / `Keyboard.current` |
| Módulos | `terrain`, `terrainphysics`, `physics`, `vehicles`, `imgui` presentes |
| Física actual | Gravedad −9.81 (por defecto), Fixed Timestep 0.02 s |
| Escena actual | `Assets/Scenes/SampleScene.unity` (cámara, luz, Global Volume). No se toca. |
| Pipeline package | `com.unity.pipeline` 0.5.0-exp.1 → **demasiado antiguo**: el CLI rechaza comandos con argumentos (`eval`, capturas). Última: 1.0.0 |

APIs confirmadas en la documentación XML del Editor instalado (no inventadas): `PhysicsMaterial` (+ `dynamicFriction`), `Rigidbody.linearDamping / angularDamping / linearVelocity / centerOfMass / interpolation`, `TerrainData.SetHeights / GetSteepness / GetInterpolatedNormal / SetAlphamaps`, `Terrain.SampleHeight`, `TerrainLayer`, `RenderPipelineAsset.defaultTerrainMaterial`, `LineRenderer`, `GUI.Label / GUI.Button`, `WheelCollider`.

**Dos avisos sobre el enunciado:**
1. *Prueba de caída:* el texto dice «≈1.0 s» pero la fórmula que cita da **t = √(2·1 / 3.71) = 0.734 s** (en la Tierra serían 0.452 s). Validaré contra **0.73 s**; 1.0 s no corresponde a g = 3.71 con h = 1 m.
2. *Velocidad:* a 0.5 m/s, cruzar 150 m son 5 minutos reales. Mantengo 0.5 m/s como valor físico y añado aceleración de tiempo (×1/×5/×10/×20 con `Time.timeScale`), que no altera la física simulada.

---

## 1. Estructura: escena, scripts y paquetes

**Escena única:** `Assets/Scenes/MarsDemo.unity` (nueva).

```
MarsDemo
├─ Main Camera            (Camera + OrbitCamera)            fondo color cielo marciano
├─ Directional Light      (luz cálida)
├─ MarsEnvironment        (MarsEnvironment)                 gravedad + generación
│   ├─ Terrain            (Terrain + TerrainCollider, material «Regolith»)
│   └─ Obstacles          (≈45 rocas, capa Obstacle, colliders estáticos)
├─ Rover                  (Rigidbody + BoxCollider + RoverController, capa Rover)
│   └─ Body / Mast / Wheel_FL..RR   (cubos y cilindros, solo visuales)
└─ DemoController         (MarsDemoController + LineRenderer)
    └─ OriginMarker / DestinationMarker
```

**Scripts (5, en `Assets/MarsDemo/Scripts/`, un `MonoBehaviour` por archivo):**

| Script | Responsabilidad |
|---|---|
| `MarsEnvironment.cs` | Constantes físicas de Marte documentadas; aplica `Physics.gravity` en `Awake`; genera terreno y rocas (`[ContextMenu]`, determinista por semilla); prueba de caída de 1 m. |
| `RoverPathfinder.cs` | Clase C# pura: rejilla de transitabilidad (pendiente + colliders) y A* con suavizado. |
| `RoverController.cs` | Rigidbody del rover, suspensión por raycast, tracción limitada por fricción, seguimiento de ruta, validación de pendiente, parada en destino. |
| `MarsDemoController.cs` | Clics con raycast, marcadores, `LineRenderer`, HUD y mensajes, reinicio, escala de tiempo. |
| `OrbitCamera.cs` | Cámara orbital (rotar, zoom, desplazar, seguir al rover). |

No hay scripts de Editor: la escena se monta con el Editor en vivo vía `unity command` (como recomienda la skill unity-cli) y queda guardada en el `.unity`.

**Paquetes:**

| Paquete | Decisión | Justificación |
|---|---|---|
| `com.unity.render-pipelines.universal` 17.6.0 | Ya instalado, se usa | Requisito URP |
| `com.unity.inputsystem` 1.20.1 | Ya instalado, se usa | Es el único manejador de entrada activo en el proyecto |
| `com.unity.pipeline` 0.5.0-exp.1 → 1.0.0 | **Actualizar** (`unity pipeline upgrade`) | Solo herramienta de Editor; sin ello el CLI no puede ejecutar `eval` ni capturas para verificar. No afecta al runtime. |
| `com.unity.ai.navigation` (NavMesh) | **No** | Ver §4: no permite coste por pendiente ni compartir el límite de pendiente con la validación física |
| Cinemachine, TextMeshPro, Terrain Tools, ProBuilder | **No** | Una cámara orbital son ~60 líneas; el resto es estética |

Resultado: **cero paquetes nuevos de runtime**.

**¿Hay que descargar algo?** Para la demo, nada: terreno, física, URP e Input System ya están instalados. La **única descarga** es la actualización de `com.unity.pipeline` (paquete pequeño de herramientas de Editor, desde el registro de Unity). Si prefieres no actualizarlo, la demo se construye igual, pero no podré ejecutar código ni tomar capturas en tu Editor y las pruebas de §Verificación tendrías que hacerlas tú a mano.

---

## 2. Generación del terreno

- **Componente:** `Terrain` de Unity (módulo ya presente) → `TerrainCollider` gratis y consultas de altura/pendiente/normal nativas.
- **Tamaño:** 200 × 200 m, altura máxima 40 m (dentro del límite de 300 × 300).
- **Resolución:** heightmap 513 × 513 (≈ 0.39 m por muestra); alphamap 256 × 256.
- **Método (procedural, semilla fija → siempre el mismo terreno):** se suma en un `float[,]`:
  1. **Base ondulada:** ruido fBm (`Mathf.PerlinNoise`, 4 octavas), amplitud ≈ ±2.5 m sobre un nivel base de 10 m (para poder excavar cráteres).
  2. **3 montañas:** perfil gaussiano de 12–22 m de alto y 25–35 m de radio, modulado con ruido para romper la simetría. Posiciones por semilla, lejos del centro.
  3. **8 cráteres:** radio 6–18 m; interior parabólico `−d·(1 − r²/R²)` con profundidad ≈ 0.25·R y borde elevado que decae exponencialmente fuera. **Uno de ellos se hace profundo con paredes > 30° en todo el perímetro**: su interior es inalcanzable y sirve para demostrar el mensaje «no existe ruta».
- **Aspecto:** dos `TerrainLayer` con texturas procedurales pequeñas: regolito rojizo y roca oscura. El alphamap pinta de oscuro donde la pendiente supera el límite → las zonas no transitables se ven a simple vista. Material: `defaultTerrainMaterial` del asset URP.
- **Cuándo se genera:** en modo edición con `[ContextMenu("Regenerar terreno y rocas")]`; el `TerrainData` y las capas se guardan en `Assets/MarsDemo/Generated/`. Así el terreno es visible sin entrar en Play y no hay coste de arranque. (El código que usa `AssetDatabase` va bajo `#if UNITY_EDITOR`.)

---

## 3. Obstáculos: por pendiente **y** por colisionadores

Ambos, fusionados en una única rejilla de transitabilidad (celda de 1 m → 200 × 200 = 40 000 celdas), construida al iniciar Play:

| Tipo | Cómo se detecta | Margen |
|---|---|---|
| **Pendiente** | `TerrainData.GetSteepness` en centro y 4 esquinas de la celda; bloqueada si el máximo > pendiente de planificación | Dilatación de 1 celda (≈ medio rover) |
| **Rocas** | ≈45 primitivas (esferas de escala uniforme y cajas rotadas, 0.4–1.8 m) en capa `Obstacle`; `Physics.CheckSphere` por celda con radio = semidiagonal del rover + 0.2 m ≈ 1.1 m | Incluido en el radio |
| **Borde** | Franja de 2 m en el perímetro del terreno | — |

Las rocas se colocan por semilla solo en zonas con pendiente < 15° y sin solaparse. Capas nuevas: `Obstacle` y `Rover` (esta última para que los raycasts de clic y de suspensión ignoren al propio rover).

---

## 4. Algoritmo de ruta óptima

| Opción | Pros | Contras |
|---|---|---|
| **NavMesh** (`NavMeshSurface` + `CalculatePath`) | Integrado, robusto, incluye pendiente máxima de agente | Exige paquete extra; coste = solo distancia (no penaliza pendiente); `NavMeshAgent` es cinemático y choca con un Rigidbody; el límite de pendiente queda en el bake, separado de la validación física; poco transparente |
| **Dijkstra en rejilla** | Óptimo, muy simple | Explora en todas direcciones: más nodos que A* para un solo par origen-destino, sin ventaja a cambio |
| **A\* en rejilla 8-conexa** | Óptimo con heurística admisible; coste personalizable (pendiente); usa la misma constante de pendiente que el rover; «sin ruta» se detecta de forma exacta (lista abierta vacía); sin dependencias; 40 000 nodos se resuelven en milisegundos | Rutas en zigzag (ángulos de 45°) → necesita suavizado; resolución limitada a la celda |
| **Theta\* / any-angle** | Rutas más cortas y naturales directamente | Más complejo; la comprobación de visibilidad con coste por pendiente complica garantizar optimalidad |
| **RRT\* (muestreo)** | Bueno en espacios continuos de alta dimensión | No determinista, solo asintóticamente óptimo; excesivo para 2D |
| **D\* Lite** | Replanifica barato si el mapa cambia | El mapa es estático y conocido: complejidad sin beneficio |

**Elegido: A\* en rejilla 8-conexa + suavizado por línea de visión.**

- **Sin nodos manuales, movimiento libre:** no se coloca ningún nodo ni waypoint a mano. La rejilla se calcula sola al iniciar a partir del terreno y las rocas, y se puede hacer clic en **cualquier punto transitable**. El rover no salta de celda en celda: tras el suavizado la ruta son tramos rectos en cualquier ángulo y el rover la sigue de forma continua con fuerzas físicas, apuntando a un punto adelantado de la línea. Único límite: no distingue pasos más estrechos que ≈ 1 m (tamaño de celda). La conducción manual (WASD) no está incluida; se puede añadir como modo extra si se pide.
- **Coste de arista:** `distancia3D × (1 + 2·(θ/θmáx)²)`, con θ = pendiente media de las dos celdas → prefiere rutas llanas (menos energía y riesgo), no solo cortas.
- **Heurística:** distancia octil horizontal; es admisible porque el coste nunca es menor que la distancia horizontal → A* devuelve el óptimo de la rejilla.
- **Restricciones:** celdas bloqueadas (§3); pendiente entre celdas vecinas `atan(|Δh|/d)` ≤ límite; prohibido cortar esquinas en diagonal entre celdas bloqueadas.
- **Cola de prioridad:** montículo binario propio (~30 líneas); no dependo de `System.Collections.Generic.PriorityQueue`, cuya disponibilidad en el perfil de Unity no he verificado.
- **Suavizado:** se eliminan puntos intermedios cuando el segmento directo pasa solo por celdas transitables (muestreo cada 0.5 m). La ruta sigue siendo válida; la optimalidad estricta es respecto a la rejilla (limitación documentada).
- **Justificación:** es la única opción que cumple a la vez «óptima», «respeta la misma pendiente máxima que la física», «detecta ausencia de ruta» y «sin paquetes», con código corto y auditable.

---

## 5. Física marciana

Todas las constantes viven en `MarsEnvironment.cs`, comentadas, y son ajustables en el Inspector.

| Parámetro | Valor | Dónde se aplica |
|---|---|---|
| Gravedad | **(0, −3.71, 0) m/s²** | `Physics.gravity` en `MarsEnvironment.Awake()` (`[DefaultExecutionOrder(-1000)]`) y también en Project Settings › Physics para coherencia |
| Atmósfera (≈1 % terrestre) | `linearDamping = 0`; `angularDamping = 0.05` (solo estabilidad numérica) | Rigidbody del rover y objeto de la prueba de caída |
| Masa del rover | **150 kg** (ajustable) | `Rigidbody.mass` |
| Centro de masa | (0, −0.25, 0) local, por debajo del chasis | `Rigidbody.centerOfMass` |
| Interpolación | `Interpolate` | `Rigidbody.interpolation` |
| Fricción del regolito | `PhysicsMaterial` «Regolith»: **dinámica 0.5**, estática 0.6, rebote 0 | `TerrainCollider` y rocas; el modelo de tracción del rover lee este mismo valor |
| Pendiente máxima | **25°** (la ruta se planifica con 3° de margen → 22°) | Constante única usada por `RoverPathfinder` y por la validación de `RoverController` |
| Velocidad de crucero | **0.5 m/s** | Consigna de `RoverController` |
| Aceleración máxima | 0.2 m/s² (rampa suave); giro máx. 20°/s | Limitador de consignas |
| Paso de física | 0.02 s (por defecto, sin cambios) | — |

**Coherencia fricción ↔ pendiente:** un vehículo se sostiene en una rampa si tan θ ≤ μ, independientemente de g. Con μ = 0.5 el límite teórico es atan(0.5) = 26.6°, justo por encima de los 25° definidos: los dos valores son consistentes entre sí.

**Suspensión — elegida: 4 raycasts con muelle-amortiguador** (no `WheelCollider`, no anclaje por altura):

- `WheelCollider` usa su propia curva de fricción (ignora el `PhysicsMaterial`) y es inestable a 0.5 m/s; el anclaje por altura no sería física real.
- Cada rueda lanza un rayo hacia abajo; fuerza `F = k·compresión − c·velocidad` (≥ 0) aplicada con `AddForceAtPosition`.
- Rigidez **derivada de la gravedad marciana**: `k = m·g / (4 · 0.10 m)` ≈ 1 390 N/m para 10 cm de hundimiento estático; amortiguación con ζ = 0.8. Si se cambia la masa o g, se recalcula.
- **Tracción:** cada rueda apoyada aplica una fuerza tangente al suelo que lleva la velocidad de su punto de contacto hacia la consigna (avance + giro), **limitada a μ·N**, donde N es la fuerza de su muelle y μ la fricción del `PhysicsMaterial` del suelo. De ahí salen el empuje, el agarre lateral, el giro y el freno, y el rover patina si se le pide más de lo que la fricción permite.
- En baja gravedad N es pequeño (≈ 139 N por rueda), así que la tracción disponible también: la inercia se nota y por eso la aceleración se limita.

**Validación del movimiento:** en cada `FixedUpdate` se mide la pendiente real bajo el rover (media de las normales de los 4 rayos). Si supera 25° (+2° de tolerancia) de forma sostenida, el rover frena y el HUD muestra «Detenido: pendiente excedida». También se detecta atasco (sin avance durante 10 s).

**Prueba de caída:** tecla `T` o botón. Se suelta una esfera (1 kg, sin drag) con su base exactamente a 1.000 m del suelo (medido por raycast) y se cronometra en pasos de física hasta el contacto. Se muestra y se registra en consola: medido vs. teórico 0.734 s (y 0.452 s terrestre como referencia). Aceptación: 0.73 ± 0.03 s (la resolución es un paso de 0.02 s).

---

## 6. Flujo de interacción

1. **Play.** HUD: «Clic izquierdo: elige el ORIGEN».
2. **Primer clic izquierdo** → raycast de cámara contra el terreno. Si la celda no es transitable: «Origen no transitable (pendiente u obstáculo)». Si lo es: marcador verde y el rover se coloca ahí, en reposo.
3. **Segundo clic** → marcador rojo de destino y cálculo de A*.
   - **Hay ruta:** se dibuja con `LineRenderer` (pegada al terreno, +0.3 m) y el rover arranca solo: acelera suave, mantiene 0.5 m/s constantes, decelera con `v = √(2·a·d)` y **se detiene** a < 0.3 m del destino. HUD: velocidad, pendiente actual, distancia restante, ETA.
   - **No hay ruta:** mensaje en rojo «No existe ruta posible entre origen y destino»; el rover no se mueve.
4. **Reiniciar:** tecla `R` o botón → borra marcadores y línea, detiene el rover, vuelve al paso 1.
5. **Cámara:** arrastrar botón derecho = orbitar; rueda = zoom; botón central o WASD = desplazar; `F` = seguir al rover.
6. **Extras:** `1/2/3/4` = tiempo ×1/×5/×10/×20; `T` = prueba de caída.

HUD con IMGUI (`OnGUI`): cero assets y funciona con el Input System. La «etiqueta visual» del rover es un rótulo «ROVER» dibujado sobre su posición en pantalla, además de su color distintivo. Antes de escribir el HUD consultaré la skill `ui`; si desaconseja IMGUI en runtime, lo paso a UI Toolkit sin cambiar el alcance.

`MarsDemoController` expone `SetOrigin(Vector3)` y `SetDestination(Vector3)` públicos: los clics llaman a esos métodos y yo los invoco desde el CLI para probar exactamente el mismo camino de código.

---

## 7. Tareas en orden y criterio de terminado

| # | Tarea | Terminado cuando… |
|---|---|---|
| 0 | Copiar este plan a `PLAN.md` en la raíz del proyecto | El archivo existe (en modo plan solo puedo escribir el archivo de plan interno) |
| 1 | `unity pipeline upgrade` y esperar al Editor | `unity status` = `ready` y `unity command eval` devuelve un valor |
| 2 | Capas `Obstacle` y `Rover`; gravedad del proyecto a −3.71 | `get_tags_layers` y `get_physics_settings` lo reflejan |
| 3 | `MarsEnvironment.cs` (constantes, gravedad, terreno, rocas) | Compila sin errores; el terreno generado tiene zonas llanas, > 25° y el cráter cerrado (comprobado midiendo pendientes con `eval`) |
| 4 | Montar `MarsDemo.unity`: terreno, rocas, luz, cámara, material Regolith | Captura de la vista muestra terreno marciano con rocas; escena guardada |
| 5 | Prueba de caída | En Play, t medido = 0.73 ± 0.03 s registrado en consola |
| 6 | `RoverPathfinder.cs` | Ruta válida entre dos puntos alcanzables; `null` hacia el cráter cerrado; ninguna celda de la ruta supera 22° ni toca rocas |
| 7 | `RoverController.cs` + objeto Rover | El rover reposa estable sobre su suspensión, no resbala parado en una ladera de ~20°, y sigue una ruta de prueba |
| 8 | `MarsDemoController.cs` + `OrbitCamera.cs` | Clics, línea, mensajes, reinicio y cámara funcionan |
| 9 | Verificación integral en Play (abajo) | Todos los puntos pasan; consola sin errores ni excepciones |
| 10 | Reporte final | Entregable completo con valores medidos |

---

## Verificación (con Unity CLI, sobre el Editor abierto)

1. **Compilación:** `unity command recompile` → `recompile_status` → `console` sin errores.
2. **Play:** `set_autotick` (para que el Editor avance sin foco) + `editor_play`; confirmar que el tiempo de juego avanza antes de medir.
3. **Gravedad:** leer `Physics.gravity` en Play = (0, −3.71, 0).
4. **Caída:** lanzar la prueba y leer el tiempo en consola (objetivo 0.73 ± 0.03 s).
5. **Ruta alcanzable:** `SetOrigin` / `SetDestination` por `eval`, tiempo ×10; muestrear posición y velocidad hasta la llegada. Comprobar: llega a < 0.3 m, velocidad máxima ≤ 0.5 m/s (+5 %), pendiente máxima registrada ≤ 25°, sin contacto con rocas, queda detenido.
6. **Sin ruta:** destino dentro del cráter cerrado → mensaje visible y rover inmóvil.
7. **Reinicio:** tras `R`, estado inicial y posibilidad de elegir de nuevo.
8. **Capturas** con `capture_game_view` de los casos 5 y 6 como evidencia.
9. Salir de Play, guardar escena. **Sin build.**

Los clics reales del ratón y la cámara orbital no puedo accionarlos desde el CLI: los verifico por la ruta de código compartida y los señalaré como «a confirmar por ti» en los 3 pasos de prueba.

## Riesgos conocidos de antemano

- `unity license status` indica «ninguna activa» aunque el Editor está abierto y operativo. No bloquea nada mientras no haya que lanzar un Editor en batch; si el Editor se cerrara, pararía y te avisaría.
- Ajuste de la suspensión/tracción: es la parte con más iteración; los valores de muelle parten de fórmulas, pero pueden requerir afinado en Play.
- La actualización del Pipeline package provoca una recarga de dominio en el Editor abierto (unos segundos).
