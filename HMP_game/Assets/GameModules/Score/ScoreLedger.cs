using System;
using System.Collections.Generic;

namespace HMProtection.Modules.Score
{
    public sealed class ScoreEntry
    {
        public string BusinessKey { get; internal set; }
        public string QuestionId { get; internal set; }
        public string OptionId { get; internal set; }
        public bool Correct { get; internal set; }
        public bool TimedOut { get; internal set; }
        public float ElapsedSeconds { get; internal set; }
    }

    /// <summary>Append-only per-run score data. A business key can be recorded once only.</summary>
    public sealed class ScoreLedger
    {
        readonly Dictionary<string, ScoreEntry> byKey = new Dictionary<string, ScoreEntry>(StringComparer.Ordinal);
        readonly List<ScoreEntry> entries = new List<ScoreEntry>();
        public IReadOnlyList<ScoreEntry> Entries => entries;
        public bool TryRecord(string businessKey, string questionId, string optionId, bool correct, bool timedOut, float elapsedSeconds, out ScoreEntry entry, out string error)
        {
            entry = null;
            if (string.IsNullOrWhiteSpace(businessKey)) { error = "A score business key is required."; return false; }
            if (byKey.TryGetValue(businessKey, out entry)) { error = "This score business key was already recorded."; return false; }
            entry = new ScoreEntry { BusinessKey = businessKey, QuestionId = questionId, OptionId = optionId, Correct = correct, TimedOut = timedOut, ElapsedSeconds = float.IsNaN(elapsedSeconds) || float.IsInfinity(elapsedSeconds) ? 0f : Math.Max(0f, elapsedSeconds) };
            byKey.Add(businessKey, entry); entries.Add(entry); error = null; return true;
        }
        public bool TryGet(string businessKey, out ScoreEntry entry) => byKey.TryGetValue(businessKey, out entry);
        public void Clear() { byKey.Clear(); entries.Clear(); }
    }
}
