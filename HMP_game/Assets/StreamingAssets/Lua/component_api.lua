-- HM Protection component API client.
-- The runtime injects `host` with exactly one callable member:
--   host:Call(method, argsJson) -> resultJson
-- It also injects a JSON module with encode(value) and decode(text). This file never accesses CLR objects.
local component_api = {}
component_api.API_VERSION = 1

local function failure(code, message, data)
    return { apiVersion = component_api.API_VERSION, ok = false, code = code or "client.error", error = message or "Unknown error", data = data }
end

local function decode_result(json, raw)
    if type(raw) ~= "string" then
        return failure("transport.invalid_result", "Host Call did not return a JSON string")
    end
    local ok, result = pcall(json.decode, raw)
    if not ok or type(result) ~= "table" then
        return failure("transport.invalid_json", "Host Call returned invalid JSON")
    end
    if result.apiVersion ~= component_api.API_VERSION then
        return failure("api.version_mismatch", "Unexpected API version", result)
    end
    if result.ok ~= true then
        result.ok = false
        result.code = result.code or "host.error"
        result.error = result.error or "Host rejected the request"
    end
    return result
end

function component_api.new(host, json)
    assert(host ~= nil, "component_api.new requires a host")
    assert(json ~= nil and type(json.encode) == "function" and type(json.decode) == "function",
        "component_api.new requires json.encode/json.decode")

    local client = { host = host, json = json, subscriptions = {}, disposed = false }

    function client:call(method, args)
        if self.disposed then return failure("client.disposed", "The component client has been disposed") end
        if type(method) ~= "string" or method == "" then return failure("client.invalid_method", "Method is required") end
        local encoded_ok, encoded = pcall(self.json.encode, args or {})
        if not encoded_ok then return failure("client.encode_failed", "Arguments could not be encoded") end
        local called, raw = pcall(function() return self.host:Call(method, encoded) end)
        if not called then return failure("transport.call_failed", tostring(raw)) end
        return decode_result(self.json, raw)
    end

    function client:resolve(id) return self:call("entity.resolve", { id = id }) end
    function client:resolve_role(role) return self:call("role.resolve", { role = role }) end
    function client:find(tag) return self:call("entity.find", { tag = tag }) end
    function client:entity_active(entity) return self:call("entity.active.get", { entity = entity }) end
    function client:set_entity_active(entity, active) return self:call("entity.active.set", { entity = entity, active = active }) end
    function client:anchor(entity, slot) return self:call("entity.anchor", { entity = entity, slot = slot or "origin" }) end
    function client:fire(entity) return self:call("fire.get", { entity = entity }) end
    function client:set_fire(entity, state) return self:call("fire.set", { entity = entity, state = state }) end
    function client:freeze_fire(entity, frozen) return self:call("fire.freeze", { entity = entity, frozen = frozen }) end
    function client:set_fire_visual(entity, intensity, scale, smoke)
        return self:call("fire.visual", { entity = entity, intensity = intensity, scale = scale, smoke = smoke })
    end
    function client:interaction_prompt(entity) return self:call("interaction.prompt", { entity = entity }) end
    function client:interact(entity, request_id) return self:call("interaction.invoke", { entity = entity, requestId = request_id }) end
    function client:door(entity) return self:call("door.get", { entity = entity }) end
    function client:set_door(entity, open) return self:call("door.set", { entity = entity, open = open }) end
    function client:pickup(entity) return self:call("pickup.get", { entity = entity }) end
    function client:drop(entity, toss) return self:call("pickup.drop", { entity = entity, toss = toss or false }) end
    function client:seat(entity) return self:call("seat.get", { entity = entity }) end
    function client:leave_seat(entity) return self:call("seat.leave", { entity = entity }) end
    function client:visibility(entity) return self:call("visibility.get", { entity = entity }) end
    function client:set_visibility(entity, visible) return self:call("visibility.set", { entity = entity, visible = visible }) end
    function client:start_timer(seconds, domain, payload)
        return self:call("timer.start", { seconds = seconds, domain = domain or "simulation", payload = payload or {} })
    end
    function client:cancel_timer(timer) return self:call("timer.cancel", { timer = timer }) end
    function client:acquire_controls(masks) return self:call("control.acquire", { masks = masks }) end
    function client:release_controls(lease) return self:call("control.release", { lease = lease }) end
    function client:start_guidance(entity, slot, label)
        return self:call("guidance.start", { entity = entity, slot = slot or "origin", label = label or "" })
    end
    function client:cancel_guidance() return self:call("guidance.cancel", {}) end
    function client:session_info() return self:call("session.info", {}) end

    function client:on(topic, callback)
        if type(topic) ~= "string" or topic == "" or type(callback) ~= "function" then
            return failure("client.invalid_subscription", "Topic and callback are required")
        end
        local result = self:call("events.subscribe", { topic = topic })
        local subscription = result.ok and result.data and result.data.subscription
        if result.ok and type(subscription) == "string" and subscription ~= "" then
            self.subscriptions[subscription] = { topic = topic, callback = callback }
            return result
        end
        return result.ok and failure("events.invalid_subscription", "Host returned no subscription token") or result
    end

    function client:off(subscription)
        if type(subscription) ~= "string" or self.subscriptions[subscription] == nil then
            return failure("events.unknown_subscription", "Subscription is not owned by this client")
        end
        local result = self:call("events.unsubscribe", { subscription = subscription })
        if result.ok then self.subscriptions[subscription] = nil end
        return result
    end

    function client:update()
        if self.disposed then return failure("client.disposed", "The component client has been disposed") end
        if self.updating then return failure("client.reentrant_update", "update may not be called from an event callback") end
        self.updating = true
        local callback_errors, dropped = {}, {}
        local snapshot = {}
        for subscription, entry in pairs(self.subscriptions) do table.insert(snapshot, { subscription = subscription, entry = entry }) end
        for _, item in ipairs(snapshot) do
            local subscription, entry = item.subscription, item.entry
            if self.subscriptions[subscription] == entry and not self.disposed then
            local result = self:call("events.poll", { subscription = subscription, max = 64 })
            if result.ok then
                local events = result.data and result.data.events or {}
                if type(events) ~= "table" then self.updating = false; return failure("events.invalid_payload", "Host returned a non-array events value") end
                if result.data and result.data.dropped and result.data.dropped > 0 then table.insert(dropped, { subscription = subscription, count = result.data.dropped }) end
                for _, event in ipairs(events) do
                    if self.subscriptions[subscription] ~= entry or self.disposed then break end
                    if type(event) == "table" then
                        local invoked, callback_error = pcall(entry.callback, event)
                        if not invoked then table.insert(callback_errors, { subscription = subscription, error = tostring(callback_error) }) end
                    end
                end
            else
                self.updating = false; return result
            end
            end
        end
        self.updating = false
        return { apiVersion = component_api.API_VERSION, ok = true, code = "ok", error = "", data = { callbackErrors = callback_errors, dropped = dropped } }
    end

    function client:emit(topic, payload)
        if type(topic) ~= "string" or string.sub(topic, 1, 7) ~= "script." then
            return failure("events.topic_forbidden", "Scripts may only emit script.* topics")
        end
        return self:call("events.emit", { topic = topic, payload = payload or {} })
    end

    function client:dispose()
        if self.disposed then return { apiVersion = component_api.API_VERSION, ok = true, code = "ok", error = "", data = {} } end
        local subscriptions = {}
        for subscription, _ in pairs(self.subscriptions) do table.insert(subscriptions, subscription) end
        for _, subscription in ipairs(subscriptions) do self:off(subscription) end
        local result = self:call("client.dispose", {})
        self.disposed = true
        self.subscriptions = {}
        return result
    end

    return client
end

return component_api
