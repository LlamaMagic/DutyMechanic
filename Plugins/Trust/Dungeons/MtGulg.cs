using Buddy.Coroutines;
using Clio.Utilities;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Objects;
using RBTrust.Plugins.Trust.Helpers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Trust.Data;
using Trust.Extensions;
using Trust.Helpers;

namespace Trust.Dungeons
{
    /// <summary>
    /// Lv. 79: Mt. Gulg dungeon logic.
    /// </summary>
    public class MtGulg : AbstractDungeon
    {
        /// <summary>
        /// Gets zone ID for this dungeon.
        /// </summary>
        public new const ZoneId ZoneId = Data.ZoneId.MtGulg;

        private const int ForgivenCruelty = 8260;
        private const int ForgivenWhimsy = 8261;
        private const int Brightsphere = 7864;
        private const int JudgmentDayTower = 2009807;
        private const int ForgivenRevelry = 8270;
        private const int ForgivenObscenity = 8262;
        private const int ForgivenDissonance = 8299;

        /// <summary>
        /// Set of boss-related monster IDs.
        /// </summary>
        private static readonly HashSet<uint> BossIds = new HashSet<uint>
        {
            Brightsphere,
            ForgivenCruelty,
            ForgivenWhimsy,
            ForgivenObscenity,
            ForgivenRevelry,
            ForgivenDissonance,
        };

        private static readonly HashSet<uint> Spells = new HashSet<uint>() { 15614, 15615, 15616, 15617, 15618, 15622, 15623, 15638, 15640, 15641, 15642, 15643, 15644, 15645, 15648, 15649, 16247, 16248, 16249, 16250, 16521, 16818, 16987, 16988, 16989, 17153, 18025, };
        private static readonly HashSet<uint> LumenInfinitum = new HashSet<uint>() { 16818 };
        private static readonly HashSet<uint> Exegesis = new HashSet<uint>() { 15622, 15623, 16987, 16988, 16989, };
        private static readonly HashSet<uint> RightPalm = new HashSet<uint>() { 16247, 16248 };
        private static readonly HashSet<uint> LeftPalm = new HashSet<uint>() { 16249, 16250 };
        private static readonly HashSet<uint> GoldChaser = new HashSet<uint>() { 15652, 15653, 17066 };

        private static readonly HashSet<uint> PenancePianissimo = new HashSet<uint>() { 15644 };
        private static readonly Vector3 PenancePianissimoCenter = new Vector3(-239.9347f, 210.0f, 236.9791f);
        private static readonly int PenancePianissimoDuration = 45_000;
        private static DateTime penancePianissimoTimestamp = DateTime.MinValue;

        /// <inheritdoc/>
        public override DungeonId DungeonId => DungeonId.MtGulg;

        /// <inheritdoc/>
        public override async Task<bool> RunAsync()
        {
            if (!Core.Player.InCombat)
            {
                AvoidanceManager.RemoveAllAvoids(ai => !ai.CanRun);
            }

            if (WorldManager.SubZoneId == (uint)SubZoneId.ThePerishedPath)
            {
                if (LumenInfinitum.IsCasting())
                {
                    AvoidanceManager.RemoveAllAvoids(i => i.CanRun);
                    await MovementHelpers.GetClosestAlly.Follow();
                }
            }

            if (WorldManager.SubZoneId == (uint)SubZoneId.TheWhiteGate)
            {
                if (Exegesis.IsCasting())
                {
                    AvoidanceManager.RemoveAllAvoids(i => i.CanRun);
                    await MovementHelpers.GetClosestAlly.Follow();
                }
            }

            if (WorldManager.SubZoneId == (uint)SubZoneId.TheFalsePrayer)
            {
                BattleCharacter forgivenRevelryNpc = GameObjectManager.GetObjectsByNPCId<BattleCharacter>(NpcId: ForgivenRevelry)
                        .FirstOrDefault(bc => bc.Distance() < 50);
                if (forgivenRevelryNpc != null && forgivenRevelryNpc.IsValid)
                {
                    if (RightPalm.IsCasting() || LeftPalm.IsCasting())
                    {
                        AvoidanceManager.RemoveAllAvoids(i => i.CanRun);
                        await MovementHelpers.GetClosestAlly.Follow();
                    }
                }
            }

            if (WorldManager.SubZoneId == (uint)SubZoneId.TheWindingFlare)
            {
                BattleCharacter forgivenObscenityNpc = GameObjectManager.GetObjectsByNPCId<BattleCharacter>(NpcId: ForgivenObscenity)
                        .FirstOrDefault(bc => bc.Distance() < 50);
                if (forgivenObscenityNpc != null && forgivenObscenityNpc.IsValid)
                {
                    if (PenancePianissimo.IsCasting() && penancePianissimoTimestamp < DateTime.Now)
                    {
                        penancePianissimoTimestamp = DateTime.Now.AddMilliseconds(PenancePianissimoDuration);

                        // Create an AOE avoid for the orange swirly under the boss
                        AvoidanceHelpers.AddAvoidDonut(
                            canRun: () => Core.Player.InCombat && DateTime.Now < penancePianissimoTimestamp,
                            locationProducer: () => PenancePianissimoCenter,
                            outerRadius: 30,
                            innerRadius: 15);
                    }

                    if (GoldChaser.IsCasting())
                    {
                        Stopwatch sw = new Stopwatch();
                        sw.Start();
                        CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, 20_000, "Forgiven Obscenity Avoid");
                        while (sw.ElapsedMilliseconds < 20_000)
                        {
                            await MovementHelpers.GetClosestAlly.Follow();
                            await Coroutine.Yield();
                        }

                        sw.Stop();
                    }
                }
            }

            if (Spells.IsCasting())
            {
                CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, 1_500, "Enemy Spell Cast In Progress");
                await MovementHelpers.GetClosestAlly.Follow();
            }

            if (WorldManager.SubZoneId != (uint)SubZoneId.TheWindingFlare)
            {
                BossIds.ToggleSideStep(new uint[] { ForgivenObscenity });
            }
            else
            {
                BossIds.ToggleSideStep();
            }

            return false;
        }
    }
}
