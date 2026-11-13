using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

/// <summary>
/// Flujo de la demo: selección de origen y destino con clics, cálculo y dibujo de la ruta,
/// HUD con mensajes, reinicio, escala de tiempo y prueba de caída.
/// </summary>
public class MarsDemoController : MonoBehaviour
{
    public enum DemoState { WaitingOrigin, WaitingDestination, Moving, Finished }

    [Header("Referencias")]
    public MarsEnvironment env;
    public RoverController rover;
    public Camera cam;
    public LineRenderer routeLine;
    public Transform originMarker;
    public Transform destinationMarker;

    [Header("Rejilla de ruta")]
    public LayerMask terrainMask = 1;
    public LayerMask obstacleMask;
    [Tooltip("Tamaño de celda de la rejilla A* en metros.")]
    public float cellSize = 1f;
    [Tooltip("Radio de seguridad alrededor de las rocas (semidiagonal del rover + margen).")]
    public float clearanceRadius = 1.1f;
    public float borderMargin = 2f;
    [Tooltip("Peso de la penalización por pendiente en el coste de la ruta (0 = ruta más corta).")]
    public float slopePenalty = 2f;

    [Header("HUD (uGUI)")]
    public TMPro.TMP_Text statusText;
    public TMPro.TMP_Text messageText;
    public TMPro.TMP_Text roverLabel;
    public UnityEngine.UI.Button resetButton;
    public UnityEngine.UI.Button dropButton;
    public UnityEngine.UI.Button[] timeButtons;
    public float[] timeScales = { 1f, 5f, 10f, 20f };

    public DemoState State { get; private set; } = DemoState.WaitingOrigin;
    public string Message { get; private set; } = "";
    public bool MessageIsError { get; private set; }
    public RoverPathfinder Pathfinder { get; private set; }
    public List<Vector3> CurrentPath { get; private set; }

    Vector3 origin;
    float baseMaxDelta;
    float hudTimer;
    readonly System.Text.StringBuilder sb = new System.Text.StringBuilder(512);

    void Start()
    {
        baseMaxDelta = Time.maximumDeltaTime;
        if (cam == null) cam = Camera.main;

        // La rejilla se calcula sola a partir del terreno y las rocas (sin nodos manuales).
        Pathfinder = new RoverPathfinder(env.terrain, cellSize, env.PlanningSlopeDeg, obstacleMask, clearanceRadius, borderMargin, slopePenalty);

        if (resetButton != null) resetButton.onClick.AddListener(ResetDemo);
        if (dropButton != null) dropButton.onClick.AddListener(RunDropTest);
        if (timeButtons != null)
        {
            for (int i = 0; i < timeButtons.Length && i < timeScales.Length; i++)
            {
                float scale = timeScales[i];
                if (timeButtons[i] != null) timeButtons[i].onClick.AddListener(() => SetTimeScale(scale));
            }
        }

        routeLine.positionCount = 0;
        originMarker.gameObject.SetActive(false);
        destinationMarker.gameObject.SetActive(false);

        // El rover empieza en reposo cerca del centro del mapa.
        if (Pathfinder.NearestWalkable(env.Center, 40f, out Vector3 startPos)) rover.PlaceAt(startPos, 0f);
        Show("Clic izquierdo sobre el terreno para elegir el ORIGEN.", false);
    }

    void OnDestroy()
    {
        Time.timeScale = 1f;
        if (baseMaxDelta > 0f) Time.maximumDeltaTime = baseMaxDelta;
    }

    void Update()
    {
        Keyboard kb = Keyboard.current;
        if (kb != null)
        {
            if (kb.rKey.wasPressedThisFrame) ResetDemo();
            if (kb.tKey.wasPressedThisFrame) RunDropTest();
            if (kb.digit1Key.wasPressedThisFrame) SetTimeScale(timeScales[0]);
            if (kb.digit2Key.wasPressedThisFrame) SetTimeScale(timeScales[1]);
            if (kb.digit3Key.wasPressedThisFrame) SetTimeScale(timeScales[2]);
            if (kb.digit4Key.wasPressedThisFrame) SetTimeScale(timeScales[3]);
        }

        Mouse mouse = Mouse.current;
        if (mouse != null && mouse.leftButton.wasPressedThisFrame && !PointerOverUI())
        {
            // Raycast desde la cámara hacia el punto del clic.
            Ray ray = cam.ScreenPointToRay(mouse.position.ReadValue());
            if (Physics.Raycast(ray, out RaycastHit hit, 3000f, terrainMask | obstacleMask, QueryTriggerInteraction.Ignore))
                HandleClick(hit.point);
        }

        WatchRover();
        UpdateHud();
    }

