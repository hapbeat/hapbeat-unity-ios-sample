using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using Hapbeat;
using UnityEngine;

namespace HbCheck
{
    /// <summary>
    /// iOS check app for the Unity SDK. It shows buttons for trying a device by hand: Clip plays one pulse
    /// through StreamClip, and the Kit list comes from the device, the way Studio shows it. A scenario given at
    /// launch (HB_SCENARIO: clip-once, kit) does the same without touching the screen. Every step goes to
    /// Documents/hb-check.log, flushed line by line so the last line survives a crash.
    /// Nothing here plays through an AudioSource, so the iPhone stays silent.
    /// </summary>
    public sealed class HbCheckApp : MonoBehaviour
    {
        [Serializable]
        private class FileSettings
        {
            public string scenario;
            public string deviceIp;
            public string eventId;
            public string gain;
        }

        private const int DevicePort = 7700;
        private const float DeviceWaitSeconds = 45f;
        // Silence after the pulse, so ending the stream cannot cut off what the device still has buffered.
        private const float OneShotClipSeconds = 0.5f;
        private const float OneShotWaitSeconds = 10f;
        private const int MaxScenarioEvents = 5;
        private const int RepeatsOfNamedEvent = 5;
        private const float DragThresholdPixels = 24f;

        private static readonly object Gate = new object();
        private static readonly List<string> Lines = new List<string>();
        private static string _logPath;

        private string _scenario = "";
        private string _deviceIp = "";
        private string _eventId = "";
        private float _gain = 0.3f;
        private bool _useRoute;
        private string _routeIp = "";
        private bool _running;
        private float _nextRouteCheck;

        private bool _buttonMode;
        private bool _readingKits;
        private bool _kitReadFailed;
        private float _nextDeviceCheck;
        private readonly HashSet<string> _askedIps = new HashSet<string>();
        private string _kitStatus = "Kits: waiting for a device";
        private List<HbDeviceKits.DeviceEvent> _events = new List<HbDeviceKits.DeviceEvent>();
        private AudioClip _oneShotClip;
        private Vector2 _kitScroll;
        private float _touchTravel;
        private bool _touchDragging;
        private string[] _visibleLines = new string[0];
        private string _visibleState = "";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            var host = new GameObject("HbCheck");
            DontDestroyOnLoad(host);
            host.AddComponent<Camera>().clearFlags = CameraClearFlags.SolidColor;
            host.AddComponent<HbCheckApp>();
        }

        private void Awake()
        {
            Screen.sleepTimeout = SleepTimeout.NeverSleep;
            Application.runInBackground = true;
            Application.targetFrameRate = 30;
            _logPath = Path.Combine(Application.persistentDataPath, "hb-check.log");
            try { File.Delete(_logPath); } catch (Exception) { }
            Application.logMessageReceivedThreaded += OnUnityLog;

            FileSettings file = ReadFileSettings();
            _scenario = Setting("SCENARIO", file.scenario, "");
            _deviceIp = Setting("DEVICE_IP", file.deviceIp, "");
            _eventId = Setting("EVENT", file.eventId, _eventId);
            _gain = ParseFloat(Setting("GAIN", file.gain, ""), _gain);

            Note("app start build=" + ReadBuildStamp() + " scenario=" + (_scenario.Length > 0 ? _scenario : "(buttons)") +
                 " deviceIp=" + (_deviceIp.Length > 0 ? _deviceIp : "(broadcast)") +
                 " event=" + (_eventId.Length > 0 ? _eventId : "(from the device)") +
                 " gain=" + _gain + " os=" + SystemInfo.operatingSystem +
                 " model=" + SystemInfo.deviceModel + " backend=" + Backend());
        }

        private void Start()
        {
            if (_scenario.Length > 0)
            {
                StartCoroutine(Run(_scenario));
                return;
            }
            _buttonMode = true;
            EnsureManager();
        }

