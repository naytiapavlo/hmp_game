local component_api = require("component_api")
local level = {}
local function checked(result)
    if not result.ok then error(result.code .. ": " .. result.error) end
    return result.data
end
function level.new(host, json)
    local self = { api = component_api.new(host, json), frames = 0 }
    self.level_id = checked(self.api:session_info()).levelId
    self.fire = checked(self.api:resolve_role("primaryFire")).entity
    checked(self.api:on("fire.state_changed", function(event)
        if event.payload.entity == self.fire then
            checked(self.api:emit("script.level.fire_changed", {
                levelId = self.level_id, state = event.payload.state
            }))
        end
    end))
    checked(self.api:on("timer.elapsed", function(event)
        if event.payload.timer == self.ready_timer then
            checked(self.api:emit("script.level.ready", { levelId = self.level_id, frames = self.frames }))
        end
    end))
    self.ready_timer = checked(self.api:start_timer(0.1, "presentation")).timer
    checked(self.api:emit("script.level.started", { levelId = self.level_id }))
    return self
end
function level.update(self, delta, unscaled)
    self.frames = self.frames + 1
    local result = self.api:update()
    local report = checked(result)
    if #report.callbackErrors > 0 then error(report.callbackErrors[1].error) end
    return result
end
function level.stop(self)
    return self.api:dispose()
end
return level
