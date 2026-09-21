// 计分板——《关卡架构设计（骨架版）》§八「二 状态机引擎：LevelFlowRunner 按 stages 执行序列；
// StageTimer/ScoreBoard」+《第一阶段计划》§2：
//   「第一关 4 题、每题 7.5 分、共 30 分，至少 3 题正确才算通过。」
//   「第一关不通过不阻断后续关卡（本阶段只做第一关，接口先留好）。」
//
// 纯数据类（非 MonoBehaviour），由 LevelFlowRunner 持有并在 settlement 阶段读 Summary()。
// 结算 UI 尚未交付：现在把 Summary() 打进 Console，UI 到位后直接读同样的属性即可。
using System;
using System.Collections.Generic;
using System.Text;

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

        public void Configure(int totalQuestions, float scorePerQuestion, int passCorrectCount)
        {
            TotalQuestions = totalQuestions < 1 ? 1 : totalQuestions;
            ScorePerQuestion = scorePerQuestion < 0f ? 0f : scorePerQuestion;
            PassCorrectCount = passCorrectCount < 0 ? 0 : passCorrectCount;
        }

        public void Reset() => answers.Clear();

        /// <summary>记录一次作答。optionId 为空表示超时未作答（按错误计，但不阻断流程）。</summary>
        public AnswerRecord Record(string questionId, string optionId, string correctOptionId,
                                   float elapsedSeconds, bool timedOut)
        {
            bool correct = !timedOut
                           && !string.IsNullOrEmpty(optionId)
                           && !string.IsNullOrEmpty(correctOptionId)
                           && string.Equals(optionId, correctOptionId, StringComparison.Ordinal);
            var record = new AnswerRecord
            {
                questionId = questionId,
                optionId = optionId,
                correct = correct,
                timedOut = timedOut,
                elapsedSeconds = elapsedSeconds,
            };
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
