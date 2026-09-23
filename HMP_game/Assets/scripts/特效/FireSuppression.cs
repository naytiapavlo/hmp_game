using System;
using HMProtection.Modules.Fire;
using UnityEngine;

public enum SprayHitQuality { None, TooFar, TooHigh, Effective }

/// <summary>Unity driver for one FireStateModel. It owns the single simulation Tick.</summary>
[DisallowMultipleComponent]
public class FireSuppression : MonoBehaviour
{
    [Header("接线（留空取同物体上的 FireEffectController）")]
    [SerializeField] private FireEffectController fire;
    [Tooltip("可选共享参数档案；只读，不会被运行时状态回写。留空时保持以下旧场景参数。")]
    [SerializeField] private FireProfile profile;
    [Header("压制节奏")]
    public float suppressSpeed = 1.1f;
    public float recoverSpeed = .12f;
    public float lowerAt = .2f;
    public float smokeLowerAt = .15f;
    [Header("复燃与扑灭确认")]
    public float reigniteAfter = 4f;
    public float confirmSeconds = 5f;
    [SerializeField] private bool verboseLog = true;

    public float Pressed01 => Model.Pressed01;
    public bool Confirming => Model.Confirming;
    public bool ExtinguishedConfirmed => Model.ExtinguishedConfirmed;
    public SprayHitQuality LastQuality { get; private set; }
    public FireStateModel Model { get { ResolveFire(); return fire != null ? fire.Model : fallbackModel; } }
    public event Action<FireLevel> LevelLowered;
    public event Action Reignited;
    public event Action Extinguished;

    readonly FireStateModel fallbackModel = new FireStateModel();
    FireStateModel subscribed;
    string lastProfileError;

    void Awake() { ResolveFire(); SyncModel(); }
    void OnEnable() { ResolveFire(); SyncModel(); }
    void OnDisable() => Unsubscribe();
    void OnValidate() => ApplySettings();

    public void Configure(FireEffectController value) { fire = value; SyncModel(); }
    public void ConfigureProfile(FireProfile value) { profile = value; ApplySettings(); }
    public void SetSimulationPaused(bool value)
    {
        ResolveFire();
        if (fire != null) fire.SetSimulationPaused(value);
        else Model.SetSimulationPaused(value);
    }
    public void ApplySpray(SprayHitQuality quality) => ApplySpray(quality, 1f);
    public void ApplySpray(SprayHitQuality quality, float coverage)
    {
        LastQuality = quality;
        Model.ApplySpray(quality == SprayHitQuality.Effective, coverage);
    }

    void Update()
    {
        ResolveFire();
        SyncModel();
        if (fire != null) fire.RefreshSimulationPause();
        Model.Tick(Time.deltaTime);
    }
    void ResolveFire() { if (fire == null) fire = GetComponent<FireEffectController>(); }
    void SyncModel()
    {
        FireStateModel next = Model;
        if (!ReferenceEquals(subscribed, next))
        {
            Unsubscribe(); subscribed = next;
            subscribed.LevelLowered += OnLowered;
            subscribed.Reignited += OnReignited;
            subscribed.Extinguished += OnExtinguished;
        }
        ApplySettings();
    }
    void ApplySettings()
    {
        FireStateModel target = subscribed ?? (fire != null ? fire.Model : fallbackModel);
        string error = null;
        if (profile != null && profile.TryGetTuning(out FireTuning tuning, out error))
        {
            target.SuppressSpeed = tuning.SuppressSpeed;
            target.RecoverSpeed = tuning.RecoverSpeed;
            target.LowerAt = tuning.LowerAt;
            target.SmokeLowerAt = tuning.SmokeLowerAt;
            target.ReigniteAfter = tuning.ReigniteAfter;
            target.ConfirmSeconds = tuning.ConfirmSeconds;
            lastProfileError = null;
            return;
        }
        if (profile != null && !string.IsNullOrEmpty(error) && error != lastProfileError)
        {
            Debug.LogError("[FireSupp] Ignoring invalid FireProfile '" + profile.name + "': " + error, profile);
            lastProfileError = error;
        }
        target.SuppressSpeed = Mathf.Max(0f, suppressSpeed);
        target.RecoverSpeed = Mathf.Max(0f, recoverSpeed);
        target.LowerAt = Mathf.Clamp01(lowerAt);
        target.SmokeLowerAt = Mathf.Clamp01(smokeLowerAt);
        target.ReigniteAfter = Mathf.Max(0f, reigniteAfter);
        target.ConfirmSeconds = Mathf.Max(0f, confirmSeconds);
    }
    void Unsubscribe()
    {
        if (subscribed == null) return;
        subscribed.LevelLowered -= OnLowered; subscribed.Reignited -= OnReignited; subscribed.Extinguished -= OnExtinguished;
        subscribed = null;
    }
    void OnLowered(FireState state) { if (verboseLog) Debug.Log("[FireSupp] 压制降级 → " + state, this); LevelLowered?.Invoke((FireLevel)state); }
    void OnReignited() { if (verboseLog) Debug.Log("[FireSupp] 复燃：余火回升 Small → Medium", this); Reignited?.Invoke(); }
    void OnExtinguished() { if (verboseLog) Debug.Log("[FireSupp] 扑灭确认", this); Extinguished?.Invoke(); }
}
