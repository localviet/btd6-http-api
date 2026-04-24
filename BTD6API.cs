using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BTD_Mod_Helper;
using BTD_Mod_Helper.Api.Helpers;
using BTD_Mod_Helper.Extensions;
using Il2CppAssets.Scripts;
using Il2CppAssets.Scripts.Simulation;
using Il2CppAssets.Scripts.Unity.UI_New.InGame;
using MelonLoader;
using Newtonsoft.Json;
using HarmonyLib;
using Il2CppAssets.Scripts.Simulation.Towers;
using Il2CppAssets.Scripts.Unity.Bridge;
using Il2CppAssets.Scripts.Simulation.Bloons;
using BTD_Mod_Helper.Api.ModOptions;
using Il2CppAssets.Scripts.Utils;
using Il2CppAssets.Scripts.Unity;
using Il2CppAssets.Scripts.Unity.Player;
using Il2CppAssets.Scripts.Unity.Map;

[assembly: MelonInfo(typeof(BTD6API.Main), "BTD6 HTTP API", "2.0.0", "Askryne")]
[assembly: MelonGame("Ninja Kiwi", "BloonsTD6")]

namespace BTD6API
{
    public class Main : BloonsTD6Mod
    {
        public static ModSettingInt ServerPort = new(5001)
        {
            displayName = "HTTP Server Port",
            description = "The port the HTTP server listens on. (Changes take effect immediately.)",
            min = 1024,
            max = 65535
        };

        private HttpListener _listener;
        private Thread _thread;
        private CancellationTokenSource _sseDispatcherCts;
        private static readonly List<(HttpListenerResponse res, CancellationTokenSource cts)> _sseClients = new();
        private static readonly object _sseLock = new object();
        private static readonly ConcurrentQueue<(string type, string data)> _sseEventQueue =
            new ConcurrentQueue<(string, string)>();
        private volatile bool _roundIsRunning = false;
        private static readonly Dictionary<(int towerId, string abilityName), bool> _abilityReadyStates = new();

        private class MainThreadWork
        {
            public readonly Func<string> Execute;
            public readonly TaskCompletionSource<string> Tcs = new TaskCompletionSource<string>();
            public readonly DateTime Deadline;
            public MainThreadWork(Func<string> execute)
            {
                Execute = execute;
                Deadline = DateTime.UtcNow.AddSeconds(11);
            }
        }
        private static readonly ConcurrentQueue<MainThreadWork> _mainThreadQueue =
            new ConcurrentQueue<MainThreadWork>();

        private static string RunOnMainThread(Func<string> work)
        {
            var item = new MainThreadWork(work);
            _mainThreadQueue.Enqueue(item);
            if (!item.Tcs.Task.Wait(TimeSpan.FromSeconds(10)))
                return JsonConvert.SerializeObject(new { error = "timed out waiting for main thread" });
            return item.Tcs.Task.Result;
        }

        public override void OnApplicationStart()
        {
            ServerPort.onSave = _ => Serve();
            Serve();
        }

        private void Serve()
        {
            _listener?.Stop();
            _sseDispatcherCts?.Cancel();

            int port = ServerPort;
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{port}/");
            _listener.Start();

            var listener = _listener;
            _thread = new Thread(() => Listen(listener)) { IsBackground = true };
            _thread.Start();

            _sseDispatcherCts = new CancellationTokenSource();
            var token = _sseDispatcherCts.Token;
            new Thread(() => SseDispatcher(token)) { IsBackground = true }.Start();

            MelonLogger.Msg($"Listening on http://localhost:{port}/");
        }

        private void Listen(HttpListener listener)
        {
            while (listener.IsListening)
            {
                try
                {
                    var ctx = listener.GetContext();
                    if (ctx.Request.Url.AbsolutePath == "/events")
                    {
                        new Thread(() => Handle(ctx)) { IsBackground = true }.Start();
                    }
                    else
                    {
                        ThreadPool.QueueUserWorkItem(_ => Handle(ctx));
                    }
                }
                catch { /* Listener stopped. */ }
            }
        }

