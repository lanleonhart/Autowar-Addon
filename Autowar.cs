using GNWarsServer.WindowsService.Objects;
using Newtonsoft.Json;
using PersistentMapAPI;
using PersistentMapAPI.Domain;
using Serilog;
using Serilog.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Timers;


namespace AutowarAddon
{
    public class Autowar
    {
        #region -> enums
        enum LastActionID
        {
            INVALID,
            Attack,
            Invaded,
            Fortified,
        }

        enum BehavoirID
        {
            Defensive,
            Balanced,
            Aggressive,
            WarMachine,
        }
        #endregion

        #region -> class TurnAction
        class TurnAction
        {
            public Faction FactionID;
            public LastActionID LastActionID;
            public StarSystem PickedStarSystem;
            public StarSystem TargetedStarSystem;
            public Faction TargetedFactionID;
        }
        #endregion

        #region -> class FactionStats
        class FactionStats
        {
            #region -> fields
            [JsonProperty] int _strength = 1;
            [JsonProperty] int _difficulty = 1;
            [JsonProperty] int _techLevel = 3;
            [JsonProperty] BehavoirID _behavoir = BehavoirID.Balanced;
            #endregion

            #region -> properties
            [JsonProperty] public Faction FactionID { get; private set; }

            public bool Enabled = true;

            [JsonIgnore]
            public int Turns
            {
                get
                {
                    int turns = (_strength + _difficulty + _techLevel) / 3;
                    switch (_behavoir)
                    {
                        case BehavoirID.WarMachine: turns = (int)Math.Round(turns * 1.5); break;
                        case BehavoirID.Aggressive: turns = (int)Math.Round(turns * 1.25); break;

                    }

                    return turns;
                }
            }

            [JsonIgnore]
            public int AttackChance
            {
                get
                {
                    int chance = 75; //default
                    switch (_behavoir)
                    {
                        case BehavoirID.WarMachine:
                        case BehavoirID.Aggressive: chance = 95; break;
                        case BehavoirID.Defensive: chance = 15; break;
                    }

                    return chance;
                }
            }
            #endregion

            #region -> methods
            /// <summary>
            /// assigns random values
            /// </summary>
            /// <param name="id"></param>
            public FactionStats(Faction factionID)
            {
                FactionID = factionID;

                var r = new Random();
                _strength = r.Next(1, 11);
                _difficulty = r.Next(1, 11);
                _techLevel = r.Next(1, 4);
                _behavoir = (BehavoirID)r.Next(0, (int)BehavoirID.WarMachine + 1);
            }

            public FactionStats(Faction factionID, int strength, int difficulty, int techLevel, BehavoirID behavoir)
            {
                FactionID = factionID;
                _strength = strength;
                _difficulty = difficulty;
                _techLevel = techLevel;
                _behavoir = behavoir;
            }

            [JsonConstructor] FactionStats() { }

            public override string ToString()
            {
                return $"{FactionID.DisplayName()}, Enabled:{Enabled}, Strength: {_strength}, Difficulty: {_difficulty}, Tech Level: {_techLevel}, Behavoir: {_behavoir}";
            }
            #endregion
        }
        #endregion

        public static Autowar Create(System.Action<Faction, Faction, MissionResult, StarSystem, int, DateTime> UpdateSystemControl)
        {
            _instance = new Autowar(UpdateSystemControl);
            return _instance;
        }

        static Autowar _instance;
        public static Autowar Instance
        { 
            get
            {
                if (_instance == null)
                    throw new Exception("Create MUST be called FIRST!");
                return _instance;
            }
        }


        Timer _tick_timer = new Timer();
        
        bool _enabled = true;
        public bool Enabled
        {
            get => _enabled;
            set
            {
                _enabled = value;
                _tick_timer.Enabled = value;
                if (_enabled)
                {
                    _tick_timer.Start();
                    _log.Information("Autowar is enabled");
                }
                else
                {
                    _tick_timer.Stop();
                    _log.Information("Autowar is disabled");
                }
            }
        }

        public DateTime LastTick { get; private set; }
        public DateTime NextTick {get; private set; }

        Dictionary<string, UserInfo> _connectionStore;
        HashSet<PlayerHistory> _playerHistory;
        int _upperFortBorder;
        StarMap _map;
        List<string> _newsFeed;
        System.Action<Faction, Faction, MissionResult, StarSystem, int, DateTime> _updateSystemControl;


