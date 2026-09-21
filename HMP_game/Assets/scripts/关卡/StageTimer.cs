// 阶段倒计时——服务于《关卡架构设计（骨架版）》§八「统一计时冻结开关」：
//   「答题/操作段外计时与火焰一起冻结——计划书硬约束」。
//
// 冻结语义由 LevelFlowRunner 统一编排：进入动画/解说/暂停/等待「继续」时 Pause()，
// 与 FireEffectController.SetFrozen(true) 成对发生，保证「火焰与计时一起停」（第一阶段计划 §6.3）。
//
// 用 Time.deltaTime 推进：一旦 timeScale 被外部置 0（暂停菜单等），计时自然停住，不会跑飞。
// UI 进度条读 Normalized01（1=满格，0=耗尽）；顶部「进度条 + 火焰图标」直接接这个值。
using System;
using UnityEngine;

namespace HMProtection.Core
{
    [DisallowMultipleComponent]
    public sealed class StageTimer : MonoBehaviour
    {
        /// <summary>本次启动设定的总时长（秒）。0 表示未设定。</summary>
        public float Duration { get; private set; }

        /// <summary>剩余秒数。</summary>
        public float Remaining { get; private set; }

        /// <summary>已消耗秒数。</summary>
        public float Elapsed => Mathf.Max(0f, Duration - Remaining);

        /// <summary>是否正在推进（已启动且未暂停、未耗尽）。</summary>
        public bool IsRunning { get; private set; }

        /// <summary>是否已耗尽。</summary>
        public bool HasExpired { get; private set; }

        /// <summary>剩余比例 1→0，供顶部进度条使用。</summary>
        public float Normalized01 => Duration <= 0f ? 0f : Mathf.Clamp01(Remaining / Duration);

        /// <summary>耗尽时触发一次（Update 中，主线程）。</summary>
        public event Action OnExpired;

        /// <summary>启动倒计时（会清掉上一轮的耗尽标记）。</summary>
        public void Begin(float duration)
        {
            Duration = Mathf.Max(0f, duration);
            Remaining = Duration;
            HasExpired = false;
            IsRunning = Duration > 0f;
        }

        /// <summary>暂停推进：保留剩余时间，可 Resume。动画/解说/暂停/等待「继续」用这个。</summary>
        public void Pause() => IsRunning = false;

        /// <summary>从暂停处继续推进。</summary>
        public void Resume()
        {
            if (HasExpired || Duration <= 0f) return;
            IsRunning = true;
        }

        /// <summary>停止并清空（不触发 OnExpired）。阶段结束用这个，避免残留计时误推进流程。</summary>
        public void Stop()
        {
            IsRunning = false;
            HasExpired = false;
            Duration = 0f;
            Remaining = 0f;
        }
    }
}
