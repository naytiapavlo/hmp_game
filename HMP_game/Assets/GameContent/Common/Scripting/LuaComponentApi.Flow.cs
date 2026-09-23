using HMProtection.Core;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace HMProtection.Scripting
{
    public sealed partial class LuaComponentApi
    {
        LevelFlowRunner scriptFlow;

        LevelBootstrapper FlowBootstrapper()
        {
            var boot = owner.Bindings != null ? owner.Bindings.Bootstrapper : null;
            Require(boot != null && boot.definition != null, "missing_service", "A packaged level bootstrapper is required.");
            return boot;
        }
        JObject ReadLevelScriptConfig()
        {
            var boot = FlowBootstrapper();
            Require(boot.definition.scriptConfig != null, "missing_config", "This level has no script.config asset.");
            return ReadObject(boot.definition.scriptConfig.text);
        }
        JObject PrepareFlow()
        {
            var boot = FlowBootstrapper();
            Require(!boot.definition.runLegacyStages && boot.definition.scriptEnabled, "operation_rejected", "Script stages require Lua enabled and the legacy driver disabled.");
            Require(owner.FlowOwner == null, "flow_owned", "A script client already owns this flow.");
            var configJson = ReadLevelScriptConfig();
            var config = JsonUtility.FromJson<LevelConfigDto>(configJson.ToString());
            Require(config != null && config.id == Scope.LevelId && config.stages != null && config.stages.Length > 0,
                "invalid_config", "Script flow needs a matching level ID and a non-empty stage sequence.");
            ConfigLoader.Normalize(config);
            var runner = boot.GetComponent<LevelFlowRunner>();
            Require(runner != null, "missing_service", "This scene does not provide stage presentation services.");
            Check(runner.TryPrepareScript(boot.Entry, config, out var error), error);
            scriptFlow = runner; owner.FlowOwner = this;
            return FlowStatus();
        }
        void RequireFlow()
        {
            Require(owner.FlowOwner == this && scriptFlow != null, "flow_not_owned", "Call flow.prepare before controlling this client's flow.");
        }
        JObject RunFlowStage(JObject args)
        {
            RequireFlow();
            int index = Integer(args, "index", 0, scriptFlow.Config.stages.Length - 1);
            Check(scriptFlow.TryRunScriptStage(index, out var error), error);
            return FlowStatus();
        }
        JObject FlowStatus()
        {
            RequireFlow();
            return new JObject { ["running"] = scriptFlow.IsRunning, ["busy"] = scriptFlow.ScriptStageBusy,
                ["stageIndex"] = scriptFlow.StageIndex, ["completedStage"] = scriptFlow.ScriptCompletedStage,
                ["stageId"] = scriptFlow.CurrentStageId, ["error"] = scriptFlow.ScriptStageError ?? "",
                ["asked"] = scriptFlow.Score.AskedCount, ["correct"] = scriptFlow.Score.CorrectCount,
                ["passed"] = scriptFlow.Score.Passed, ["paused"] = scriptFlow.IsPaused };
        }
        JObject FinishFlow()
        {
            RequireFlow();
            Require(!scriptFlow.ScriptStageBusy && scriptFlow.ScriptCompletedStage == scriptFlow.Config.stages.Length - 1,
                "operation_rejected", "Complete all stages before finishing the flow.");
            scriptFlow.CompleteScriptFlow();
            return FlowStatus();
        }
        void ReleaseFlow()
        {
            if (owner.FlowOwner != this) return;
            owner.FlowOwner = null;
            if (scriptFlow != null) scriptFlow.CancelScriptFlow();
            scriptFlow = null;
        }
    }
}
