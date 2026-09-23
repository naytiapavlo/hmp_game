-- Example only. A Lua runtime must explicitly load this file and inject host/json.
local component_api = require("component_api")
local script = {}

function script.new(host, json)
    local self = { api = component_api.new(host, json), subscriptions = {} }
    local role = self.api:resolve_role("primaryFire")
    if not role.ok then self.api:dispose(); return nil, role end
    self.fire = role.data.entity
    local set_result = self.api:set_fire(self.fire, "Small")
    if not set_result.ok then self.api:dispose(); return nil, set_result end

    local changed = self.api:on("fire.state_changed", function(event)
        -- event = { topic, sequence, payload }; opaque entity tokens stay inside API calls.
        if event.payload and event.payload.entity == self.fire then self.last_fire_state = event.payload.state end
    end)
    if changed.ok then table.insert(self.subscriptions, changed.data.subscription) end
    local elapsed = self.api:on("timer.elapsed", function(event)
        self.last_timer = event.payload
    end)
    if elapsed.ok then table.insert(self.subscriptions, elapsed.data.subscription) end

    local timer = self.api:start_timer(8, "simulation", { purpose = "fire-check" })
    if timer.ok then self.timer = timer.data.timer end
    return self
end

function script.update(self)
    return self.api:update()
end

function script.stop(self)
    -- dispose cancels subscriptions and then calls client.dispose on the host.
    return self.api:dispose()
end

return script
