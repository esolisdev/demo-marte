using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Cámara orbital: botón derecho para orbitar, rueda para zoom,
/// botón central o WASD para desplazar el pivote y F para seguir al rover.
/// </summary>
public class OrbitCamera : MonoBehaviour
{
    public MarsEnvironment env;
    public Transform followTarget;
    public bool follow;

    public Vector3 pivot = new Vector3(100f, 10f, 100f);
    public float yaw = 30f;
    public float pitch = 40f;
    public float distance = 70f;
    public float minDistance = 5f;
    public float maxDistance = 260f;
    public float orbitSpeed = 0.25f;   // grados por píxel
    public float zoomStep = 0.12f;     // fracción por muesca de rueda

    void LateUpdate()
    {
        Mouse mouse = Mouse.current;
        Keyboard kb = Keyboard.current;
        float dt = Time.unscaledDeltaTime; // independiente de la escala de tiempo

        if (kb != null && kb.fKey.wasPressedThisFrame) follow = !follow;

        if (mouse != null)
        {
            Vector2 delta = mouse.delta.ReadValue();
            if (mouse.rightButton.isPressed)
            {
                yaw += delta.x * orbitSpeed;
                pitch = Mathf.Clamp(pitch - delta.y * orbitSpeed, 8f, 89f);
            }
            if (mouse.middleButton.isPressed)
            {
                Pan(-delta.x * distance * 0.0015f, -delta.y * distance * 0.0015f);
                follow = false;
            }
            float scroll = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(scroll) > 0.01f)
                distance = Mathf.Clamp(distance * (1f - Mathf.Sign(scroll) * zoomStep), minDistance, maxDistance);
        }

        if (kb != null)
        {
            float x = (kb.dKey.isPressed ? 1f : 0f) - (kb.aKey.isPressed ? 1f : 0f);
            float z = (kb.wKey.isPressed ? 1f : 0f) - (kb.sKey.isPressed ? 1f : 0f);
            if (x != 0f || z != 0f)
            {
                Pan(x * distance * 0.8f * dt, z * distance * 0.8f * dt);
                follow = false;
            }
        }

        if (follow && followTarget != null)
            pivot = Vector3.Lerp(pivot, followTarget.position, 1f - Mathf.Exp(-6f * dt));
        else if (env != null && env.terrain != null)
        {
            // El pivote se mantiene dentro del terreno y a su altura.
            float size = env.terrainSize;
            Vector3 o = env.terrain.transform.position;
            pivot.x = Mathf.Clamp(pivot.x, o.x, o.x + size);
            pivot.z = Mathf.Clamp(pivot.z, o.z, o.z + size);
            pivot.y = Mathf.Lerp(pivot.y, env.HeightAt(pivot.x, pivot.z), 1f - Mathf.Exp(-6f * dt));
        }

        Quaternion rot = Quaternion.Euler(pitch, yaw, 0f);
        Vector3 pos = pivot - rot * Vector3.forward * distance;
        // Evita que la cámara quede bajo el terreno.
        if (env != null && env.terrain != null)
        {
            float ground = env.HeightAt(pos.x, pos.z) + 1f;
            if (pos.y < ground) pos.y = ground;
        }
        transform.SetPositionAndRotation(pos, Quaternion.LookRotation(pivot - pos, Vector3.up));
    }

    /// <summary>Desplaza el pivote en el plano horizontal relativo a la cámara.</summary>
    void Pan(float right, float forward)
    {
        Quaternion flat = Quaternion.Euler(0f, yaw, 0f);
        pivot += flat * new Vector3(right, 0f, forward);
    }
}
