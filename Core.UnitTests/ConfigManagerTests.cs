using System.Reflection;
using NUnit.Framework;
using Switcheroo;

namespace Switcheroo.Core.UnitTests
{
    [TestFixture]
    public class ConfigManagerTests
    {
        [Test]
        public void FromJson_OpenUsageHintsByDefaultPresent_LoadsValue()
        {
            var config = FromJson("{\"OpenUsageHintsByDefault\": true, \"AzShortcutsEnabled\": false}");

            Assert.That(config.OpenUsageHintsByDefault, Is.True);
            Assert.That(config.AzShortcutsEnabled, Is.False);
        }

        [Test]
        public void ToJson_OpenUsageHintsByDefaultEnabled_WritesProperty()
        {
            var config = new SwitcherooConfig
            {
                OpenUsageHintsByDefault = true,
                AzShortcutsEnabled = false
            };

            var json = ToJson(config);

            StringAssert.Contains("\"OpenUsageHintsByDefault\": true", json);
            StringAssert.Contains("\"AzShortcutsEnabled\": false", json);
        }
        [Test]
        public void FromJson_EmptyObject_UsesSavedUserDefaults()
        {
            var c = FromJson("{}");
            Assert.That(c.EnableHotKey, Is.False);
            Assert.That(c.AltTabHook, Is.True);
            Assert.That(c.PreviousAppEnabled && c.PreviousAppCtrl && c.PreviousAppAlt, Is.True);
            Assert.That(c.PreviousAppHotKey, Is.EqualTo(221));
            Assert.That(c.MultiColumnEnabled, Is.True);
            Assert.That(c.WindowWidth, Is.EqualTo(1440));
            Assert.That(c.WindowHeight, Is.EqualTo(698));
            Assert.That(c.SwitchHighlightEnabled, Is.True);
            Assert.That(c.RecentWindowsOnTopEnabled, Is.True);
            Assert.That(c.RecentWindowsOnTopCount, Is.EqualTo(5));
            Assert.That(c.DisableImeOnQuickJumpEnabled, Is.True);
            Assert.That(c.RunAsAdmin || c.RunOnStartup || c.AutoSwitch, Is.False);
        }

        [Test]
        public void FromJson_ExplicitOverrides_PreservesValuesAndDefaultsMissingOnes()
        {
            var c = FromJson("{\"EnableHotKey\":true,\"AltTabHook\":false,\"WindowWidth\":900,\"PreviousAppHotKey\":9}");
            Assert.That(c.EnableHotKey, Is.True);
            Assert.That(c.AltTabHook, Is.False);
            Assert.That(c.WindowWidth, Is.EqualTo(900));
            Assert.That(c.PreviousAppHotKey, Is.EqualTo(9));
            Assert.That(c.WindowHeight, Is.EqualTo(698));
        }

        [Test]
        public void FromLegacyValues_OnlyActualOverrides_MergesWithNewDefaults()
        {
            var values = new System.Collections.Generic.Dictionary<string, object> { { "Alt", false }, { "HotKey", 65 } };
            var method = typeof(ConfigManager).GetMethod("FromLegacyValues", BindingFlags.NonPublic | BindingFlags.Static);
            var config = (SwitcherooConfig)method.Invoke(null, new object[] { values });
            Assert.That(config.ActivateAlt, Is.False);
            Assert.That(config.ActivateHotKey, Is.EqualTo(65));
            Assert.That(config.AltTabHook, Is.True);
            Assert.That(config.EnableHotKey, Is.False);
            Assert.That(config.PreviousAppCtrl, Is.True);
        }

        private static SwitcherooConfig FromJson(string json)
        {
            var method = typeof(ConfigManager).GetMethod("FromJson", BindingFlags.NonPublic | BindingFlags.Static);
            return (SwitcherooConfig)method.Invoke(null, new object[] { json });
        }

        private static string ToJson(SwitcherooConfig config)
        {
            var method = typeof(ConfigManager).GetMethod("ToJson", BindingFlags.NonPublic | BindingFlags.Static);
            return (string)method.Invoke(null, new object[] { config });
        }

        [Test]
        public void FromJson_MultiColumnEnabledAndSizePresent_LoadsValues()
        {
            var config = FromJson("{\"MultiColumnEnabled\": true, \"WindowWidth\": 800, \"WindowHeight\": 600}");

            Assert.That(config.MultiColumnEnabled, Is.True);
            Assert.That(config.WindowWidth, Is.EqualTo(800));
            Assert.That(config.WindowHeight, Is.EqualTo(600));
        }

        [Test]
        public void ToJson_MultiColumnEnabledAndSizeSet_WritesProperties()
        {
            var config = new SwitcherooConfig
            {
                MultiColumnEnabled = true,
                WindowWidth = 800,
                WindowHeight = 600
            };

            var json = ToJson(config);

            StringAssert.Contains("\"MultiColumnEnabled\": true", json);
            StringAssert.Contains("\"WindowWidth\": 800", json);
            StringAssert.Contains("\"WindowHeight\": 600", json);
        }

    }
}
