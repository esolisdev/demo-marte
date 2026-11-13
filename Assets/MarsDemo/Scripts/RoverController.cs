using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Rover con Rigidbody: suspensión por raycast (muelle-amortiguador), tracción limitada
/// por la fricción del regolito, seguimiento de ruta y validación de pendiente.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class RoverController : MonoBehaviour
{
    public enum RoverState { Idle, Driving, Arrived, SlopeAbort, Stuck }

    [Header("Referencias")]
    public MarsEnvironment env;
    [Tooltip("Ruedas visuales en orden: delantera izq., delantera der., trasera izq., trasera der.")]
    public Transform[] wheelVisuals = new Transform[4];
    [Tooltip("Capas contra las que se apoyan las ruedas (terreno y rocas).")]
    public LayerMask groundMask = 1;

    [Header("Rigidbody")]
    [Tooltip("Masa de un rover pequeño, en kg.")]
    public float mass = 150f;
    [Tooltip("Centro de masa bajo (local) para dar estabilidad.")]
    public Vector3 centerOfMass = new Vector3(0f, -0.25f, 0f);

    [Header("Movimiento")]
    [Tooltip("Velocidad de crucero en m/s.")]
    public float cruiseSpeed = 0.5f;
    [Tooltip("Aceleración máxima en m/s² (rampa suave: inercia en baja gravedad).")]
    public float maxAcceleration = 0.2f;
    public float maxYawRateDeg = 20f;
    public float yawAccelDeg = 40f;
    public float lookAhead = 1.5f;
    public float arriveTolerance = 0.3f;
    [Tooltip("Constante de tiempo (s) con la que las ruedas corrigen el error de velocidad.")]
    public float tractionTime = 0.1f;

    [Header("Suspensión (4 raycasts)")]
    public float halfTrack = 0.5f;
    public float halfWheelBase = 0.5f;
    public float mountHeight = -0.05f;
    public float restLength = 0.35f;
    public float wheelRadius = 0.2f;
    [Tooltip("Hundimiento estático deseado (m); de él se deriva la rigidez con la gravedad de Marte.")]
    public float staticSag = 0.10f;
    public float dampingRatio = 0.8f;

    // ---------- Telemetría ----------
    public RoverState State { get; private set; } = RoverState.Idle;
    public float Speed { get; private set; }
    public float SlopeDeg { get; private set; }
    public float Remaining { get; private set; }
    public int GroundedWheels { get; private set; }
    public float MaxSpeedRecorded { get; private set; }
    public float MaxSlopeRecorded { get; private set; }
    public int ObstacleHits { get; private set; }
    public float TravelTime { get; private set; }
    public float SpringK { get; private set; }
    public float DamperC { get; private set; }

    /// <summary>Altura del origen del rover sobre el suelo en reposo.</summary>
    public float RideHeight => restLength + wheelRadius - staticSag - mountHeight;

    Rigidbody rb;
    readonly Vector3[] mounts = new Vector3[4];
    readonly RaycastHit[] hits = new RaycastHit[4];
    readonly bool[] hasHit = new bool[4];
    readonly float[] suspLength = new float[4];

    List<Vector3> path;
    float[] cumLength;
    int segIndex;
    float travelled;
    float vCmd, yawCmd;       // consignas de avance (m/s) y giro (grados/s)
    float slopeTimer, stuckTimer, stuckMark;
    bool holding;             // freno de estacionamiento activo
    readonly Vector3[] holdPoints = new Vector3[4];
    readonly bool[] hasHold = new bool[4];
    const float holdGain = 2f; // 1/s: velocidad de retorno al punto de anclaje
    int obstacleLayer;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        rb.mass = mass;
        rb.linearDamping = 0f;      // atmósfera ≈ 1 % de la terrestre: arrastre despreciable
        rb.angularDamping = 0.05f;  // solo para estabilidad numérica
        rb.useGravity = true;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.automaticCenterOfMass = false;
        rb.centerOfMass = centerOfMass;
        obstacleLayer = LayerMask.NameToLayer("Obstacle");

        mounts[0] = new Vector3(-halfTrack, mountHeight, halfWheelBase);
        mounts[1] = new Vector3(halfTrack, mountHeight, halfWheelBase);
        mounts[2] = new Vector3(-halfTrack, mountHeight, -halfWheelBase);
        mounts[3] = new Vector3(halfTrack, mountHeight, -halfWheelBase);
        for (int i = 0; i < 4; i++) suspLength[i] = restLength - staticSag;

        if (env == null) env = MarsEnvironment.Instance;
        // Rigidez derivada de la gravedad de Marte: k = m·g / (4 · hundimiento).
        // (MarsEnvironment se ejecuta antes y ya ha fijado Physics.gravity.)
        float g = env != null ? env.gravity.magnitude : Physics.gravity.magnitude;
        SpringK = mass * g / (4f * staticSag);
        DamperC = 2f * dampingRatio * Mathf.Sqrt(SpringK * mass / 4f);
    }

    // ---------- API ----------
    /// <summary>Coloca el rover en reposo sobre el terreno, alineado con la pendiente.</summary>
    public void PlaceAt(Vector3 point, float yawDeg)
    {
        Vector3 n = env.NormalAt(point.x, point.z);
        Vector3 ground = env.GroundPoint(point.x, point.z);
        Vector3 f = Vector3.ProjectOnPlane(Quaternion.Euler(0f, yawDeg, 0f) * Vector3.forward, n).normalized;
        Quaternion rot = Quaternion.LookRotation(f, n);
        Vector3 pos = ground + n * RideHeight;
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        rb.position = pos;
        rb.rotation = rot;
        transform.SetPositionAndRotation(pos, rot);
        vCmd = 0f;
        yawCmd = 0f;
        holding = false;
        path = null;
        State = RoverState.Idle;
    }

    /// <summary>Empieza a recorrer la ruta (el primer punto es la posición de origen).</summary>
    public void BeginRoute(List<Vector3> route)
    {
        if (route == null || route.Count < 2) return;
        // En reposo en el origen: se orienta hacia el primer tramo.
        Vector3 dir = route[1] - route[0];
        PlaceAt(route[0], Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg);

        path = route;
        cumLength = new float[route.Count];
        for (int i = 1; i < route.Count; i++)
            cumLength[i] = cumLength[i - 1] + DistXZ(route[i - 1], route[i]);
        segIndex = 0;
        travelled = 0f;
        stuckMark = 0f;
        stuckTimer = 0f;
        slopeTimer = 0f;
        MaxSpeedRecorded = 0f;
        MaxSlopeRecorded = 0f;
        ObstacleHits = 0;
        TravelTime = 0f;
        Remaining = cumLength[cumLength.Length - 1];
        State = RoverState.Driving;
    }

    /// <summary>Detiene el rover (las ruedas frenan hasta velocidad cero).</summary>
    public void Stop()
    {
        path = null;
        State = RoverState.Idle;
    }

    // ---------- Física ----------
    void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;
        UpdateCommands(dt);

        float rayLen = restLength + wheelRadius;
        Vector3 up = transform.up;
        int grounded = 0;
        Vector3 normalSum = Vector3.zero;
        for (int i = 0; i < 4; i++)
        {
            Vector3 mount = transform.TransformPoint(mounts[i]);
            hasHit[i] = Physics.Raycast(mount, -up, out hits[i], rayLen, groundMask, QueryTriggerInteraction.Ignore);
            if (hasHit[i]) { grounded++; normalSum += hits[i].normal; }
            suspLength[i] = hasHit[i] ? hits[i].distance - wheelRadius : restLength;
        }
        GroundedWheels = grounded;

        if (grounded > 0)
        {
            float muStatic = env.StaticFriction, muDynamic = env.DynamicFriction;
            float share = mass / grounded;            // masa que arrastra cada rueda apoyada
            float yawRad = yawCmd * Mathf.Deg2Rad;
            for (int i = 0; i < 4; i++)
            {
                if (!hasHit[i]) continue;
                Vector3 n = hits[i].normal;
                Vector3 point = hits[i].point;
                Vector3 pointVel = rb.GetPointVelocity(point);

                // Suspensión: F = k·compresión − c·velocidad (nunca tira hacia abajo).
                float compression = rayLen - hits[i].distance;
                float normalForce = Mathf.Max(0f, SpringK * compression - DamperC * Vector3.Dot(pointVel, n));

                // Tracción: lleva la velocidad del punto de contacto a la consigna (avance + giro),
                // compensa la componente de la gravedad a lo largo de la pendiente
                // y se limita a μ·N (fricción del regolito): si se pide más, la rueda patina.
                Vector3 fwd = Vector3.ProjectOnPlane(transform.forward, n).normalized;
                Vector3 r = point - rb.worldCenterOfMass;
                Vector3 vDes = fwd * vCmd + Vector3.Cross(up * yawRad, r);
                if (holding)
                {
                    // Freno de estacionamiento: la rueda vuelve a su punto de anclaje,
                    // como la fricción estática (sigue limitado por μ·N).
                    if (!hasHold[i]) { holdPoints[i] = point; hasHold[i] = true; }
                    vDes = (holdPoints[i] - point) * holdGain;
                }
                Vector3 vErr = Vector3.ProjectOnPlane(vDes - pointVel, n);
                Vector3 gTangent = Vector3.ProjectOnPlane(Physics.gravity, n);
                Vector3 traction = share * (vErr / tractionTime - gTangent);
                float mu = vErr.magnitude < 0.05f ? muStatic : muDynamic;
                traction = Vector3.ClampMagnitude(traction, mu * normalForce);

                rb.AddForceAtPosition(n * normalForce + traction, point);
            }
            SlopeDeg = Vector3.Angle(normalSum / grounded, Vector3.up);
        }

        Speed = rb.linearVelocity.magnitude;
        ValidateMotion(dt);
    }

    /// <summary>Calcula las consignas de avance y giro siguiendo la ruta (persecución pura).</summary>
    void UpdateCommands(float dt)
    {
        float vTarget = 0f, yawTarget = 0f;
        if (State == RoverState.Driving && path != null)
        {
            Vector3 pos = rb.position;
            UpdateProgress(pos);
            Vector3 goal = path[path.Count - 1];
            float distGoal = DistXZ(pos, goal);
            Remaining = Mathf.Max(distGoal, cumLength[cumLength.Length - 1] - travelled);

            Vector3 to = PointAtDistance(travelled + lookAhead) - pos;
            to.y = 0f;
            Vector3 fwd = transform.forward;
            fwd.y = 0f;
            float err = Vector3.SignedAngle(fwd, to, Vector3.up);
            bool overshoot = distGoal < 1f && Vector3.Dot(fwd, to) < 0f;

            if (distGoal <= arriveTolerance || overshoot)
            {
                State = RoverState.Arrived; // destino alcanzado: consignas a cero
                Remaining = distGoal;
            }
            else
            {
                yawTarget = Mathf.Clamp(err * 1.5f, -maxYawRateDeg, maxYawRateDeg);
                // Si el rumbo está muy desviado, reduce la velocidad (gira casi en el sitio).
                float align = Mathf.InverseLerp(60f, 25f, Mathf.Abs(err));
                // Perfil de frenado al acercarse al destino: v = √(2·a·d).
                float brake = Mathf.Sqrt(2f * maxAcceleration * 0.8f * Mathf.Max(0f, distGoal - arriveTolerance * 0.5f));
                vTarget = Mathf.Min(cruiseSpeed * align, Mathf.Max(brake, 0.05f * align));
            }
        }
        vCmd = Mathf.MoveTowards(vCmd, vTarget, maxAcceleration * dt);
        yawCmd = Mathf.MoveTowards(yawCmd, yawTarget, yawAccelDeg * dt);

        // Parado y sin consignas: se activa el freno de estacionamiento.
        bool wantHold = vTarget == 0f && yawTarget == 0f && vCmd == 0f && yawCmd == 0f && Speed < 0.03f;
        if (wantHold && !holding)
        {
            holding = true;
            for (int i = 0; i < 4; i++) hasHold[i] = false;
        }
        else if (!wantHold && (vTarget != 0f || yawTarget != 0f)) holding = false;
    }

    /// <summary>Validación del movimiento: pendiente real bajo el rover y detección de atasco.</summary>
    void ValidateMotion(float dt)
    {
        if (State != RoverState.Driving) return;
        TravelTime += dt;
        MaxSpeedRecorded = Mathf.Max(MaxSpeedRecorded, Speed);
        if (GroundedWheels >= 3) MaxSlopeRecorded = Mathf.Max(MaxSlopeRecorded, SlopeDeg);

        // Pendiente máxima física (misma constante que usa la ruta) con 2° de tolerancia.
        if (GroundedWheels >= 3 && SlopeDeg > env.maxSlopeDeg + 2f)
        {
            slopeTimer += dt;
            if (slopeTimer > 0.5f) State = RoverState.SlopeAbort;
        }
        else slopeTimer = 0f;

        // Atasco: sin avance apreciable durante 20 s.
        if (travelled - stuckMark > 0.2f) { stuckMark = travelled; stuckTimer = 0f; }
        else
        {
            stuckTimer += dt;
            if (stuckTimer > 20f) State = RoverState.Stuck;
        }
    }

    // ---------- Seguimiento de la polilínea ----------
    void UpdateProgress(Vector3 pos)
    {
        float bestDist = float.MaxValue, bestTravel = travelled;
        int bestSeg = segIndex;
        int last = Mathf.Min(segIndex + 2, path.Count - 2);
        for (int s = segIndex; s <= last; s++)
        {
            Vector2 a = new Vector2(path[s].x, path[s].z), b = new Vector2(path[s + 1].x, path[s + 1].z);
            Vector2 p = new Vector2(pos.x, pos.z);
            Vector2 ab = b - a;
            float len2 = ab.sqrMagnitude;
            float t = len2 > 1e-6f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2) : 0f;
            float d = Vector2.Distance(p, a + ab * t);
            if (d < bestDist - 1e-4f)
            {
                bestDist = d;
                bestSeg = s;
                bestTravel = cumLength[s] + t * Mathf.Sqrt(len2);
            }
        }
        if (bestTravel > travelled) { travelled = bestTravel; segIndex = bestSeg; }
    }

    Vector3 PointAtDistance(float d)
    {
        float total = cumLength[cumLength.Length - 1];
        if (d >= total) return path[path.Count - 1];
        for (int s = segIndex; s < path.Count - 1; s++)
        {
            if (d <= cumLength[s + 1])
            {
                float segLen = cumLength[s + 1] - cumLength[s];
                float t = segLen > 1e-6f ? (d - cumLength[s]) / segLen : 0f;
                return Vector3.Lerp(path[s], path[s + 1], t);
            }
        }
        return path[path.Count - 1];
    }

    static float DistXZ(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x, dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    void OnCollisionEnter(Collision collision)
    {
        // Cuenta los choques del chasis con rocas (debería ser 0 en una ruta válida).
        if (collision.gameObject.layer == obstacleLayer) ObstacleHits++;
    }

    // ---------- Ruedas visuales ----------
    void Update()
    {
        for (int i = 0; i < 4; i++)
        {
            if (wheelVisuals == null || i >= wheelVisuals.Length || wheelVisuals[i] == null) continue;
            Vector3 target = mounts[i] + Vector3.down * suspLength[i];
            wheelVisuals[i].localPosition = Vector3.Lerp(wheelVisuals[i].localPosition, target, 0.3f);
        }
    }
}