        List<TurnAction> _turns = new List<TurnAction>();
        Dictionary<Faction, FactionStats> _factionStatsDic = new Dictionary<Faction, FactionStats>();

        Logger _log;
        
        Autowar(System.Action<Faction, Faction, MissionResult, StarSystem, int, DateTime> UpdateSystemControl)
        {
            _updateSystemControl = UpdateSystemControl;

            var settings = Settings.Load();
            settings.Save();

            string logPath = Path.Combine(settings.BaseFolder, Path.Combine(settings.LogsFolder, settings.LogFilename));
            Console.WriteLine($"Autowar log path = {Path.GetFullPath(logPath)}");

            _log = new LoggerConfiguration()
                .WriteTo.File(logPath, rollingInterval: RollingInterval.Day)
                .CreateLogger();


            _tick_timer.Elapsed += _tick_timer_Elapsed;
            _tick_timer.AutoReset = true;

            
            SetInterval(settings.Autowar_Tick_Seconds);

            //load stats
            var savePath = Path.Combine(settings.BaseFolder, "autowar_faction_stats.json");
            if (File.Exists(savePath))
            {
                _log.Debug($"Reading faction stats from {savePath}");
                using (StreamReader r = new StreamReader(savePath))
                    _factionStatsDic = JsonConvert.DeserializeObject<Dictionary<Faction, FactionStats>>(r.ReadToEnd());                   
            }
            else
            {
                _log.Debug($"Generating and saving faction stats to {savePath}");
                var factions = Enum.GetValues(typeof(Faction));
                foreach (Faction f in factions)
                {
                    if (f == Faction.INVALID_UNSET || f == Faction.NoFaction) continue;

                    _factionStatsDic.Add(f, new FactionStats(f));
                }

                var data = JsonConvert.SerializeObject(_factionStatsDic);
                using (StreamWriter writer = new StreamWriter(savePath, false))
                    writer.Write(data);
            }
                        
            LoadTurns();
        }

        public void SetInterval(int seconds)
        {
            if (seconds > 0 && seconds < int.MaxValue)
            {
                _tick_timer.Interval = seconds * 1000;
                
                _log.Information($"Autowar: tick set to every {seconds} seconds");
            }
            else
                _log.Error($"Autowar: invalid value for seconds: {seconds}");
        }

        public void PreTurn(Dictionary<string, UserInfo> connectionStore, HashSet<PlayerHistory> playerHistory, int upperFortBorder, StarMap map, List<string> newsFeed)
        {
            _connectionStore = connectionStore;
            _playerHistory = playerHistory;
            _map = map;
            _newsFeed = newsFeed;
            _upperFortBorder = upperFortBorder;
        }

