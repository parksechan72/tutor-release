using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.LowLevel;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
#endif

namespace ClassTutor
{
    /// <summary>
    /// 튜터의 플레이 검사. 튜터(Claude Code)가 .tutor/play-plan.json 을 쓰면 Play 를 돌리고,
    /// 무슨 일이 있었는지 .tutor/play-log.md 로 남긴다. 튜터는 그 기록을 읽고 과제 요구가 되는지 판단한다.
    ///
    ///   bot   — 이 도구가 새를 조종한다. 앞에 있는 트리거(점수 칸·아이템)를 차례로 노린다
    ///   human — 학생이 직접 플레이한다. 이 도구는 기록만 한다. Play 를 멈추면 끝
    ///
    /// 학생 코드는 고치지 않는다. 하는 일은 Play 시작·멈춤, 새 조종, 관찰뿐이다. MCP 없이 돈다.
    /// 에디터 전용이며 빌드와 제출 zip 에는 들어가지 않는다.
    /// </summary>
    [InitializeOnLoad]
    public static class PlayCheck
    {
        const string PlanPath = ".tutor/play-plan.json";
        const string LogPath = ".tutor/play-log.md";
        const string PrevLogPath = ".tutor/play-log.prev.md";
        const string KeyRunning = "ClassTutor.PlayCheck.Running";   // 돌리는 계획. Play 에 들어가며 도메인을 다시 읽어도 남는다
        const string KeyRequested = "ClassTutor.PlayCheck.RequestedAt";
        const int MaxEvents = 400;

        // 튜터가 쓰는 계획. 빠진 칸은 아래 기본값
        [Serializable]
        class Plan
        {
            public string id = "";
            public string mode = "bot";            // bot / human
            public string steer = "velocity";      // velocity: 위아래 속도를 직접 맞춘다 / key: 스페이스를 대신 누른다
            public float seconds = 40;             // 이 시간(실제 초)이 지나면 멈춘다. human 도 최대 이만큼
            public float speed = 1;                // Time.timeScale. 게임 오버로 0 이 되면 손대지 않는다
            public string player = "";             // 조종할 오브젝트 이름. 비우면 Bird → score 를 가진 Rigidbody
            public string[] aim = new string[0];   // 노릴 오브젝트 이름 조각. 비우면 앞에 있는 트리거 전부
            public string[] watch = new string[0]; // "스크립트.변수". 비우면 score 라는 변수를 찾아서 본다
            public float margin = 0.4f;            // key 조종: 목표보다 이만큼 아래로 떨어지면 스페이스
        }

        class Tracked
        {
            public int id;
            public GameObject go;
            public string name, baseName;
            public Vector3 lastPos;
            public string valueAtBirth = "";
            public string howGone = "";
            public bool gone, initial;
            public int plusOnes;          // 같은 것이 직전에 생긴 뒤(처음이면 시작부터) 첫 지켜본 값이 +1 된 횟수
            public float distAtGone;
        }

        class Watch
        {
            public string label;
            public UnityEngine.Object target;
            public FieldInfo field;
            public PropertyInfo prop;
        }

        static Plan _plan;
        static bool _recording, _firstTick, _stopping, _hadPlayer, _keyHeld;
        static float _start, _pausedSince;
        static double _lastPoll;
        static string _endReason, _playerDesc, _scene;
        static DateTime _startedAt;
        static GameObject _player;
        static List<Watch> _watches = new List<Watch>();
        static readonly Dictionary<string, string> _first = new Dictionary<string, string>();
        static readonly Dictionary<string, string> _last = new Dictionary<string, string>();
        static readonly Dictionary<string, Dictionary<string, int>> _deltas = new Dictionary<string, Dictionary<string, int>>();
        static readonly Dictionary<int, Tracked> _tracked = new Dictionary<int, Tracked>();
        static readonly List<string> _events = new List<string>();
        static readonly List<string> _errors = new List<string>();
        static readonly Dictionary<string, int> _plusAtLastSpawn = new Dictionary<string, int>();
        static int _dropped, _plusTotal;
        static string _pauseNote = "";
#if ENABLE_INPUT_SYSTEM
        static InputSettings.EditorInputBehaviorInPlayMode _oldInputBehavior;
        static bool _inputChanged;
#endif