        private void Update()
        {
            if (_buttonMode)
            {
                TrackTouchScroll();
                ReadKitsOfNewDevices();
            }

            // A reconnect rebuilds the SDK's route list, so the unicast route is put back when that happens.
            if (!_useRoute || Time.realtimeSinceStartup < _nextRouteCheck) return;
            _nextRouteCheck = Time.realtimeSinceStartup + 1f;
            if (HapbeatManager.Instance != null) ApplyRoute(HapbeatManager.Instance);
        }

        private IEnumerator Run(string scenario)
        {
            _running = true;
            Note("scenario=" + scenario + " step=start");
            switch (scenario)
            {
                case "clip-once": yield return RunClipOnce(); break;
                case "kit": yield return RunKit(); break;
                default: Note("result=error reason=unknown-scenario"); break;
            }
            _running = false;
        }

        // One press, one pulse. A loop never ends by itself, so ended=yes shows this really was a one-shot.
        private IEnumerator RunClipOnce()
        {
            HapbeatManager manager = EnsureManager();
            yield return WaitForDevice(manager, "clip-once");
            if (manager.AliveDeviceCount <= 0)
            {
                Note("result=no-device scenario=clip-once");
                yield break;
            }
            if (_oneShotClip == null) _oneShotClip = MakePulse(OneShotClipSeconds);
            HapbeatStreamPlayback playback = manager.StreamAudioClip(_oneShotClip, _gain, null, false);
            if (playback == null)
            {
                Note("result=error scenario=clip-once reason=stream-returned-null");
                yield break;
            }
            Note("scenario=clip-once step=stream-requested status=" + playback.Status);

            bool active = false;
            float deadline = Time.realtimeSinceStartup + OneShotWaitSeconds;
            while (!playback.IsStopped && Time.realtimeSinceStartup < deadline)
            {
                active |= playback.IsActive;
                yield return null;
            }
            bool ended = playback.IsStopped;
            if (!ended) playback.Stop();
            Note("result=survived scenario=clip-once active=" + (active ? "yes" : "no") + " ended=" + (ended ? "yes" : "no") +
                 " sentPcm=" + SentPcm(manager) + " alive=" + manager.AliveDeviceCount);
        }

        // Reads the Kit list from the device and plays what it lists, or the event named at launch.
        private IEnumerator RunKit()
        {
            HapbeatManager manager = EnsureManager();
            yield return WaitForDevice(manager, "kit");
            if (manager.AliveDeviceCount <= 0)
            {
                Note("result=no-device scenario=kit");
                yield break;
            }

            var playlist = new List<HbDeviceKits.DeviceEvent>();
            if (_eventId.Length > 0)
            {
                for (int i = 0; i < RepeatsOfNamedEvent; i++)
                    playlist.Add(new HbDeviceKits.DeviceEvent { Id = _eventId, Playable = true });
            }
            else
            {
                yield return ReadKits(manager, "kit");
                if (_kitReadFailed)
                {
                    Note("result=error scenario=kit reason=kit-list-failed " + _kitStatus);
                    yield break;
                }
                foreach (HbDeviceKits.DeviceEvent listed in _events)
                    if (listed.Playable && playlist.Count < MaxScenarioEvents) playlist.Add(listed);
            }
            if (playlist.Count == 0)
            {
                Note("result=no-events scenario=kit listed=" + _events.Count);
                yield break;
            }

            foreach (HbDeviceKits.DeviceEvent item in playlist)
            {
                manager.Play(item.Id, _gain);
                Note("scenario=kit step=play event=" + item.Id + " gain=" + _gain);
                yield return new WaitForSecondsRealtime(2f);
                if (item.Loop) manager.Stop(item.Id);
            }
            Note("result=survived scenario=kit played=" + playlist.Count + " listed=" + _events.Count +
                 " mixerCreated=" + (SentPcm(manager) != -1L ? "yes" : "no") + " alive=" + manager.AliveDeviceCount);
        }

