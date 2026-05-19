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
    }
}