        static PlayCheck()
        {
            EditorApplication.update -= Poll;
            EditorApplication.update += Poll;
            EditorApplication.playModeStateChanged -= OnPlayMode;
            EditorApplication.playModeStateChanged += OnPlayMode;
        }

        static string Root() => Directory.GetParent(Application.dataPath).FullName;
        static string Full(string rel) => Path.Combine(Root(), rel);

        // ── 계획 파일 보기 ─────────────────────────────────────────
        // 계획이 기록보다 새것이면 돌린다. 시각만 비교하므로 Unity 를 다시 켜도 옛 계획을 또 돌리지 않는다
        static void Poll()
        {
            if (EditorApplication.timeSinceStartup - _lastPoll < 1) return;
            _lastPoll = EditorApplication.timeSinceStartup;
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
                return;

            if (!string.IsNullOrEmpty(SessionState.GetString(KeyRunning, "")))
            {
                // Play 에 못 들어갔다 (컴파일 에러, 저장 취소 등)
                if (EditorApplication.timeSinceStartup - SessionState.GetFloat(KeyRequested, 0) < 5) return;
                SessionState.EraseString(KeyRunning);
                WriteText(LogPath, "# 플레이 기록\n\nPlay 를 시작하지 못했다. Unity 화면에 에러나 창이 떠 있는지 학생에게 물어본다.\n");
                return;
            }

            string plan = Full(PlanPath), log = Full(LogPath);
            if (!File.Exists(plan)) return;
            if (File.Exists(log) && File.GetLastWriteTimeUtc(log) >= File.GetLastWriteTimeUtc(plan)) return;
            StartRun(File.ReadAllText(plan));
        }

        [MenuItem("제출/플레이 검사 다시 돌리기", false, 21)]
        static void RunAgain()
        {
            if (!File.Exists(Full(PlanPath)))
            {
                EditorUtility.DisplayDialog("플레이 검사", "계획 파일이 없다 (" + PlanPath + ").\n튜터에게 「과제N 면담」 이라고 하면 튜터가 만든다.", "확인");
                return;
            }
            if (EditorApplication.isPlaying) { EditorUtility.DisplayDialog("플레이 검사", "Play 를 멈춘 뒤에 누른다.", "확인"); return; }
            StartRun(File.ReadAllText(Full(PlanPath)));
        }

        static void StartRun(string planText)
        {
            Plan plan;
            try { plan = JsonUtility.FromJson<Plan>(planText) ?? new Plan(); }
            catch (Exception e)
            {
                WriteText(LogPath, "# 플레이 기록\n\n계획 파일을 못 읽었다: " + e.Message + "\n" + PlanPath + " 가 JSON 꼴인지 본다.\n");
                return;
            }
            if (EditorUtility.scriptCompilationFailed)
            {
                WriteText(LogPath, "# 플레이 기록\n\n컴파일 에러가 있어서 Play 를 못 돌렸다. .tutor/errors.md 를 본다.\n");
                return;
            }
            try { if (File.Exists(Full(LogPath))) File.Copy(Full(LogPath), Full(PrevLogPath), true); } catch { }
            WriteText(LogPath, "# 플레이 기록 — 진행 중\n\n계획 「" + plan.id + "」 를 Unity 에서 돌리는 중이다. Play 가 멈추면 이 파일이 결과로 바뀐다.\n");
            SessionState.SetString(KeyRunning, JsonUtility.ToJson(plan));
            SessionState.SetFloat(KeyRequested, (float)EditorApplication.timeSinceStartup);
            EditorApplication.EnterPlaymode();
        }

