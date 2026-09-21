using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace HMProtection.Quiz
{
    [Serializable] public sealed class SceneChoiceTarget
    {
        public string mode, anchorId;
        public float[] position, offset;
        public Vector3 Position => Vector(position);
        public Vector3 Offset => Vector(offset);
        static Vector3 Vector(float[] a) => a == null ? Vector3.zero : new Vector3(a[0], a[1], a[2]);
    }
    [Serializable] public sealed class SceneChoiceOption
    {
        public string id, label;
        public string interactionTargetId, interactionPrompt, routeLabel;
        public SceneChoiceTarget target;
        public float[] bubbleOffset;
        public float width;
        public float Width => width == 0 ? 300 : width;
        public Vector2 Offset => bubbleOffset == null ? new Vector2(-180, 120) : new Vector2(bubbleOffset[0], bubbleOffset[1]);
    }
    [Serializable] public sealed class SceneChoiceQuestion
    {
        public string id, prompt;
        public string correctOptionId;
        public bool numberedOptions;
        public float timeLimitSeconds;
        public SceneChoiceOption[] options;
        public InstructorLessonConfig instructor;
        public SceneChoiceSource[] sources;
    }
    [Serializable] public sealed class SceneChoiceSource
    {
        public string title, url, accessedOn;
    }
    [Serializable] public sealed class InstructorDialogue
    {
        public string first, second, third;
    }
    [Serializable] public sealed class InstructorLessonConfig
    {
        public bool enabled;
        public string speaker, videoPath;
        public InstructorDialogue correct, incorrect;
    }
    [Serializable] public sealed class SceneChoiceResult
    {
        public string questionId, optionId, targetMode, anchorId;
        public Vector3 worldPosition;
    }
    [Serializable] public sealed class SceneChoiceCatalog
    {
        public int version;
        public SceneChoiceQuestion[] questions;
        public static string DefaultPath => Path.Combine(Application.streamingAssetsPath, "Quiz/scene_choices.json");
        public SceneChoiceQuestion Find(string id) => Array.Find(questions, q => q.id == id);
        public static bool TryLoad(out SceneChoiceCatalog catalog, out string error)
        {
            catalog = null;
            try { return TryParse(File.ReadAllText(DefaultPath), out catalog, out error); }
            catch (Exception e) { error = "Cannot read scene choices: " + e.Message; return false; }
        }
        static void Require(bool value, string message) { if (!value) throw new FormatException(message); }
        static bool VectorValid(float[] a, int length, bool optional = true)
        {
            if (a == null) return optional;
            if (a.Length != length) return false;
            foreach (float n in a) if (float.IsNaN(n) || float.IsInfinity(n)) return false;
            return true;
        }
        public static bool TryParse(string json, out SceneChoiceCatalog catalog, out string error)
        {
            catalog = null; error = null;
            try
            {
                var c = JsonUtility.FromJson<SceneChoiceCatalog>(json);
                Require(c != null && c.version == 1 && c.questions != null && c.questions.Length > 0, "Expected version 1 and a nonempty questions array.");
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (var q in c.questions)
                {
                    Require(q != null && !string.IsNullOrWhiteSpace(q.id) && ids.Add(q.id), "Empty or duplicate question ID.");
                    Require(q.options != null && q.options.Length > 0, q.id + ": options are empty.");
                    Require(!float.IsNaN(q.timeLimitSeconds) && !float.IsInfinity(q.timeLimitSeconds) && q.timeLimitSeconds >= 0f,
                        q.id + ": timeLimitSeconds must be nonnegative (0 uses the scene default).");
                    if (q.instructor != null && q.instructor.enabled)
                    {
                        Require(!string.IsNullOrWhiteSpace(q.instructor.speaker), q.id + ": instructor speaker is empty.");
                        foreach (var dialogue in new[] { q.instructor.correct, q.instructor.incorrect })
                            Require(dialogue != null && !string.IsNullOrWhiteSpace(dialogue.first)
                                && !string.IsNullOrWhiteSpace(dialogue.second) && !string.IsNullOrWhiteSpace(dialogue.third),
                                q.id + ": correct/incorrect must each contain first, second and third dialogue lines.");
                    }
                    var optionIds = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var o in q.options)
                    {
                        Require(o != null && !string.IsNullOrWhiteSpace(o.id) && optionIds.Add(o.id), q.id + ": empty or duplicate option ID.");
                        Require(!string.IsNullOrWhiteSpace(o.label), q.id + ": empty label.");
                        Require(o.target != null, q.id + ": missing target.");
                        var t = o.target;
                        Require(t.mode == "anchor" || t.mode == "position", q.id + ": target.mode must be anchor or position.");
                        Require(t.mode != "anchor" || !string.IsNullOrWhiteSpace(t.anchorId), q.id + ": missing anchorId.");
                        Require(VectorValid(t.position, 3, t.mode != "position") && VectorValid(t.offset, 3) && VectorValid(o.bubbleOffset, 2), q.id + ": invalid coordinate array.");
                        Require(!float.IsNaN(o.width) && (o.width == 0 || o.width >= 240 && o.width <= 800), q.id + ": width must be 240–800 or 0 (default).");
                    }
                    if (!string.IsNullOrEmpty(q.correctOptionId))
                    {
                        Require(optionIds.Contains(q.correctOptionId), q.id + ": correctOptionId must name an option.");
                        var targets = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var o in q.options)
                            Require(!string.IsNullOrWhiteSpace(o.interactionTargetId) && targets.Add(o.interactionTargetId)
                                && !string.IsNullOrWhiteSpace(o.interactionPrompt), q.id + ": each physical option needs a distinct target and interaction prompt.");
                    }
                }
                catalog = c; return true;
            }
            catch (Exception e) { error = e.Message; return false; }
        }
    }
}
