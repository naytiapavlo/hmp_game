using NUnit.Framework;

namespace HMProtection.LevelPackages.Tests
{
    public sealed class LevelPackageConfigTests
    {
        const string Valid = "{\"schemaVersion\":1,\"levelId\":\"fire-01\",\"displayName\":\"中文 试点\",\"scene\":\"Scenes/第三场景.unity\",\"script\":{\"enabled\":true,\"entry\":\"Scripts/fire.lua\"},\"artRoots\":[\"Art/Props\",\"Art/火焰\"],\"legacy\":{\"enabled\":false,\"configResource\":\"\"},\"initialFires\":[{\"entityId\":\"fire.a\",\"state\":\"Small\"}]}";

        [Test]
        public void ParsesStrictValidPackageAndResolvesChineseAssetPath()
        {
            Assert.That(LevelPackageConfig.TryParse(Valid, out var config, out var error), Is.True, error);
            Assert.That(config.SchemaVersion, Is.EqualTo(1));
            Assert.That(config.LevelId, Is.EqualTo("fire-01"));
            Assert.That(config.Script.Entry, Is.EqualTo("Scripts/fire.lua"));
            Assert.That(config.Script.ConfigPath, Is.Null);
            Assert.That(config.InitialFires[0].State, Is.EqualTo("Small"));
            Assert.That(LevelPackageConfig.TryResolveAssetPath("Assets/Levels/fire-01", config.Scene, out var path, out error), Is.True, error);
            Assert.That(path, Is.EqualTo("Assets/Levels/fire-01/Scenes/第三场景.unity"));
        }

        [Test]
        public void ParsesOptionalScriptConfigOnlyFromPackageConfigDirectory()
        {
            var withConfig = Valid.Replace("\"entry\":\"Scripts/fire.lua\"", "\"entry\":\"Scripts/fire.lua\",\"config\":\"Config/flow.json\"");
            Assert.That(LevelPackageConfig.TryParse(withConfig, out var config, out var error), Is.True, error);
            Assert.That(config.Script.ConfigPath, Is.EqualTo("Config/flow.json"));

            foreach (var invalid in new[] { "Scripts/flow.json", "Config/../flow.json", "Config/flow.lua", "Config\\flow.json" })
            {
                var json = withConfig.Replace("Config/flow.json", invalid);
                Assert.That(LevelPackageConfig.TryParse(json, out _, out _), Is.False, invalid);
            }
            Assert.That(LevelPackageConfig.TryParse(withConfig.Replace("\"Config/flow.json\"", "false"), out _, out _), Is.False);
        }

        [TestCase("{\"schemaVersion\":1}")]
        [TestCase("{\"schemaVersion\":2,\"levelId\":\"fire-01\",\"displayName\":\"x\",\"scene\":\"Scenes/a.unity\",\"script\":{\"enabled\":false,\"entry\":\"Scripts/a.lua\"},\"artRoots\":[],\"legacy\":{\"enabled\":false,\"configResource\":\"\"},\"initialFires\":[]}")]
        [TestCase("{\"schemaVersion\":1.0,\"levelId\":\"fire-01\",\"displayName\":\"x\",\"scene\":\"Scenes/a.unity\",\"script\":{\"enabled\":false,\"entry\":\"Scripts/a.lua\"},\"artRoots\":[],\"legacy\":{\"enabled\":false,\"configResource\":\"\"},\"initialFires\":[]}")]
        public void RejectsMissingOrWrongVersionAndTypes(string json)
        {
            Assert.That(LevelPackageConfig.TryParse(json, out _, out _), Is.False);
        }

        [TestCase("Scenes/../escape.unity")]
        [TestCase("Scenes\\escape.unity")]
        [TestCase("/Scenes/escape.unity")]
        [TestCase("Scenes/C:escape.unity")]
        [TestCase("Assets/Scenes/escape.unity")]
        [TestCase("Scenes/escape.unity ")]
        [TestCase("Scenes/escape?.unity")]
        [TestCase("Scenes/%2e%2e/escape.unity")]
        public void RejectsEscapingPaths(string scene)
        {
            var json = Valid.Replace("Scenes/第三场景.unity", scene);
            Assert.That(LevelPackageConfig.TryParse(json, out _, out _), Is.False);
            Assert.That(LevelPackageConfig.TryResolveAssetPath("Assets/Levels/fire-01", scene, out _, out _), Is.False);
        }

        [Test]
        public void RejectsUnknownDuplicateFireAndInvalidEnum()
        {
            Assert.That(LevelPackageConfig.TryParse(Valid.Replace("\"initialFires\"", "\"unknown\":true,\"initialFires\""), out _, out _), Is.False);
            var duplicate = Valid.Replace("[{\"entityId\":\"fire.a\",\"state\":\"Small\"}]", "[{\"entityId\":\"fire.a\",\"state\":\"Small\"},{\"entityId\":\"fire.a\",\"state\":\"Large\"}]");
            Assert.That(LevelPackageConfig.TryParse(duplicate, out _, out _), Is.False);
            Assert.That(LevelPackageConfig.TryParse(Valid.Replace("\"Small\"", "1"), out _, out _), Is.False);
            Assert.That(LevelPackageConfig.TryParse(Valid.Replace("\"enabled\":true", "\"enabled\":\"true\""), out _, out _), Is.False);
        }

        [Test]
        public void RejectsDuplicateJsonKeysAndTrailingJson()
        {
            Assert.That(LevelPackageConfig.TryParse(Valid.Replace("\"levelId\":\"fire-01\"", "\"levelId\":\"fire-01\",\"levelId\":\"other\""), out _, out _), Is.False);
            Assert.That(LevelPackageConfig.TryParse(Valid + " {}", out _, out _), Is.False);
        }

        [Test]
        public void RootMustBeExactlyAssetsLevelsAsciiId()
        {
            Assert.That(LevelPackageConfig.TryResolveAssetPath("Assets/Levels/中文", "Scenes/a.unity", out _, out _), Is.False);
            Assert.That(LevelPackageConfig.TryResolveAssetPath("Assets/Else/fire-01", "Scenes/a.unity", out _, out _), Is.False);
        }

        [Test]
        public void AllowsArtRootAndRejectsOverOneMiBJson()
        {
            Assert.That(LevelPackageConfig.TryParse(Valid.Replace("\"Art/Props\",\"Art/火焰\"", "\"Art\""), out var config, out var error), Is.True, error);
            Assert.That(config.ArtRoots, Is.EqualTo(new[] { "Art" }));
            Assert.That(LevelPackageConfig.TryParse(new string(' ', 1024 * 1024 + 1), out _, out _), Is.False);
        }
    }
}
