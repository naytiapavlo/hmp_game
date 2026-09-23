// 计分板——《关卡架构设计（骨架版）》§八「二 状态机引擎：LevelFlowRunner 按 stages 执行序列；
// StageTimer/ScoreBoard」+《第一阶段计划》§2：
//   「第一关 4 题、每题 7.5 分、共 30 分，至少 3 题正确才算通过。」
//   「第一关不通过不阻断后续关卡（本阶段只做第一关，接口先留好）。」
//
// 纯数据类（非 MonoBehaviour），由 LevelFlowRunner 持有并在 settlement 阶段读 Summary()。
// TrainingSettlementView 与 Console 共用这些作答记录，不计入 CG、加载或教官讲解时长。
using System;
using System.Collections.Generic;
using System.Text;
using HMProtection.Modules.Score;

namespace HMProtection.Core
{
    /// <summary>单题作答记录。</summary>
    [Serializable]
    public sealed class AnswerRecord
    {
        public string questionId;
        public string optionId;
        public bool correct;
        public bool timedOut;
        public float elapsedSeconds;
    }

    public sealed class ScoreBoard
    {
        public int TotalQuestions = 4;
        public float ScorePerQuestion = 7.5f;
        public int PassCorrectCount = 3;

        private readonly List<AnswerRecord> answers = new List<AnswerRecord>();
        private readonly ScoreLedger ledger = new ScoreLedger();
        public ScoreLedger Ledger => ledger;

        /// <summary>已出题数（含超时未作答）。</summary>
        public int AskedCount => answers.Count;

        /// <summary>已答对数。</summary>
        public int CorrectCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < answers.Count; i++) if (answers[i].correct) n++;
                return n;
            }
        }

        /// <summary>当前得分（答对数 × 每题分值）。</summary>
        public float Score => CorrectCount * ScorePerQuestion;

        /// <summary>本关满分。</summary>
        public float MaxScore => TotalQuestions * ScorePerQuestion;

        /// <summary>是否达到通过线（计划书口径：至少 3 题正确）。</summary>
        public bool Passed => CorrectCount >= PassCorrectCount;

        /// <summary>最后一次作答是否正确；还没答过题为 null。</summary>
        public bool? LastAnswerCorrect =>
            answers.Count == 0 ? (bool?)null : answers[answers.Count - 1].correct;

        public IReadOnlyList<AnswerRecord> Answers => answers;

        /// <summary>三题实际答题与行动耗时合计，排除讲解和加载。</summary>
        public float TotalElapsedSeconds
        {
            get
            {
                float total = 0f;
                foreach (var answer in answers)
                    if (!float.IsNaN(answer.elapsedSeconds) && !float.IsInfinity(answer.elapsedSeconds))
                        total += Math.Max(0f, answer.elapsedSeconds);
                return total;
            }
        }

        public void Configure(int totalQuestions, float scorePerQuestion, int passCorrectCount)
        {
            TotalQuestions = totalQuestions < 1 ? 1 : totalQuestions;
            ScorePerQuestion = scorePerQuestion < 0f ? 0f : scorePerQuestion;
            PassCorrectCount = passCorrectCount < 0 ? 0 : passCorrectCount;
        }

        public void Reset() { answers.Clear(); ledger.Clear(); }

        /// <summary>记录一次作答。optionId 为空表示超时未作答（按错误计，但不阻断流程）。</summary>
        public AnswerRecord Record(string questionId, string optionId, string correctOptionId,
                                   float elapsedSeconds, bool timedOut)
        {
            bool correct = !timedOut
                           && !string.IsNullOrEmpty(optionId)
                           && !string.IsNullOrEmpty(correctOptionId)
                           && string.Equals(optionId, correctOptionId, StringComparison.Ordinal);
            // questionId is the legacy business key. Future QuestionSession callers may use attempt IDs
            // through RecordAttempt; duplicate delivery must never award a second score.
            if (!ledger.TryRecord(questionId, questionId, optionId, correct, timedOut, elapsedSeconds, out var entry, out _))
            {
                for (int i = 0; i < answers.Count; i++)
                    if (string.Equals(answers[i].questionId, questionId, StringComparison.Ordinal)) return answers[i];
                return null;
            }
            var record = new AnswerRecord
            {
                questionId = entry != null ? entry.QuestionId : questionId,
                optionId = entry != null ? entry.OptionId : optionId,
                correct = entry != null && entry.Correct,
                timedOut = entry != null && entry.TimedOut,
                elapsedSeconds = entry != null ? entry.ElapsedSeconds : elapsedSeconds,
            };
            answers.Add(record);
            return record;
        }

        public AnswerRecord RecordAttempt(string attemptId, string questionId, string optionId, string correctOptionId,
            float elapsedSeconds, bool timedOut)
        {
            bool correct = !timedOut && !string.IsNullOrEmpty(optionId) && string.Equals(optionId, correctOptionId, StringComparison.Ordinal);
            if (!ledger.TryRecord(attemptId, questionId, optionId, correct, timedOut, elapsedSeconds, out var entry, out _))
                return null;
            var record = new AnswerRecord { questionId = entry.QuestionId, optionId = entry.OptionId, correct = entry.Correct, timedOut = entry.TimedOut, elapsedSeconds = entry.ElapsedSeconds };
            answers.Add(record);
            return record;
        }

        /// <summary>结算用摘要（Console 输出 / 结算面板文本）。</summary>
        public string Summary()
        {
            var sb = new StringBuilder();
            sb.Append("得分 ").Append(Score.ToString("0.#")).Append('/').Append(MaxScore.ToString("0.#"))
              .Append("　答对 ").Append(CorrectCount).Append('/').Append(TotalQuestions)
              .Append("　").Append(Passed ? "通过" : "未通过");
            for (int i = 0; i < answers.Count; i++)
            {
                AnswerRecord a = answers[i];
                sb.Append('\n').Append("  #").Append(i + 1).Append(' ').Append(a.questionId)
                  .Append(" → ").Append(string.IsNullOrEmpty(a.optionId) ? "(未作答)" : a.optionId)
                  .Append(a.timedOut ? " [超时]" : (a.correct ? " [正确]" : " [错误]"))
                  .Append("　用时 ").Append(a.elapsedSeconds.ToString("0.0")).Append('s');
            }
            return sb.ToString();
        }
    }
}
