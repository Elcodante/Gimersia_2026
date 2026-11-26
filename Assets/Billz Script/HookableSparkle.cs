using System.Collections;
using UnityEngine;

/// <summary>
/// Memainkan efek kilauan secara berkala hanya pada objek yang ditandai HookableTarget.
/// Pasang pada root prefab yang bisa di-hook, lalu assign ParticleSystem dari child.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(HookableTarget))]
public sealed class HookableSparkle : MonoBehaviour
{
    [Header("Sparkle Effect")]
    [Tooltip("ParticleSystem yang menjadi efek kilauan. Disarankan berada di child object.")]
    [SerializeField] private ParticleSystem sparkleParticles;

    [Header("Timing")]
    [Tooltip("Jeda minimum antar-kilauan dalam detik.")]
    [SerializeField, Min(0.1f)] private float minInterval = 3f;

    [Tooltip("Jeda maksimum antar-kilauan dalam detik.")]
    [SerializeField, Min(0.1f)] private float maxInterval = 5f;

    [Tooltip("Jeda sebelum kilauan pertama muncul.")]
    [SerializeField, Min(0f)] private float firstSparkleDelay = 1f;

    private HookableTarget hookableTarget;
    private Coroutine sparkleCoroutine;

    private void Awake()
    {
        hookableTarget = GetComponent<HookableTarget>();

        if (sparkleParticles == null)
            sparkleParticles = GetComponentInChildren<ParticleSystem>(true);

        if (sparkleParticles == null)
        {
            Debug.LogWarning(
                $"[{nameof(HookableSparkle)}] {name} belum memiliki ParticleSystem. " +
                "Tambahkan ParticleSystem pada child lalu assign ke Inspector.", this);
            enabled = false;
            return;
        }

        // Efek diputar sebagai burst berkala, bukan loop terus-menerus.
        ParticleSystem.MainModule main = sparkleParticles.main;
        main.playOnAwake = false;
        main.loop = false;

        sparkleParticles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
    }

    private void OnEnable()
    {
        if (hookableTarget == null)
            hookableTarget = GetComponent<HookableTarget>();

        if (sparkleParticles != null && hookableTarget != null && hookableTarget.CanBeHooked)
            sparkleCoroutine = StartCoroutine(SparkleRoutine());
    }

    private void OnDisable()
    {
        if (sparkleCoroutine != null)
        {
            StopCoroutine(sparkleCoroutine);
            sparkleCoroutine = null;
        }

        if (sparkleParticles != null)
            sparkleParticles.Stop(true, ParticleSystemStopBehavior.StopEmitting);
    }

    private IEnumerator SparkleRoutine()
    {
        if (firstSparkleDelay > 0f)
            yield return new WaitForSeconds(firstSparkleDelay);

        while (hookableTarget != null && hookableTarget.CanBeHooked)
        {
            sparkleParticles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            sparkleParticles.Play(true);

            float interval = Random.Range(
                Mathf.Min(minInterval, maxInterval),
                Mathf.Max(minInterval, maxInterval));

            yield return new WaitForSeconds(interval);
        }

        sparkleCoroutine = null;
    }

    private void OnValidate()
    {
        minInterval = Mathf.Max(0.1f, minInterval);
        maxInterval = Mathf.Max(minInterval, maxInterval);
        firstSparkleDelay = Mathf.Max(0f, firstSparkleDelay);
    }
}
