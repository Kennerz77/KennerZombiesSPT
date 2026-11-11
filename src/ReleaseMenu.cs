using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace KennerZombies
{
    /// <summary>
    /// Release builds (built with -p:ReleaseBuild=true, the RELEASE flag): F12 shows players only the settings meant
    /// for them (volumes and the normal options); every debug and tuning entry is hidden from the menu. Hidden entries are
    /// put back to their defaults once per start (a leftover debug value can't apply unseen). Dev builds show all.
    /// Run again a few times after start-up, for settings bound late.
    /// </summary>
    internal static class ReleaseMenu
    {
        /// <summary>Hides every entry of this plugin's config that keep() says no to (release builds only).</summary>
        public static void Install(BaseUnityPlugin plugin, Func<ConfigDefinition, bool> keep)
        {
#if RELEASE
            Apply(plugin.Config, keep);
            plugin.StartCoroutine(Again(plugin.Config, keep));
#endif
        }

        private static IEnumerator Again(ConfigFile config, Func<ConfigDefinition, bool> keep)
        {
            foreach (float wait in new[] { 1f, 5f, 15f, 60f })
            {
                yield return new WaitForSecondsRealtime(wait);
                Apply(config, keep);
            }
        }

        private static readonly FieldInfo DescriptionField = typeof(ConfigEntryBase).GetField("<Description>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly System.Collections.Generic.HashSet<ConfigEntryBase> Reset = new System.Collections.Generic.HashSet<ConfigEntryBase>();

        private static void Apply(ConfigFile config, Func<ConfigDefinition, bool> keep)
        {
            foreach (ConfigDefinition def in config.Keys.ToList())
            {
                try
                {
                    if (keep(def))
                        continue;
                    ConfigEntryBase e = config[def];
                    // A hidden option is back to its default, once per start: a debug switch left on in an older
                    // version (or a copied dev cfg) can't stay on unseen (2026-11-11: "No slaps (god mode)" was)
                    if (Reset.Add(e) && !Equals(e.BoxedValue, e.DefaultValue))
                        e.BoxedValue = e.DefaultValue;
                    object[] tags = e.Description?.Tags ?? new object[0];
                    // An entry with its own F12 attributes: those say hidden
                    object attr = tags.FirstOrDefault(t => t != null && t.GetType().Name == "ConfigurationManagerAttributes");
                    if (attr != null)
                    {
                        FieldInfo f = attr.GetType().GetField("Browsable");
                        if (f != null)
                        {
                            f.SetValue(attr, (bool?)false);
                            continue;
                        }
                        PropertyInfo p = attr.GetType().GetProperty("Browsable");
                        if (p != null && p.CanWrite)
                        {
                            p.SetValue(attr, (bool?)false);
                            continue;
                        }
                    }
                    if (DescriptionField == null)
                        continue;
                    var hidden = new ConfigurationManagerAttributes { Browsable = false };
                    DescriptionField.SetValue(e, new ConfigDescription(e.Description?.Description ?? "", e.Description?.AcceptableValues,
                        tags.Concat(new object[] { hidden }).ToArray()));
                }
                catch
                {
                }
            }
        }

        // The configuration manager reads any class of this name
        private sealed class ConfigurationManagerAttributes
        {
            public bool? Browsable;
        }
    }
}
