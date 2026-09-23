-- This package owns the sequence; C# supplies reusable stage presentation.
local component_api = require("component_api")
local level = {}
local function checked(result)
    if not result.ok then error(result.code .. ": " .. result.error) end
    return result.data
end
local function enter(self)
    local stage = self.config.stages[self.index]
    checked(self.api:call("flow.stage", { index = self.index - 1 }))
    checked(self.api:emit("script.level.stage_entered", { stageId = stage.id, index = self.index }))
end
function level.new(host, json)
    local self = { api = component_api.new(host, json), index = 0, finished = false }
    self.config = checked(self.api:call("level.config"))
    assert(type(self.config.stages) == "table" and #self.config.stages > 0, "No lesson stages")
    checked(self.api:call("flow.prepare"))
    checked(self.api:emit("script.level.started", { levelId = self.config.id }))
    return self
end
function level.update(self, delta, unscaled)
    local events = checked(self.api:update())
    if #events.callbackErrors > 0 then error(events.callbackErrors[1].error) end
    if self.finished then return end
    local state = checked(self.api:call("flow.status"))
    if state.error ~= "" then error(state.error) end
    if state.busy or state.paused then return end
    assert(state.completedStage == self.index - 1, "Unexpected stage completion")
    if self.index > 0 then checked(self.api:emit("script.level.stage_completed", {
        stageId = self.config.stages[self.index].id, index = self.index,
        asked = state.asked, correct = state.correct
    })) end
    self.index = self.index + 1
    if self.index > #self.config.stages then
        state = checked(self.api:call("flow.finish"))
        self.finished = true
        checked(self.api:emit("script.level.finished", {
            levelId = self.config.id, asked = state.asked, correct = state.correct, passed = state.passed
        }))
    else
        enter(self)
    end
end
function level.stop(self)
    self.api:dispose()
end
return level
