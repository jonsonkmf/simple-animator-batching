using System.Collections.Generic;
using SimpleAnimatorBatching;
using UnityEngine;

/// <summary>
/// Demo driver for the Simple Animator Batching crowd renderer. Spawns a grid of instances through
/// the ordinary <see cref="CrowdSpawner"/> public API, gives each one an independent animation phase
/// and speed (so the single batched draw call still shows fully independent motion), and can
/// continuously churn the pool — despawn a few and spawn a few each interval — to demonstrate the
/// dynamic "waves of enemies" use case that the fixed-capacity pool is built for.
/// </summary>
public class CrowdWaveDemo : MonoBehaviour
{
    [Header("References")]
    [Tooltip("The spawner whose pool this demo drives.")]
    [SerializeField] CrowdSpawner spawner;

    [Header("Layout")]
    [Tooltip("How many instances to spawn at startup.")]
    [SerializeField] int initialCount = 40;
    [SerializeField] int columns = 8;
    [SerializeField] float spacing = 1.4f;

    [Header("Animation variety")]
    [Tooltip("Animator state every instance plays (X Bot demo controller uses \"Taunt\").")]
    [SerializeField] string animatorState = "Taunt";
    [SerializeField] Vector2 speedRange = new Vector2(0.6f, 1.3f);

    [Header("Pool churn (waves)")]
    [Tooltip("Continuously despawn/respawn instances to show the pool recycling slots.")]
    [SerializeField] bool churn = true;
    [SerializeField] float churnInterval = 1.0f;
    [SerializeField] int churnBatch = 3;

    readonly List<CrowdInstanceHandle> live = new List<CrowdInstanceHandle>();
    float timer;

    void Start()
    {
        if (spawner == null)
        {
            Debug.LogError("[CrowdWaveDemo] No spawner assigned.", this);
            enabled = false;
            return;
        }

        for (int i = 0; i < initialCount; i++)
            SpawnAt(GridPosition(i));
    }

    void Update()
    {
        if (!churn)
            return;

        timer += Time.deltaTime;
        if (timer < churnInterval)
            return;
        timer = 0f;

        // Despawn a few of the oldest instances, then spawn the same number at fresh slots. The pool
        // reuses the freed slots without any Instantiate/Destroy — the whole point of the design.
        for (int k = 0; k < churnBatch && live.Count > 0; k++)
        {
            spawner.Despawn(live[0]);
            live.RemoveAt(0);
        }
        for (int k = 0; k < churnBatch; k++)
            SpawnAt(GridPosition(Random.Range(0, Mathf.Max(initialCount, columns))));
    }

    Vector3 GridPosition(int index)
    {
        int col = index % columns;
        int row = index / columns;
        float x = (col - (columns - 1) * 0.5f) * spacing;
        float z = row * spacing;
        return transform.position + new Vector3(x, 0f, z);
    }

    void SpawnAt(Vector3 position)
    {
        var handle = spawner.Spawn(position, Quaternion.identity);
        if (!spawner.IsValid(handle))
            return;

        // Drive the instance with the ordinary Mecanim API: a random phase + speed makes every
        // instance visibly out of sync despite sharing one draw call.
        if (spawner.TryGetAnimator(handle, out var animator))
        {
            animator.Play(animatorState, 0, Random.value);
            animator.speed = Random.Range(speedRange.x, speedRange.y);
        }

        live.Add(handle);
    }
}
