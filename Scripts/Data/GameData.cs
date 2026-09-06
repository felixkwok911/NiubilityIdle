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
        // 转生窗口三行：指数 / 倍率 / 点击计数（原版：点击 5 次进行转生）
        [JsonProperty] public double prestigeExp = 1.0;
        [JsonProperty] public double prestigeMult = 1.0;
        [JsonProperty] public int prestigeClicks = 0;
        // 批量购买档 1/10/100（原版圈条旁的数字按钮）
        [JsonProperty] public int bulkBuy = 1;
        // Revolution.progress:每圈转圈进度 0..1,满一圈产出(原版 Revolution.cs progress/maxProgress)
        [JsonProperty] public List<double> revProgress = new();
        // 已解锁圈数:原版从 1 个圈逐渐解锁到 11 个
        [JsonProperty] public int unlocked = 1;
        // 已解锁成就 id(原版 unlockedAch List<int>)
        [JsonProperty] public List<int> unlockedAch = new();
        // 自动买圈开关(自动化系统第一项)
        [JsonProperty] public bool autoBuy = false;
        // 无限达成标记(用于触发一次性提示)
        [JsonProperty] public bool infBroken = false;
        // 商店增益等级:每级全局产出 ×2(原版商店 boost)
        [JsonProperty] public int boostLevel = 0;
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