        private void Handle(HttpListenerContext ctx)
        {
            var res = ctx.Response;
            try
            {
                var req = ctx.Request;
                res.ContentType = "application/json";
                res.Headers.Add("Access-Control-Allow-Origin", "*");
                var path = req.Url.AbsolutePath;

                if (path == "/events" && req.HttpMethod == "GET")
                {
                    res.ContentType = "text/event-stream";
                    res.Headers.Add("Cache-Control", "no-cache");
                    res.Headers.Add("X-Accel-Buffering", "no");
                    res.SendChunked = true;

                    var cts = new CancellationTokenSource();

                    lock (_sseLock)
                        _sseClients.Add((res, cts));

                    cts.Token.WaitHandle.WaitOne();

                    lock (_sseLock)
                        _sseClients.RemoveAll(c => c.cts == cts);

                    try { res.Abort(); } catch { }
                    return;
                }

                string body;
                if (path == "/match" && req.HttpMethod == "GET")
                {
                    body = RunOnMainThread(GetMatchStateJson);
                }
                else if (path == "/player" && req.HttpMethod == "GET")
                {
                    body = RunOnMainThread(GetPlayerJson);
                }
                else if (path == "/action" && req.HttpMethod == "POST")
                {
                    var json = new StreamReader(req.InputStream).ReadToEnd();
                    body = RunOnMainThread(() => HandleAction(json));
                }
                else if (path == "/health" && req.HttpMethod == "GET")
                {
                    body = JsonConvert.SerializeObject(new { ok = true });
                }
                else
                {
                    res.StatusCode = 404;
                    body = JsonConvert.SerializeObject(new { error = "not found" });
                }

                res.KeepAlive = false;
                var bytes = Encoding.UTF8.GetBytes(body);
                try
                {
                    res.ContentLength64 = bytes.Length;
                    res.OutputStream.Write(bytes, 0, bytes.Length);
                    res.Close();
                }
                catch
                {
                    try { res.Abort(); } catch { }
                }
            }
            catch
            {
                try { res.Abort(); } catch { }
            }
        }

        public static void BroadcastEvent(string eventType, string data = "{}")
        {
            _sseEventQueue.Enqueue((eventType, data));
        }

        private static void SseDispatcher(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!_sseEventQueue.TryDequeue(out var ev))
                {
                    Thread.Sleep(1);
                    continue;
                }

                byte[] bytes = Encoding.UTF8.GetBytes($"event: {ev.type}\ndata: {ev.data}\n\n");

                List<(HttpListenerResponse, CancellationTokenSource)> snapshot;
                lock (_sseLock)
                    snapshot = _sseClients.ToList();

                var dead = new List<(HttpListenerResponse, CancellationTokenSource)>();
                foreach (var (client, cts) in snapshot)
                {
                    var t = Task.Run(() => { client.OutputStream.Write(bytes, 0, bytes.Length); client.OutputStream.Flush(); }, cancellationToken);
                    bool ok;
                    try { ok = t.Wait(TimeSpan.FromSeconds(3)) && !t.IsFaulted; }
                    catch { ok = false; }
                    if (!ok) { cts.Cancel(); dead.Add((client, cts)); }
                }