        // ── Play 시작·끝 ─────────────────────────────────────────
        static void OnPlayMode(PlayModeStateChange s)
        {
            if (s == PlayModeStateChange.EnteredPlayMode && !string.IsNullOrEmpty(SessionState.GetString(KeyRunning, "")))
                Begin();
            else if (s == PlayModeStateChange.ExitingPlayMode && _recording)
                Finish(_endReason ?? (_plan.mode == "human" ? "학생이 Play 를 멈췄다" : "Play 가 멈췄다"));
        }

        static void Begin()
        {
            if (_recording) return;
            _plan = JsonUtility.FromJson<Plan>(SessionState.GetString(KeyRunning, "{}")) ?? new Plan();
            _recording = true;
            _firstTick = true;
            _stopping = false;
            _hadPlayer = false;
            _keyHeld = false;
            _endReason = null;
            _pausedSince = -1;
            _pauseNote = "";
            _player = null;
            _watches = new List<Watch>();
            _first.Clear(); _last.Clear(); _deltas.Clear(); _tracked.Clear(); _events.Clear(); _errors.Clear();
            _plusAtLastSpawn.Clear();
            _dropped = 0;
            _plusTotal = 0;
            _startedAt = DateTime.Now;
            _scene = EditorSceneManager.GetActiveScene().name;

            Application.runInBackground = true;          // 학생이 Claude Code 창을 보고 있어도 Play 가 돈다
            if (_plan.speed > 0 && Mathf.Abs(_plan.speed - 1) > 0.01f) Time.timeScale = _plan.speed;
#if ENABLE_INPUT_SYSTEM
            if (_plan.mode != "human" && _plan.steer == "key")
            {
                // 포커스가 다른 창에 있어도 대신 누른 스페이스가 게임으로 가게. 끝나면 되돌린다
                _oldInputBehavior = InputSystem.settings.editorInputBehaviorInPlayMode;
                InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
                _inputChanged = true;
            }
#endif
            Application.logMessageReceived -= OnLog;
            Application.logMessageReceived += OnLog;
            SetLoop(true);
        }

        static void Stop(string reason)
        {
            if (_stopping) return;
            _stopping = true;
            _endReason = reason;
            EditorApplication.ExitPlaymode();
        }

        static void Finish(string reason)
        {
            _recording = false;
            SetLoop(false);
            Application.logMessageReceived -= OnLog;
#if ENABLE_INPUT_SYSTEM
            if (_inputChanged) { InputSystem.settings.editorInputBehaviorInPlayMode = _oldInputBehavior; _inputChanged = false; }
#endif
            SessionState.EraseString(KeyRunning);
            try { WriteLog(reason); }
            catch (Exception e) { WriteText(LogPath, "# 플레이 기록\n\n기록을 쓰다가 실패했다: " + e.Message + "\n"); }
        }

        // 매 프레임 Update 단계 끝에 끼어든다. 에디터 폴더 코드라 오브젝트에 컴포넌트를 붙일 수 없어서 이렇게 한다
        static void SetLoop(bool on)
        {
            var root = PlayerLoop.GetCurrentPlayerLoop();
            var subs = root.subSystemList;
            for (int i = 0; i < subs.Length; i++)
            {
                if (subs[i].type != typeof(UnityEngine.PlayerLoop.Update)) continue;
                var list = (subs[i].subSystemList ?? new PlayerLoopSystem[0]).Where(x => x.type != typeof(PlayCheck)).ToList();
                if (on) list.Add(new PlayerLoopSystem { type = typeof(PlayCheck), updateDelegate = Tick });
                subs[i].subSystemList = list.ToArray();
            }
            root.subSystemList = subs;
            PlayerLoop.SetPlayerLoop(root);
        }

        // ── 매 프레임 ─────────────────────────────────────────
        static void Tick()
        {
            if (!_recording || _stopping || !EditorApplication.isPlaying) return;
            try { Step(); }
            catch (Exception e) { Note("도구 에러: " + e.Message); Stop("플레이 도구 에러 (" + e.GetType().Name + ")"); }
        }

