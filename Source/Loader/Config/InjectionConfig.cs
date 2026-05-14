/*
 * Dark Souls 3 - Open Server
 * Copyright (C) 2021 Tim Leonard
 *
 * This program is free software; licensed under the MIT license. 
 * You should have received a copy of the license along with this program. 
 * If not, see <https://opensource.org/licenses/MIT>.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Loader
{
    // Server configuration, stores the needed information required to connect to a 
    // given open server. Can be freely converted to/from json.
    [Serializable]
    public class InjectionConfig
    {
        public string ServerName            { get; set; }
        public string ServerHostname        { get; set; }
        public string ServerPublicKey       { get; set; }
        public string ServerGameType        { get; set; }
        public int ServerPort               { get; set; }
        public bool EnableSeperateSaveFiles { get; set; }
        public bool EnableModFileOverrides  { get; set; }
        public string ModOverrideDirectory  { get; set; } = "";
        public bool CacheModFilePaths       { get; set; } = true;
        public string SaveFileExtension     { get; set; } = ".ds3os";
        public bool EnableDs2ShadowResolutionPatches { get; set; }
        public int Ds2DirectionalShadowResolution    { get; set; }
        public int Ds2DynamicAtlasShadowResolution   { get; set; }
        public int Ds2DynamicPointShadowResolution   { get; set; }
        public int Ds2DynamicSpotShadowResolution    { get; set; }
        public bool EnableDs2NativeRuntime           { get; set; }
        public string Ds2NativeRuntimeSessionId      { get; set; } = "";
        public string Ds2NativeRuntimeEventLog       { get; set; } = "";
        public string Ds2NativeRuntimeCommandInbox   { get; set; } = "";

        public string ToJson()
        {
            return JsonSerializer.Serialize(this);
        }

        public static bool FromJson(string json, out ServerConfig config)
        {
            try
            {
                config = JsonSerializer.Deserialize<ServerConfig>(json);
                config.ManualImport = true;
                return true;
            }
            catch (JsonException)
            {
                config = new ServerConfig();
                return false;
            }
        }
    }
}