                if (dead.Count > 0)
                    lock (_sseLock)
                        foreach (var d in dead) _sseClients.Remove(d);
            }
        }

        public override void OnMatchStart()
        {
            _abilityReadyStates.Clear();
            BroadcastEvent("matchStart");
            base.OnMatchStart();
        }
        public override void OnMainMenu()
        {
            BroadcastEvent("mainMenu");
            base.OnMainMenu();
        }
        public override void OnRoundStart()
        {
            _roundIsRunning = true;
            BroadcastEvent("roundStart");
        }
        public override void OnRoundEnd()
        {
            _roundIsRunning = false;
            BroadcastEvent("roundEnd");
        }
        public override void OnVictory()
        {
            BroadcastEvent("victory");
            base.OnVictory();
        }
        public override void OnDefeat()
        {
            BroadcastEvent("defeat");
            base.OnDefeat();
        }
        [HarmonyPatch(typeof(Btd6Player), nameof(Btd6Player.RankUp))]
        public class RankUpPatch
        {
            [HarmonyPostfix]
            public static void Postfix()
            {
                BroadcastEvent("rankUp");
            }
        }
        [HarmonyPatch(typeof(UnityToSimulation), nameof(UnityToSimulation.Restart))]
        public class RestartPatch
        {
            [HarmonyPostfix]
            public static void Postfix()
            {
                BroadcastEvent("restart");
            }
        }
        [HarmonyPatch(typeof(Tower), nameof(Tower.Initialise))]
        public class TowerPlacedPatch
        {
            [HarmonyPostfix]
            public static void Postfix(Tower __instance)
            {
                BroadcastEvent("towerPlaced", JsonConvert.SerializeObject(new { id = __instance.Id.Id }));
            }
        }
        [HarmonyPatch(typeof(Tower), nameof(Tower.OnUpgrade))]
        public class TowerUpgradedPatch
        {
            [HarmonyPostfix]
            public static void Postfix(Tower __instance)
            {
                if (InGame.instance != null && __instance.model.name.Contains(InGame.instance.SelectedHero))
                {
                    BroadcastEvent("heroLeveledUp", JsonConvert.SerializeObject(new { id = __instance.Id.Id }));
                }
                else
                {
                    BroadcastEvent("towerUpgraded", JsonConvert.SerializeObject(new { id = __instance.Id.Id }));
                }
            }
        }
        [HarmonyPatch(typeof(Tower), nameof(Tower.OnSold))]
        public class TowerSoldPatch
        {
            [HarmonyPrefix]
            public static void Prefix(Tower __instance, out Dictionary<string, int> __state)
            {
                __state = new Dictionary<string, int>
                {
                    ["id"] = __instance.Id.Id,
                    ["value"] = (int)__instance.SaleValue
                };
            }
            [HarmonyPostfix]
            public static void Postfix(Dictionary<string, int> __state)
            {
                BroadcastEvent("towerSold", JsonConvert.SerializeObject(new { id = __state["id"], value = __state["value"] }));
            }
        }
        [HarmonyPatch(typeof(AbilityToSimulation), nameof(AbilityToSimulation.Activate))]
        public class AbilityActivatedPatch
        {
            [HarmonyPostfix]
            public static void Postfix(AbilityToSimulation __instance)
            {
                BroadcastEvent(
                    "abilityActivated",
                    JsonConvert.SerializeObject(new { towerId = __instance.tower.Id, abilityName = __instance.model.displayName })
                );
            }
        }
        [HarmonyPatch(typeof(Bloon), nameof(Bloon.Leaked))]
        public class BloonLeakPatch
        {
            [HarmonyPrefix]
            public static bool Prefix(Bloon __instance, out Dictionary<string, string> __state)
            {
                __state = new Dictionary<string, string>();
                __state["type"] = __instance.model.name;
                __state["health"] = InGame.instance.GetHealth().ToString();
                return true;
            }
            [HarmonyPostfix]
            public static void Postfix(Dictionary<string, string> __state)
            {
                if (InGame.instance == null) return;
                var dmg = double.Parse(__state["health"]) - InGame.instance.GetHealth();
                BroadcastEvent("bloonLeak", JsonConvert.SerializeObject(new { type = __state["type"], damage = dmg }));
            }
        }

        public override void OnUpdate()
        {
            while (_mainThreadQueue.TryDequeue(out var item))
            {
                // Skip items that already timed out on the HTTP side:
                if (DateTime.UtcNow > item.Deadline)
                {
                    item.Tcs.TrySetResult(JsonConvert.SerializeObject(new { error = "timed out waiting for main thread" }));
                    continue;
                }
                string result;
                try { result = item.Execute(); }
                catch (Exception e) { result = JsonConvert.SerializeObject(new { error = e.Message }); }
                item.Tcs.TrySetResult(result);
            }

            var inGame = InGame.instance;
            if (inGame?.bridge != null)
            {
                foreach (var ability in inGame.bridge.GetAllAbilities(false).ToArray())
                {
                    var key = (ability.tower.Id, ability.model.displayName);
                    bool isNowReady = ability.IsReady;

                    if (_abilityReadyStates.TryGetValue(key, out bool wasReady)
                        && isNowReady && !wasReady)
                    {
                        BroadcastEvent("abilityReady", JsonConvert.SerializeObject(new
                        {
                            towerId = ability.tower.Id,
                            abilityName = ability.model.displayName
                        }));
                    }

                    _abilityReadyStates[key] = isNowReady;
                }
            }

            base.OnUpdate();
        }

        private string GetMatchStateJson()
        {
            try
            {
                var inGame = InGame.instance;
                if (inGame == null) return JsonConvert.SerializeObject(new { status = "notInMatch" });
                var sim = inGame.GetSimulation();

                // Meta:
                var map = inGame.SelectedMap;
                var difficulty = inGame.SelectedDifficulty.ToString();
                var gameMode = inGame.SelectedMode.ToString();
                var gameType = inGame.GameType.ToString();
                var isPaused = TimeManager.gamePaused;
                var isFastForwarding = TimeManager.FastForwardActive;
                var isAutostartEnabled = sim.autoPlay;

                // General:
                var cash = inGame.GetCash();
                var cashSpent = sim.cashSpent.Value;
                var lives = inGame.GetHealth();
                var maxLives = inGame.GetMaxHealth();
                var round = sim.GetCurrentRound();
                var endRound = sim.GetEndRound();

                // Towers:
                var towers = new List<object>();
                var allAbilities = inGame.bridge.GetAllAbilities(false).ToArray();
                foreach (var tower in inGame.GetTowers())
                {
                    var model = tower.towerModel;

                    var abilities = new List<object>();
                    var towerAbilities = allAbilities.Where(ab => ab.tower == tower.Id).ToList();
                    foreach (var ability in towerAbilities)
                    {
                        abilities.Add(
                        new
                        {
                            name = ability.model.displayName,
                            isReady = ability.IsReady,
                            ability.model.cooldown,
                            cooldownRemaining = ability.CooldownRemaining,
                            isActive = ability.IsActive,
                            additionalCharges = ability.CurrentAdditionalCharges,
                            isPassive = ability.IsPassive,
                            ability.model.activateOnLeak,
                            ability.model.activateOnPreLeak,
                            ability.model.activateOnLivesLost,
                            ability.model.activateOnRoundEnd
                        });
                    }

                    towers.Add(new
                    {
                        id = tower.Id.Id,
                        type = model?.name ?? "unknown",
                        upgrades = model?.tiers,
                        pos = new { x = tower.Position.X, y = tower.Position.Y },
                        roundPlaced = tower.createdOnRound - 1,
                        sellValue = tower.SaleValue,
                        cashEarned = tower?.cashEarned ?? 0,
                        livesGained = tower?.livesGained ?? 0,
                        isHero = tower?.towerModel?.IsHero() ?? false,
                        targetingMode = tower?.TargetType.id ?? "unknown",
                        canTargetCamo = tower?.canTargetCamo ?? false,
                        canUpgradeToParagon = tower?.CanUpgradeToParagon() ?? false,
                        isParagon = model?.isParagon ?? false,
                        abilities,
                        pops = tower?.pops ?? 0,
                        damage = tower?.damageDealt ?? 0,
                        necroBloonsReanimated = tower?.necroBloonsReanimated ?? 0
                    });
                }
                var selectedHero = inGame.SelectedHero;
                var availableTowers = Helpers.ValidBaseTowerNames().ToList().Where(t =>
                    inGame.GetTowerInventory().HasInventory(inGame.GetGameModel().GetTowerModel(t))
                    && GameExt.GetBtd6Player(Game.instance).HasUnlockedTower(t)
                );

                return JsonConvert.SerializeObject(new
                {
                    status = "inMatch",
                    map,
                    difficulty,
                    gameMode,
                    gameType,
                    isPaused,
                    isFastForwarding,
                    isAutostartEnabled,
                    cash,
                    cashSpent,
                    lives,
                    maxLives,
                    round,
                    endRound,
                    roundIsRunning = _roundIsRunning,
                    towers,
                    selectedHero,
                    availableTowers
                });
            }
            catch (Exception e)
            {
                return JsonConvert.SerializeObject(new { error = e.Message });
            }
        }

        private static string GetPlayerJson()
        {
            var btd6Player = GameExt.GetBtd6Player(Game.instance);
            var playerProfile = GameExt.GetPlayerProfile(Game.instance);

            var acquiredKnowledge = new List<string>();
            foreach (var mk in playerProfile.acquiredKnowledge)
                acquiredKnowledge.Add(mk);

            return JsonConvert.SerializeObject(new
            {
                rank = playerProfile.rank.ValueInt,
                veteranRank = playerProfile.veteranRank.ValueInt,
                monkeyMoney = btd6Player.GetMonkeyMoney(),
                knowledgePoints = playerProfile.KnowledgePoints,
                acquiredKnowledge,
                isKnowledgeEnabled = !playerProfile.knowledgeDisabled
            });
        }

        private static string HandleAction(string json)
        {
            try
            {
                dynamic action = JsonConvert.DeserializeObject(json) ?? new { };
                string type = action?.type ?? "";
                var inGame = InGame.instance;
                if (inGame == null)
                    return JsonConvert.SerializeObject(new { error = "not in match" });
                var sim = inGame.GetUnityToSimulation();

                switch (type)
                {
                    // Time:
                    case "setFastForward":
                        sim.SetFastForward((bool)action.enabled);
                        return JsonConvert.SerializeObject(new { ok = true });
                    case "setFastForwardSpeed":
                        TimeHelper.OverrideFastForwardTimeScale = (double)action.speed;
                        return JsonConvert.SerializeObject(new { ok = true });
                    case "setAutostart":
                        sim.SetAutoPlay((bool)action.enabled);
                        return JsonConvert.SerializeObject(new { ok = true });
                    case "setPaused":
                        TimeManager.gamePaused = (bool)action.paused;
                        return JsonConvert.SerializeObject(new { ok = true });

                    // Game flow:
                    case "startRound":
                        sim.StartRound();
                        return JsonConvert.SerializeObject(new { ok = true });
                    case "setRound":
                        sim.SetRound((int)action.round);
                        return JsonConvert.SerializeObject(new { ok = true });
                    case "restart":
                        inGame.bridge.Restart();
                        return JsonConvert.SerializeObject(new { ok = true });

                    // Economy:
                    case "setCash":
                        inGame.SetCash((double)action.value);
                        return JsonConvert.SerializeObject(new { ok = true });
                    case "addCash":
                        inGame.AddCash((double)action.value);
                        return JsonConvert.SerializeObject(new { ok = true });
                    case "setLives":
                        inGame.SetHealth((double)action.value);
                        return JsonConvert.SerializeObject(new { ok = true });
                    case "addLives":
                        inGame.AddHealth((double)action.value);
                        return JsonConvert.SerializeObject(new { ok = true });

                    // Towers:
                    case "placeTower":
                        var allModels = InGame.instance.GetGameModel().GetTowerModels((string)action.towerId);
                        var model = allModels.FirstOrDefault(m => m.name == (string)action.towerId);
                        // GetTowerModel doesn't always find the right model,
                        // so we use GetTowerModels with FirstOrDefault.
                        if (model == null)
                            return JsonConvert.SerializeObject(new { error = "tower not found" });
                        sim.CreateTowerAt(
                            new UnityEngine.Vector2((float)action.x, (float)action.y),
                            model,
                            ObjectId.FromData(0),
                            (bool)action.free,
                            null
                        );
                        return JsonConvert.SerializeObject(new { ok = true });
                    case "upgradeTower":
                        sim.UpgradeTower(ObjectId.FromData((uint)action.id), (int)action.path, 0d, null);
                        return JsonConvert.SerializeObject(new { ok = true });
                    case "sellTower":
                        sim.SellTower(ObjectId.FromData((uint)action.id));
                        return JsonConvert.SerializeObject(new { ok = true });
                    case "changeTargetPriority":
                        sim.SetNextTowerTargetType(ObjectId.FromData((uint)action.id), (bool)action.prev);
                        return JsonConvert.SerializeObject(new { ok = true });
                    case "activateAbility":
                        var targetAbility = inGame.bridge.GetAllAbilities(false).ToArray()
                            .FirstOrDefault(ab =>
                                ab.tower == sim.GetTower(ObjectId.FromData((uint)action.id)).Id &&
                                ab.model.displayName == (string)action.abilityName);
                        if (targetAbility == null)
                            return JsonConvert.SerializeObject(new { error = "ability not found" });
                        targetAbility.Activate();
                        return JsonConvert.SerializeObject(new { ok = true });
                    case "activateAllAbilities":
                        var abilities = inGame.bridge.GetAllAbilities(false).ToList();
                        foreach (var ab in abilities)
                            ab.Activate();
                        return JsonConvert.SerializeObject(new { ok = true });

                    default:
                        return JsonConvert.SerializeObject(new { error = $"unknown action: {type}" });
                }
            }
            catch (Exception e)
            {
                return JsonConvert.SerializeObject(new { error = e.Message });
            }
        }

        public override void OnApplicationQuit()
        {
            _sseDispatcherCts?.Cancel();
            _listener?.Stop();
        }
    }
}