        static void Step()
        {
            float now = Time.realtimeSinceStartup;
            if (_firstTick)
            {
                _firstTick = false;
                _start = now;
                Setup();
            }
            SampleWatches();
            SampleObjects();
            if (_plan.mode != "human" && Time.timeScale > 0) Steer();

            if (_hadPlayer && _player == null) { Stop("조종하던 오브젝트(" + _playerDesc + ")가 사라졌다"); return; }
            if (Time.timeScale == 0)
            {
                if (_pausedSince < 0) { _pausedSince = now; Note("게임이 멈췄다 (Time.timeScale 0)"); _pauseNote = "t=" + F(Time.time); }
                else if (now - _pausedSince > 1.5f) { Stop("게임이 멈췄다 — 게임 오버로 보인다"); return; }
            }
            else if (_pausedSince >= 0) { _pausedSince = -1; Note("게임이 다시 움직인다"); }
            if (now - _start >= _plan.seconds)
                Stop(_plan.mode == "human" ? "최대 시간 " + _plan.seconds + "초가 다 됐다" : "정한 시간 " + _plan.seconds + "초가 다 됐다 (끝까지 살아 있었다)");
        }

        static void Setup()
        {
            _player = FindPlayer(out _playerDesc);
            _hadPlayer = _player != null;
            _watches = FindWatches();
            foreach (var w in _watches) { var v = Read(w); _first[w.label] = v; _last[w.label] = v; }
            Note("시작 — 조종: " + _playerDesc + " · 지켜보는 값: " + (_watches.Count > 0 ? string.Join(", ", _watches.Select(w => w.label + "=" + _last[w.label])) : "없음"));
            foreach (var go in Clones()) Track(go, true);
        }

