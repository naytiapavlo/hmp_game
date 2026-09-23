using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MoonSharp.Interpreter;
using MoonSharp.Interpreter.Loaders;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HMProtection.LuaRuntime
{
    /// <summary>
    /// Per-level MoonSharp adapter. It exports only string host calls and JSON
    /// tables; it deliberately never registers CLR or Unity userdata.
    /// </summary>
    public sealed class LevelLuaVm : IDisposable
    {
        const int MaxSourceUtf8Bytes = 1024 * 1024;
        // LuaComponentApi accepts requests at 64 KiB / depth 16. Successful
        // events.poll responses can legitimately contain 64 inbox entries and
        // are therefore allowed a larger receive envelope.
        const int MaxJsonEncodeUtf8Bytes = 64 * 1024;
        const int MaxJsonEncodeDepth = 16;
        const int MaxJsonDecodeUtf8Bytes = 2 * 1024 * 1024;
        const int MaxJsonDecodeDepth = 32;
        const long InstructionBudget = 100000;

        Script script;
        Func<string, string, string> call;
        Table nullValue;
        Table arrayMetaTable;
        DynValue module;
        Table instance;
        bool disposed;
        bool stopInvoked;
        bool faulted;

        LevelLuaVm(Script script, Func<string, string, string> call, Table nullValue, Table arrayMetaTable, DynValue module, Table instance)
        {
            this.script = script;
            this.call = call;
            this.nullValue = nullValue;
            this.arrayMetaTable = arrayMetaTable;
            this.module = module;
            this.instance = instance;
        }

        public string LastError { get; private set; }
        public bool IsFaulted => faulted;
        public bool IsDisposed => disposed;

        public static bool TryCreate(string sharedApiSource, string entrySource, string scriptName, Func<string, string, string> call, out LevelLuaVm vm, out string error)
        {
            vm = null;
            error = null;
            if (call == null) { error = "Lua host call delegate is required."; return false; }
            if (!ValidateSource(sharedApiSource, "shared API", out error) || !ValidateSource(entrySource, "entry", out error)) return false;
            if (string.IsNullOrWhiteSpace(scriptName) || scriptName.Length > 128) { error = "Lua script name must contain 1-128 characters."; return false; }

            try
            {
                var moon = new Script(CoreModules.Preset_HardSandbox | CoreModules.ErrorHandling | CoreModules.Metatables);
                // This per-instance loader has no files. It prevents later LoadFile/
                // module resolution without mutating MoonSharp's global defaults.
                moon.Options.ScriptLoader = new UnityAssetsScriptLoader(new Dictionary<string, string>());
                var nullTable = new Table(moon);
                var arrayMeta = new Table(moon);
                DynValue shared = ExecuteTopLevel(moon, sharedApiSource, scriptName + ":component_api", out error);
                if (shared == null) return false;
                if (shared.Type != DataType.Table) { error = "component_api must return a module table."; return false; }

                var host = new Table(moon);
                host.Set("Call", DynValue.NewCallback((context, args) => HostCall(call, args)));
                var json = CreateJsonTable(moon, nullTable, arrayMeta);
                moon.Globals.Set("host", DynValue.NewTable(host));
                moon.Globals.Set("json", DynValue.NewTable(json));
                moon.Globals.Set("require", DynValue.NewCallback((context, args) => RequireComponentApi(shared, args)));

                DynValue entry = ExecuteTopLevel(moon, entrySource, scriptName + ":entry", out error);
                if (entry == null) return false;
                if (entry.Type != DataType.Table) { error = "Lua entry must return a module table."; return false; }
                DynValue constructor = entry.Table.Get("new");
                if (constructor.Type != DataType.Function) { error = "Lua entry module must define new(host, json)."; return false; }
                DynValue created = InvokeOnce(moon, constructor, scriptName + ":new", out error, DynValue.NewTable(host), DynValue.NewTable(json));
                if (created == null) return false;
                created = created.ToScalar();
                if (created.Type != DataType.Table) { error = "Lua new(host, json) must return an instance table."; return false; }
                DynValue update = entry.Table.Get("update");
                if (update.Type != DataType.Function) { error = "Lua entry module must define update(self, delta, unscaled)."; return false; }
                DynValue stop = entry.Table.Get("stop");
                if (stop.Type != DataType.Function) { error = "Lua entry module must define stop(self)."; return false; }
                vm = new LevelLuaVm(moon, call, nullTable, arrayMeta, entry, created.Table);
                return true;
            }
            catch (Exception exception)
            {
                error = "Lua VM creation failed: " + Describe(exception);
                return false;
            }
        }

        public bool Tick(float delta, float unscaled, out string error)
        {
            error = null;
            if (disposed) { error = "Lua VM is disposed."; return false; }
            if (faulted) { error = LastError ?? "Lua VM is faulted."; return false; }
            if (!IsFiniteNonNegative(delta) || !IsFiniteNonNegative(unscaled)) return Fail("Lua tick deltas must be finite and non-negative.", out error);
            try
            {
                DynValue update = module.Table.Get("update");
                if (update.Type != DataType.Function) return Fail("Lua entry update function is unavailable.", out error);
                DynValue result = InvokeOnce(script, update, "update", out var invocationError, DynValue.NewTable(instance), DynValue.NewNumber(delta), DynValue.NewNumber(unscaled));
                if (result == null) return Fail(invocationError, out error);
                return true;
            }
            catch (Exception exception) { return Fail("Lua update failed: " + Describe(exception), out error); }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try
            {
                if (!stopInvoked)
                {
                    stopInvoked = true;
                    DynValue stop = module.Table.Get("stop");
                    if (stop.Type == DataType.Function && InvokeOnce(script, stop, "stop", out var error, DynValue.NewTable(instance)) == null)
                    {
                        faulted = true;
                        if (string.IsNullOrEmpty(LastError)) LastError = error;
                    }
                }
            }
            catch (Exception exception)
            {
                faulted = true;
                if (string.IsNullOrEmpty(LastError)) LastError = "Lua stop failed: " + Describe(exception);
            }
            finally
            {
                if (script != null)
                {
                    script.Globals.Set("host", DynValue.Nil);
                    script.Globals.Set("json", DynValue.Nil);
                    script.Globals.Set("require", DynValue.Nil);
                }
                call = null;
                instance = null;
                module = DynValue.Nil;
                nullValue = null;
                arrayMetaTable = null;
                script = null;
            }
        }

        static bool ValidateSource(string source, string name, out string error)
        {
            if (string.IsNullOrEmpty(source)) { error = name + " source is required."; return false; }
            if (Encoding.UTF8.GetByteCount(source) > MaxSourceUtf8Bytes) { error = name + " source exceeds the 1 MiB UTF-8 limit."; return false; }
            error = null;
            return true;
        }

        static DynValue ExecuteTopLevel(Script script, string source, string sourceName, out string error)
        {
            try
            {
                DynValue chunk = script.LoadString(source, null, sourceName);
                return InvokeOnce(script, chunk, sourceName, out error);
            }
            catch (Exception exception) { error = sourceName + " compile failed: " + Describe(exception); return null; }
        }

        static DynValue InvokeOnce(Script script, DynValue function, string operation, out string error, params DynValue[] args)
        {
            error = null;
            try
            {
                DynValue coroutine = script.CreateCoroutine(function);
                coroutine.Coroutine.AutoYieldCounter = InstructionBudget;
                DynValue result = coroutine.Coroutine.Resume(args);
                if (coroutine.Coroutine.State != CoroutineState.Dead)
                {
                    error = operation + " yielded or exceeded the " + InstructionBudget + " instruction budget; the VM will not resume it.";
                    return null;
                }
                return result;
            }
            catch (Exception exception) { error = operation + " failed: " + Describe(exception); return null; }
        }

        static DynValue HostCall(Func<string, string, string> call, CallbackArguments args)
        {
            // Lua invokes host:Call(method, argsJson), so args[0] is the host table.
            if (args.Count != 3 || args[1].Type != DataType.String || args[2].Type != DataType.String)
                throw new ScriptRuntimeException("host:Call requires method and argsJson strings.");
            string result;
            try { result = call(args[1].String, args[2].String); }
            catch (Exception exception) { throw new ScriptRuntimeException("host:Call failed: " + exception.Message); }
            if (result == null) throw new ScriptRuntimeException("host:Call returned null.");
            return DynValue.NewString(result);
        }

        static DynValue RequireComponentApi(DynValue componentApi, CallbackArguments args)
        {
            if (args.Count != 1 || args[0].Type != DataType.String || args[0].String != "component_api")
                throw new ScriptRuntimeException("Only preloaded module 'component_api' is available.");
            return componentApi;
        }

        static Table CreateJsonTable(Script script, Table nullValue, Table arrayMeta)
        {
            var json = new Table(script);
            json.Set("null", DynValue.NewTable(nullValue));
            json.Set("encode", DynValue.NewCallback((context, args) =>
            {
                if (args.Count != 1) throw new ScriptRuntimeException("json.encode requires one value.");
                JToken token = ToJson(args[0], nullValue, arrayMeta, new HashSet<Table>(), 0);
                string result = token.ToString(Formatting.None);
                if (Encoding.UTF8.GetByteCount(result) > MaxJsonEncodeUtf8Bytes) throw new ScriptRuntimeException("json.encode output exceeds the 64 KiB UTF-8 limit.");
                return DynValue.NewString(result);
            }));
            json.Set("decode", DynValue.NewCallback((context, args) =>
            {
                if (args.Count != 1 || args[0].Type != DataType.String) throw new ScriptRuntimeException("json.decode requires one JSON string.");
                if (Encoding.UTF8.GetByteCount(args[0].String) > MaxJsonDecodeUtf8Bytes) throw new ScriptRuntimeException("json.decode input exceeds the 2 MiB UTF-8 limit.");
                try
                {
                    using (var reader = new JsonTextReader(new StringReader(args[0].String)) { DateParseHandling = DateParseHandling.None, MaxDepth = MaxJsonDecodeDepth })
                    {
                        JToken token = JToken.ReadFrom(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                        if (reader.Read()) throw new ScriptRuntimeException("json.decode accepts exactly one JSON value.");
                        return FromJson(script, token, nullValue, arrayMeta, 0);
                    }
                }
                catch (JsonException exception) { throw new ScriptRuntimeException("json.decode failed: " + exception.Message); }
            }));
            json.Set("array", DynValue.NewCallback((context, args) =>
            {
                if (args.Count == 0)
                {
                    var value = new Table(script) { MetaTable = arrayMeta };
                    return DynValue.NewTable(value);
                }
                if (args.Count == 1 && args[0].Type == DataType.Table)
                {
                    args[0].Table.MetaTable = arrayMeta;
                    return args[0];
                }
                throw new ScriptRuntimeException("json.array accepts zero arguments or one table.");
            }));
            return json;
        }

        static JToken ToJson(DynValue value, Table nullValue, Table arrayMeta, HashSet<Table> stack, int depth)
        {
            if (depth > MaxJsonEncodeDepth) throw new ScriptRuntimeException("json.encode exceeds the maximum depth.");
            if (value.IsNil() || (value.Type == DataType.Table && ReferenceEquals(value.Table, nullValue))) return JValue.CreateNull();
            switch (value.Type)
            {
                case DataType.Boolean: return new JValue(value.Boolean);
                case DataType.Number:
                    if (double.IsNaN(value.Number) || double.IsInfinity(value.Number)) throw new ScriptRuntimeException("json.encode rejects non-finite numbers.");
                    if (value.Number == Math.Floor(value.Number) && Math.Abs(value.Number) <= 9007199254740991d) return new JValue((long)value.Number);
                    return new JValue(value.Number);
                case DataType.String: return new JValue(value.String);
                case DataType.Table: return TableToJson(value.Table, nullValue, arrayMeta, stack, depth + 1);
                default: throw new ScriptRuntimeException("json.encode rejects Lua " + value.Type + " values.");
            }
        }

        static JToken TableToJson(Table table, Table nullValue, Table arrayMeta, HashSet<Table> stack, int depth)
        {
            if (!stack.Add(table)) throw new ScriptRuntimeException("json.encode rejects cyclic tables.");
            try
            {
                var pairs = new List<TablePair>();
                foreach (var pair in table.Pairs) pairs.Add(pair);
                bool markedArray = ReferenceEquals(table.MetaTable, arrayMeta);
                bool numeric = true;
                int maxIndex = 0;
                var seen = new HashSet<int>();
                foreach (var pair in pairs)
                {
                    if (pair.Key.Type != DataType.Number || pair.Key.Number < 1 || pair.Key.Number != Math.Floor(pair.Key.Number) || pair.Key.Number > int.MaxValue) { numeric = false; break; }
                    int index = (int)pair.Key.Number;
                    seen.Add(index); if (index > maxIndex) maxIndex = index;
                }
                bool contiguous = numeric && seen.Count == maxIndex;
                if (markedArray || (pairs.Count > 0 && contiguous))
                {
                    if (!contiguous) throw new ScriptRuntimeException("json.encode array tables require contiguous positive integer keys.");
                    var array = new JArray();
                    for (int index = 1; index <= maxIndex; index++) array.Add(ToJson(table.Get(index), nullValue, arrayMeta, stack, depth));
                    return array;
                }
                var obj = new JObject();
                foreach (var pair in pairs)
                {
                    if (pair.Key.Type != DataType.String) throw new ScriptRuntimeException("json.encode object keys must be strings.");
                    obj.Add(pair.Key.String, ToJson(pair.Value, nullValue, arrayMeta, stack, depth));
                }
                return obj;
            }
            finally { stack.Remove(table); }
        }

        static DynValue FromJson(Script script, JToken token, Table nullValue, Table arrayMeta, int depth)
        {
            if (depth > MaxJsonDecodeDepth) throw new ScriptRuntimeException("json.decode exceeds the maximum depth.");
            switch (token.Type)
            {
                case JTokenType.Null: return DynValue.NewTable(nullValue);
                case JTokenType.Boolean: return DynValue.NewBoolean((bool)token);
                case JTokenType.Integer: return DynValue.NewNumber((double)(long)token);
                case JTokenType.Float:
                    double number = (double)token;
                    if (double.IsNaN(number) || double.IsInfinity(number)) throw new ScriptRuntimeException("json.decode rejects non-finite numbers.");
                    return DynValue.NewNumber(number);
                case JTokenType.String: return DynValue.NewString((string)token);
                case JTokenType.Array:
                    var array = new Table(script) { MetaTable = arrayMeta };
                    int index = 1;
                    foreach (var child in (JArray)token) array.Set(index++, FromJson(script, child, nullValue, arrayMeta, depth + 1));
                    return DynValue.NewTable(array);
                case JTokenType.Object:
                    var obj = new Table(script);
                    foreach (var property in ((JObject)token).Properties()) obj.Set(property.Name, FromJson(script, property.Value, nullValue, arrayMeta, depth + 1));
                    return DynValue.NewTable(obj);
                default: throw new ScriptRuntimeException("json.decode rejects unsupported JSON token " + token.Type + ".");
            }
        }

        bool Fail(string message, out string error)
        {
            faulted = true;
            LastError = message;
            error = message;
            return false;
        }
        static string Describe(Exception exception)
        {
            var interpreter = exception as InterpreterException;
            if (interpreter != null && !string.IsNullOrEmpty(interpreter.DecoratedMessage)) return interpreter.DecoratedMessage;
            return exception.Message;
        }
        static bool IsFiniteNonNegative(float value) => !float.IsNaN(value) && !float.IsInfinity(value) && value >= 0f;
    }
}
