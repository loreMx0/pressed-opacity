using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.UI;

namespace PressedOpacity
{
    [BepInPlugin("pressedopacity", "Pressed Opacity", "2.0.2")]
    public class PressedOpacityPlugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        private static PressedOpacityPlugin Instance;

        private static ConfigEntry<bool>   cfgEnabled;
        private static ConfigEntry<float>  cfgRestingAlpha;
        private static ConfigEntry<float>  cfgPressedAlpha;
        private static ConfigEntry<string> cfgInclude;
        private static ConfigEntry<string> cfgExclude;
        private static ConfigEntry<bool>   cfgLogMatches;
        private static ConfigEntry<bool>   cfgDiagnosticDump;
        private static ConfigEntry<bool>   cfgLogPressEvents;

        private class Target
        {
            public SpriteRenderer sr;
            public Graphic g;
            public Color orig;
            public Component touch;
            public Func<Component, bool> pressed;
            public string path;
            public bool lastPressed;
            public string memberName;
        }

        private static readonly List<Target> _targets = new List<Target>();
        private static string[] _include = new string[0];
        private static string[] _exclude = new string[0];
        private static bool _wasEnabled;
        private static bool _initialized;
        private static bool _dumpedComponent;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            try
            {
                cfgEnabled = Config.Bind("General", "Enabled", true, "Master toggle.");
                cfgRestingAlpha = Config.Bind("General", "RestingAlpha", 0f, "Alpha when not pressed.");
                cfgPressedAlpha = Config.Bind("General", "PressedAlpha", 0f, "Alpha while pressed.");

                cfgInclude = Config.Bind("Targets", "IncludeFilters", "InControlManager", "Path substrings.");
                cfgExclude = Config.Bind("Targets", "ExcludeFilters", "InControlManager/WaterMark", "Excluded.");

                cfgLogMatches = Config.Bind("Debug", "LogMatches", false, "Log matched paths once.");
                cfgDiagnosticDump = Config.Bind("Debug", "DiagnosticDump", false,
                    "Dump every field/property of the first TouchButtonControl found.");
                cfgLogPressEvents = Config.Bind("Debug", "LogPressEvents", false,
                    "Log every press/release. Spammy.");

                RebuildFilters();

                Log.LogInfo("Pressed Opacity v2.0.2 loaded.");
                Log.LogInfo("  Enabled=" + cfgEnabled.Value);
                Log.LogInfo("  RestingAlpha=" + cfgRestingAlpha.Value);
                Log.LogInfo("  PressedAlpha=" + cfgPressedAlpha.Value);
                Log.LogInfo("  IncludeFilters='" + cfgInclude.Value + "'");

                StartCoroutine(Guard(InitRoutine(), "InitRoutine"));
            }
            catch (Exception ex) { Log.LogError("Awake failed: " + ex); }
        }

        private void OnDestroy() { try { RestoreOriginals(); } catch { } }

        private static System.Collections.IEnumerator InitRoutine()
        {
            float elapsed = 0f;
            while (elapsed < 30f)
            {
                try { ScanOnce(); } catch (Exception ex) { Log.LogError("Scan: " + ex); }
                if (_targets.Count > 0) break;
                yield return new WaitForSecondsRealtime(3f);
                elapsed += 3f;
            }
            if (_targets.Count == 0)
                Log.LogWarning("No targets matched after 30s. Check IncludeFilters.");
            else
                Log.LogInfo("Pressed Opacity initialized with " + _targets.Count + " target(s).");
            _initialized = true;
        }

        private static void ScanOnce()
        {
            _targets.Clear();
            int spriteCount = 0, graphicCount = 0, detectorCount = 0;

            var sprites = Resources.FindObjectsOfTypeAll<SpriteRenderer>();
            for (int i = 0; i < sprites.Length; i++)
            {
                var sr = sprites[i];
                if (sr == null) continue;
                var go = sr.gameObject;
                if (go == null) continue;
                if (!IsSceneObject(go)) continue;
                string path = BuildPath(go);
                if (!ShouldHide(path)) continue;

                var t = new Target { sr = sr, orig = sr.color, path = path };
                AttachPressDetector(t, go);
                if (t.pressed != null) detectorCount++;
                _targets.Add(t);
                spriteCount++;
                if (cfgLogMatches.Value) Log.LogInfo("  HIDE sprite: " + path);
            }

            var graphics = Resources.FindObjectsOfTypeAll<Graphic>();
            for (int i = 0; i < graphics.Length; i++)
            {
                var g = graphics[i];
                if (g == null) continue;
                var go = g.gameObject;
                if (go == null) continue;
                if (!IsSceneObject(go)) continue;
                string path = BuildPath(go);
                if (!ShouldHide(path)) continue;

                var t = new Target { g = g, orig = g.color, path = path };
                AttachPressDetector(t, go);
                if (t.pressed != null) detectorCount++;
                _targets.Add(t);
                graphicCount++;
                if (cfgLogMatches.Value) Log.LogInfo("  HIDE graphic: " + path);
            }

            Log.LogInfo("Scan: " + spriteCount + " sprite(s), " + graphicCount
                + " graphic(s). Press detectors attached: " + detectorCount);
        }

        // Walk up the hierarchy looking for the *leaf* control component.
        // Stop at TouchButtonControl or TouchStickControl. Do NOT keep walking
        // to TouchManager / TouchControlCustom — those are scene-wide wrappers
        // and do not carry per-button state.
        private static void AttachPressDetector(Target t, GameObject go)
        {
            try
            {
                Transform p = go.transform;
                int depth = 0;
                while (p != null && depth < 8)
                {
                    var comps = p.GetComponents<Component>();
                    for (int c = 0; c < comps.Length; c++)
                    {
                        var comp = comps[c];
                        if (comp == null) continue;
                        string tn = comp.GetType().Name;

                        bool isLeafControl =
                            tn == "TouchButtonControl" ||
                            tn == "TouchStickControl" ||
                            tn == "TouchControl";

                        if (!isLeafControl) continue;

                        if (cfgDiagnosticDump.Value && !_dumpedComponent)
                        {
                            _dumpedComponent = true;
                            DumpComponent(comp, tn, p.name);
                        }

                        var getter = BuildPressedGetter(comp.GetType(), tn, p.name);
                        if (getter != null)
                        {
                            t.touch = comp;
                            t.pressed = getter;
                            t.memberName = _lastMemberName;
                        }
                        // Whether or not we found a getter, stop walking — this
                        // is the leaf control, its parents are not useful.
                        return;
                    }
                    p = p.parent;
                    depth++;
                }
                Log.LogWarning("  no TouchButtonControl/TouchStickControl found above " + t.path);
            }
            catch (Exception ex) { Log.LogWarning("AttachPressDetector: " + ex.Message); }
        }

        private static string _lastMemberName;

        // Priority order:
        //   1. bool field/property exactly named "buttonState" / "ButtonState"
        //   2. bool field/property exactly named one of: pressed, isPressed,
        //      pressDown, Pressed
        //   3. enum field containing "state" — non-zero = pressed
        //   4. bool field containing "state" (catches stateMachine-ish names)
        //   5. bool field containing "press", excluding config-flag names
        private static readonly string[] StateBoolNames =
        {
            "buttonState", "ButtonState", "controlState", "ControlState",
        };

        private static readonly string[] PreferredBoolNames =
        {
            "pressed", "isPressed", "pressDown", "pressedDown", "Pressed", "IsPressed",
        };

        private static Func<Component, bool> BuildPressedGetter(Type type, string typeName, string ownerName)
        {
            const BindingFlags F =
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            FieldInfo[] fields = null;
            PropertyInfo[] props = null;
            try { fields = type.GetFields(F); } catch { }
            try { props = type.GetProperties(F); } catch { }

            // ---- 1. exact bool "state" names (confirmed: buttonState) ----
            var g1 = MatchExactBool(fields, props, StateBoolNames, typeName, ownerName, "state bool");
            if (g1 != null) return g1;

            // ---- 2. exact bool "pressed" names ----
            var g2 = MatchExactBool(fields, props, PreferredBoolNames, typeName, ownerName, "press bool");
            if (g2 != null) return g2;

            // ---- 3. enum field containing "state" ----
            if (fields != null)
            {
                for (int i = 0; i < fields.Length; i++)
                {
                    var fi = fields[i];
                    if (!fi.FieldType.IsEnum) continue;
                    if (fi.Name.IndexOf("state", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var captured = fi;
                    _lastMemberName = fi.Name + " (enum field; !=0 => pressed)";
                    Log.LogInfo("  press signal: " + _lastMemberName
                        + " on " + typeName + " (" + ownerName + ")");
                    return c =>
                    {
                        try
                        {
                            object v = captured.GetValue(c);
                            if (v == null) return false;
                            return Convert.ToInt32(v) != 0;
                        }
                        catch { return false; }
                    };
                }
            }

            // ---- 4. bool field containing "state" ----
            if (fields != null)
            {
                for (int i = 0; i < fields.Length; i++)
                {
                    var fi = fields[i];
                    if (fi.FieldType != typeof(bool)) continue;
                    if (fi.Name.IndexOf("state", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (IsConfigFlag(fi.Name)) continue;
                    var captured = fi;
                    _lastMemberName = fi.Name + " (bool field with 'state')";
                    Log.LogInfo("  press signal: " + _lastMemberName
                        + " on " + typeName + " (" + ownerName + ")");
                    return c => { try { return (bool)captured.GetValue(c); } catch { return false; } };
                }
            }

            // ---- 5. bool containing "press", excluding config flags ----
            if (fields != null)
            {
                for (int i = 0; i < fields.Length; i++)
                {
                    var fi = fields[i];
                    if (fi.FieldType != typeof(bool)) continue;
                    if (fi.Name.IndexOf("press", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (IsConfigFlag(fi.Name)) continue;
                    var captured = fi;
                    _lastMemberName = fi.Name + " (bool field with 'press')";
                    Log.LogInfo("  press signal: " + _lastMemberName
                        + " on " + typeName + " (" + ownerName + ")");
                    return c => { try { return (bool)captured.GetValue(c); } catch { return false; } };
                }
            }

            Log.LogWarning("  no press signal found on " + typeName + " (" + ownerName + ")");
            return null;
        }

        // Look for a field or property whose type is bool and whose name
        // matches one of the wanted names exactly (case-insensitive).
        private static Func<Component, bool> MatchExactBool(
            FieldInfo[] fields, PropertyInfo[] props,
            string[] wanted, string typeName, string ownerName, string label)
        {
            const BindingFlags F =
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            if (fields != null)
            {
                for (int w = 0; w < wanted.Length; w++)
                {
                    for (int i = 0; i < fields.Length; i++)
                    {
                        var fi = fields[i];
                        if (fi.FieldType != typeof(bool)) continue;
                        if (!string.Equals(fi.Name, wanted[w], StringComparison.OrdinalIgnoreCase)) continue;
                        var captured = fi;
                        _lastMemberName = fi.Name + " (field, " + label + ")";
                        Log.LogInfo("  press signal: " + _lastMemberName
                            + " on " + typeName + " (" + ownerName + ")");
                        return c => { try { return (bool)captured.GetValue(c); } catch { return false; } };
                    }
                }
            }

            if (props != null)
            {
                for (int w = 0; w < wanted.Length; w++)
                {
                    for (int i = 0; i < props.Length; i++)
                    {
                        var pi = props[i];
                        if (pi.PropertyType != typeof(bool)) continue;
                        if (!pi.CanRead) continue;
                        if (pi.GetIndexParameters().Length > 0) continue;
                        if (!string.Equals(pi.Name, wanted[w], StringComparison.OrdinalIgnoreCase)) continue;
                        var captured = pi;
                        _lastMemberName = pi.Name + " (prop, " + label + ")";
                        Log.LogInfo("  press signal: " + _lastMemberName
                            + " on " + typeName + " (" + ownerName + ")");
                        return c => { try { return (bool)captured.GetValue(c, null); } catch { return false; } };
                    }
                }
            }

            return null;
        }

        private static bool IsConfigFlag(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            string lower = name.ToLowerInvariant();
            if (lower.StartsWith("pressure")) return true;
            if (lower.Contains("sensitive")) return true;
            if (lower.Contains("enabled")) return true;
            if (lower.Contains("threshold")) return true;
            return false;
        }

        private static void DumpComponent(Component comp, string typeName, string ownerName)
        {
            try
            {
                var type = comp.GetType();
                const BindingFlags F =
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

                Log.LogInfo("=== Component dump: " + typeName + " on '" + ownerName + "' ===");

                try
                {
                    Log.LogInfo("  Fields:");
                    foreach (var fi in type.GetFields(F))
                        Log.LogInfo("    " + fi.FieldType.Name + " " + fi.Name);
                }
                catch { }

                try
                {
                    Log.LogInfo("  Properties:");
                    foreach (var pi in type.GetProperties(F))
                    {
                        if (!pi.CanRead) continue;
                        if (pi.GetIndexParameters().Length > 0) continue;
                        Log.LogInfo("    " + pi.PropertyType.Name + " " + pi.Name);
                    }
                }
                catch { }

                Log.LogInfo("=== End dump ===");
            }
            catch (Exception ex) { Log.LogWarning("DumpComponent: " + ex.Message); }
        }

        private void LateUpdate()
        {
            if (!_initialized) return;
            bool enabled = cfgEnabled.Value;
            if (_wasEnabled && !enabled) RestoreOriginals();
            _wasEnabled = enabled;
            if (!enabled) return;

            float resting = Mathf.Clamp01(cfgRestingAlpha.Value);
            float pressed = Mathf.Clamp01(cfgPressedAlpha.Value);

            try
            {
                for (int i = 0; i < _targets.Count; i++)
                {
                    var t = _targets[i];
                    if (t == null) continue;

                    bool isPressed = false;
                    if (t.touch != null && t.pressed != null)
                    {
                        try { isPressed = t.pressed(t.touch); } catch { }
                    }

                    if (cfgLogPressEvents.Value && isPressed != t.lastPressed)
                    {
                        Log.LogInfo("PRESS " + (isPressed ? "DOWN" : "UP  ") + "  " + t.path
                            + "  (" + t.memberName + ")");
                        t.lastPressed = isPressed;
                    }

                    float a = isPressed ? pressed : resting;

                    if (t.sr != null)
                    {
                        var c = t.sr.color;
                        if (Math.Abs(c.a - a) > 0.001f) { c.a = a; t.sr.color = c; }
                    }
                    else if (t.g != null)
                    {
                        var c = t.g.color;
                        if (Math.Abs(c.a - a) > 0.001f) { c.a = a; t.g.color = c; }
                    }
                }
            }
            catch { }
        }

        private static void RestoreOriginals()
        {
            try
            {
                for (int i = 0; i < _targets.Count; i++)
                {
                    var t = _targets[i];
                    if (t == null) continue;
                    if (t.sr != null) t.sr.color = t.orig;
                    else if (t.g != null) t.g.color = t.orig;
                }
            }
            catch { }
        }

        private static void RebuildFilters()
        {
            _include = SplitCsv(cfgInclude.Value);
            _exclude = SplitCsv(cfgExclude.Value);
        }

        private static string[] SplitCsv(string s)
        {
            if (string.IsNullOrEmpty(s)) return new string[0];
            var parts = s.Split(',');
            var list = new List<string>();
            for (int i = 0; i < parts.Length; i++)
            {
                var t = parts[i].Trim();
                if (t.Length > 0) list.Add(t);
            }
            return list.ToArray();
        }

        private static bool ShouldHide(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            for (int i = 0; i < _exclude.Length; i++)
                if (path.IndexOf(_exclude[i], StringComparison.OrdinalIgnoreCase) >= 0) return false;
            for (int i = 0; i < _include.Length; i++)
                if (path.IndexOf(_include[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static bool IsSceneObject(GameObject go)
        {
            try { return go.scene.IsValid(); } catch { return false; }
        }

        private static string BuildPath(GameObject go)
        {
            var sb = new StringBuilder(64);
            var t = go.transform;
            while (t != null)
            {
                if (sb.Length > 0) sb.Insert(0, '/');
                sb.Insert(0, t.name);
                t = t.parent;
            }
            return sb.ToString();
        }

        private static System.Collections.IEnumerator Guard(
            System.Collections.IEnumerator inner, string label)
        {
            while (true)
            {
                object cur;
                try { if (!inner.MoveNext()) break; cur = inner.Current; }
                catch (Exception ex) { Log.LogError(label + " failed: " + ex); break; }
                yield return cur;
            }
        }
    }
}