        // The list follows the devices, as in Studio: one that has not been asked yet brings a new read.
        private void ReadKitsOfNewDevices()
        {
            if (_readingKits || _deviceIp.Length > 0 || Time.realtimeSinceStartup < _nextDeviceCheck) return;
            _nextDeviceCheck = Time.realtimeSinceStartup + 1f;
            HapbeatManager manager = HapbeatManager.Instance;
            if (manager == null || manager.AliveDeviceCount <= 0) return;
            foreach (string ip in DeviceIps(manager))
            {
                if (_askedIps.Contains(ip)) continue;
                StartCoroutine(ReadKits(manager, "read-kits"));
                return;
            }
        }

        // The same kit_list query Studio sends through the Helper, on a worker thread so the screen keeps drawing.
        private IEnumerator ReadKits(HapbeatManager manager, string scenario)
        {
            List<string> ips = DeviceIps(manager);
            if (ips.Count == 0)
            {
                _kitReadFailed = true;
                _kitStatus = "Kits: no device to read from";
                Note("scenario=" + scenario + " step=kit-list no-device");
                yield break;
            }

            _readingKits = true;
            _askedIps.UnionWith(ips);
            _kitStatus = "Kits: reading from " + string.Join(", ", ips.ToArray());
            var found = new List<HbDeviceKits.DeviceEvent>();
            var errors = new List<string>();
            var worker = new Thread(() =>
            {
                foreach (string ip in ips)
                {
                    try { HbDeviceKits.AddEvents(found, HbDeviceKits.Ask(ip)); }
                    catch (Exception ex) { errors.Add(ip + " " + ex.GetType().Name + ": " + ex.Message); }
                }
            }) { Name = "HbCheckKitList", IsBackground = true };
            worker.Start();
            while (worker.IsAlive) yield return null;

            _readingKits = false;
            _events = found;
            // Another device that does not answer is no reason to hide the Kits of the one that did.
            _kitReadFailed = found.Count == 0 && errors.Count > 0;
            if (found.Count > 0)
                _kitStatus = "Kits on the device" + (errors.Count > 0 ? "  (" + errors.Count + " device did not answer)" : "");
            else
                _kitStatus = errors.Count > 0 ? "Kits: could not read " + string.Join("; ", errors.ToArray()) : "Kits: the device has none";
            Note("scenario=" + scenario + " step=kit-list devices=" + ips.Count + " events=" + found.Count + " errors=" + errors.Count);
            foreach (string error in errors) Note("kit-list error: " + error);
        }

        private List<string> DeviceIps(HapbeatManager manager)
        {
            var ips = new List<string>();
            if (IsFullAddress(_deviceIp))
            {
                ips.Add(_deviceIp);
                return ips;
            }
            foreach (HapbeatStreamEndpointInfo info in manager.GetStreamEndpoints())
            {
                string ip = info.Ip?.ToString();
                if (ip != null && !ips.Contains(ip)) ips.Add(ip);
            }
            return ips;
        }

        // IPAddress.TryParse also takes a half-typed "192.168".
        private static bool IsFullAddress(string text) => IPAddress.TryParse(text, out _) && text.Split('.').Length == 4;

        private void PlayDeviceEvent(HbDeviceKits.DeviceEvent item)
        {
            HapbeatManager manager = HapbeatManager.Instance;
            if (manager == null) return;
            manager.Play(item.Id, _gain);
            Note("kit play event=" + item.Id + " gain=" + _gain);
        }

        private void StopEverything()
        {
            HapbeatManager manager = HapbeatManager.Instance;
            if (manager == null) return;
            manager.StopStream();
            manager.StopAll();
            Note("stop all");
        }