        // ── 관찰 ─────────────────────────────────────────
        static void SampleWatches()
        {
            foreach (var w in _watches)
            {
                string v = Read(w), old = _last[w.label];
                if (v == old) continue;
                _last[w.label] = v;
                string d = "";
                if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var nv) &&
                    double.TryParse(old, NumberStyles.Float, CultureInfo.InvariantCulture, out var ov))
                {
                    d = (nv - ov >= 0 ? "+" : "") + (nv - ov).ToString("0.##", CultureInfo.InvariantCulture);
                    if (!_deltas.TryGetValue(w.label, out var map)) _deltas[w.label] = map = new Dictionary<string, int>();
                    map[d] = map.TryGetValue(d, out var c) ? c + 1 : 1;
                    if (w == _watches[0] && d == "+1") _plusTotal++;      // 플래피 버드에선 파이프 하나 통과
                }
                Note(w.label + " " + old + " → " + v + (d != "" ? " (" + d + ")" : ""));
            }
        }

        static IEnumerable<GameObject> Clones()
        {
            // Instantiate 로 생긴 것 중 맨 위 것만 (Pipe(Clone) 의 자식 ScoreZone 은 따로 안 센다)
            foreach (var t in UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (!t.name.Contains("(Clone)")) continue;
                if (t.parent != null && t.parent.name.Contains("(Clone)")) continue;
                yield return t.gameObject;
            }
        }

        static void SampleObjects()
        {
            var seen = new HashSet<int>();
            foreach (var go in Clones())
            {
                int id = go.GetInstanceID();
                seen.Add(id);
                if (!_tracked.TryGetValue(id, out var tr)) { Track(go, false); continue; }
                if (tr.gone) continue;
                if (!go.activeInHierarchy) { Gone(tr, "꺼짐(SetActive false)"); continue; }
                tr.lastPos = go.transform.position;
            }
            foreach (var tr in _tracked.Values.ToList())
                if (!tr.gone && !seen.Contains(tr.id)) Gone(tr, "사라짐(Destroy)");
        }

        static void Track(GameObject go, bool initial)
        {
            var tr = new Tracked
            {
                id = go.GetInstanceID(), go = go, name = go.name,
                baseName = go.name.Replace("(Clone)", "").Trim(),
                lastPos = go.transform.position,
                valueAtBirth = _watches.Count > 0 ? _last[_watches[0].label] : "",
                initial = initial,
            };
            if (!initial)
            {
                tr.plusOnes = _plusTotal - (_plusAtLastSpawn.TryGetValue(tr.baseName, out var at) ? at : 0);
                _plusAtLastSpawn[tr.baseName] = _plusTotal;
            }
            _tracked[tr.id] = tr;
            var p = go.transform.position;
            Note((initial ? "처음부터 있음 " : "생김 ") + go.name + " (x " + F(p.x) + ", y " + F(p.y) + ")" + ColliderNote(go)
                 + Neighbors(tr) + ValuesNote());
        }

        static void Gone(Tracked tr, string how)
        {
            tr.gone = true;
            tr.howGone = how;
            string dist = "";
            if (_player != null)
            {
                tr.distAtGone = Vector3.Distance(tr.lastPos, _player.transform.position);
                dist = " · 새와 거리 " + F(tr.distAtGone);
            }
            Note(how + " " + tr.name + " (x " + F(tr.lastPos.x) + ", y " + F(tr.lastPos.y) + ")" + dist + ValuesNote());
        }

        // 기록이 끝난 뒤에 본 자리. 생길 때는 오른쪽 파이프가 아직 없을 수 있어서 끝에서 다시 계산한다
        static string Place(Tracked me, List<Tracked> others)
        {
            Tracked left = null, right = null;
            foreach (var t in others)
            {
                if (t.lastPos.x <= me.lastPos.x && (left == null || t.lastPos.x > left.lastPos.x)) left = t;
                if (t.lastPos.x > me.lastPos.x && (right == null || t.lastPos.x < right.lastPos.x)) right = t;
            }
            return "x " + F(me.lastPos.x) + ", y " + F(me.lastPos.y) + " — "
                   + (left != null ? left.baseName + " " + F(left.lastPos.x) : "없음") + " ~ "
                   + (right != null ? right.baseName + " " + F(right.lastPos.x) : "없음");
        }

        static string ColliderNote(GameObject go)
        {
            var c3 = go.GetComponentsInChildren<Collider>(true);
            var c2 = go.GetComponentsInChildren<Collider2D>(true);
            if (c3.Length == 0 && c2.Length == 0) return " · 콜라이더 없음";
            bool trig = c3.Any(c => c.isTrigger) || c2.Any(c => c.isTrigger);
            bool solid = c3.Any(c => !c.isTrigger) || c2.Any(c => !c.isTrigger);
            return trig && solid ? " · 트리거+막는 콜라이더" : trig ? " · 트리거" : " · 막는 콜라이더(트리거 아님)";
        }

        // 다른 종류 중 바로 왼쪽·오른쪽에 있는 것 — 「파이프와 파이프 사이」 를 보려고
        static string Neighbors(Tracked me)
        {
            float x = me.lastPos.x;
            Tracked left = null, right = null;
            foreach (var t in _tracked.Values)
            {
                if (t.gone || t.baseName == me.baseName) continue;
                if (t.lastPos.x <= x && (left == null || t.lastPos.x > left.lastPos.x)) left = t;
                if (t.lastPos.x > x && (right == null || t.lastPos.x < right.lastPos.x)) right = t;
            }
            if (left == null && right == null) return "";
            return " · 사이: " + (left != null ? left.baseName + " x " + F(left.lastPos.x) : "없음") + " ~ "
                   + (right != null ? right.baseName + " x " + F(right.lastPos.x) : "없음");
        }

        static string ValuesNote() =>
            _watches.Count == 0 ? "" : " · " + string.Join(", ", _watches.Select(w => w.label + "=" + _last[w.label]));

        // ── 조종 ─────────────────────────────────────────
        static void Steer()
        {
            if (_player == null) return;
            float ty = Target(out _);
#if ENABLE_INPUT_SYSTEM
            if (_plan.steer == "key" && Keyboard.current != null) { SteerKey(ty); return; }
#endif
            SteerVelocity(ty);
        }

        // 앞에 있는 노릴 것 중 가장 가까운 것의 높이. 없으면 화면 가운데쯤
        static float Target(out string what)
        {
            what = "";
            float px = _player.transform.position.x;
            float bestX = float.MaxValue, y = 0.5f;
            if (_plan.aim != null && _plan.aim.Length > 0)
            {
                foreach (var t in UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                {
                    if (t.IsChildOf(_player.transform) || !_plan.aim.Any(a => t.name.Contains(a))) continue;
                    var b = BoundsOf(t.gameObject);
                    if (b.max.x <= px + 0.05f || b.center.x >= bestX) continue;
                    bestX = b.center.x; y = b.center.y; what = t.name;
                }
                return y;
            }
            foreach (var c in UnityEngine.Object.FindObjectsByType<Collider>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (!c.enabled || !c.isTrigger || c.transform.IsChildOf(_player.transform)) continue;
                var b = c.bounds;
                if (b.max.x <= px + 0.05f || b.center.x >= bestX) continue;
                bestX = b.center.x; y = b.center.y; what = c.name;
            }
            foreach (var c in UnityEngine.Object.FindObjectsByType<Collider2D>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (!c.enabled || !c.isTrigger || c.transform.IsChildOf(_player.transform)) continue;
                var b = c.bounds;
                if (b.max.x <= px + 0.05f || b.center.x >= bestX) continue;
                bestX = b.center.x; y = b.center.y; what = c.name;
            }
            return y;
        }

        static Bounds BoundsOf(GameObject go)
        {
            var c = go.GetComponent<Collider>();
            if (c != null) return c.bounds;
            var c2 = go.GetComponent<Collider2D>();
            if (c2 != null) return c2.bounds;
            return new Bounds(go.transform.position, Vector3.zero);
        }

        static void SteerVelocity(float ty)
        {
            float vy = Mathf.Clamp((ty - _player.transform.position.y) * 4f, -8f, 8f);
            var rb = _player.GetComponent<Rigidbody>();
            if (rb != null && !rb.isKinematic) { var v = rb.linearVelocity; v.y = vy; rb.linearVelocity = v; return; }
            var rb2 = _player.GetComponent<Rigidbody2D>();
            if (rb2 != null && rb2.bodyType == RigidbodyType2D.Dynamic) { var v = rb2.linearVelocity; v.y = vy; rb2.linearVelocity = v; return; }
            var p = _player.transform.position;
            p.y = Mathf.MoveTowards(p.y, ty, 8f * Time.deltaTime);
            _player.transform.position = p;
        }

#if ENABLE_INPUT_SYSTEM
        static void SteerKey(float ty)
        {
            var kb = Keyboard.current;
            if (_keyHeld) { InputSystem.QueueStateEvent(kb, new KeyboardState()); _keyHeld = false; return; }
            float vy = 0;
            var rb = _player.GetComponent<Rigidbody>();
            if (rb != null) vy = rb.linearVelocity.y;
            var rb2 = _player.GetComponent<Rigidbody2D>();
            if (rb2 != null) vy = rb2.linearVelocity.y;
            if (_player.transform.position.y < ty - _plan.margin && vy < 0.5f)
            {
                InputSystem.QueueStateEvent(kb, new KeyboardState(Key.Space));
                _keyHeld = true;
            }
        }
#endif

        // ── 누구를, 무엇을 ─────────────────────────────────────────
        static GameObject FindPlayer(out string desc)
        {
            if (!string.IsNullOrEmpty(_plan.player))
            {
                var named = GameObject.Find(_plan.player);
                desc = named != null ? named.name : _plan.player + " (못 찾음)";
                return named;
            }
            var bird = GameObject.Find("Bird");
            if (bird != null) { desc = "Bird"; return bird; }
            foreach (var rb in UnityEngine.Object.FindObjectsByType<Rigidbody>(FindObjectsSortMode.None))
                if (rb.GetComponents<MonoBehaviour>().Any(m => m != null && ScoreMember(m.GetType()) != null)) { desc = rb.name; return rb.gameObject; }
            var any = UnityEngine.Object.FindObjectsByType<Rigidbody>(FindObjectsSortMode.None).FirstOrDefault(r => !r.isKinematic);
            desc = any != null ? any.name + " (짐작)" : "없음";
            return any != null ? any.gameObject : null;
        }

        static List<Watch> FindWatches()
        {
            var list = new List<Watch>();
            var behaviours = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(m => m != null).ToList();
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            if (_plan.watch != null && _plan.watch.Length > 0)
            {
                foreach (var spec in _plan.watch)
                {
                    int dot = spec.LastIndexOf('.');
                    if (dot <= 0) { Note("지켜볼 값 꼴이 틀렸다: " + spec + " (스크립트.변수)"); continue; }
                    string type = spec.Substring(0, dot), member = spec.Substring(dot + 1);
                    var m = behaviours.FirstOrDefault(b => b.GetType().Name == type);
                    if (m == null) { Note("지켜볼 스크립트를 씬에서 못 찾았다: " + type); continue; }
                    var f = m.GetType().GetField(member, flags);
                    var p = f == null ? m.GetType().GetProperty(member, flags) : null;
                    if (f == null && p == null) { Note("지켜볼 변수를 못 찾았다: " + spec); continue; }
                    list.Add(new Watch { label = spec, target = m, field = f, prop = p });
                }
                return list;
            }
            foreach (var m in behaviours)
            {
                var f = ScoreMember(m.GetType());
                if (f == null || list.Any(w => w.field == f)) continue;
                list.Add(new Watch { label = m.GetType().Name + "." + f.Name, target = m, field = f });
            }
            return list;
        }

        // 학생 스크립트의 score 라는 숫자 변수 (Unity·패키지 스크립트는 안 본다)
        static FieldInfo ScoreMember(Type t)
        {
            // GetName() 은 한글 경로 프로젝트에서 예외가 난다. FullName 으로 본다
            if (!t.Assembly.FullName.StartsWith("Assembly-CSharp")) return null;
            return t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                .FirstOrDefault(f => string.Equals(f.Name, "score", StringComparison.OrdinalIgnoreCase)
                                     && (f.FieldType == typeof(int) || f.FieldType == typeof(float) || f.FieldType == typeof(long)));
        }

        static string Read(Watch w)
        {
            try
            {
                if (w.target == null && (w.field == null || !w.field.IsStatic)) return "(없어짐)";
                object v = w.field != null ? w.field.GetValue(w.field.IsStatic ? null : w.target) : w.prop.GetValue(w.target);
                return v is IFormattable fv ? fv.ToString(null, CultureInfo.InvariantCulture) : (v?.ToString() ?? "null");
            }
            catch (Exception e) { return "(못 읽음: " + e.GetType().Name + ")"; }
        }

        // ── 기록 ─────────────────────────────────────────
        static void OnLog(string message, string stack, LogType type)
        {
            if (!_recording || (type != LogType.Error && type != LogType.Exception && type != LogType.Assert)) return;
            string line = (message ?? "").Split('\n')[0].Trim();
            if (_errors.Count < 20) _errors.Add(line);
            Note("에러: " + line);
        }

        static void Note(string s)
        {
            if (_events.Count >= MaxEvents) { _dropped++; return; }
            _events.Add("t=" + F(Time.time) + "  " + s);
        }

        static void WriteLog(string reason)
        {
            float real = Time.realtimeSinceStartup - _start;
            var sb = new StringBuilder();
            string how = _plan.mode == "human" ? "학생이 직접 플레이" : _plan.steer == "key" ? "봇 (스페이스를 대신 누름)" : "봇 (위아래 속도를 직접 맞춤)";
            sb.AppendLine("# 플레이 기록");
            sb.AppendLine();
            sb.AppendLine("- 계획: " + (_plan.id == "" ? "(이름 없음)" : _plan.id) + " · 방식: " + how + " · 씬: " + _scene);
            sb.AppendLine("- 시작: " + _startedAt.ToString("MM-dd HH:mm:ss") + " · 길이: 게임 " + F(Time.time) + "초 (실제 " + F(real) + "초)");
            sb.AppendLine("- **끝난 이유: " + reason + "**");
            sb.AppendLine("- 조종: " + (_playerDesc ?? "없음") + (_plan.mode == "human" ? " (조종은 학생)" : ""));
            sb.AppendLine("- 지켜본 값: " + (_watches.Count > 0 ? string.Join(", ", _watches.Select(w => w.label)) : "없음 — score 를 못 찾았다. 계획의 watch 에 \"스크립트.변수\" 를 적는다"));
            sb.AppendLine();
            sb.AppendLine("## 요약");
            sb.AppendLine();
            foreach (var w in _watches)
            {
                var d = _deltas.TryGetValue(w.label, out var map) ? string.Join(", ", map.OrderByDescending(kv => kv.Value).Select(kv => kv.Key + " ×" + kv.Value)) : "바뀐 적 없음";
                sb.AppendLine("- " + w.label + ": 처음 " + _first[w.label] + " → 끝 " + _last[w.label] + " · 변화: " + d);
            }
            var groups = _tracked.Values.GroupBy(t => t.baseName).OrderByDescending(g => g.Count()).ToList();
            for (int gi = 0; gi < groups.Count; gi++)
            {
                var g = groups[gi];
                var items = g.ToList();
                var goneList = items.Where(t => t.gone).ToList();
                string gone = goneList.Count == 0 ? " · 사라진 것 없음" :
                    " · 사라짐 " + goneList.Count + "개 (" + string.Join(", ", goneList.GroupBy(t => t.howGone).Select(x => x.Key + " " + x.Count())) + ")";
                sb.AppendLine("- " + g.Key + ": " + items.Count + "개" + gone + " · 끝까지 남은 것 " + (items.Count - goneList.Count) + "개");
                if (gi == 0) continue;     // 가장 많은 것(보통 파이프)은 개수만
                var born = items.Where(t => !t.initial).ToList();
                if (_watches.Count > 0 && born.Count > 0)
                    sb.AppendLine("  - 생기기까지 " + _watches[0].label + " +1 횟수 (처음은 시작부터, 다음부터는 직전 " + g.Key + " 이 생긴 뒤부터): "
                                  + string.Join(", ", born.Select(t => t.plusOnes)));
                var others = _tracked.Values.Where(t => t.baseName != g.Key).ToList();
                sb.AppendLine("  - 자리 (x, y — 좌우의 다른 것): " + string.Join(" / ", born.Take(10).Select(t => Place(t, others))));
                var near = goneList.Where(t => _player != null).Select(t => t.distAtGone).ToList();
                if (near.Count > 0) sb.AppendLine("  - 사라질 때 새와 거리: " + string.Join(", ", near.Select(F)));
            }
            if (_tracked.Count == 0) sb.AppendLine("- Instantiate 로 생긴 오브젝트: 없음");
            sb.AppendLine("- 게임 멈춤(Time.timeScale 0): " + (_pauseNote == "" ? "없음" : "있음 (" + _pauseNote + ")"));
            sb.AppendLine("- 에러: " + (_errors.Count == 0 ? "없음" : _errors.Count + "건 — " + string.Join(" / ", _errors.Distinct().Take(3))));
            sb.AppendLine();
            sb.AppendLine("## 사건 (t = 게임 시각, 초)");
            sb.AppendLine();
            foreach (var e in _events) sb.AppendLine("- " + e);
            if (_dropped > 0) sb.AppendLine("- … 기록이 길어 뒤의 " + _dropped + "줄은 뺐다");
            WriteText(LogPath, sb.ToString());
        }

        static string F(float v) => v.ToString("0.0", CultureInfo.InvariantCulture);

        static void WriteText(string rel, string text)
        {
            try
            {
                var path = Full(rel);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, text, new UTF8Encoding(false));
            }
            catch (Exception e) { Debug.LogWarning("[PlayCheck] 기록 실패: " + e.Message); }
        }
    }
}
