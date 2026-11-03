using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace ClassSubmit
{
    /// <summary>
    /// 튜터 업데이트. 튜터 배포 저장소의 version.json 과 이 프로젝트 CLAUDE.md 의 「튜터 버전」 을 비교해
    /// 옛 버전이면 파일을 받아 덮어쓴다. 제출 버튼이 zip 을 만들기 전에 부르고, 메뉴 「제출 ▸ 튜터 업데이트」 로도 부른다.
    /// 봇도 같은 version.json 으로 제출의 튜터 버전을 본다 — 옛 버전이면 진도·과제 둘 다 미통과다.
    /// </summary>
    public static class TutorUpdate
    {
        // tools/build-tutor-package.py 가 학생 배포본을 만들 때 주소를 채운다.
        // 교재 개발용 프로젝트(이 저장소)에서는 비어 있어서 아무것도 확인하지 않는다
        const string ReleaseRaw = "https://raw.githubusercontent.com/Nichi72/tutor-release/main/";
        const int TimeoutSeconds = 15;

        static readonly Regex VersionLine = new Regex(@"튜터 버전[:：]\s*([0-9]{4}-[0-9]{2}-[0-9]{2}\.[0-9]+)");
        // 받은 파일을 쓸 수 있는 곳. 배포 저장소가 잘못돼도 이 밖은 건드리지 않는다
        static readonly string[] Allowed =
        {
            "CLAUDE.md", "syllabus.md", ".claude/settings.json", "Assets/Editor/ErrorCapture.cs",
            "Assets/Editor/Submit/", "Assets/Editor/Tutor/"
        };

        [Serializable] class FileEntry { public string path; public string sha1; }
        [Serializable] class Info { public string version; public FileEntry[] files; }

        static string Root() => Directory.GetParent(Application.dataPath).FullName;

        /// <summary>CLAUDE.md 맨 위의 「튜터 버전: 2026-11-03.1」. 없으면 null (옛 튜터).</summary>
        public static string LocalVersion(string root)
        {
            var path = Path.Combine(root, "CLAUDE.md");
            if (!File.Exists(path)) return null;
            try
            {
                int n = 0;
                foreach (var line in File.ReadLines(path))
                {
                    var m = VersionLine.Match(line);
                    if (m.Success) return m.Groups[1].Value;
                    if (++n > 15) break;
                }
            }
            catch (Exception e) { Debug.LogWarning("[튜터 업데이트] CLAUDE.md 를 못 읽었다: " + e.Message); }
            return null;
        }

        /// <summary>zip 을 만들기 전에 부른다. false 면 제출을 멈춘다 (업데이트했거나 학생이 취소했다).</summary>
        public static bool BeforeSubmit(string root)
        {
            if (string.IsNullOrEmpty(ReleaseRaw)) return true;
            var info = FetchInfo();
            if (info == null)
                return EditorUtility.DisplayDialog("제출",
                    "최신 튜터 버전을 확인하지 못했다 (인터넷 연결?).\n\n그대로 묶을 수는 있지만, 튜터가 옛 버전이면 봇이 미통과로 돌려보낸다.",
                    "그래도 묶기", "취소");
            string local = LocalVersion(root);
            if (local == info.version) return true;
            if (EditorUtility.DisplayDialog("튜터 업데이트",
                    "튜터가 옛 버전이다.\n지금 " + (local ?? "버전 표시 없음") + " → 최신 " + info.version + "\n\n" +
                    "옛 버전으로 내면 봇이 미통과로 돌려보낸다. 지금 업데이트할까?", "업데이트", "취소"))
                Apply(root, info);
            return false;
        }

        [MenuItem("제출/튜터 업데이트", false, 30)]
        static void Menu()
        {
            if (string.IsNullOrEmpty(ReleaseRaw))
            {
                EditorUtility.DisplayDialog("튜터 업데이트", "이 프로젝트에는 튜터 업데이트 주소가 없다.\n선생님이 준 튜터 설치 zip 으로 설치한 프로젝트에서 쓴다.", "확인");
                return;
            }
            var info = FetchInfo();
            if (info == null)
            {
                EditorUtility.DisplayDialog("튜터 업데이트", "최신 버전을 확인하지 못했다. 인터넷 연결을 보고 다시 누른다.", "확인");
                return;
            }
            string root = Root(), local = LocalVersion(root);
            if (local == info.version &&
                !EditorUtility.DisplayDialog("튜터 업데이트", "이미 최신이다 (" + local + ").", "그래도 다시 받기", "닫기"))
                return;
            Apply(root, info);
        }

        static Info FetchInfo()
        {
            var data = Get(ReleaseRaw + "version.json");
            if (data == null) return null;
            try
            {
                var info = JsonUtility.FromJson<Info>(System.Text.Encoding.UTF8.GetString(data));
                return info != null && !string.IsNullOrEmpty(info.version) && info.files != null ? info : null;
            }
            catch (Exception e) { Debug.LogWarning("[튜터 업데이트] version.json 을 못 읽었다: " + e.Message); return null; }
        }

        // 전부 받아서 확인한 뒤에야 쓴다. 중간에 실패하면 아무것도 안 바꾼다
        static void Apply(string root, Info info)
        {
            var got = new List<KeyValuePair<string, byte[]>>();
            try
            {
                for (int i = 0; i < info.files.Length; i++)
                {
                    var f = info.files[i];
                    EditorUtility.DisplayProgressBar("튜터 업데이트", f.path, (float)i / info.files.Length);
                    var full = TargetPath(root, f.path) ?? throw new Exception("받을 수 없는 경로: " + f.path);
                    var data = Get(ReleaseRaw + "files/" + f.path) ?? throw new Exception("못 받았다: " + f.path);
                    if (!string.Equals(Sha1(data), f.sha1, StringComparison.OrdinalIgnoreCase))
                        throw new Exception("받은 파일이 깨졌다: " + f.path);
                    got.Add(new KeyValuePair<string, byte[]>(full, data));
                }
            }
            catch (Exception e)
            {
                EditorUtility.ClearProgressBar();
                EditorUtility.DisplayDialog("튜터 업데이트", "업데이트 실패 — 아무것도 바꾸지 않았다.\n\n" + e.Message, "확인");
                return;
            }
            EditorUtility.ClearProgressBar();
            foreach (var kv in got)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(kv.Key));
                File.WriteAllBytes(kv.Key, kv.Value);
            }
            Debug.Log("[튜터 업데이트] " + info.version + " · 파일 " + got.Count + "개");
            EditorUtility.DisplayDialog("튜터 업데이트",
                "튜터를 " + info.version + " 으로 바꿨다.\n\n" +
                "1. Claude Code 를 껐다가 다시 켠다 (새 규칙을 읽게)\n" +
                "2. Unity 가 스크립트를 다시 읽고 나면 제출 버튼을 다시 누른다", "확인");
            AssetDatabase.Refresh();
        }

        static string TargetPath(string root, string rel)
        {
            if (string.IsNullOrEmpty(rel) || rel.Contains("..") || rel.Contains("\\") || Path.IsPathRooted(rel)) return null;
            bool ok = false;
            foreach (var a in Allowed)
                if (a.EndsWith("/") ? rel.StartsWith(a, StringComparison.Ordinal) : rel == a) { ok = true; break; }
            return ok ? Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)) : null;
        }

        // 에디터 메뉴 안이라 기다려도 된다. 파일이 작고 시간 제한이 있다
        static byte[] Get(string url)
        {
            using (var req = UnityWebRequest.Get(url + "?t=" + DateTime.UtcNow.Ticks))
            {
                req.timeout = TimeoutSeconds;
                var op = req.SendWebRequest();
                var until = DateTime.UtcNow.AddSeconds(TimeoutSeconds + 2);
                while (!op.isDone && DateTime.UtcNow < until) Thread.Sleep(20);
                if (!op.isDone || req.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning("[튜터 업데이트] 못 받았다: " + url + " — " + (op.isDone ? req.error : "시간 초과"));
                    return null;
                }
                return req.downloadHandler.data;
            }
        }

        static string Sha1(byte[] data)
        {
            using (var sha = SHA1.Create())
                return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
        }
    }
}