        private IEnumerator WaitForDevice(HapbeatManager manager, string scenario)
        {
            float deadline = Time.realtimeSinceStartup + DeviceWaitSeconds;
            while (manager.AliveDeviceCount <= 0 && Time.realtimeSinceStartup < deadline)
            {
                Note("scenario=" + scenario + " tick waiting-for-device alive=" + manager.AliveDeviceCount);
                yield return new WaitForSecondsRealtime(1f);
            }
            Note("scenario=" + scenario + " step=device-wait-end alive=" + manager.AliveDeviceCount);
        }

        private HapbeatManager EnsureManager()
        {
            HapbeatManager manager = HapbeatManager.Instance;
            if (manager == null)
            {
                manager = gameObject.AddComponent<HapbeatManager>();
                manager.OnError += message => Note("sdk error: " + message);
            }
            _useRoute = IsFullAddress(_deviceIp);
            if (_deviceIp.Length > 0 && !_useRoute) Note("ignored a device IP that is not an address: " + _deviceIp);
            if (_useRoute)
            {
                _routeIp = _deviceIp;
                ApplyRoute(manager);
            }
            return manager;
        }

        private void ApplyRoute(HapbeatManager manager)
        {
            try
            {
                if (HbUnicastRoute.Apply(manager, _routeIp, DevicePort))
                    Note("route: discovery goes to " + _routeIp + ":" + DevicePort + " by unicast");
            }
            catch (Exception ex)
            {
                _useRoute = false;
                Note("result=error reason=route-reflection " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // An 80 Hz pulse of 200 ms, then silence up to the given length. 16 kHz mono.
        private static AudioClip MakePulse(float seconds)
        {
            const int rate = 16000;
            var samples = new float[Mathf.RoundToInt(rate * seconds)];
            int pulse = rate / 5;
            for (int i = 0; i < pulse; i++)
            {
                float fade = Mathf.Min(1f, Mathf.Min(i, pulse - i) / 160f);
                samples[i] = fade * Mathf.Sin(2f * Mathf.PI * 80f * i / rate);
            }
            AudioClip clip = AudioClip.Create("hbcheck-pulse", samples.Length, 1, rate, false);
            clip.SetData(samples, 0);
            return clip;
        }

        private static string Endpoints(HapbeatManager manager)
        {
            var parts = new List<string>();
            foreach (HapbeatStreamEndpointInfo info in manager.GetStreamEndpoints()) parts.Add(info.ToString());
            return parts.Count > 0 ? "[" + string.Join("; ", parts.ToArray()) + "]" : "[]";
        }

        // PCM bytes the mixer has sent, by reflection (internal to the SDK). -1 = no mixer was ever created.
        private static long SentPcm(HapbeatManager manager)
        {
            try
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                object mixer = typeof(HapbeatManager).GetField("_endpointStreamMixer", flags).GetValue(manager);
                if (mixer == null) return -1L;
                object diagnostics = mixer.GetType().GetProperty("Diagnostics", flags).GetValue(mixer, null);
                return (long)diagnostics.GetType().GetField("SentPcmBytes").GetValue(diagnostics);
            }
            catch (Exception)
            {
                return -2L;
            }
        }

        private static string Backend()
        {
#if ENABLE_IL2CPP
            return "IL2CPP";
#else
            return "Mono";
#endif
        }

        private static void OnUnityLog(string message, string stack, LogType type)
        {
            if (message.StartsWith("[Hapbeat]", StringComparison.Ordinal) || type == LogType.Exception || type == LogType.Error)
                Note("unity " + type + ": " + message);
        }

        public static void Note(string line)
        {
            string stamped = DateTime.UtcNow.ToString("HH:mm:ss.fff") + " " + line;
            lock (Gate)
            {
                Lines.Add(stamped);
                if (Lines.Count > 24) Lines.RemoveAt(0);
                try { File.AppendAllText(_logPath, stamped + "\n"); } catch (Exception) { }
            }
        }

        private static string Setting(string key, string fromFile, string fallback)
        {
            string value = Environment.GetEnvironmentVariable("HB_" + key);
            if (!string.IsNullOrEmpty(value)) return value;
            string prefix = "--hb-" + key.ToLowerInvariant() + "=";
            foreach (string arg in Environment.GetCommandLineArgs())
                if (arg.StartsWith(prefix, StringComparison.Ordinal)) return arg.Substring(prefix.Length);
            return string.IsNullOrEmpty(fromFile) ? fallback : fromFile;
        }

        private static float ParseFloat(string text, float fallback)
        {
            return float.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float value) ? value : fallback;
        }

        // Settings without environment variables: Data/Raw/hb-check.json in the Xcode project.
        private static FileSettings ReadFileSettings()
        {
            try
            {
                string path = Path.Combine(Application.streamingAssetsPath, "hb-check.json");
                if (File.Exists(path)) return JsonUtility.FromJson<FileSettings>(File.ReadAllText(path)) ?? new FileSettings();
            }
            catch (Exception) { }
            return new FileSettings();
        }

        private static string ReadBuildStamp()
        {
            try { return File.ReadAllText(Path.Combine(Application.streamingAssetsPath, "hb-check-build.txt")).Trim(); }
            catch (Exception) { return "unknown"; }
        }

        private static float UiScale() => Mathf.Max(1f, Screen.width / 640f);

        // The list follows the finger. Once a touch is a drag its release must not press the button under it.
        private void TrackTouchScroll()
        {
            if (Input.touchCount == 0) return;
            Touch touch = Input.GetTouch(0);
            if (touch.phase == TouchPhase.Began)
            {
                _touchTravel = 0f;
                _touchDragging = false;
            }
            else if (touch.phase == TouchPhase.Moved)
            {
                _touchTravel += Mathf.Abs(touch.deltaPosition.y);
                if (_touchTravel > DragThresholdPixels) _touchDragging = true;
                if (_touchDragging) _kitScroll.y += touch.deltaPosition.y / UiScale();
            }
        }

        private void OnGUI()
        {
            // Layout and the event after it must see the same controls, and a worker thread can log at any time.
            if (Event.current.type == EventType.Layout)
            {
                _visibleState = ReadStateLine();
                lock (Gate) _visibleLines = Lines.ToArray();
            }

            float scale = UiScale();
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            float width = Screen.width / scale - 32f;
            float height = Screen.height / scale - 64f;
            GUILayout.BeginArea(new Rect(16f, 48f, width, height));
            GUILayout.Label("Hapbeat check  " + ReadBuildStamp() + "  gain=" + _gain);
            GUILayout.Label(_visibleState);
            GUILayout.Label("Device IP (empty = find it automatically)");
            _deviceIp = GUILayout.TextField(_deviceIp, GUILayout.Height(44f));

            GUILayout.BeginHorizontal();
            GUI.enabled = !_running;
            if (GUILayout.Button("Clip (once)", GUILayout.Height(72f))) StartCoroutine(Run("clip-once"));
            GUI.enabled = !_readingKits;
            if (GUILayout.Button("Reload kits", GUILayout.Height(72f))) StartCoroutine(ReadKits(EnsureManager(), "read-kits"));
            GUI.enabled = true;
            if (GUILayout.Button("Stop", GUILayout.Height(72f))) StopEverything();
            GUILayout.EndHorizontal();

            GUILayout.Label(_kitStatus);
            _kitScroll = GUILayout.BeginScrollView(_kitScroll, GUILayout.Height(height * 0.4f));
            string kit = null;
            foreach (HbDeviceKits.DeviceEvent item in _events)
            {
                if (item.KitId != kit)
                {
                    kit = item.KitId;
                    GUILayout.Label(kit);
                }
                GUI.enabled = item.Playable && !_running && !_touchDragging;
                if (GUILayout.Button(item.Label, GUILayout.Height(64f))) PlayDeviceEvent(item);
            }
            GUI.enabled = true;
            GUILayout.EndScrollView();

            for (int i = _visibleLines.Length - 1; i >= 0; i--) GUILayout.Label(_visibleLines[i]);
            GUILayout.EndArea();
        }

        private string ReadStateLine()
        {
            HapbeatManager manager = HapbeatManager.Instance;
            if (manager == null) return "(SDK not started)";
            int alive = manager.AliveDeviceCount;
            if (alive <= 0) return "No device yet. Same Wi-Fi as the iPhone? Local Network allowed in Settings?";
            return "Devices: " + alive + "  " + string.Join(", ", DeviceIps(manager).ToArray());
        }

        private void OnDestroy()
        {
            Application.logMessageReceivedThreaded -= OnUnityLog;
        }
    }

