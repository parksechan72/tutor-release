using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ClassTutor
{
    /// <summary>
    /// Unity 콘솔 에러를 프로젝트 루트의 .tutor/errors.md 로 저장한다.
    /// 학생이 에러를 복사해 붙이지 않아도 튜터가 상황을 읽을 수 있게 하기 위한 것.
    /// 에디터 전용이며 빌드에는 포함되지 않는다.
    /// </summary>
    [InitializeOnLoad]
    public static class ErrorCapture
    {
        const string OutputPath = ".tutor/errors.md";
        const string Separator = "\n---\n";
        const int MaxEntries = 8;

        static readonly Queue<string> _pending = new Queue<string>();
        static readonly object _lock = new object();
        static string _lastMessage;

        static ErrorCapture()
        {
            Application.logMessageReceived -= OnLog;
            Application.logMessageReceived += OnLog;

            EditorApplication.update -= Flush;
            EditorApplication.update += Flush;
        }

        // 로그 콜백은 메인 스레드가 아닐 수 있다. 큐에만 넣고 파일 쓰기는 update 에서 한다.
        static void OnLog(string message, string stackTrace, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert)
                return;

            // 같은 에러가 매 프레임 쏟아지는 경우가 흔하다. 연속 중복은 무시한다.
            if (message == _lastMessage) return;
            _lastMessage = message;

            lock (_lock) { _pending.Enqueue(Format(message, stackTrace)); }
        }

        static string Format(string message, string stackTrace)
        {
            // 스택트레이스는 학생이 쓴 코드(Assets/)만 남긴다. 나머지는 초보에게 소음이다.
            var own = (stackTrace ?? string.Empty)
                .Split('\n')
                .Where(l => l.Contains("Assets/") || l.Contains("Assets\\"))
                .Take(3)
                .Select(l => l.Trim());

            var where = string.Join("\n", own);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"### {DateTime.Now:HH:mm:ss}");
            sb.AppendLine("```");
            sb.AppendLine(message.Trim());
            sb.AppendLine("```");
            if (!string.IsNullOrWhiteSpace(where))
            {
                sb.AppendLine("위치:");
                sb.AppendLine("```");
                sb.AppendLine(where);
                sb.AppendLine("```");
            }
            return sb.ToString().TrimEnd();
        }

        static void Flush()
        {
            string[] items;
            lock (_lock)
            {
                if (_pending.Count == 0) return;
                items = _pending.ToArray();
                _pending.Clear();
            }

            try
            {
                var root = Directory.GetParent(Application.dataPath).FullName;
                var path = Path.Combine(root, OutputPath);
                Directory.CreateDirectory(Path.GetDirectoryName(path));

                var entries = new List<string>();
                if (File.Exists(path))
                {
                    entries.AddRange(File.ReadAllText(path)
                        .Split(new[] { Separator }, StringSplitOptions.RemoveEmptyEntries)
                        .Where(e => e.TrimStart().StartsWith("### "))
                        .Select(e => e.Trim()));
                }
                entries.AddRange(items);

                // 최근 것만 남긴다. 오래된 에러까지 읽히면 튜터가 엉뚱한 걸 붙잡는다.
                if (entries.Count > MaxEntries)
                    entries = entries.Skip(entries.Count - MaxEntries).ToList();

                var header = "<!-- Unity 콘솔 에러 자동 기록. 직접 고칠 필요 없다. -->";
                File.WriteAllText(path, header + Separator + string.Join(Separator, entries) + "\n");
            }
            catch (Exception e)
            {
                // 파일을 못 써도 수업이 멈추면 안 되므로 조용히 넘어간다.
                Debug.LogWarning($"[ErrorCapture] 기록 실패: {e.Message}");
            }
        }
    }
}
