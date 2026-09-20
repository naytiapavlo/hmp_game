// Passive Play-mode acceptance recorder. Does not modify scenes or drive input.
using System;
using System.Collections.Generic;
using System.IO;
using HMProtection.UI;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.Video;

[InitializeOnLoad]
public static class CgTransitionValidation
{
    [Serializable] private sealed class Report
    {
        public string startedUtc;
        public bool menuInitiallyUncovered, videoObserved, officeObserved, overlayRemoved;
        public bool aspectPreserved, videoAboveBlackout, videoDone;
        public float maxAudioPeak, maxBlackoutAlpha;
        public double firstTransition = -1, firstVideo = -1, lastVideo = -1, officeAt = -1, overlayGoneAt = -1;
        public long maxVideoFrame;
        public double clipLength;
        public ushort audioTracks;
        public bool editorAudioMuted, listenerPaused, audioSourcePlaying;
        public float listenerVolume, maxListenerPeak;
        public int activeListeners;
        public List<string> errors = new List<string>();
    }
    private static Report report;
    private static string path;
    private static double start, lastWrite;
    private static CgTransitionPlayer observedPlayer;
    private static readonly float[] samples = new float[512];
    static CgTransitionValidation()
    {
        EditorApplication.playModeStateChanged += StateChanged;
        EditorApplication.update += Observe;
        Application.logMessageReceived += Log;
    }
    private static void StateChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.EnteredPlayMode)
        {
            report = new Report { startedUtc = DateTime.UtcNow.ToString("o") };
            path = "Library/CgTransitionValidation-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".json";
            start = EditorApplication.timeSinceStartup;
            lastWrite = 0;
            observedPlayer = null;
            foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (observedPlayer == null) observedPlayer = root.GetComponentInChildren<CgTransitionPlayer>(true);
                report.activeListeners += root.GetComponentsInChildren<AudioListener>().Length;
            }
        }
        if (state == PlayModeStateChange.ExitingPlayMode) { Write(); report = null; }
    }
    private static void Log(string message, string stack, LogType type)
    {
        if (report != null && (type == LogType.Error || type == LogType.Exception || type == LogType.Assert))
            report.errors.Add(message);
    }
    private static void Write()
    {
        if (report != null) File.WriteAllText(path, JsonUtility.ToJson(report, true));
    }
    private static void Observe()
    {
        if (report == null || !EditorApplication.isPlaying) return;
        double now = EditorApplication.timeSinceStartup - start;
        var player = observedPlayer;
        if (SceneManager.GetActiveScene().name == "初始界面" && player != null && !player.gameObject.activeSelf)
            report.menuInitiallyUncovered = true;
        if (player != null && player.gameObject.activeSelf)
        {
            if (report.firstTransition < 0) report.firstTransition = now;
            var vp = player.GetComponent<VideoPlayer>();
            var image = player.GetComponentInChildren<RawImage>();
            var black = player.transform.Find("Blackout").GetComponent<Image>();
            report.maxBlackoutAlpha = Mathf.Max(report.maxBlackoutAlpha, black.color.a);
            report.videoAboveBlackout = image.transform.GetSiblingIndex() > black.transform.GetSiblingIndex();
            report.aspectPreserved = image.GetComponent<AspectRatioFitter>() != null;
            report.clipLength = vp.clip.length; report.audioTracks = vp.clip.audioTrackCount;
            report.videoDone |= player.VideoDone;
            if (vp.isPlaying && image.enabled && vp.frame >= 0)
            {
                report.videoObserved = true;
                if (report.firstVideo < 0) report.firstVideo = now;
                report.lastVideo = now; report.maxVideoFrame = Math.Max(report.maxVideoFrame, vp.frame);
                var audio = player.GetComponent<AudioSource>();
                report.editorAudioMuted = EditorUtility.audioMasterMute;
                report.listenerPaused = AudioListener.pause;
                report.listenerVolume = AudioListener.volume;
                AudioListener.GetOutputData(samples, 0);
                foreach (float value in samples) report.maxListenerPeak = Mathf.Max(report.maxListenerPeak, Mathf.Abs(value));
                if (audio != null)
                {
                    report.audioSourcePlaying |= audio.isPlaying;
                    audio.GetOutputData(samples, 0);
                    foreach (float value in samples) report.maxAudioPeak = Mathf.Max(report.maxAudioPeak, Mathf.Abs(value));
                }
            }
        }
        if (SceneManager.GetActiveScene().name == "办公室场景")
        {
            report.officeObserved = true;
            if (report.officeAt < 0) report.officeAt = now;
            if (player == null) { report.overlayRemoved = true; if (report.overlayGoneAt < 0) report.overlayGoneAt = now; }
        }
        if (now - lastWrite > .25) { Write(); lastWrite = now; }
    }
}
