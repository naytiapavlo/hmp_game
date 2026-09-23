using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HMProtection.LevelPackages
{
    /// <summary>Pure, strict JSON model for a package rooted at Assets/Levels/&lt;levelId&gt;.</summary>
    public sealed class LevelPackageConfig
    {
        public const int CurrentSchemaVersion = 1;

        public int SchemaVersion { get; private set; }
        public string LevelId { get; private set; }
        public string DisplayName { get; private set; }
        public string Scene { get; private set; }
        public LevelPackageScript Script { get; private set; }
        public string[] ArtRoots { get; private set; }
        public LevelPackageLegacy Legacy { get; private set; }
        public LevelPackageInitialFire[] InitialFires { get; private set; }

        private LevelPackageConfig() { }

        public static bool TryParse(string json, out LevelPackageConfig config, out string error)
        {
            config = null;
            if (!TryReadRoot(json, out var root, out error)) return false;
            if (!RequireProperties(root, "root", out error, "schemaVersion", "levelId", "displayName", "scene", "script", "artRoots", "legacy", "initialFires")) return false;

            if (!TryInteger(root, "schemaVersion", out var version, out error)) return false;
            if (version != CurrentSchemaVersion) { error = "schemaVersion must be exactly 1."; return false; }
            if (!TryAsciiLevelId(root, "levelId", out var levelId, out error)
                || !TryRequiredString(root, "displayName", out var displayName, out error)
                || !TryRelativePath(root, "scene", "Scenes/", ".unity", out var scene, out error)
                || !TryScript(root, out var script, out error)
                || !TryArtRoots(root, out var artRoots, out error)
                || !TryLegacy(root, out var legacy, out error)
                || !TryInitialFires(root, out var fires, out error)) return false;

            config = new LevelPackageConfig
            {
                SchemaVersion = version,
                LevelId = levelId,
                DisplayName = displayName,
                Scene = scene,
                Script = script,
                ArtRoots = artRoots,
                Legacy = legacy,
                InitialFires = fires
            };
            error = null;
            return true;
        }

        /// <summary>Combines a checked package root and relative asset path without touching IO or AssetDatabase.</summary>
        public static bool TryResolveAssetPath(string root, string relative, out string path, out string error)
        {
            path = null;
            if (!TryPackageRoot(root, out var normalizedRoot, out error) || !TrySafeRelativePath(relative, out var normalizedRelative, out error)) return false;
            path = normalizedRoot + "/" + normalizedRelative;
            error = null;
            return true;
        }

        static bool TryReadRoot(string json, out JObject root, out string error)
        {
            root = null;
            if (json == null) { error = "JSON is required."; return false; }
            if (System.Text.Encoding.UTF8.GetByteCount(json) > 1024 * 1024) { error = "Package JSON exceeds the 1 MiB UTF-8 limit."; return false; }
            try
            {
                using (var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None, MaxDepth = 32 })
                {
                    var token = JToken.ReadFrom(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                    if (!(token is JObject objectRoot) || reader.Read()) { error = "Package JSON must contain exactly one object."; return false; }
                    root = objectRoot;
                    error = null;
                    return true;
                }
            }
            catch (JsonException exception) { error = "Invalid JSON: " + exception.Message; return false; }
        }

        static bool TryScript(JObject root, out LevelPackageScript script, out string error)
        {
            script = null;
            if (!TryObject(root, "script", out var value, out error)
                || !RequirePropertiesWithOptional(value, "script", out error, new[] { "enabled", "entry" }, new[] { "config" })
                || !TryBoolean(value, "enabled", out var enabled, out error)
                || !TryRelativePath(value, "entry", "Scripts/", ".lua", out var entry, out error)) return false;
            string configPath = null;
            if (value["config"] != null && !TryRelativePath(value, "config", "Config/", ".json", out configPath, out error)) return false;
            script = new LevelPackageScript(enabled, entry, configPath);
            return true;
        }

        static bool TryLegacy(JObject root, out LevelPackageLegacy legacy, out string error)
        {
            legacy = null;
            if (!TryObject(root, "legacy", out var value, out error) || !RequireProperties(value, "legacy", out error, "enabled", "configResource")
                || !TryBoolean(value, "enabled", out var enabled, out error) || !TryString(value, "configResource", out var resource, out error)) return false;
            if (enabled && string.IsNullOrWhiteSpace(resource)) { error = "legacy.configResource is required when legacy.enabled is true."; return false; }
            if (!string.IsNullOrEmpty(resource) && !TrySafeRelativePath(resource, out _, out error)) { error = "legacy.configResource: " + error; return false; }
            legacy = new LevelPackageLegacy(enabled, resource);
            return true;
        }

        static bool TryArtRoots(JObject root, out string[] artRoots, out string error)
        {
            artRoots = null;
            if (!(root["artRoots"] is JArray values)) { error = "artRoots must be an array."; return false; }
            var unique = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<string>();
            foreach (var token in values)
            {
                if (token.Type != JTokenType.String) { error = "artRoots entries must be strings."; return false; }
                var value = (string)token;
                if (!TrySafeRelativePath(value, out var relative, out error)) { error = "artRoots: " + error; return false; }
                if (relative != "Art" && !relative.StartsWith("Art/", StringComparison.Ordinal)) { error = "artRoots entries must be Art or a directory under Art/."; return false; }
                if (!unique.Add(relative)) { error = "artRoots contains a duplicate path."; return false; }
                result.Add(relative);
            }
            artRoots = result.ToArray();
            error = null;
            return true;
        }

        static bool TryInitialFires(JObject root, out LevelPackageInitialFire[] fires, out string error)
        {
            fires = null;
            if (!(root["initialFires"] is JArray values)) { error = "initialFires must be an array."; return false; }
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<LevelPackageInitialFire>();
            foreach (var token in values)
            {
                if (!(token is JObject item)) { error = "initialFires entries must be objects."; return false; }
                if (!RequireProperties(item, "initialFires entry", out error, "entityId", "state")) return false;
                if (!TryRequiredString(item, "entityId", out var id, out error) || !TryRequiredString(item, "state", out var state, out error)) return false;
                if (!IsFireState(state)) { error = "initialFires.state must be None, SmokeOnly, Small, Medium or Large."; return false; }
                if (!ids.Add(id)) { error = "initialFires contains a duplicate entityId."; return false; }
                result.Add(new LevelPackageInitialFire(id, state));
            }
            fires = result.ToArray();
            error = null;
            return true;
        }

        static bool TryRelativePath(JObject value, string key, string requiredPrefix, string requiredSuffix, out string relative, out string error)
        {
            relative = null;
            if (!TryRequiredString(value, key, out var candidate, out error)) return false;
            if (!TrySafeRelativePath(candidate, out relative, out error)) { error = key + ": " + error; return false; }
            if (!relative.StartsWith(requiredPrefix, StringComparison.Ordinal) || !relative.EndsWith(requiredSuffix, StringComparison.Ordinal))
            { error = key + " must be a " + requiredPrefix + "*" + requiredSuffix + " relative path."; return false; }
            return true;
        }

        static bool TryPackageRoot(string root, out string normalized, out string error)
        {
            normalized = null;
            if (string.IsNullOrEmpty(root) || root.IndexOf('\\') >= 0 || root.IndexOf(':') >= 0) { error = "Package root must use Assets/Levels/<levelId>."; return false; }
            var parts = root.Split('/');
            if (parts.Length != 3 || parts[0] != "Assets" || parts[1] != "Levels" || !IsAsciiLevelId(parts[2])) { error = "Package root must use Assets/Levels/<levelId>."; return false; }
            normalized = root;
            error = null;
            return true;
        }

        static bool TrySafeRelativePath(string value, out string relative, out string error)
        {
            relative = null;
            if (string.IsNullOrWhiteSpace(value)) { error = "Relative path is required."; return false; }
            if (value.IndexOf('\\') >= 0 || value.IndexOf(':') >= 0 || value[0] == '/') { error = "Relative path cannot be absolute or use backslashes/colons."; return false; }
            var parts = value.Split('/');
            foreach (var part in parts)
                if (part.Length == 0 || part == "." || part == ".." || part.EndsWith(" ", StringComparison.Ordinal) || part.EndsWith(".", StringComparison.Ordinal)
                    || ContainsControl(part) || ContainsReservedPathCharacter(part)) { error = "Relative path contains an invalid segment."; return false; }
            if (parts[0] == "Assets") { error = "Relative path must not include the Assets root."; return false; }
            relative = value;
            error = null;
            return true;
        }

        static bool RequireProperties(JObject value, string name, out string error, params string[] required)
        {
            var allowed = new HashSet<string>(required, StringComparer.Ordinal);
            foreach (var property in value.Properties())
                if (!allowed.Contains(property.Name)) { error = name + " contains unknown field '" + property.Name + "'."; return false; }
            foreach (var property in required)
                if (value[property] == null) { error = name + " is missing required field '" + property + "'."; return false; }
            error = null;
            return true;
        }

        static bool RequirePropertiesWithOptional(JObject value, string name, out string error, string[] required, string[] optional)
        {
            var allowed = new HashSet<string>(required, StringComparer.Ordinal);
            foreach (var field in optional) allowed.Add(field);
            foreach (var property in value.Properties())
                if (!allowed.Contains(property.Name)) { error = name + " contains unknown field '" + property.Name + "'."; return false; }
            foreach (var property in required)
                if (value[property] == null) { error = name + " is missing required field '" + property + "'."; return false; }
            error = null;
            return true;
        }

        static bool TryObject(JObject root, string key, out JObject value, out string error)
        {
            value = root[key] as JObject;
            if (value == null) { error = key + " must be an object."; return false; }
            error = null;
            return true;
        }
        static bool TryBoolean(JObject value, string key, out bool result, out string error)
        {
            result = false;
            if (value[key]?.Type != JTokenType.Boolean) { error = key + " must be a boolean."; return false; }
            result = (bool)value[key]; error = null; return true;
        }
        static bool TryInteger(JObject value, string key, out int result, out string error)
        {
            result = 0;
            if (value[key]?.Type != JTokenType.Integer) { error = key + " must be an integer."; return false; }
            try { result = checked((int)(long)value[key]); }
            catch (Exception) { error = key + " is outside the Int32 range."; return false; }
            error = null; return true;
        }
        static bool TryString(JObject value, string key, out string result, out string error)
        {
            result = null;
            if (value[key]?.Type != JTokenType.String) { error = key + " must be a string."; return false; }
            result = (string)value[key]; error = null; return true;
        }
        static bool TryRequiredString(JObject value, string key, out string result, out string error)
        {
            if (!TryString(value, key, out result, out error)) return false;
            if (string.IsNullOrWhiteSpace(result)) { error = key + " must not be empty."; return false; }
            return true;
        }
        static bool TryAsciiLevelId(JObject value, string key, out string result, out string error)
        {
            if (!TryRequiredString(value, key, out result, out error)) return false;
            if (!IsAsciiLevelId(result)) { error = key + " must match [a-z0-9_-]+."; return false; }
            return true;
        }
        static bool IsAsciiLevelId(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            foreach (var character in value)
                if (!((character >= 'a' && character <= 'z') || (character >= '0' && character <= '9') || character == '_' || character == '-')) return false;
            return true;
        }
        static bool ContainsControl(string value)
        {
            foreach (var character in value) if (char.IsControl(character)) return true;
            return false;
        }
        static bool ContainsReservedPathCharacter(string value)
        {
            foreach (var character in value)
                if (character == '<' || character == '>' || character == '|' || character == '?' || character == '*' || character == '"' || character == '%' || character == '#') return true;
            return false;
        }
        static bool IsFireState(string value) => value == "None" || value == "SmokeOnly" || value == "Small" || value == "Medium" || value == "Large";
    }

    public sealed class LevelPackageScript
    {
        internal LevelPackageScript(bool enabled, string entry, string configPath) { Enabled = enabled; Entry = entry; ConfigPath = configPath; }
        public bool Enabled { get; private set; }
        public string Entry { get; private set; }
        /// <summary>Optional JSON configuration asset under the package Config directory.</summary>
        public string ConfigPath { get; private set; }
    }
    public sealed class LevelPackageLegacy
    {
        internal LevelPackageLegacy(bool enabled, string configResource) { Enabled = enabled; ConfigResource = configResource; }
        public bool Enabled { get; private set; }
        public string ConfigResource { get; private set; }
    }
    public sealed class LevelPackageInitialFire
    {
        internal LevelPackageInitialFire(string entityId, string state) { EntityId = entityId; State = state; }
        public string EntityId { get; private set; }
        public string State { get; private set; }
    }
}
