using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Minimal file-based command bridge for external tooling (Claude Code).
/// Exists because Unity's official MCP server is gated behind an account
/// entitlement this project doesn't have. This bridge needs NO account, no
/// network, no packages: an external tool writes a JSON command to
/// <c>Library/FileBridge/cmd.json</c>; the bridge polls it from
/// <c>EditorApplication.update</c> (ticks even when the editor is unfocused),
/// executes, deletes the command file, and writes
/// <c>Library/FileBridge/result_&lt;id&gt;.json</c>.
///
/// Command JSON shape (all fields strings unless noted):
///   { "id":"unique", "op":"ping|scene_info|console|menu|play|stop|
///                          screenshot_game|screenshot_scene|exec",
///     "arg":"...", "n":50 }
///
/// Ops:
///   ping              — liveness + editor/play state.
///   scene_info        — active scene name/path + root objects.
///   console           — last n buffered log entries (buffer survives since
///                       last domain reload; arg filters "error" / "warn").
///   menu              — EditorApplication.ExecuteMenuItem(arg).
///   play / stop       — enter / exit Play Mode.
///   screenshot_game   — renders the main camera to PNG at arg (or default path).
///   screenshot_scene  — renders the last active SceneView camera to PNG.
///   exec              — invoke public static method: arg = "TypeName.MethodName"
///                       (parameterless). Lets new tooling be added as plain
///                       editor scripts and invoked by name.
///
/// Security note: this executes commands from the local filesystem only —
/// same trust domain as the project source itself (any .cs in the project
/// already executes on import). Delete this file to disable the bridge.
/// </summary>
[InitializeOnLoad]
public static class FileBridge
{
    private const string Dir     = "Library/FileBridge";
    private const string CmdPath = Dir + "/cmd.json";

    private static double _nextPoll;
    private static readonly StringBuilder _logBuf = new StringBuilder();
    private static int _logCount;

    [Serializable] private class Cmd    { public string id; public string op; public string arg; public int n; }
    [Serializable] private class Result { public string id; public bool ok; public string message; }

    static FileBridge()
    {
        Directory.CreateDirectory(Dir);
        Application.logMessageReceived += OnLog;
        EditorApplication.update += Poll;
    }

    private static void OnLog(string condition, string stackTrace, LogType type)
    {
        if (_logCount > 4000) return; // hard cap per domain
        _logCount++;
        _logBuf.Append('[').Append(type).Append("] ").AppendLine(condition);
        if (type == LogType.Error || type == LogType.Exception)
        {
            // first stack line helps locate errors without flooding the buffer
            string first = (stackTrace ?? "").Split('\n').FirstOrDefault(l => l.Contains(".cs"));
            if (!string.IsNullOrEmpty(first)) _logBuf.Append("    at ").AppendLine(first.Trim());
        }
    }

    private static void Poll()
    {
        if (EditorApplication.timeSinceStartup < _nextPoll) return;
        _nextPoll = EditorApplication.timeSinceStartup + 0.25;

        if (!File.Exists(CmdPath)) return;

        Cmd cmd = null;
        try
        {
            cmd = JsonUtility.FromJson<Cmd>(File.ReadAllText(CmdPath));
        }
        catch (Exception e)
        {
            TryDelete(CmdPath);
            WriteResult(new Result { id = "parse-error", ok = false, message = e.Message });
            return;
        }
        TryDelete(CmdPath); // consume first so a throwing op can't loop forever

        var res = new Result { id = cmd.id, ok = true, message = "" };
        try   { res.message = Execute(cmd); }
        catch (Exception e) { res.ok = false; res.message = e.GetType().Name + ": " + e.Message; }
        WriteResult(res);
    }