        public void TakeTurn()
        {
            _log.Information("***Autowar turn started***");
            _log.Information(DateTime.Now.ToString());
            var settings = Settings.Load();
            _newsFeed.Clear();

            //get all worlds that have a valid faction on it
            var systems = _map.systems.FindAll(delegate (StarSystem x)
            {
                if (x.factions.Count > 0)
                    return x.FindHighestControl().faction != Faction.NoFaction && x.FindHighestControl().faction != Faction.INVALID_UNSET;
                else
                    return false;
            });

            if (systems.Count == 0)
            {
                _log.Warning("No valid systems found");
                return;
            }

            var rand = new Random((int)DateTime.Now.Ticks);
                
            //create list of factions and systems the factions are on            
            Dictionary<Faction, List<StarSystem>> factionSystemList = new Dictionary<Faction, List<StarSystem>>();
            systems.ForEach((s) => s.factions.ForEach((f) =>
                {
                    if (!factionSystemList.ContainsKey(f.faction))
                        factionSystemList.Add(f.faction, new List<StarSystem>());

                    factionSystemList[f.faction].Add(s);
                }));

            //every faction goes at least once
            foreach (var kv in _factionStatsDic)
            {
                if (!factionSystemList.ContainsKey(kv.Key))
                    continue;

                //skipp disabled factions
                if(kv.Value.Enabled == false)
                    continue;

                var factionID = kv.Key;
                var factionStats = kv.Value;
                var turns = factionStats.Turns;

                _log.Information(factionStats.ToString());                    
                _log.Information($"{factionID.DisplayName()}'s taking {turns} turns");

                for (int i = 0; i < turns; i++)
                {
                    StarSystem pickedWorld = null;
                    //check for previous turn action and use the world if found
                    //var lastTurn = _turns.Find(x => x.FactionID == factionID);
                    //if (lastTurn != null)
                    //{
                    //    //if factions last turn was fortify pick a new faction system
                    //    if (lastTurn.LastActionID == LastActionID.Fortified)
                    //    {
                    //        var list = new List<StarSystem>(factionSystemList[factionID]);
                    //        list.Remove(lastTurn.TargetedStarSystem);
                    //        if (list.Count > 1)
                    //        {
                    //            pickedWorld = factionSystemList[factionID][rand.Next(factionSystemList[factionID].Count)];
                    //            lastTurn.PickedStarSystem = pickedWorld;
                    //        }
                    //        else
                    //            pickedWorld = lastTurn.PickedStarSystem;
                    //    }
                    //    else
                    //        pickedWorld = lastTurn.PickedStarSystem;
                    //}
                    //else
                    //{
                        //pick a random system the faction is on
                        pickedWorld = factionSystemList[factionID][rand.Next(factionSystemList[factionID].Count)];

                        ////add new turn entry since no previous turn was found
                        var lastTurn = new TurnAction();
                        //lastTurn.FactionID = factionID;
                        //lastTurn.PickedStarSystem = pickedWorld;
                        //lastTurn.TargetedStarSystem = null;
                        //lastTurn.TargetedFactionID = Faction.INVALID_UNSET;
                        //lastTurn.LastActionID = LastActionID.INVALID;
                        //_turns.Add(lastTurn);
                    //}
                    //_log.Info($"Selected {pickedWorld.name}");

                    //pick faction to do one of the actions below
                    // - attack local faction
                    // - attack faction on world or invade world
                    // - fortify                    
                    FactionControl actingFaction = pickedWorld.factions.Find(x => x.faction == factionID);
                    if (pickedWorld.factions.Count == 1) //attack or fortify if no worlds in range
                    {
                        var worlds = _map.GetSystemsInRange(pickedWorld, settings.Autowar_Planets_In_Range_Distance);
                        //remove all worlds that have the same owner and only one faction on it
                        worlds.RemoveAll((x) => x.owner == pickedWorld.owner && x.factions.Count == 1);
                        if (worlds.Count > 0)
                        {     
                            # region -> rolled attacking
                            if (rand.Next(0, 100) < factionStats.AttackChance)
                            {
                                StarSystem targetWorld;
                                if (lastTurn.TargetedStarSystem != null)
                                    targetWorld = lastTurn.TargetedStarSystem;
                                else
                                    targetWorld = worlds[rand.Next(0, worlds.Count)];

                                #region -> invade planet
                                if (targetWorld.factions.Count == 0) //no factions to attack
                                {
                                    int damage = rand.Next(settings.Autowar_Min_Faction_Control_Change, settings.Autowar_Max_Faction_Control_Change);
                                    targetWorld.SetFactionControlByFaction(actingFaction.faction, 0);
                                    var attackingFaction = targetWorld.FindFactionControlByFaction(actingFaction.faction);                                    
                                    
                                    _updateSystemControl.Invoke(attackingFaction.faction, Faction.INVALID_UNSET, MissionResult.Victory, targetWorld, damage, DateTime.UtcNow);
                                    RecordFactionActivity(actingFaction, null, targetWorld);  

                                    lastTurn.LastActionID = LastActionID.Invaded;
                                    lastTurn.TargetedFactionID = Faction.INVALID_UNSET;

                                    var msg = $"{attackingFaction.Name} invaded {targetWorld.name}";
                                    _newsFeed.Add(msg);
                                    _log.Information(msg);
                                }
                                #endregion
                                #region -> attack a faction on the planet
                                else
                                {
                                    FactionControl attackedFaction = null;
                                    if (lastTurn.TargetedFactionID != Faction.INVALID_UNSET)
                                        attackedFaction = targetWorld.FindFactionControlByFaction(lastTurn.TargetedFactionID);
                                    else
                                    {
                                        var validTargets = targetWorld.factions.FindAll(x => x.faction != actingFaction.faction); //make sure faction doesn't attack it self
                                        if (validTargets.Count > 0) //valid faction to attack
                                            attackedFaction = validTargets[rand.Next(0, validTargets.Count)];
                                    }

                                    if (attackedFaction != null)
                                    {
                                        //added entry if not on planet already
                                        var attackingFaction = targetWorld.FindFactionControlByFaction(actingFaction.faction);
                                        if (attackingFaction == null)
                                            targetWorld.SetFactionControlByFaction(actingFaction.faction, 0);

                                        int controlChange = rand.Next(settings.Autowar_Min_Faction_Control_Change, settings.Autowar_Max_Faction_Control_Change);
                                        _updateSystemControl.Invoke(actingFaction.faction, attackedFaction.faction, MissionResult.Victory, targetWorld, controlChange, DateTime.UtcNow);
                                        RecordFactionActivity(actingFaction, attackedFaction, targetWorld);

                                        //add entry to companies on planet
                                        if (targetWorld.companies.Find(x => x.Name == actingFaction.Name) == null)
                                            targetWorld.companies.Add(new Company() { Name = actingFaction.Name, Faction = actingFaction.faction });

                                        lastTurn.TargetedFactionID = attackedFaction.faction;
                                        lastTurn.LastActionID = LastActionID.Attack;

                                        var msg = $"{actingFaction.Name} attacked {attackedFaction.Name} on {targetWorld.name}. Control changed by {controlChange}";
                                        _newsFeed.Add(msg);
                                        _log.Information(msg);
                                    
                                        msg = $"{actingFaction.Name} control {actingFaction.percentage}% and {attackedFaction.Name} control {attackedFaction.percentage}%";
                                        _newsFeed.Add(msg);
                                        _log.Information(msg);
                                    }
                                    else
                                        Fortify(pickedWorld.name, actingFaction, _newsFeed, lastTurn);
                                }
                                #endregion
                            }
                            #endregion
                            //rolled fortify
                            else
                                Fortify(pickedWorld.name, actingFaction, _newsFeed, lastTurn);
                        }
                        else
                        {
                            _log.Information($"No attackable systems in range of {pickedWorld.name}");
                            Fortify(pickedWorld.name, actingFaction, _newsFeed, lastTurn);
                        }

                    }
                    #region -> attack local faction
                    else
                    {
                        var factionList = new List<FactionControl>(pickedWorld.factions);
                        factionList.Remove(actingFaction);
                        factionList.RemoveAll(x => x.percentage <= 0);
                        if (factionList.Count > 0)
                        {
                            FactionControl attackedFaction = factionList[rand.Next(0, factionList.Count)];
                            int controlChange = rand.Next(settings.Autowar_Min_Faction_Control_Change, settings.Autowar_Max_Faction_Control_Change + 1);

                            if (controlChange > attackedFaction.percentage)
                                controlChange = attackedFaction.percentage;

                            actingFaction.percentage += controlChange;
                            attackedFaction.percentage -= controlChange;

                            //add entry to companies on planet
                            if (pickedWorld.companies.Find(x => x.Name == actingFaction.Name) == null)
                                pickedWorld.companies.Add(new Company() { Name = actingFaction.Name, Faction = actingFaction.faction });

                            lastTurn.LastActionID = LastActionID.Attack;
                            lastTurn.TargetedFactionID = attackedFaction.faction;

                            //log
                            var msg = $"{actingFaction.Name} attacked {attackedFaction.Name} on {pickedWorld.name}. Control changed by {controlChange}";
                            _newsFeed.Add(msg);
                            _log.Information(msg);
                            //log
                            msg = $"{actingFaction.Name} control {actingFaction.percentage}% and {attackedFaction.Name} control {attackedFaction.percentage}%";
                            _newsFeed.Add(msg);
                            _log.Information(msg);
                        }
                        else
                        {
                            _log.Information($"No attackable factions on {pickedWorld.name}");
                            Fortify(pickedWorld.name, actingFaction, _newsFeed, lastTurn);
                        }
                    }
                    #endregion
                }
                _log.Information("---");
            }
                

            SaveTurns();

            StringBuilder stringBuilder = new StringBuilder();            
            _newsFeed.ForEach(x => stringBuilder.AppendLine(x));
            SendDiscordMessage(stringBuilder.ToString());

            _log.Information("***Autowar turn ended***");
        }

