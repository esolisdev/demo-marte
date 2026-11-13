# Rover en Marte

Demo de Unity para probar un rover que calcula una ruta sobre terreno marciano y evita pendientes y rocas.

## Clonar y abrir

1. Clona el repositorio (sustituye `<URL-del-repositorio>` por su dirección):

   ```bash
   git clone <URL-del-repositorio>
   ```

2. Abre **Unity Hub** y pulsa **Add > Add project from disk**. Selecciona la carpeta clonada, la que contiene `Assets`, `Packages` y `ProjectSettings`.
3. Abre el proyecto con **Unity 6.6**. Espera a que Unity termine de importar los assets y resolver los paquetes.
4. En el panel **Project**, abre con doble click `Assets/Scenes/MarsDemo.unity`. Espera a que cargue.
5. Pulsa **Play** arriba en el centro para probar la escena dentro del Editor. 

## Controles de prueba

- Clic izquierdo en una zona transitable para fijar el origen; otro clic fija el destino y calcula la ruta.
- Pulsa **R** para reiniciar y escoger otra ruta.
- Arrastra con el botón derecho para orbitar la cámara; usa la rueda para acercar o alejar.
- Usa el botón central o **WASD** para desplazar la vista; pulsa **F** para seguir al rover.
- **1–4** cambian la velocidad de simulación; **T** ejecuta la prueba de caída.