    /// <summary>
    /// Asks a device which Kits it has, over the TCP config port: the query Studio's kit_list goes through
    /// the Helper as. Reads only. The SDK has no call for this, so the app talks to the port directly.
    /// </summary>
    public static class HbDeviceKits
    {
        private const int ConfigPort = 7701;
        private const int ConnectTimeoutMs = 1500;
        private const int ReplyTimeoutMs = 5000;
        // The firmware has one client slot and frees a stuck one after 5 s.
        private const int RetryDelayMs = 6000;
        private const int MaxReplyBytes = 256 * 1024;
        private const string StreamMode = "stream_clip";

        /// <summary>One row of the list. Stream-only events cannot be fired with Play, as in Studio.</summary>
        public sealed class DeviceEvent
        {
            public string KitId;
            public string Id;
            public string Label;
            public bool Playable;
            public bool Loop;
        }

        [Serializable]
        public class Reply
        {
            public string status;
            public Kit[] kits;
        }

        [Serializable]
        public class Kit
        {
            public string kit_id;
            public string version;
            public Row[] events;
        }

        [Serializable]
        public class Row
        {
            public string name;
            public string mode;
            public bool loop;
        }

        /// <summary>
        /// Returns the device's reply line. A device that connects but stays silent gets one retry, once its
        /// client slot is free again; one that does not connect at all throws at once.
        /// </summary>
        public static string Ask(string ip)
        {
            IPAddress address = IPAddress.Parse(ip);
            using (Socket socket = Connect(address))
            {
                try
                {
                    return ReadList(socket);
                }
                catch (Exception ex) when (ex is SocketException || ex is IOException)
                {
                    HbCheckApp.Note("kit-list retry " + ip + ": " + ex.Message);
                }
            }
            Thread.Sleep(RetryDelayMs);
            using (Socket socket = Connect(address)) return ReadList(socket);
        }