    static bool PointerOverUI()
    {
        return EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
    }

    void HandleClick(Vector3 point)
    {
        switch (State)
        {
            case DemoState.WaitingOrigin: SetOrigin(point); break;
            case DemoState.WaitingDestination: SetDestination(point); break;
            default: Show("Pulsa R o «Reiniciar» para elegir otra ruta.", false); break;
        }
    }

    // ---------- API pública (la usan los clics y las pruebas) ----------
    /// <summary>Primer clic: define el origen y coloca ahí el rover.</summary>
    public bool SetOrigin(Vector3 point)
    {
        if (!Pathfinder.IsWalkable(point))
        {
            Show("Origen no transitable: " + Reason(point) + ". Elige otro punto.", true);
            return false;
        }
        origin = env.GroundPoint(point.x, point.z);
        originMarker.position = origin;
        originMarker.gameObject.SetActive(true);
        destinationMarker.gameObject.SetActive(false);
        routeLine.positionCount = 0;
        rover.PlaceAt(origin, rover.transform.eulerAngles.y);
        State = DemoState.WaitingDestination;
        Show("Origen fijado. Clic izquierdo para elegir el DESTINO.", false);
        return true;
    }

    /// <summary>Segundo clic: define el destino, calcula la ruta y pone el rover en marcha.</summary>
    public bool SetDestination(Vector3 point)
    {
        if (State != DemoState.WaitingDestination) return false;
        if (!Pathfinder.IsWalkable(point))
        {
            Show("Destino no transitable: " + Reason(point) + ". Elige otro punto.", true);
            return false;
        }
        Vector3 destination = env.GroundPoint(point.x, point.z);
        destinationMarker.position = destination;
        destinationMarker.gameObject.SetActive(true);
        if (Vector3.Distance(origin, destination) < 1.5f)
        {
            Show("El destino está demasiado cerca del origen. Elige otro punto.", true);
            return false;
        }

        List<Vector3> path = Pathfinder.FindPath(origin, destination);
        if (path == null)
        {
            routeLine.positionCount = 0;
            CurrentPath = null;
            Show("No existe ruta posible entre el origen y el destino (pendiente u obstáculos). Elige otro destino o pulsa R.", true);
            return false;
        }

        // Dibuja la ruta pegada al terreno.
        CurrentPath = path;
        List<Vector3> line = Pathfinder.Densify(path, 1f, 0.3f);
        routeLine.positionCount = line.Count;
        routeLine.SetPositions(line.ToArray());

        rover.BeginRoute(path);
        State = DemoState.Moving;
        Show($"Ruta de {Pathfinder.LastLength:F1} m calculada. El rover está en marcha.", false);
        return true;
    }

    /// <summary>Reinicia la demo: borra la ruta y vuelve a pedir el origen.</summary>
    public void ResetDemo()
    {
        rover.Stop();
        routeLine.positionCount = 0;
        CurrentPath = null;
        originMarker.gameObject.SetActive(false);
        destinationMarker.gameObject.SetActive(false);
        State = DemoState.WaitingOrigin;
        Show("Reiniciado. Clic izquierdo sobre el terreno para elegir el ORIGEN.", false);
    }

    public void SetTimeScale(float scale)
    {
        // Acelera el tiempo sin cambiar el paso de física (la simulación es la misma).
        Time.timeScale = scale;
        Time.maximumDeltaTime = Mathf.Max(baseMaxDelta, 0.04f * scale);
    }

    public void RunDropTest()
    {
        env.RunDropTest(rover.transform.position + rover.transform.right * 6f);
    }

    // ---------- Estado y HUD ----------
    string Reason(Vector3 point)
    {
        switch (Pathfinder.StateAt(point))
        {
            case RoverPathfinder.CellState.Slope: return $"pendiente superior a {env.PlanningSlopeDeg:F0}° o muy próxima a una";
            case RoverPathfinder.CellState.Obstacle: return "demasiado cerca de una roca";
            case RoverPathfinder.CellState.Border: return "fuera del área transitable (borde del terreno)";
            default: return "celda bloqueada";
        }
    }

