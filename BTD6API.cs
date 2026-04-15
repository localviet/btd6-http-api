using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
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

[assembly: MelonInfo(typeof(BTD6API.Main), "BTD6 HTTP API", "1.0.0", "Askryne")]
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
        private static readonly List<(HttpListenerResponse res, CancellationTokenSource cts)> _sseClients = new();
        private static readonly object _sseLock = new object();
        private volatile bool _roundIsRunning = false;
        private volatile bool _pendingRestart = false;

        public override void OnApplicationStart()
        {
            ServerPort.onSave = _ => Serve();
            Serve();
        }

        private void Serve()
        {
            _listener?.Stop();

            int port = ServerPort;
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{port}/");
            _listener.Start();
            _thread = new Thread(Listen) { IsBackground = true };
            _thread.Start();
            Console.WriteLine($"[BTD6 API] Listening on http://localhost:{port}/");
        }

        private void Listen()
        {
            while (_listener.IsListening)
            {
                try
                {
                    var ctx = _listener.GetContext();
                    if (ctx.Request.Url.AbsolutePath == "/events")
                    {
                        // Dedicated thread for SSE:
                        new Thread(() => Handle(ctx)) { IsBackground = true }.Start();
                    }
                    else
                    {
                        ThreadPool.QueueUserWorkItem(_ => Handle(ctx));
                    }
                }
                catch { /* _listener stopped. */ }
            }
        }

        private void Handle(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var res = ctx.Response;
            res.ContentType = "application/json";
            res.Headers.Add("Access-Control-Allow-Origin", "*");
            var path = req.Url.AbsolutePath;

            string body;
            if (path == "/state/game" && req.HttpMethod == "GET")
            {
                body = GetStateJson();
            }
            else if (path == "/action" && req.HttpMethod == "POST")
            {
                var reader = new StreamReader(req.InputStream);
                var json = reader.ReadToEnd();
                body = HandleAction(json);
            }
            else if (path == "/events" && req.HttpMethod == "GET")
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

                return;
            }
            else
            {
                res.StatusCode = 404;
                body = JsonConvert.SerializeObject(new { error = "not found" });
            }

            var bytes = Encoding.UTF8.GetBytes(body);
            res.ContentLength64 = bytes.Length;
            res.OutputStream.Write(bytes, 0, bytes.Length);
            res.OutputStream.Close();
        }

        public static void BroadcastEvent(string eventType, string data = "{}")
        {
            string sseMessage = $"event: {eventType}\ndata: {data}\n\n";
            byte[] bytes = Encoding.UTF8.GetBytes(sseMessage);

            lock (_sseLock)
            {
                var dead = new List<(HttpListenerResponse, CancellationTokenSource)>();
                foreach (var (client, cts) in _sseClients)
                {
                    try
                    {
                        client.OutputStream.Write(bytes, 0, bytes.Length);
                        client.OutputStream.Flush();
                    }
                    catch
                    {
                        cts.Cancel();
                        dead.Add((client, cts));
                    }
                }
                foreach (var d in dead)
                    _sseClients.Remove(d);
            }
        }

        public override void OnMatchStart()
        {
            BroadcastEvent("matchStart");
            base.OnMatchStart();
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
                if (__instance.model.name.Contains(InGame.instance.SelectedHero))
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
                var dmg = (int)(double.Parse(__state["health"]) - InGame.instance.GetHealth());
                BroadcastEvent("bloonLeak", JsonConvert.SerializeObject(new { type = __state["type"], damage = dmg }));
            }
        }

        public override void OnUpdate()
        {
            if (_pendingRestart)
            {
                _pendingRestart = false;
                InGame.instance.bridge.Restart();
                BroadcastEvent("restart");
            }
            base.OnUpdate();
        }

        private string GetStateJson()
        {
            try
            {
                var inGame = InGame.instance;
                if (inGame == null) return JsonConvert.SerializeObject(new { status = "notInGame" });
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
                        createdOnRound = tower.createdOnRound - 1,
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

                return JsonConvert.SerializeObject(new
                {
                    status = "inGame",
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
                    selectedHero
                });
            }
            catch (Exception e)
            {
                return JsonConvert.SerializeObject(new { error = e.Message });
            }
        }

        private string HandleAction(string json)
        {
            try
            {
                dynamic action = JsonConvert.DeserializeObject(json) ?? new { };
                string type = action?.type ?? "";
                var inGame = InGame.instance;
                var sim = inGame.GetUnityToSimulation();

                switch (type)
                {
                    // Time:
                    case "setFastForward":
                        sim.SetFastForward((bool)action.enabled);
                        return "{\"ok\":true}";
                    case "setFastForwardSpeed":
                        TimeHelper.OverrideFastForwardTimeScale = (double)action.speed;
                        return "{\"ok\":true}";
                    case "setAutostart":
                        sim.SetAutoPlay((bool)action.enabled);
                        return "{\"ok\":true}";
                    case "setPaused":
                        TimeManager.gamePaused = (bool)action.paused;
                        return "{\"ok\":true}";

                    // Game flow:
                    case "startRound":
                        sim.StartRound();
                        return "{\"ok\":true}";
                    case "setRound":
                        sim.SetRound((int)action.round);
                        return "{\"ok\":true}";
                    case "restart":
                        // We'll crash if we try to restart from a thread,
                        // so set restart to true and OnUpdate will handle it next tick.
                        _pendingRestart = true;
                        return "{\"ok\":true}";

                    // Economy:
                    case "setCash":
                        inGame.SetCash((double)action.value);
                        return "{\"ok\":true}";
                    case "addCash":
                        inGame.AddCash((double)action.value);
                        return "{\"ok\":true}";
                    case "setLives":
                        inGame.SetHealth((double)action.value);
                        return "{\"ok\":true}";
                    case "addLives":
                        inGame.AddHealth((double)action.value);
                        return "{\"ok\":true}";

                    // Towers:
                    case "placeTower":
                        sim.CreateTowerAt(
                            new UnityEngine.Vector2((float)action.x, (float)action.y),
                            inGame.GetGameModel().GetTowerModel((string)action.towerId),
                            ObjectId.FromData(0),
                            (bool)action.free,
                            null
                        );
                        return "{\"ok\":true}";
                    case "upgradeTower":
                        sim.UpgradeTower(ObjectId.FromData((uint)action.id), (int)action.path, 0d, null);
                        return "{\"ok\":true}";
                    case "sellTower":
                        sim.SellTower(ObjectId.FromData((uint)action.id));
                        return "{\"ok\":true}";
                    case "changeTargetPriority":
                        sim.SetNextTowerTargetType(ObjectId.FromData((uint)action.id), (bool)action.prev);
                        return "{\"ok\":true}";
                    case "activateAbility":
                        var targetAbility = inGame.bridge.GetAllAbilities(false).ToArray()
                            .FirstOrDefault(ab =>
                                ab.tower == sim.GetTower(ObjectId.FromData((uint)action.id)).Id &&
                                ab.model.displayName == (string)action.name);
                        if (targetAbility == null)
                            return JsonConvert.SerializeObject(new { error = "ability not found" });
                        targetAbility.Activate();
                        return "{\"ok\":true}";
                    case "activateAllAbilities":
                        var abilities = inGame.bridge.GetAllAbilities(false).ToList();
                        foreach (var ab in abilities)
                            ab.Activate();
                        return "{\"ok\":true}";

                    default:
                        return $"{{\"error\":\"unknown action: {type}\"}}";
                }
            }
            catch (Exception e)
            {
                return JsonConvert.SerializeObject(new { error = e.Message });
            }
        }

        public override void OnApplicationQuit()
        {
            _listener?.Stop();
        }
    }
}