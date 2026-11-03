using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace ClassSubmit
{
    /// <summary>
    /// 메뉴 「제출」. 학생이 손댄 것(스크립트·씬·프리팹·머티리얼·설정 일부)만 zip 으로 묶는다.
    /// 만든 zip 을 디스코드 채점 봇 DM 에 올리면 된다. 선생님도 같은 버튼으로 기준(정답지)을 만든다.
    ///
    ///   진도 zip — 수업 끝 제출. 그대로 묶는다.
    ///   과제 zip — 자유 과제. 튜터 면담 파일(.tutor/과제N-면담.md)이 「제출 가능」 이어야 묶인다.
    ///             면담 뒤에 스크립트가 바뀌었거나 컴파일 에러가 있으면 안 묶는다. 면담 파일과 과제/과제N.md 를 같이 넣는다.
    ///
    /// 이 폴더(Assets/Editor/Submit)만 복사하면 다른 프로젝트에서도 돈다. 빌드에는 안 들어간다.
    /// </summary>
    public static class SubmitTool
    {
        const string OutFolder = "제출";
        const int FormatVersion = 2;
        const long MaxFileBytes = 2L * 1024 * 1024;   // 이보다 큰 파일은 뺀다. 모델·텍스처는 채점에 안 쓴다
        const long WarnZipBytes = 8L * 1024 * 1024;   // 디스코드 첨부 한도(10 MB) 근처면 경고
        const string TutorDir = ".tutor";
        const string InterviewSuffix = "-면담.md";
        const string AssignDir = "과제";
        const string VerdictOk = "제출 가능";

        static readonly HashSet<string> AssetExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".unity", ".prefab", ".mat", ".asmdef", ".physicmaterial", ".physicsmaterial",
            ".anim", ".controller", ".inputactions"
        };
        // 이 이름의 폴더는 통째로 뺀다. 에디터 도구(튜터·기록기·이 스크립트)와 플러그인은 학생 작업이 아니다
        static readonly HashSet<string> SkipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Editor", "Plugins", "StreamingAssets", "Gizmos" };
        static readonly string[] SettingsFiles =
        {
            "ProjectVersion.txt", "ProjectSettings.asset", "TagManager.asset", "EditorBuildSettings.asset",
            "InputManager.asset", "DynamicsManager.asset", "Physics2DSettings.asset", "TimeManager.asset", "QualitySettings.asset"
        };

        class Homework
        {
            public string Name;            // 과제1
            public string InterviewPath;   // <root>/.tutor/과제1-면담.md
            public string AssignPath;      // <root>/과제/과제1.md
            public string Verdict;         // 제출 가능 / 아직
            public DateTime InterviewTime;
        }

        [MenuItem("제출/진도 zip 만들기", false, 1)]
        public static void MakeProgressZip() => Make(null);

        [MenuItem("제출/과제 zip 만들기", false, 2)]
        public static void MakeHomeworkZip()
        {
            string root = Directory.GetParent(Application.dataPath).FullName;
            var hw = FindHomework(root);
            if (hw == null) return;
            Make(hw);
        }

        static void Make(Homework hw)
        {
            string what = hw == null ? "진도" : "과제";
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            { EditorUtility.DisplayDialog("제출", "Play 를 멈춘 뒤에 만든다.", "확인"); return; }
            if (EditorApplication.isCompiling)
            { EditorUtility.DisplayDialog("제출", "컴파일이 끝난 뒤에 다시 누른다.", "확인"); return; }
            bool compileErrors = EditorUtility.scriptCompilationFailed;
            if (compileErrors && hw != null)
            {
                EditorUtility.DisplayDialog("제출", "컴파일 에러가 있다 (Console 의 빨간 줄).\n과제는 에러를 고친 뒤에 낸다.", "확인");
                return;
            }
            // 튜터가 옛 버전이면 봇이 진도·과제 둘 다 미통과로 돌려보낸다. 묶기 전에 업데이트를 권한다
            if (!TutorUpdate.BeforeSubmit(Directory.GetParent(Application.dataPath).FullName)) return;

            // 저장 안 한 것이 있으면 저장한다. 안 하면 지난번 상태가 제출된다
            if (!EditorSceneManager.SaveOpenScenes())
            {
                if (!EditorUtility.DisplayDialog("제출", "씬을 저장하지 않았다.\n저장 안 된 상태로 묶을까?", "그래도 묶기", "취소"))
                    return;
            }
            AssetDatabase.SaveAssets();

            string root = Directory.GetParent(Application.dataPath).FullName;
            var files = new List<string>();
            var skipped = new List<string>();
            CollectAssets(root, Path.Combine(root, "Assets"), files, skipped);
            foreach (var s in SettingsFiles)
            {
                var p = Path.Combine(root, "ProjectSettings", s);
                if (File.Exists(p)) files.Add(p);
            }
            var pkgManifest = Path.Combine(root, "Packages", "manifest.json");
            if (File.Exists(pkgManifest)) files.Add(pkgManifest);
            if (hw != null)
            {
                files.Add(hw.InterviewPath);      // 봇이 과제 요구와 대조하는 면담 결과
                files.Add(hw.AssignPath);         // 학생이 받아 둔 과제 파일 — 원본과 같은지 봇이 본다
                var playLog = Path.Combine(root, TutorDir, "play-log.md");
                if (File.Exists(playLog)) files.Add(playLog);   // 튜터 플레이 검사 기록 — 선생님이 본다
            }

            string outDir = Path.Combine(root, OutFolder);
            Directory.CreateDirectory(outDir);
            string tag = hw == null ? "" : "_" + Safe(hw.Name);
            string zipPath = Path.Combine(outDir, Safe(new DirectoryInfo(root).Name) + tag + "_" + DateTime.Now.ToString("yyyyMMdd_HHmm") + ".zip");
            try { WriteZip(zipPath, root, files, skipped, hw, compileErrors); }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog("제출", "zip 을 못 만들었다.\n" + e.Message, "확인");
                return;
            }

            long size = new FileInfo(zipPath).Length;
            var msg = new StringBuilder();
            msg.AppendLine(what + " zip · 파일 " + files.Count + "개, " + Math.Max(1, size / 1024) + " KB");
            msg.AppendLine(zipPath);
            msg.AppendLine();
            msg.AppendLine("이 zip 을 디스코드 채점 봇 DM 에 올리면 된다.");
            if (hw != null) msg.AppendLine("면담 파일(" + hw.Name + ")을 같이 넣었다.");
            if (compileErrors) msg.AppendLine("\n주의: 컴파일 에러가 있는 상태다. 봇 기록에 표시된다.");
            if (skipped.Count > 0) msg.AppendLine("\n큰 파일 " + skipped.Count + "개는 뺐다. 채점에는 필요 없다.");
            if (size > WarnZipBytes) msg.AppendLine("\n주의: 디스코드 첨부 한도에 가깝다. 큰 에셋이 섞였는지 본다.");
            Debug.Log("[제출] " + zipPath);
            if (EditorUtility.DisplayDialog("제출 zip 완성", msg.ToString(), "폴더 열기", "닫기"))
                EditorUtility.RevealInFinder(zipPath);
        }

        // ── 과제: 튜터 면담 파일을 찾고 조건을 본다 ──────────────────────
        static Homework FindHomework(string root)
        {
            if (!TutorInstalled(root))
            {
                EditorUtility.DisplayDialog("과제 제출",
                    "이 프로젝트에 튜터가 설치돼 있지 않다 (CLAUDE.md 와 .claude\\settings.json).\n" +
                    "과제는 튜터 면담을 거쳐야 낼 수 있다. 선생님이 준 tutor-package 로 설치한다.", "확인");
                return null;
            }
            string tutorDir = Path.Combine(root, TutorDir);
            var candidates = new List<string>();
            if (Directory.Exists(tutorDir))
                foreach (var f in Directory.GetFiles(tutorDir, "*" + InterviewSuffix))
                    candidates.Add(f);
            if (candidates.Count == 0)
            {
                EditorUtility.DisplayDialog("과제 제출",
                    "면담 파일이 없다 (" + TutorDir + "\\과제N" + InterviewSuffix + ").\n\n" +
                    "먼저 Claude Code 에서 튜터에게 「과제1 면담」 이라고 말해 면담을 받는다.\n" +
                    "튜터가 「제출 가능」 이라고 판정하면 이 버튼을 다시 누른다.", "확인");
                return null;
            }
            // 가장 최근 면담
            candidates.Sort((a, b) => File.GetLastWriteTime(b).CompareTo(File.GetLastWriteTime(a)));
            string path = candidates[0];
            string fileName = Path.GetFileName(path);
            var hw = new Homework
            {
                InterviewPath = path,
                Name = fileName.Substring(0, fileName.Length - InterviewSuffix.Length),
                InterviewTime = File.GetLastWriteTime(path),
            };
            ReadHead(path, hw);
            if (candidates.Count > 1)
                Debug.Log("[제출] 면담 파일이 " + candidates.Count + "개다. 가장 최근 것(" + hw.Name + ")으로 간다.");

            if (!string.Equals(hw.Verdict, VerdictOk, StringComparison.Ordinal))
            {
                EditorUtility.DisplayDialog("과제 제출",
                    hw.Name + " 면담 판정이 「" + (string.IsNullOrEmpty(hw.Verdict) ? "없음" : hw.Verdict) + "」 이다.\n\n" +
                    "「" + VerdictOk + "」 이 될 때까지 고치고 튜터 면담을 다시 받는다.", "확인");
                return null;
            }
            hw.AssignPath = FindAssignment(root, hw.Name);
            if (hw.AssignPath == null)
            {
                EditorUtility.DisplayDialog("과제 제출",
                    "과제 파일이 없다 (" + AssignDir + " 폴더에 첫 줄이 「# " + hw.Name + "」 인 .md).\n\n" +
                    "디스코드 #과제 에서 " + hw.Name + " 의 과제 파일(첨부 .md)을 받아 프로젝트 폴더의 " + AssignDir + " 폴더에 넣는다.\n" +
                    "파일 이름은 바꾸지 않아도 된다.", "확인");
                return null;
            }
            string changed = ScriptChangedAfter(root, Path.Combine(root, "Assets"), hw.InterviewTime.AddSeconds(2));
            if (changed != null)
            {
                EditorUtility.DisplayDialog("과제 제출",
                    "면담 뒤에 스크립트가 바뀌었다: " + changed + "\n\n" +
                    "바뀐 코드로 튜터 면담을 다시 받은 뒤 낸다 (면담 파일이 코드보다 새것이어야 한다).", "확인");
                return null;
            }
            return hw;
        }

        // 과제 폴더의 .md 중 첫 제목 줄이 「# 과제1 — …」 인 것. 디스코드가 첨부 이름의 한글을 지워 「1.md」 가 되고,
        // 다시 받으면 「과제1 (1).md」 가 되므로 이름이 아니라 내용으로 찾는다. 여러 개면 가장 최근 것
        static string FindAssignment(string root, string name)
        {
            string dir = Path.Combine(root, AssignDir);
            if (!Directory.Exists(dir)) return null;
            string best = null;
            foreach (var f in Directory.GetFiles(dir, "*.md"))
            {
                string head = null;
                try
                {
                    foreach (var raw in File.ReadLines(f))
                    {
                        var line = raw.Trim().TrimStart('﻿');
                        if (line.Length == 0) continue;
                        head = line;
                        break;
                    }
                }
                catch { continue; }
                if (head == null || !head.StartsWith("# ")) continue;
                var first = head.Substring(2).Replace("`", "").Split(new[] { ' ', '\t', '—', '–' }, StringSplitOptions.RemoveEmptyEntries);
                if (first.Length == 0 || first[0] != name) continue;
                if (best == null || File.GetLastWriteTime(f) > File.GetLastWriteTime(best)) best = f;
            }
            return best;
        }

        static bool TutorInstalled(string root)
        {
            string claude = Path.Combine(root, "CLAUDE.md");
            string settings = Path.Combine(root, ".claude", "settings.json");
            if (!File.Exists(claude) || !File.Exists(settings)) return false;
            try { return File.ReadAllText(claude).Contains("[튜터 규칙"); }
            catch { return false; }
        }

        // 면담 파일 머리 — "과제: 과제1" / "판정: 제출 가능"
        static void ReadHead(string path, Homework hw)
        {
            try
            {
                foreach (var raw in File.ReadLines(path))
                {
                    var line = raw.Trim();
                    if (line.StartsWith("## ")) break;
                    int i = line.IndexOf(':');
                    if (i < 0) i = line.IndexOf('：');
                    if (i <= 0) continue;
                    string key = line.Substring(0, i).Trim(), val = line.Substring(i + 1).Trim().Trim('`', ' ');
                    if (key == "판정") hw.Verdict = val;
                    else if (key == "과제" && !string.IsNullOrEmpty(val))
                    {
                        // 튜터가 "과제1 — 아이템" 처럼 제목까지 적어도 이름(과제1)만 쓴다. 이름에는 공백이 없다
                        var name = val.Replace("`", "").Split(new[] { ' ', '\t', '—', '–' }, StringSplitOptions.RemoveEmptyEntries);
                        if (name.Length > 0) hw.Name = name[0];
                    }
                }
            }
            catch (Exception e) { Debug.LogWarning("[제출] 면담 파일을 읽지 못했다: " + e.Message); }
        }

        // 면담 시각보다 뒤에 고친 스크립트가 있으면 그 상대 경로. 도구 폴더는 안 본다
        static string ScriptChangedAfter(string root, string dir, DateTime after)
        {
            foreach (var sub in Directory.GetDirectories(dir))
            {
                var name = Path.GetFileName(sub);
                if (name.StartsWith(".") || name.EndsWith("~") || SkipDirs.Contains(name)) continue;
                var hit = ScriptChangedAfter(root, sub, after);
                if (hit != null) return hit;
            }
            foreach (var f in Directory.GetFiles(dir, "*.cs"))
                if (File.GetLastWriteTime(f) > after) return Rel(root, f);
            return null;
        }

        [MenuItem("제출/제출 폴더 열기", false, 20)]
        static void OpenFolder()
        {
            string dir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, OutFolder);
            if (!Directory.Exists(dir)) { EditorUtility.DisplayDialog("제출", "아직 만든 zip 이 없다. 「진도 zip 만들기」 나 「과제 zip 만들기」 부터.", "확인"); return; }
            EditorUtility.RevealInFinder(dir);
        }

        [MenuItem("제출/안내", false, 40)]
        static void Help()
        {
            EditorUtility.DisplayDialog("제출 안내",
                "진도 (수업 끝)\n" +
                "  1. 제출 ▸ 진도 zip 만들기\n" +
                "  2. 열린 폴더의 zip 을 디스코드 채점봇 DM 에 드래그\n" +
                "  3. 몇 초 뒤 결과가 온다. 미통과면 고쳐서 다시 올린다\n\n" +
                "과제\n" +
                "  1. #과제 에서 받은 과제N.md 를 프로젝트의 과제 폴더에 넣는다\n" +
                "  2. 다 만들면 Claude Code 튜터에게 「과제N 면담」 이라고 말한다\n" +
                "     튜터가 플레이 검사를 하면 Unity 가 저절로 Play 된다. 멈출 때까지 기다린다\n" +
                "  3. 「제출 가능」 이 나오면 제출 ▸ 과제 zip 만들기 → 채점봇 DM\n\n" +
                "튜터가 옛 버전이면 봇이 진도·과제 둘 다 미통과로 돌려보낸다.\n" +
                "제출 버튼이 알려 주면 업데이트하고, Claude Code 를 다시 켠다 (제출 ▸ 튜터 업데이트).\n\n" +
                "zip 에는 스크립트·씬·프리팹·머티리얼과 프로젝트 설정 일부만 들어간다.", "확인");
        }

        static void CollectAssets(string root, string dir, List<string> files, List<string> skipped)
        {
            foreach (var sub in Directory.GetDirectories(dir))
            {
                var name = Path.GetFileName(sub);
                if (name.StartsWith(".") || name.EndsWith("~") || SkipDirs.Contains(name)) continue;
                CollectAssets(root, sub, files, skipped);
            }
            foreach (var f in Directory.GetFiles(dir))
            {
                if (!AssetExts.Contains(Path.GetExtension(f))) continue;
                if (new FileInfo(f).Length > MaxFileBytes) { skipped.Add(Rel(root, f)); continue; }
                files.Add(f);
                // .meta 의 GUID 가 있어야 씬이 어느 스크립트를 가리키는지 풀 수 있다
                if (File.Exists(f + ".meta")) files.Add(f + ".meta");
            }
        }

        static void WriteZip(string zipPath, string root, List<string> files, List<string> skipped, Homework hw, bool compileErrors)
        {
            var entries = new StringBuilder();
            using (var fs = new FileStream(zipPath, FileMode.Create))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                foreach (var f in files)
                {
                    string rel = Rel(root, f);
                    var e = zip.CreateEntry(rel, System.IO.Compression.CompressionLevel.Optimal);
                    using (var src = File.OpenRead(f))
                    using (var dst = e.Open()) src.CopyTo(dst);
                    if (entries.Length > 0) entries.Append(",");
                    entries.Append("{\"path\":" + J(rel) + ",\"bytes\":" + new FileInfo(f).Length + ",\"sha1\":\"" + Sha1(f) + "\"}");
                }

                var skippedJson = new StringBuilder();
                foreach (var s in skipped) { if (skippedJson.Length > 0) skippedJson.Append(","); skippedJson.Append(J(s)); }

                // 봇이 읽는 꼬리표. JsonUtility 는 리스트를 못 다루므로 손으로 쓴다
                string manifest =
                    "{\n" +
                    "  \"format\": " + FormatVersion + ",\n" +
                    "  \"kind\": " + J(hw == null ? "progress" : "homework") + ",\n" +
                    "  \"homework\": " + J(hw == null ? "" : hw.Name) + ",\n" +
                    "  \"interview\": " + J(hw == null ? "" : Rel(root, hw.InterviewPath)) + ",\n" +
                    "  \"compile_errors\": " + (compileErrors ? "true" : "false") + ",\n" +
                    "  \"tutor_version\": " + J(TutorUpdate.LocalVersion(root) ?? "") + ",\n" +
                    "  \"unity\": " + J(Application.unityVersion) + ",\n" +
                    "  \"product\": " + J(PlayerSettings.productName) + ",\n" +
                    "  \"project\": " + J(new DirectoryInfo(root).Name) + ",\n" +
                    "  \"created\": " + J(DateTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz")) + ",\n" +
                    "  \"user\": " + J(Environment.UserName) + ",\n" +
                    "  \"device\": " + J(ShortHash(SystemInfo.deviceUniqueIdentifier)) + ",\n" +
                    "  \"files\": [" + entries + "],\n" +
                    "  \"skipped\": [" + skippedJson + "]\n" +
                    "}\n";
                var m = zip.CreateEntry("submit-manifest.json", System.IO.Compression.CompressionLevel.Optimal);
                using (var w = new StreamWriter(m.Open(), new UTF8Encoding(false))) w.Write(manifest);
            }
        }

        static string Rel(string root, string path) => path.Substring(root.Length + 1).Replace('\\', '/');

        static string Safe(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Replace(' ', '_');
        }

        static string Sha1(string path)
        {
            using (var sha = SHA1.Create())
            using (var s = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
        }

        // 기기 식별자는 그대로 안 남긴다. 같은 PC 에서 나온 제출인지만 알면 된다
        static string ShortHash(string s)
        {
            using (var sha = SHA1.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(s ?? ""))).Replace("-", "").Substring(0, 12).ToLowerInvariant();
        }

        static string J(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (var c in s ?? "")
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: if (c < ' ') sb.Append("\\u" + ((int)c).ToString("x4")); else sb.Append(c); break;
                }
            }
            return sb.Append('"').ToString();
        }
    }
}