    private static string Execute(Cmd cmd)
    {
        switch ((cmd.op ?? "").ToLowerInvariant())
        {
            case "ping":
                return "pong | Unity " + Application.unityVersion +
                       " | play=" + EditorApplication.isPlaying +
                       " | compiling=" + EditorApplication.isCompiling +
                       " | scene=" + SceneManager.GetActiveScene().name;

            case "scene_info":
            {
                Scene s = SceneManager.GetActiveScene();
                var sb = new StringBuilder();
                sb.Append("scene='").Append(s.name).Append("' path='").Append(s.path)
                  .Append("' roots=").Append(s.rootCount).AppendLine();
                foreach (GameObject go in s.GetRootGameObjects())
                    sb.Append(go.activeSelf ? "[on]  " : "[off] ").AppendLine(go.name);
                return sb.ToString();
            }

            case "console":
            {
                string all = _logBuf.ToString();
                string[] lines = all.Split('\n');
                string f = (cmd.arg ?? "").ToLowerInvariant();
                if (f == "error") lines = lines.Where(l => l.StartsWith("[Error") || l.StartsWith("[Exception") || l.StartsWith("    at")).ToArray();
                else if (f == "warn") lines = lines.Where(l => l.StartsWith("[Warning")).ToArray();
                int n = cmd.n > 0 ? cmd.n : 80;
                return string.Join("\n", lines.Reverse().Take(n).Reverse());
            }

            case "menu":
                if (string.IsNullOrEmpty(cmd.arg)) throw new ArgumentException("menu requires arg = menu path");
                return EditorApplication.ExecuteMenuItem(cmd.arg)
                    ? "executed: " + cmd.arg
                    : "MENU NOT FOUND: " + cmd.arg;

            case "play":
                EditorApplication.isPlaying = true;
                return "entering play mode (domain reload follows — re-ping after a few seconds)";

            case "stop":
                EditorApplication.isPlaying = false;
                return "exiting play mode";

            case "screenshot_game":
            {
                Camera cam = Camera.main ?? UnityEngine.Object.FindFirstObjectByType<Camera>();
                if (cam == null) throw new InvalidOperationException("no camera in scene");
                return CaptureCamera(cam, string.IsNullOrEmpty(cmd.arg) ? Dir + "/game.png" : cmd.arg);
            }

            case "screenshot_scene":
            {
                var sv = SceneView.lastActiveSceneView;
                if (sv == null || sv.camera == null) throw new InvalidOperationException("no active SceneView");
                return CaptureCamera(sv.camera, string.IsNullOrEmpty(cmd.arg) ? Dir + "/scene.png" : cmd.arg);
            }

            case "exec":
            {
                int dot = (cmd.arg ?? "").LastIndexOf('.');
                if (dot <= 0) throw new ArgumentException("exec arg must be 'TypeName.StaticMethod'");
                string typeName = cmd.arg.Substring(0, dot), methodName = cmd.arg.Substring(dot + 1);
                Type t = AppDomain.CurrentDomain.GetAssemblies()
                          .Select(a => a.GetType(typeName, false))
                          .FirstOrDefault(x => x != null);
                if (t == null) throw new ArgumentException("type not found: " + typeName);
                MethodInfo m = t.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (m == null) throw new ArgumentException("static method not found: " + cmd.arg);
                object ret = m.Invoke(null, null);
                return "invoked " + cmd.arg + (ret != null ? " → " + ret : " (void)");
            }

            default:
                throw new ArgumentException("unknown op: " + cmd.op);
        }
    }

    private static string CaptureCamera(Camera cam, string path)
    {
        const int W = 1280, H = 720;
        var rt  = new RenderTexture(W, H, 24);
        var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
        RenderTexture prevTarget = cam.targetTexture;
        RenderTexture prevActive = RenderTexture.active;
        try
        {
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            return "saved: " + Path.GetFullPath(path);
        }
        finally
        {
            cam.targetTexture = prevTarget;
            RenderTexture.active = prevActive;
            UnityEngine.Object.DestroyImmediate(rt);
            UnityEngine.Object.DestroyImmediate(tex);
        }
    }

    private static void WriteResult(Result r)
    {
        try
        {
            string json = JsonUtility.ToJson(r);
            File.WriteAllText(Dir + "/result_" + r.id + ".json", json);
            File.WriteAllText(Dir + "/last_result.json", json);
        }
        catch { /* result write must never throw into the editor loop */ }
    }

    private static void TryDelete(string p)
    {
        try { File.Delete(p); } catch { }
    }
}
