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
using Il2CppAssets.Scripts.Models.Towers;
using Il2CppAssets.Scripts.Data;
using Il2CppAssets.Scripts.Models.Rounds;
using Il2CppAssets.Scripts.Models;
using Il2CppAssets.Scripts.Simulation.Track;
using Il2CppAssets.Scripts.Simulation.SMath;

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
        private static double livesState = new();

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
                else if (path == "/match/bloons" && req.HttpMethod == "GET")
                {
                    body = RunOnMainThread(GetMatchBloonsJson);
                }
                else if (path == "/match/rounds" && req.HttpMethod == "GET")
                {
                    if (int.TryParse(req.QueryString["i"], out int i))
                        body = RunOnMainThread(() => GetMatchRoundsJson(i));
                    else
                        body = RunOnMainThread(() => GetMatchRoundsJson());
                }
                else if (path == "/match/can-place-tower-at" && req.HttpMethod == "GET")
                {
                    if (float.TryParse(req.QueryString["x"], out float x)
                        && float.TryParse(req.QueryString["y"], out float y)
                        && req.QueryString["towerId"] != null)
                    {
                        var towerId = req.QueryString["towerId"];
                        body = RunOnMainThread(() => CanPlaceTowerAtJson(x, y, towerId));
                    }
                    else
                    {
                        body = JsonConvert.SerializeObject(new { error = "invalid parameters" });
                    }
                }
                else if (path == "/match/towers-placeable-at" && req.HttpMethod == "GET")
                {
                    if (float.TryParse(req.QueryString["x"], out float x)
                        && float.TryParse(req.QueryString["y"], out float y))
                    {
                        body = RunOnMainThread(() => TowersPlaceableAtJson(x, y));
                    }
                    else
                    {
                        body = JsonConvert.SerializeObject(new { error = "invalid parameters" });
                    }
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
                else if (path == "/data/maps" && req.HttpMethod == "GET")
                {
                    body = GetMapsJson();
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

        public static void BroadcastEvent(string eventType, object? data = null)
        {
            data ??= new { };
            var stringData = JsonConvert.SerializeObject(data);
            _sseEventQueue.Enqueue((eventType, stringData));
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

        private static TowerModel? GetTowerModel(string towerId)
        {
            // GetTowerModel sometimes makes mistakes (e.g., "Quincy" -> "RareQuincyActionFigure"),
            // so we generally use GetTowerModels and then filter.
            var allModels = Game.instance.model.GetTowerModels(towerId);
            var model = allModels.FirstOrDefault(m => m.name == towerId);
            return model;
        }
        private static void StartMatch(string map, string difficulty, string gamemode)
        {
            InGameData.Editable.selectedMap = map;
            InGameData.Editable.selectedDifficulty = difficulty;
            InGameData.Editable.selectedMode = gamemode;
            Il2CppAssets.Scripts.Unity.UI_New.UI.instance.LoadGame();
        }
        private static double GetRoundCash(RoundModel round, int roundNumber, GameModel gameModel)
        {
            double cash = 100 + roundNumber;
            double multiplier = roundNumber switch
            {
                <= 50 => 1f,
                <= 60 => 0.5f,
                <= 85 => 0.2f,
                <= 100 => 0.1f,
                _ => 0.02f
            };
            foreach (var g in round.groups)
                cash += g.GetBloonModel().GetTotalLayers(gameModel) * g.count * multiplier;
            return System.Math.Round(cash, 3);
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
            base.OnRoundStart();
        }
        public override void OnRoundEnd()
        {
            _roundIsRunning = false;
            BroadcastEvent("roundEnd");
            base.OnRoundEnd();
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
                BroadcastEvent("towerPlaced", new { id = __instance.Id.Id, type = __instance.model.name });
            }
        }
        [HarmonyPatch(typeof(Tower), nameof(Tower.OnUpgrade))]
        public class TowerUpgradedPatch
        {
            [HarmonyPostfix]
            public static void Postfix(Tower __instance)
            {
                var et = (InGame.instance != null && __instance.model.name.Contains(InGame.instance.SelectedHero))
                          ? "heroLeveledUp"
                          : "towerUpgraded";
                BroadcastEvent(et, new { id = __instance.Id.Id });
            }
        }
        [HarmonyPatch(typeof(Tower), nameof(Tower.OnSold))]
        public class TowerSoldPatch
        {
            [HarmonyPrefix]
            public static bool Prefix(Tower __instance, out Dictionary<string, int> __state)
            {
                __state = new Dictionary<string, int>
                {
                    ["id"] = __instance.Id.Id,
                    ["value"] = (int)__instance.SaleValue
                };
                return true;
            }
            [HarmonyPostfix]
            public static void Postfix(Dictionary<string, int> __state)
            {
                BroadcastEvent("towerSold", new { id = __state["id"], value = __state["value"] });
            }
        }
        [HarmonyPatch(typeof(AbilityToSimulation), nameof(AbilityToSimulation.Activate))]
        public class AbilityActivatedPatch
        {
            [HarmonyPrefix]
            public static bool Prefix(AbilityToSimulation __instance, out bool __state)
            {
                __state = __instance.IsReady;
                return true;
            }

            [HarmonyPostfix]
            public static void Postfix(AbilityToSimulation __instance, bool __state)
            {
                // Only broadcast if the ability was actually activated:
                if (__state && !__instance.IsReady)
                {
                    BroadcastEvent(
                        "abilityActivated",
                        new { towerId = __instance.tower.Id, abilityName = __instance.model.displayName }
                    );
                }
            }
        }
        public override void OnRemoveableDestroyed(Removeable removeable)
        {
            BroadcastEvent("removableSold", new { id = removeable.Id.Id });
            base.OnRemoveableDestroyed(removeable);
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
                BroadcastEvent("bloonLeak", new { type = __state["type"], damage = dmg });
            }
        }
        [HarmonyPatch(typeof(InGame), nameof(InGame.CashChanged))]
        public class CachChangedPatch
        {
            [HarmonyPostfix]
            public static void Postfix(InGame __instance)
            {
                var cash = __instance.GetCash();
                BroadcastEvent("cashChanged", new { cash });
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
                catch (Exception e) { result = JsonConvert.SerializeObject(new { error = e.Message + "\n\n" + e.StackTrace }); }
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
                        BroadcastEvent("abilityReady", new { towerId = ability.tower.Id, abilityName = ability.model.displayName });
                    }

                    _abilityReadyStates[key] = isNowReady;
                }

                var lives = inGame.GetHealth();
                if (lives != livesState)
                {
                    BroadcastEvent("livesChanged", new { delta = lives - livesState, lives });
                    livesState = lives;
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
                var map = inGame.GetMap();

                // Meta:
                var mapName = inGame.SelectedMap;
                var difficulty = inGame.SelectedDifficulty.ToString();
                var gameMode = inGame.SelectedMode.ToString();
                var gameType = inGame.GameType.ToString();
                var isPaused = TimeManager.gamePaused;
                var isFastForwarding = TimeManager.FastForwardActive;
                var isAutostartEnabled = sim.autoPlay;

                // Map:
                var paths = inGame.GetMap().pathManager.paths.ToList().Select(p =>
                    new
                    {
                        waypoints = p.segments
                            .Where(s => p.IsPointOnMap(new Vector3(s.point.x, s.point.y)))
                            .Select(s => new { s.point.x, s.point.y }),
                        length = p.leakDist
                    }
                );
                var removables = inGame.GetAllObjectsOfType<Removeable>().Select(r =>
                    {
                        var rm = r.removeableModel;
                        return new
                        {
                            id = r.Id.Id,
                            cost = rm.removealCost,
                            position = new { rm.position.x, rm.position.y }
                        };
                    }
                );

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
                    var tts = tower.GetTowerToSim();

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

                    var upgradePrices = new List<float>([0f, 0f, 0f]);
                    if (model?.tiers != null)
                        for (var i = 0; i < 3; i++)
                        {
                            upgradePrices[i] = tts.GetUpgradeCost(i, model.tiers[i], -1f, false);
                        }

                    towers.Add(new
                    {
                        id = tower.Id.Id,
                        type = model?.name ?? "unknown",
                        upgrades = model?.tiers.ToList() ?? new List<int>([0, 0, 0]),
                        upgradePrices,
                        pos = new { x = tower.Position.X, y = tower.Position.Y },
                        range = model?.range ?? 0,
                        hasGlobalRange = model?.isGlobalRange ?? false,
                        roundPlaced = tower.createdOnRound - 1,
                        sellValue = tower.SaleValue,
                        cashEarned = tower?.cashEarned ?? 0,
                        livesGained = tower?.livesGained ?? 0,
                        isHero = model?.IsHero() ?? false,
                        targetingMode = tower?.TargetType.id ?? "unknown",
                        canTargetCamo = tower?.canTargetCamo ?? false,
                        canUpgradeToParagon = tower?.CanUpgradeToParagon() ?? false,
                        isParagon = model?.isParagon ?? false,
                        abilities,
                        mutators = tower?.mutators.ToArray().Select(m => m.mutator.id) ?? [],
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
                    map = mapName,
                    difficulty,
                    gameMode,
                    gameType,
                    isPaused,
                    isFastForwarding,
                    isAutostartEnabled,
                    paths,
                    removables,
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
                return JsonConvert.SerializeObject(new { error = e.Message + "\n\n" + e.StackTrace });
            }
        }
        private static string GetMatchBloonsJson()
        {
            var inGame = InGame.instance;
            if (inGame == null) return JsonConvert.SerializeObject(new { error = "not in match" });
            var bloonToSims = inGame.bridge.GetAllBloons().ToList();
            var data = new List<object>();
            foreach (var bts in bloonToSims)
            {
                var bloon = bts.GetBloon();
                var model = bloon.bloonModel;
                data.Add(new
                {
                    id = bts.id.Id,
                    type = model.id,
                    pos = new { bts.position.x, bts.position.y },
                    model.speed,
                    bloon.health,
                    model.maxHealth,
                    model.isCamo,
                    isRegrow = model.isGrow,
                    model.isFortified,
                    model.isMoab,
                    bloon.distanceTraveled,
                    progress = bloon.distanceTraveled / bloon.path.leakDist
                });
            }
            return JsonConvert.SerializeObject(data);
        }
        private static string GetMatchRoundsJson(int? idx = null)
        {
            var inGame = InGame.instance;
            if (inGame == null) return JsonConvert.SerializeObject(new { error = "not in match" });
            var rounds = inGame.GetGameModel().roundSet.rounds;
            var data = new Dictionary<int, object>();
            double cumCash = 0;
            for (var i = 0; i < rounds.Length; i++)
            {
                var round = rounds[i];
                var cash = GetRoundCash(round, i + 1, inGame.GetGameModel());
                cumCash = System.Math.Round(cumCash + cash, 3);
                data[i] = new
                {
                    groups = round.groups.Select(g => new { g.bloon, g.count, g.start, g.end }),
                    cash,
                    cumCash
                };
            }
            if (idx != null)
                if (data.ContainsKey((int)idx))
                    return JsonConvert.SerializeObject(data[(int)idx]);
                else
                    return JsonConvert.SerializeObject(new { error = $"no data for round index {(int)idx}" });
            else
                return JsonConvert.SerializeObject(data);
        }
        private static bool CanPlaceTowerAt(float x, float y, TowerModel towerModel)
        {
            var bridge = InGame.instance.bridge;
            return bridge.CanPlaceTowerAt(
                new UnityEngine.Vector2(x, y),
                towerModel,
                bridge.MyPlayerNumber,
                ObjectId.FromData(0)
            );
        }
        private static string CanPlaceTowerAtJson(float x, float y, string towerId)
        {
            if (InGame.instance == null)
                return JsonConvert.SerializeObject(new { error = "not in match" });
            var model = GetTowerModel(towerId);
            if (model == null)
                return JsonConvert.SerializeObject(new { error = "tower not found" });
            return JsonConvert.SerializeObject(new { can = CanPlaceTowerAt(x, y, model) });
        }
        private static string TowersPlaceableAtJson(float x, float y)
        {
            var inGame = InGame.instance;
            if (InGame.instance == null)
                return JsonConvert.SerializeObject(new { error = "not in match" });
            var placeables = Helpers.ValidBaseTowerNames().ToList().Where(t =>
            {
                var m = GetTowerModel(t);
                return inGame.GetTowerInventory().HasInventory(m)
                && GameExt.GetBtd6Player(Game.instance).HasUnlockedTower(t)
                && CanPlaceTowerAt(x, y, m);
            });
            return JsonConvert.SerializeObject(placeables);
        }

        private static string GetPlayerJson()
        {
            var btd6Player = Game.instance.GetBtd6Player();
            var playerProfile = Game.instance.GetPlayerProfile();

            var acquiredKnowledge = new List<string>();
            foreach (var mk in playerProfile.acquiredKnowledge)
                acquiredKnowledge.Add(mk);

            var unlockedTowers = new List<string>();
            foreach (var t in playerProfile.unlockedTowers)
                unlockedTowers.Add(t);
            var unlockedUpgrades = new Dictionary<string, List<int>>();
            foreach (var t in unlockedTowers)
            {
                List<int> unlocked = [0, 0, 0];
                for (var i = 0; i < 3; i++)
                {
                    for (var j = 1; j <= 5; j++)
                    {
                        var path1 = i == 0 ? j : 0;
                        var path2 = i == 1 ? j : 0;
                        var path3 = i == 2 ? j : 0;
                        var model = Game.instance.model.GetTowerModel(t, path1, path2, path3);
                        if (model.IsUpgradeUnlocked(i, j) ?? false) unlocked[i] = j;
                        else break;
                    }
                }
                unlockedUpgrades[t] = unlocked;
            }
            var unlockedHeroes = new List<string>();
            foreach (var hero in playerProfile.unlockedHeroes)
                unlockedHeroes.Add(hero);

            return JsonConvert.SerializeObject(new
            {
                rank = playerProfile.rank.ValueInt,
                veteranRank = playerProfile.veteranRank.ValueInt,
                xp = playerProfile.xp.Value,
                veteranXp = playerProfile.veteranXp.Value,
                monkeyMoney = btd6Player.GetMonkeyMoney(),
                knowledgePoints = playerProfile.KnowledgePoints,
                trophies = playerProfile.trophies.ValueInt,
                acquiredKnowledge,
                isKnowledgeEnabled = !playerProfile.knowledgeDisabled,
                unlockedTowers,
                unlockedUpgrades,
                unlockedHeroes
            });
        }

        private static string GetMapsJson()
        {
            var data = new Dictionary<string, object>();
            var maps = GameData._instance.mapSet.Maps.items;
            foreach (var mapDetails in maps)
            {
                data[mapDetails.id] = new
                {
                    mapDetails.difficulty,
                    isStandard = mapDetails.IsStandard
                };
            }
            return JsonConvert.SerializeObject(data);
        }

        private static string HandleAction(string json)
        {
            try
            {
                dynamic action = JsonConvert.DeserializeObject(json) ?? new { };
                string type = action.type ?? "";
                var inGame = InGame.instance;

                if (inGame == null)
                {
                    // Meta actions:
                    switch (type)
                    {
                        case "startMatch":
                            StartMatch((string)action.map, (string)action.difficulty, (string)action.gamemode);
                            return JsonConvert.SerializeObject(new { ok = true });
                        case "selectHero":
                            if (!Game.instance.GetPlayerProfile().unlockedHeroes.Contains((string)action.hero))
                                return JsonConvert.SerializeObject(new { error = "hero not found, or not unlocked" });
                            Game.instance.playerService.Player.Data.primaryHero = (string)action.hero;
                            return JsonConvert.SerializeObject(new { ok = true });
                        case "setMkEnabled":
                            Game.instance.GetPlayerProfile().knowledgeDisabled = !(bool)action.enabled;
                            return JsonConvert.SerializeObject(new { ok = true });
                    }
                }
                else
                {
                    // Match actions:
                    var bridge = inGame.bridge;
                    switch (type)
                    {
                        // Time:
                        case "setFastForward":
                            bridge.SetFastForward((bool)action.enabled);
                            return JsonConvert.SerializeObject(new { ok = true });
                        case "setFastForwardSpeed":
                            TimeHelper.OverrideFastForwardTimeScale = (double)action.speed;
                            return JsonConvert.SerializeObject(new { ok = true });
                        case "setAutostart":
                            bridge.SetAutoPlay((bool)action.enabled);
                            return JsonConvert.SerializeObject(new { ok = true });
                        case "setPaused":
                            TimeManager.gamePaused = (bool)action.paused;
                            return JsonConvert.SerializeObject(new { ok = true });

                        // Game flow:
                        case "startRound":
                            bridge.StartRound();
                            return JsonConvert.SerializeObject(new { ok = true });
                        case "setRound":
                            bridge.SetRound((int)action.round);
                            return JsonConvert.SerializeObject(new { ok = true });
                        case "restart":
                            inGame.bridge.Restart();
                            return JsonConvert.SerializeObject(new { ok = true });
                        case "quitMatch":
                            if (!inGame.quitting)
                                inGame.Quit();
                            return JsonConvert.SerializeObject(new { ok = true });

                        // Map:
                        case "sellRemovable":
                            var removable = inGame.GetAllObjectsOfType<Removeable>()
                                .FirstOrDefault(r => r.Id.Id == (int)action.id);
                            if (removable == null)
                                return JsonConvert.SerializeObject(new { error = "removable not found" });
                            inGame.GetMap().SellRemoveable(removable);
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
                            var model = GetTowerModel((string)action.towerId);
                            if (model == null)
                                return JsonConvert.SerializeObject(new { error = "tower not found" });
                            bridge.CreateTowerAt(
                                new UnityEngine.Vector2((float)action.x, (float)action.y),
                                model,
                                ObjectId.FromData(0),
                                (bool)action.free,
                                null
                            );
                            return JsonConvert.SerializeObject(new { ok = true });
                        case "upgradeTower":
                            bridge.UpgradeTower(ObjectId.FromData((uint)action.id), (int)action.path, 0d, null);
                            return JsonConvert.SerializeObject(new { ok = true });
                        case "sellTower":
                            bridge.SellTower(ObjectId.FromData((uint)action.id));
                            return JsonConvert.SerializeObject(new { ok = true });
                        case "changeTargetPriority":
                            bridge.SetNextTowerTargetType(ObjectId.FromData((uint)action.id), (bool)action.prev);
                            return JsonConvert.SerializeObject(new { ok = true });
                        case "setTargetPriority":
                            var tower = bridge.GetTower(ObjectId.FromData((uint)action.id))?.GetTower();
                            if (tower != null)
                            {
                                var targetType = tower.towerModel.TargetTypes.FirstOrDefault(tt => tt.id == (string)action.target);
                                if (targetType != null)
                                    tower.SetTargetType(targetType);
                                else
                                    return JsonConvert.SerializeObject(new { error = $"invalid targeting option '{(string)action.target}' for tower {tower.towerModel.name}" });
                            }
                            else
                                return JsonConvert.SerializeObject(new { error = "tower not found" });
                            return JsonConvert.SerializeObject(new { ok = true });
                        case "activateAbility":
                            var targetAbility = inGame.bridge.GetAllAbilities(false).ToArray()
                                .FirstOrDefault(ab =>
                                    ab.tower == bridge.GetTower(ObjectId.FromData((uint)action.id)).Id &&
                                    ab.model.displayName == (string)action.abilityName);
                            if (targetAbility == null)
                                return JsonConvert.SerializeObject(new { error = "ability not found" });
                            targetAbility.Activate();
                            return JsonConvert.SerializeObject(new { ok = true });
                        case "activateAllAbilities":
                            var abilities = inGame.bridge.GetAllAbilities(false).ToList();
                            foreach (var ab in abilities.Where(a => !a.IsPassive && a.IsReady))
                                ab.Activate();
                            return JsonConvert.SerializeObject(new { ok = true });
                    }
                }
                return JsonConvert.SerializeObject(new { error = $"unknown action '{type}', or it cannot be used here" });
            }
            catch (Exception e)
            {
                return JsonConvert.SerializeObject(new { error = e.Message + "\n\n" + e.StackTrace });
            }
        }

        public override void OnApplicationQuit()
        {
            _sseDispatcherCts?.Cancel();
            _listener?.Stop();
        }
    }
}