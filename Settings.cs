using Newtonsoft.Json;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;

namespace AutowarAddon
{
    internal class Settings
    {
        const string FILENAME = "GNAutowar_Settings.json";

        public string BaseFolder = "../GNWarsData";
        public string LogsFolder = "GNAutowarLogs";
        public string LogFilename = "current.log";

        public bool Autowar_Enabled = true;
        public int Autowar_Tick_Seconds = 3600; //every hour        
        public int Autowar_Planets_In_Range_Distance = 25;
        public int Autowar_Max_Faction_Control_Change = 20;
        public int Autowar_Min_Faction_Control_Change = 5;
        public int Autowar_Max_Fortify_Change = 25;
        public int Autowar_Min_Fortify_Change = 3;

        public HashSet<string> Autowar_TurnDiscordWebhookURLs = new HashSet<string>()
        {
#if DEBUG
            //rabid bot test channel            
            "https://discord.com/api/webhooks/1247188043404804137/1XKiDDTniwXB3kyPk9p3pDAZmgzuTtctiDitSpFywHQOyGRHCnv2UupYS3hJ4YtnKA2o",
#else
            //GN Wars bot-testing channel
            "https://discord.com/api/webhooks/1247188984845701130/Bo1mhWJ4TPf3u348JayrjDEj0wCRYmz4-vtdLO5aM5_5XAyRULP5ZPgdCM_mIdlv8ROY"
#endif
        };

        static public Settings Load()
        {
            if (File.Exists(FILENAME))
            {                
                var file = File.ReadAllText(FILENAME);
                var s = JsonConvert.DeserializeObject<Settings>(file);
                return s;
            }
            else
                return new Settings();
        }

        private Settings() { }

        public void Save()
        {
            try
            {
                if (!File.Exists(FILENAME))
                {
                    var json = JsonConvert.SerializeObject(this);
                    File.WriteAllText(FILENAME, json);
                }
            }
            catch { Console.WriteLine("FAILED to save autowar settings"); }
        }
    }
}