        void RecordFactionActivity(FactionControl actingFaction, FactionControl target, PersistentMapAPI.Domain.StarSystem system)
        {
            DateTime resultTime = DateTime.Now;
            string clientId = actingFaction.Name;
            _log.Debug($"{system.name} {actingFaction.Name} {(target == null ? "Invaded" : target.Name)} {clientId} {resultTime}");

            var companyActivity = new CompanyActivity
            {
                employer = actingFaction.Name,
                target = (target == null ? "Invaded" : target.Name), 
                systemId = system.name,
                companyName = actingFaction.Name,
                resultTime = resultTime,
                result = MissionResult.Victory
            };

            if (!_connectionStore.ContainsKey(clientId))
                _connectionStore.Add(clientId, new UserInfo());
                        
            _connectionStore[clientId].companyName = actingFaction.Name;
            _connectionStore[clientId].lastSystemFoughtAt = system.name;
            _connectionStore[clientId].ip = "server";
            _connectionStore[clientId].lastFactionFoughtForInWar = actingFaction.faction;
            _connectionStore[clientId].LastDataSend = DateTime.UtcNow;

            system.AddCompany(new Company { Name = actingFaction.Name, Faction = actingFaction.faction });

            var history = _playerHistory.SingleOrDefault(x => clientId.Equals(x.Id));
            if (history == null)
            {
                history = new PlayerHistory
                {
                    Id = clientId,
                    lastActive = resultTime
                };
            }
            history.lastActive = resultTime;
            history.activities.Add(companyActivity);

            _playerHistory.Add(history);
        }