        private static Socket Connect(IPAddress address)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                IAsyncResult connecting = socket.BeginConnect(address, ConfigPort, null, null);
                if (!connecting.AsyncWaitHandle.WaitOne(ConnectTimeoutMs)) throw new TimeoutException("connect timed out");
                socket.EndConnect(connecting);
            }
            catch (Exception)
            {
                socket.Dispose();
                throw;
            }
            // Reset on close: a half-closed socket would keep the firmware's client slot busy.
            socket.LingerState = new LingerOption(true, 0);
            socket.ReceiveTimeout = ReplyTimeoutMs;
            socket.SendTimeout = ReplyTimeoutMs;
            return socket;
        }

        private static string ReadList(Socket socket)
        {
            socket.Send(Encoding.ASCII.GetBytes("{\"cmd\":\"kit_list\"}\n"));
            var reply = new MemoryStream();
            var buffer = new byte[4096];
            while (reply.Length < MaxReplyBytes)
            {
                int count = socket.Receive(buffer);
                if (count <= 0) throw new IOException("the device closed the connection before the reply ended");
                int newline = Array.IndexOf(buffer, (byte)'\n', 0, count);
                reply.Write(buffer, 0, newline >= 0 ? newline : count);
                if (newline >= 0) return Encoding.UTF8.GetString(reply.ToArray());
            }
            throw new IOException("the reply is too long");
        }

        /// <summary>Adds the reply's events to the list. An event two devices both have stays one row.</summary>
        public static void AddEvents(List<DeviceEvent> list, string replyLine)
        {
            Reply reply = JsonUtility.FromJson<Reply>(replyLine);
            if (reply == null || reply.status != "ok") throw new InvalidDataException("unexpected reply: " + replyLine);
            if (reply.kits == null) return;
            foreach (Kit kit in reply.kits)
            {
                if (kit.events == null) continue;
                foreach (Row row in kit.events)
                {
                    if (string.IsNullOrEmpty(row.name) || list.Exists(known => known.Id == row.name)) continue;
                    bool playable = row.mode != StreamMode;
                    string prefix = kit.kit_id + ".";
                    string shortName = row.name.StartsWith(prefix, StringComparison.Ordinal) ? row.name.Substring(prefix.Length) : row.name;
                    list.Add(new DeviceEvent
                    {
                        KitId = kit.kit_id,
                        Id = row.name,
                        Label = playable ? shortName : shortName + " (stream only)",
                        Playable = playable,
                        Loop = row.loop,
                    });
                }
            }
        }
    }

    /// <summary>
    /// Sends the SDK's discovery PING to one IP instead of broadcast: the Device IP field, for a network or a
    /// signing where broadcast does not get through. By reflection, so this app needs nothing added to the
    /// SDK. The route is never picked by the SDK's subnet lock (IsLimited), which would send later PINGs back
    /// to a broadcast address.
    /// </summary>
    public static class HbUnicastRoute
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        /// <summary>Returns true when the route list had to be changed.</summary>
        public static bool Apply(HapbeatManager manager, string ip, int port)
        {
            object client = typeof(HapbeatManager).GetField("_client", Flags).GetValue(manager);
            if (client == null) return false;
            FieldInfo routesField = typeof(HapbeatClient).GetField("_broadcastRoutes", Flags);
            var routes = (IList)routesField.GetValue(client);
            Type routeType = RouteType();
            FieldInfo endPoint = routeType.GetField("EndPoint", Flags);
            IPAddress address = IPAddress.Parse(ip);
            if (routes.Count == 1 && Equals(((IPEndPoint)endPoint.GetValue(routes[0]))?.Address, address)) return false;

            object route = Activator.CreateInstance(routeType, true);
            endPoint.SetValue(route, new IPEndPoint(address, port));
            routeType.GetField("IsLimited", Flags).SetValue(route, true);
            routes.Clear();
            routes.Add(route);
            typeof(HapbeatClient).GetField("_lockedRoute", Flags).SetValue(client, null);
            return true;
        }

        /// <summary>Throws when a field or type the route relies on is missing.</summary>
        public static void Validate()
        {
            Require(typeof(HapbeatManager).GetField("_client", Flags), "HapbeatManager._client");
            Require(typeof(HapbeatManager).GetField("_endpointStreamMixer", Flags), "HapbeatManager._endpointStreamMixer");
            Require(typeof(HapbeatClient).GetField("_broadcastRoutes", Flags), "HapbeatClient._broadcastRoutes");
            Require(typeof(HapbeatClient).GetField("_lockedRoute", Flags), "HapbeatClient._lockedRoute");
            Type routeType = RouteType();
            Require(routeType, "HapbeatClient.BroadcastRoute");
            Require(routeType.GetField("EndPoint", Flags), "BroadcastRoute.EndPoint");
            Require(routeType.GetField("IsLimited", Flags), "BroadcastRoute.IsLimited");
        }

        private static Type RouteType() => typeof(HapbeatClient).GetNestedType("BroadcastRoute", BindingFlags.NonPublic);

        private static void Require(object member, string name)
        {
            if (member == null) throw new MissingMemberException(name);
        }
    }
}
