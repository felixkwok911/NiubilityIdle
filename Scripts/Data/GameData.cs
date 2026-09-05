using System.Collections.Generic;
using Newtonsoft.Json;
using NiubilityIdle.Core;

namespace NiubilityIdle.Data
{
    // 对应原游戏 dump.cs:172948 GameData / 173671 InfinityData / 172503 EternityData
    public class GameData
    {
        [JsonProperty] public BigDouble score = BigDouble.Zero;
        [JsonProperty] public BigDouble totalScore = BigDouble.Zero;
        [JsonProperty] public List<BigDouble> circleCosts = new();
        [JsonProperty] public List<int> circleLevels = new();
        [JsonProperty] public int prestigeCount = 0;
        [JsonProperty] public double playTime = 0;
        // 完全体扩展： prestige / 自动化 / 深层
        [JsonProperty] public BigDouble souls = BigDouble.Zero;
        [JsonProperty] public BigDouble timeFlux = BigDouble.Zero;
        [JsonProperty] public List<int> promotionLevels = new();
        [JsonProperty] public Dictionary<string,bool> automation = new();
        [JsonProperty] public BigDouble unityShards = BigDouble.Zero;
        [JsonProperty] public BigDouble minerals = BigDouble.Zero;
        [JsonProperty] public bool allUnlocked = false;
    }

    public class InfinityData
    {
        [JsonProperty] public BigDouble infinities = BigDouble.Zero;
        [JsonProperty] public BigDouble infinityPoints = BigDouble.Zero;
        [JsonProperty] public List<InfinityStat> stats = new();
        // 无限树 12 节点等级，对应原游戏 InfTUpConfig 树
        [JsonProperty] public List<int> treeLevels = new();
    }

    public class InfinityStat
    {
        [JsonProperty] public string id;
        [JsonProperty] public int level;
    }

    public class EternityData
    {
        [JsonProperty] public BigDouble EP = BigDouble.Zero; // dump.cs: EternityData EP
        [JsonProperty] public BigDouble eters = BigDouble.Zero;
        [JsonProperty] public List<bool> eternityMilestones = new();
        [JsonProperty] public List<bool> animalMilestones = new();
    }

    public class SaveData
    {
        public GameData game = new();
        public InfinityData infinity = new();
        public EternityData eternity = new();
        public string version = "1.0.0-godot";
    }
}