        private void _tick_timer_Elapsed(object sender, ElapsedEventArgs e)
        {
            LastTick = DateTime.Now;
            NextTick = LastTick.AddMilliseconds(_tick_timer.Interval);
            TakeTurn();
            SaveTurns();
        }

        void Fortify(string worldName, FactionControl faction, List<string> newsFeed, TurnAction lastTurn)
        {
            lastTurn.LastActionID = LastActionID.Fortified;
            lastTurn.TargetedFactionID = Faction.INVALID_UNSET;

            var settings = Settings.Load();
            var rand = new Random((int)DateTime.Now.Ticks);
            if (faction.percentage < _upperFortBorder)
            {
                faction.percentage += rand.Next(settings.Autowar_Min_Fortify_Change, settings.Autowar_Max_Fortify_Change + 1);
                if (faction.percentage > _upperFortBorder)
                    faction.percentage = _upperFortBorder;
            }

            var msg = $"{faction.Name} fortifying on {worldName}. Control now {faction.percentage}";
            newsFeed.Add(msg);
            _log.Information(msg);
        }

        const string FILENAME = "autowar_faction_turns.json";
        void SaveTurns()
        {
            string path = $"{Settings.Load().BaseFolder}/{FILENAME}";
            _log.Information($"Autowar.Saving {path}");
            using (StreamWriter sw = new StreamWriter(path))
                sw.WriteLine(JsonConvert.SerializeObject(_turns));
        }

        void LoadTurns()
        {            
            string path = $"{Settings.Load().BaseFolder}/{FILENAME}";
            if (File.Exists(path))
            {
                _log.Information($"Autowar.Loading {path}");
                using (StreamReader sr = new StreamReader(path))
                    _turns = JsonConvert.DeserializeObject<List<TurnAction>>(sr.ReadToEnd());
            }
        }

        async void SendDiscordMessage(string message)
        {
            
            using (HttpClient client = new HttpClient())
            {
                //https://discord.com/developers/docs/resources/channel#embed-object
                var payload = new
                {                    
                    embeds = new[]
                    {                        
                        new
                        {
                            title = "Autowar Turn",
                            type = "rich",
                            description = message,
                        }
                    }
                };

                var jsonPayload = Newtonsoft.Json.JsonConvert.SerializeObject(payload);
                var httpContent = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                var urls = Settings.Load().Autowar_TurnDiscordWebhookURLs;
                foreach (var url in urls)
                {
                    HttpResponseMessage response = await client.PostAsync(url, httpContent);
                    if (response.IsSuccessStatusCode)
                        Console.WriteLine($"Autowar.SendDiscordMessage() - Successfully sent message to {url}");
                    else
                        Console.WriteLine($"Autowar.SendDiscordMessage() - Failed to send message to {url}. Status code: {response.StatusCode}");
                }
            }
        }
    }
}