    void WatchRover()
    {
        if (State != DemoState.Moving) return;
        switch (rover.State)
        {
            case RoverController.RoverState.Arrived:
                State = DemoState.Finished;
                Show($"Destino alcanzado en {rover.TravelTime:F0} s. Pulsa R para reiniciar.", false);
                break;
            case RoverController.RoverState.SlopeAbort:
                State = DemoState.Finished;
                Show($"Detenido: pendiente excedida ({rover.SlopeDeg:F1}° > {env.maxSlopeDeg:F0}°). Pulsa R.", true);
                break;
            case RoverController.RoverState.Stuck:
                State = DemoState.Finished;
                Show("Detenido: el rover no avanza (atascado). Pulsa R.", true);
                break;
        }
    }

    void Show(string text, bool error)
    {
        Message = text;
        MessageIsError = error;
        if (messageText != null)
        {
            messageText.text = text;
            messageText.color = error ? new Color(1f, 0.35f, 0.3f) : Color.white;
        }
        if (error) Debug.LogWarning("[Demo] " + text); else Debug.Log("[Demo] " + text);
    }

    void UpdateHud()
    {
        // Etiqueta visual que sigue al rover en pantalla.
        if (roverLabel != null)
        {
            Vector3 screen = cam.WorldToScreenPoint(rover.transform.position + Vector3.up * 1.4f);
            roverLabel.gameObject.SetActive(screen.z > 0f);
            roverLabel.rectTransform.position = new Vector3(screen.x, screen.y, 0f);
        }

        // La línea y los marcadores se ensanchan con la distancia para verse desde lejos.
        float camDist = Vector3.Distance(cam.transform.position, rover.transform.position);
        routeLine.widthMultiplier = Mathf.Max(0.35f, camDist * 0.006f);
        float markerScale = Mathf.Max(1f, camDist * 0.015f);
        originMarker.localScale = Vector3.one * markerScale;
        destinationMarker.localScale = Vector3.one * markerScale;

        hudTimer -= Time.unscaledDeltaTime;
        if (statusText == null || hudTimer > 0f) return;
        hudTimer = 0.1f;

        sb.Clear();
        sb.Append("ROVER EN MARTE   g = ").Append(Mathf.Abs(Physics.gravity.y).ToString("F2")).Append(" m/s²   tiempo ×").Append(Time.timeScale.ToString("F0")).Append('\n');
        sb.Append("Masa ").Append(rover.mass.ToString("F0")).Append(" kg   fricción ").Append(env.DynamicFriction.ToString("F2"))
          .Append("   pendiente máx. ").Append(env.maxSlopeDeg.ToString("F0")).Append("°\n");
        sb.Append("Velocidad ").Append(rover.Speed.ToString("F2")).Append(" m/s   pendiente ").Append(rover.SlopeDeg.ToString("F1"))
          .Append("°   ruedas ").Append(rover.GroundedWheels).Append("/4\n");
        if (CurrentPath != null)
        {
            float eta = rover.Remaining / Mathf.Max(0.05f, rover.cruiseSpeed);
            sb.Append("Ruta ").Append(Pathfinder.LastLength.ToString("F1")).Append(" m   restante ").Append(rover.Remaining.ToString("F1"))
              .Append(" m   ETA ").Append(eta.ToString("F0")).Append(" s\n");
            sb.Append("A*: ").Append(Pathfinder.LastExpanded).Append(" nodos en ").Append(Pathfinder.LastMs.ToString("F1")).Append(" ms\n");
        }
        if (env.LastDropMeasured > 0f)
            sb.Append("Caída ").Append(env.dropHeight.ToString("F0")).Append(" m: ").Append(env.LastDropInterpolated.ToString("F3"))
              .Append(" s (teórico ").Append(env.DropTheoretical.ToString("F3")).Append(" s)\n");
        else if (env.DropRunning) sb.Append("Prueba de caída en curso…\n");
        sb.Append("R reiniciar · T caída · 1-4 tiempo · botón der. orbitar · rueda zoom · WASD mover · F seguir");
        statusText.text = sb.ToString();
    }
}
